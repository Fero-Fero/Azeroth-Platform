using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;
using Serilog;
using Serilog.Events;
using System.Text.Json.Serialization;
using AzerothPlatform.Api.Hubs;
using AzerothPlatform.Api.Services;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;

// Configure Serilog. Keep application logs at Information, but suppress framework noise. In particular
// Microsoft.EntityFrameworkCore.Database.Command logs every SQL statement at Information by default,
// which floods production logs with full query text (and, if sensitive-data logging is ever enabled,
// parameter values). Overriding it to Warning keeps the logs clean and avoids incidental disclosure of
// the manager's schema/queries. ASP.NET request/routing chatter is likewise dropped to Warning.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

try
{
    Log.Information("Starting Azeroth Platform API");

    var builder = WebApplication.CreateBuilder(args);

    // Use Serilog for logging
    builder.Host.UseSerilog();

    // Add services to the container
    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
        });
    
    // Add SignalR with optimized settings for real-time streaming
    builder.Services.AddSignalR(options =>
    {
        options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
        options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    })
    // Serialize enums as strings over the hub too (the REST API already does via AddJsonOptions), so
    // DTOs like ArmoryJobStatusDto arrive with string phases/actions matching the frontend types.
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

    // Configure CORS. The production SPA is served same-origin from wwwroot, so CORS only matters for
    // the dev server and cross-origin SignalR. Scope it to explicit origins/methods/headers instead of
    // AllowAny* (which, combined with AllowCredentials, is a dangerous configuration).
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowFrontend", policy =>
        {
            var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() 
                ?? new[] { "http://localhost:5173" };
            
            policy.WithOrigins(allowedOrigins)
                  .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
                  .WithHeaders(
                      "Authorization",
                      "Content-Type",
                      "Accept",
                      "x-requested-with",
                      "x-signalr-user-agent")
                  .AllowCredentials(); // Required for SignalR
        });
    });

    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddSingleton<ICloudAuditActorProvider, HttpCloudAuditActorProvider>();

    // Single-admin authentication (JWT bearer). The admin secret + signing key live in AdminAuthService;
    // ConfigureJwtBearerOptions resolves that same singleton so issuance and validation share a key.
    builder.Services.AddSingleton<AdminAuthService>();
    builder.Services.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearerOptions>();
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
    // Deny by default: any endpoint without an explicit [Authorize]/[AllowAnonymous] requires an
    // authenticated admin. Intended public routes (launcher distribution, auth/login, health, the SPA
    // fallback) are opted out with [AllowAnonymous]. This prevents a newly added controller/action from
    // being silently exposed.
    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    });

    // Throttle admin login attempts per client IP to slow brute-force guessing.
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy("login", httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
    });

    // Register SignalR event publisher
    builder.Services.AddSingleton<AzerothPlatform.Core.Services.Interfaces.IBuildEventPublisher, AzerothPlatform.Api.Services.SignalRBuildEventPublisher>();
    builder.Services.AddSingleton<AzerothPlatform.Core.Services.Interfaces.IArmoryEventPublisher, AzerothPlatform.Api.Services.SignalRArmoryEventPublisher>();
    builder.Services.AddSingleton<AzerothPlatform.Core.Services.Interfaces.IClientEventPublisher, AzerothPlatform.Api.Services.SignalRClientEventPublisher>();
    builder.Services.AddSingleton<AzerothPlatform.Core.Services.Interfaces.IStackEventPublisher, AzerothPlatform.Api.Services.SignalRStackEventPublisher>();
    builder.Services.AddSingleton<AzerothPlatform.Core.Services.Interfaces.IDockerCleanupEventPublisher, AzerothPlatform.Api.Services.SignalRDockerCleanupEventPublisher>();

    // Configure Swagger/OpenAPI
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    var app = builder.Build();

    // Wire migration tracing to the logger so patch-apply spans (trace id, stage, duration) are
    // visible in the console. Registering the ActivitySource with OpenTelemetry later would export
    // the same spans to an APM backend without changing the service code.
    AzerothPlatform.Infrastructure.Services.Migrations.MigrationTelemetry.RegisterLoggingListener(
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AzerothPlatform.Migrations.Tracing"));

    await using (var scope = app.Services.CreateAsyncScope())
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        
        Log.Information("Applying database migrations...");
        await dbContext.Database.MigrateAsync();
        Log.Information("Database migrations applied successfully");
        
        // Ensure builds directory exists
        var dockerOptions = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DockerOptions>>().Value;
        Directory.CreateDirectory(dockerOptions.BuildsPath);
        Log.Information("Builds directory ready at: {BuildsPath}", dockerOptions.BuildsPath);

        var buildService = scope.ServiceProvider.GetRequiredService<IBuildService>();
        await buildService.RecoverInterruptedBuildsAsync();
        Log.Information("Interrupted build recovery completed");

        // Ensure client distribution directory exists
        var clientOptions = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ClientDistributionOptions>>().Value;
        Directory.CreateDirectory(Path.Combine(clientOptions.RootPath, "game"));
        Directory.CreateDirectory(Path.Combine(clientOptions.RootPath, "settings"));
        Log.Information("Client distribution directory ready at: {ClientRootPath}", clientOptions.RootPath);
    }

    // Configure the HTTP request pipeline
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "Azeroth Platform API v1");
            options.RoutePrefix = "swagger";
        });
    }

    // The SPA entry document (index.html, served directly or via the fallback) must never be
    // cached, so that a fresh deploy's new (content-hashed) JS/CSS bundle references are always
    // picked up; otherwise a browser can keep loading a stale bundle after we rebuild (e.g. old
    // routing code). Hashed assets under /assets remain cacheable. This runs for any text/html
    // response, covering both UseStaticFiles and MapFallbackToFile("index.html").
    app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var contentType = context.Response.ContentType;
            if (!string.IsNullOrEmpty(contentType) &&
                contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                context.Response.Headers.Pragma = "no-cache";
                context.Response.Headers.Expires = "0";
            }
            return Task.CompletedTask;
        });
        await next();
    });

    // Serve static files (frontend) from wwwroot
    app.UseDefaultFiles();
    app.UseStaticFiles();

    // Missing hashed bundles under /assets must 404 anonymously. If the request falls through to the
    // global FallbackPolicy (RequireAuthenticatedUser), browsers load lazy chunks without a JWT and
    // get 401 + empty/wrong MIME — which surfaces as "disallowed MIME type" on module import.
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/assets", out _))
        {
            var env = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
            var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
            var assetsRoot = Path.GetFullPath(Path.Combine(webRoot, "assets"));
            var relative = context.Request.Path.Value!.TrimStart('/');
            var candidate = Path.GetFullPath(Path.Combine(webRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(assetsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !candidate.Equals(assetsRoot, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!File.Exists(candidate))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.ContentType = "text/plain";
                return;
            }
        }

        await next();
    });

    // Use CORS
    app.UseCors("AllowFrontend");

    // Use routing
    app.UseRouting();

    // Rate limiting (applied to endpoints that opt in, e.g. login).
    app.UseRateLimiter();

    // Authn/Authz (admin-only API behind a single admin secret).
    app.UseAuthentication();
    app.UseAuthorization();

    // Map controllers
    app.MapControllers();

    app.MapHub<BuildProgressHub>("/hubs/buildprogress");
    app.MapHub<BuildProgressHub>("/hubs/build-progress");
    app.MapHub<ContainerLogsHub>("/hubs/container-logs");
    app.MapHub<ArmoryProgressHub>("/hubs/armory-progress");
    app.MapHub<StackProgressHub>("/hubs/stack-progress");
    app.MapHub<CloudTerminalHub>("/hubs/cloud-terminal");

    // Fallback to index.html for client-side routing (SPA). Only for extensionless paths — if a hashed
    // bundle under /assets is missing (stale deploy / cache mismatch), return 404 instead of serving
    // HTML, which browsers reject as a JS module with a MIME/type error.
    app.MapFallback(async (HttpContext context) =>
    {
        var requestPath = context.Request.Path.Value ?? string.Empty;
        if (Path.HasExtension(requestPath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var env = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
        var indexPath = Path.Combine(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"), "index.html");
        if (!File.Exists(indexPath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(indexPath);
    }).AllowAnonymous();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// Required for WebApplicationFactory in integration tests
public partial class Program { }
