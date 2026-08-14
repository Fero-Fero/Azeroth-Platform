using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Services;
using AzerothPlatform.Infrastructure.Services.Parsers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AzerothPlatform.Infrastructure;

/// <summary>
/// Infrastructure service registration helpers.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");

        services
            .AddOptions<DockerOptions>()
            .Bind(configuration.GetSection(DockerOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.SocketPath), "Docker:SocketPath is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.BuildsPath), "Docker:BuildsPath is required.")
            .ValidateOnStart();

        services
            .AddOptions<StackUpdateCheckerOptions>()
            .Bind(configuration.GetSection("StackUpdateChecker"))
            .ValidateOnStart();

        services
            .AddOptions<GitHubOptions>()
            .Bind(configuration.GetSection("GitHub"))
            .ValidateOnStart();

        services
            .AddOptions<ClientDistributionOptions>()
            .Bind(configuration.GetSection(ClientDistributionOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.RootPath), "Client:RootPath is required.")
            .ValidateOnStart();

        services
            .AddOptions<MigrationOptions>()
            .Bind(configuration.GetSection(MigrationOptions.SectionName));

        services
            .AddOptions<LauncherBuildOptions>()
            .Bind(configuration.GetSection(LauncherBuildOptions.SectionName));

        services
            .AddOptions<ArmoryOptions>()
            .Bind(configuration.GetSection(ArmoryOptions.SectionName));

        services
            .AddOptions<ArmoryAssetsOptions>()
            .Bind(configuration.GetSection(ArmoryAssetsOptions.SectionName));

        services
            .AddOptions<ClientServerOptions>()
            .Bind(configuration.GetSection(ClientServerOptions.SectionName));

        services
            .AddOptions<ServerTypeCatalogOptions>()
            .Bind(configuration.GetSection(ServerTypeCatalogOptions.SectionName));

        services.AddDbContext<AzerothCoreDbContext>(options => options.UseSqlite(connectionString));
        services.AddHttpClient();
        services.AddHttpClient("GitHubApi"); // Dedicated client for GitHub API
        services.AddScoped<IMySqlConnectionFactory, MySqlConnectionFactory>();
        services.AddScoped<ISoapProxyService, SoapProxyService>();
        services.AddScoped<IAccountManagementService, AccountManagementService>();
        services.AddScoped<IRealmService, RealmService>();
        services.AddScoped<IArmoryAccountsService, ArmoryAccountsService>();
        services.AddScoped<IDockerService, DockerService>();
        services.AddScoped<IGitService, GitService>();
        services.AddScoped<IBuildService, BuildService>();
        services.AddSingleton<IModulePackageStorage, ModulePackageStorage>();
        services.AddScoped<IModuleCatalogService, ModuleCatalogService>();
        services.AddScoped<ICommunityModuleCatalogService, CommunityModuleCatalogService>();
        services.AddMemoryCache();
        services.AddScoped<IModuleConfigService, ModuleConfigService>();
        services.AddSingleton<IServiceEnvTemplateService, ServiceEnvTemplateService>();
        services.AddSingleton<IServerTypeCatalog, ServerTypeCatalog>();
        services.AddScoped<IStackConfigurationValidator, StackConfigurationValidator>();
        services.AddSingleton<IRemoteEngineService, RemoteEngineService>();
        services.AddScoped<IStackImageShippingService, StackImageShippingService>();
        services.AddScoped<IStackService, StackService>();
        services.AddScoped<IStackDockerService, StackDockerService>();
        services.AddScoped<IStackVersionService, StackVersionService>();
        services.AddScoped<IStackDiscoveryService, StackDiscoveryService>();
        services.AddScoped<IGitHubApiService, GitHubApiService>();
        services.AddSingleton<ISecretProtector, SecretProtector>();
        services.AddSingleton<IManifestSigningKeyProvider, ManifestSigningKeyProvider>();
        services.AddSingleton<IClientDistributionService, ClientDistributionService>();
        services.AddScoped<IClientService, ClientService>();
        services.AddScoped<IClientContainerService, ClientContainerService>();
        services.AddScoped<IMigrationService, Services.Migrations.MigrationService>();
        services.AddSingleton<IMigrationImageService, Services.Migrations.MigrationImageService>();
        services.AddSingleton<IMigrationApplyRunner, Services.Migrations.MigrationApplyRunner>();
        services.AddScoped<IStackLauncherService, Services.Migrations.StackLauncherService>();
        services.AddScoped<IAddonService, AddonService>();
        services.AddScoped<ILuaScriptService, LuaScriptService>();
        services.AddScoped<IServerConfigService, ServerConfigService>();
        services.AddScoped<IIndividualProgressionSyncService, Services.IndividualProgression.IndividualProgressionSyncService>();
        services.AddScoped<IConfigMigrationService, ConfigMigrationService>();
        services.AddScoped<IRevisionService, RevisionService>();
        services.AddScoped<ILauncherPortalService, LauncherPortalService>();
        services.AddScoped<IStackRegistryService, StackRegistryService>();
        services.AddSingleton<ILauncherBuildService, LauncherBuildService>();
        services.AddSingleton<IArmoryImageService, ArmoryImageService>();
        services.AddSingleton<IArmoryAssetsService, ArmoryAssetsService>();
        services.AddScoped<IArmoryDbcService, ArmoryDbcService>();
        services.AddSingleton<IClientServerImageService, ClientServerImageService>();
        services.AddSingleton<IArmoryJobService, ArmoryJobService>();
        services.AddSingleton<IStackJobService, StackJobService>();
        services.AddSingleton<IDockerCleanupJobService, DockerCleanupJobService>();

        // Register module configuration parsers
        services.AddScoped<IModuleConfigParser, PlayerbotConfigParser>();
        services.AddScoped<IModuleConfigParser, TransmogConfigParser>();
        services.AddScoped<IModuleConfigParser, AutoBalanceConfigParser>();
        services.AddScoped<IModuleConfigParser, AhBotConfigParser>();
        
        // Background services
        services.AddHostedService<StackUpdateCheckerService>();

        return services;
    }
}
