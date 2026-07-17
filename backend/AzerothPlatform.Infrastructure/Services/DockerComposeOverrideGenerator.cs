using System.Text;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Generates docker-compose.override.yml content for AzerothCore stacks.
/// Centralized to avoid duplication between BuildService and StackService.
/// </summary>
public static class DockerComposeOverrideGenerator
{
    /// <summary>
    /// The Docker Compose project name for a stack. This is intentionally id-only and stable: it
    /// drives the compose project label, named volumes, network and all lifecycle commands, so it
    /// must not change when a stack is renamed (that would orphan its data volume).
    /// </summary>
    public static string GetComposeProjectName(string stackId)
    {
        return $"acore-{stackId}";
    }

    /// <summary>
    /// Prefix used for the actual <c>container_name</c> values, embedding both the (sanitized) stack
    /// name and its unique id so containers are easy to recognize in <c>docker ps</c>
    /// (e.g. <c>acore-my-server-ab12cd34…-worldserver</c>). Falls back to the id-only project name
    /// when no usable name is supplied. The migration pipeline uses this same prefix when it execs
    /// into those containers, so the two must always agree.
    /// </summary>
    public static string GetContainerPrefix(string stackId, string? stackName)
    {
        var slug = SanitizeName(stackName);
        return string.IsNullOrEmpty(slug) ? GetComposeProjectName(stackId) : $"acore-{slug}-{stackId}";
    }

    /// <summary>Named docker volume that holds the modules tree for an external stack (pre-seeded on the remote).</summary>
    public static string ModulesVolumeName(string stackId) => $"acore-{stackId}-modules";

    /// <summary>Named docker volume that holds the Lua scripts for an external stack (pre-seeded on the remote).</summary>
    public static string LuaVolumeName(string stackId) => $"acore-{stackId}-lua";

    /// <summary>
    /// Per-stack base client volume (the ~17 GB base WoW client) seeded from that stack's own uploaded
    /// client. Each stack maintains its own base so admins upload the client per stack.
    /// </summary>
    public static string ClientBaseVolumeName(string stackId) => $"acore-{stackId}-client-base";

    /// <summary>Per-stack read-write overlay volume for the client container (published patch MPQs).</summary>
    public static string ClientOverlayVolumeName(string stackId) => $"acore-{stackId}-client-overlay";

    /// <summary>Per-stack writable cache volume for the client container (hash cache + manifest snapshot).</summary>
    public static string ClientCacheVolumeName(string stackId) => $"acore-{stackId}-client-cache";

    /// <summary>
    /// Per-stack launcher distribution volume: holds the built launcher exe + <c>build.json</c> that the
    /// client container serves at <c>/launcher/download</c> + <c>/launcher/latest</c>. Seeded by the
    /// manager's LauncherBuildService when a build targets this stack.
    /// </summary>
    public static string ClientLauncherDistVolumeName(string stackId) => $"acore-{stackId}-launcher-dist";

    /// <summary>Named volume holding the server config (env/dist/etc), pre-seeded from the manager.</summary>
    public static string EtcVolumeName(string stackId) => $"acore-{stackId}-etc";

    /// <summary>Named volume holding the server logs (env/dist/logs).</summary>
    public static string LogsVolumeName(string stackId) => $"acore-{stackId}-logs";

    /// <summary>
    /// All Docker named volumes owned by a stack (external per-stack volumes plus compose-managed DB/client-data).
    /// Used when deleting a stack to reclaim disk space on the engine.
    /// </summary>
    public static IEnumerable<string> GetAllStackVolumeNames(string stackId)
    {
        var project = GetComposeProjectName(stackId);
        yield return ModulesVolumeName(stackId);
        yield return LuaVolumeName(stackId);
        yield return EtcVolumeName(stackId);
        yield return LogsVolumeName(stackId);
        yield return ClientBaseVolumeName(stackId);
        yield return ClientOverlayVolumeName(stackId);
        yield return ClientCacheVolumeName(stackId);
        yield return ClientLauncherDistVolumeName(stackId);
        yield return ArmoryAssetsVolumeName(stackId);
        yield return $"{project}_ac-database";
        yield return $"{project}_ac-client-data";
    }

    /// <summary>
    /// Per-stack 3D model-viewer asset volume (the <c>frontend-armory/data</c> dataset). Seeded from
    /// that stack's own uploaded armory data and served read-only by its <c>armory-assets</c> sidecar.
    /// </summary>
    public static string ArmoryAssetsVolumeName(string stackId) => $"acore-{stackId}-armory-assets";

    /// <summary>Compose volume key for the per-stack armory asset dataset (resolves to <c>ArmoryAssetsVolumeName(stackId)</c>).</summary>
    public const string ArmoryAssetsVolumeKey = "armory_assets";

    /// <summary>Canonical per-service env bucket ids (must match ServiceEnvTemplateService).</summary>
    public const string WorldserverService = "worldserver";
    public const string AuthserverService = "authserver";
    public const string ArmoryService = "armory";
    public const string ClientService = "client";

    private static readonly IReadOnlyDictionary<string, string> EmptyEnv =
        new Dictionary<string, string>();

    public static string Generate(
        string stackId,
        string? stackName,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? serviceEnvironment,
        bool includeLua = false,
        ArmoryComposeOptions? armory = null,
        bool external = false,
        ClientComposeOptions? client = null)
    {
        var containerPrefix = GetContainerPrefix(stackId, stackName);
        var sb = new StringBuilder();

        sb.AppendLine("# Docker Compose Override - Custom Configuration");
        sb.AppendLine("# Generated by Azeroth Platform");
        sb.AppendLine("# Per-stack data lives in named volumes seeded by the manager (local and external alike).");
        if (external)
        {
            sb.AppendLine("# External stack: images are pre-shipped to the remote engine.");
        }
        sb.AppendLine();
        sb.AppendLine("services:");

        var assets = armory is not null && armory.AssetsAvailable;

        AppendDatabaseOverride(sb, $"{containerPrefix}-database");
        AppendDbImportOverride(sb, stackId, $"{containerPrefix}-db-import", external);
        AppendWorldserverOverride(sb, stackId, containerPrefix, Bucket(serviceEnvironment, WorldserverService), includeLua, external);
        AppendAuthserverOverride(sb, stackId, containerPrefix, Bucket(serviceEnvironment, AuthserverService), external);
        AppendServiceOverride(sb, "ac-client-data-init", $"{containerPrefix}-client-data-init");
        AppendServiceOverride(sb, "ac-tools", $"{containerPrefix}-tools");
        AppendServiceOverride(sb, "ac-dev-server", $"{containerPrefix}-dev-server");

        if (armory is not null)
        {
            AppendArmoryService(sb, $"{containerPrefix}-armory", armory, Bucket(serviceEnvironment, ArmoryService));
            if (assets)
            {
                AppendArmoryAssetsService(sb, $"{containerPrefix}-armory-assets");
            }
        }

        if (client is not null)
        {
            AppendClientService(sb, $"{containerPrefix}-client", client, Bucket(serviceEnvironment, ClientService));
        }

        // All per-stack data lives in named volumes the manager creates and seeds (a daemon-side copy
        // locally, a tar stream over SSH for external). They are declared external so compose reuses the
        // volumes we populated rather than auto-creating empty project volumes.
        sb.AppendLine();
        sb.AppendLine("volumes:");
        AppendExternalVolume(sb, "modules", ModulesVolumeName(stackId));
        // etc/logs use the full volume name as their key because the base compose references them via
        // ${DOCKER_VOL_ETC}/${DOCKER_VOL_LOGS}, which the manager sets to these same names.
        AppendExternalVolume(sb, EtcVolumeName(stackId), EtcVolumeName(stackId));
        AppendExternalVolume(sb, LogsVolumeName(stackId), LogsVolumeName(stackId));
        if (includeLua)
        {
            AppendExternalVolume(sb, "lua_scripts", LuaVolumeName(stackId));
        }
        if (client is not null)
        {
            // Per-stack base (seeded from this stack's uploaded client) + per-stack overlay/cache.
            AppendExternalVolume(sb, "client_base", ClientBaseVolumeName(stackId));
            AppendExternalVolume(sb, "client_overlay", ClientOverlayVolumeName(stackId));
            AppendExternalVolume(sb, "client_cache", ClientCacheVolumeName(stackId));
            AppendExternalVolume(sb, "client_launcher", ClientLauncherDistVolumeName(stackId));
        }
        if (assets)
        {
            AppendExternalVolume(sb, ArmoryAssetsVolumeKey, ArmoryAssetsVolumeName(stackId));
        }

        return sb.ToString();
    }

    private static void AppendExternalVolume(StringBuilder sb, string key, string name)
    {
        sb.AppendLine($"  {key}:");
        sb.AppendLine("    external: true");
        sb.AppendLine($"    name: {name}");
    }

    /// <summary>
    /// Per-stack self-contained client file server (<c>azeroth-platform-client</c>). Mounts a shared
    /// read-only base client layer plus this stack's read-write overlay (published patch MPQs) and a
    /// writable cache, computes its own SHA-256 manifest, and serves manifest + files to the launcher.
    /// For local stacks the mounts are host paths; for external stacks they are pre-seeded named
    /// volumes (see the <c>volumes:</c> block).
    /// </summary>
    private static void AppendClientService(
        StringBuilder sb, string containerName, ClientComposeOptions client, IReadOnlyDictionary<string, string> overrides)
    {
        sb.AppendLine("  client:");
        sb.AppendLine($"    image: {client.ImageName}");
        sb.AppendLine($"    container_name: {containerName}");
        sb.AppendLine("    restart: unless-stopped");
        // The container verifies player logins against the stack's auth DB over the host's published DB
        // port, so it needs host.docker.internal to resolve (same as the armory).
        sb.AppendLine("    extra_hosts:");
        sb.AppendLine("      - \"host.docker.internal:host-gateway\"");
        sb.AppendLine("    environment:");
        var defaults = new List<(string, string)>
        {
            ("CLIENT_BASE_ROOT", "/client/base"),
            ("CLIENT_OVERLAY_ROOT", "/client/overlay"),
            ("CLIENT_CACHE_DIR", "/client/cache"),
            ("CLIENT_LAUNCHER_DIST_DIR", "/launcher-dist"),
            ("CLIENT_MANAGED_PREFIXES", client.ManagedPrefixes),
            ("CLIENT_AUTH_TOKEN", client.AuthToken),
            ("CLIENT_MANIFEST_PRIVATE_KEY", client.ManifestPrivateKey),
            // Player portal identity + registry fallback.
            ("CLIENT_STACK_ID", client.StackId),
            ("CLIENT_APP_NAME", client.AppName),
            ("CLIENT_DISPLAY_NAME", client.DisplayName),
            ("CLIENT_REALMLIST_HOST", client.RealmlistHost),
            ("CLIENT_REALMLIST_PORT", client.RealmlistPort.ToString()),
            ("CLIENT_ARMORY_PORT", client.ArmoryPort.ToString()),
            ("CLIENT_TEMPLATE", client.Template),
            ("CLIENT_ACCENT_COLOR", client.AccentColor),
            ("CLIENT_REQUIRE_LOGIN", client.RequireLogin ? "1" : "0"),
            // In-container login against the stack auth DB.
            ("CLIENT_LOGIN_ENABLED", client.LoginEnabled ? "1" : "0"),
            ("CLIENT_DB_HOST", client.DbHost),
            ("CLIENT_DB_PORT", client.DbPort.ToString()),
            ("CLIENT_DB_USER", client.DbUser),
            ("CLIENT_DB_PASSWORD", client.DbPassword),
            ("CLIENT_AUTH_DATABASE", "acore_auth"),
        };
        // Mount paths, auth token, signing key, DB credentials and stack identity are managed and must
        // not be overridden by operator env tweaks.
        var protectedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "CLIENT_BASE_ROOT", "CLIENT_OVERLAY_ROOT", "CLIENT_CACHE_DIR", "CLIENT_LAUNCHER_DIST_DIR",
            "CLIENT_AUTH_TOKEN", "CLIENT_MANIFEST_PRIVATE_KEY", "CLIENT_STACK_ID",
            "CLIENT_LOGIN_ENABLED", "CLIENT_DB_HOST", "CLIENT_DB_PORT", "CLIENT_DB_USER",
            "CLIENT_DB_PASSWORD", "CLIENT_AUTH_DATABASE",
        };
        EmitMergedEnv(sb, defaults, overrides, protectedKeys);
        sb.AppendLine("    volumes:");
        sb.AppendLine("      - client_base:/client/base:ro");
        sb.AppendLine("      - client_overlay:/client/overlay");
        sb.AppendLine("      - client_cache:/client/cache");
        sb.AppendLine("      - client_launcher:/launcher-dist");
        sb.AppendLine("    ports:");
        sb.AppendLine($"      - \"${{DOCKER_CLIENT_EXTERNAL_PORT}}:{client.ContainerPort}\"");
    }

    /// <summary>
    /// Per-stack 3D model-viewer asset server. Serves the heavy datasets (meta/mo3/bone/textures)
    /// that live in the platform's <c>frontend-armory/data</c> directory as plain static files, so
    /// the armory can proxy its <c>/data/*</c> routes to it instead of baking multi-GB assets into
    /// its image. It lives on the stack (one per armory) and shares the compose project's default
    /// network with <c>frontend-armory</c>, which reaches it by the <c>armory-assets</c> service name.
    /// The dataset is served from this stack's own pre-seeded armory assets volume.
    /// </summary>
    private static void AppendArmoryAssetsService(StringBuilder sb, string containerName)
    {
        sb.AppendLine("  armory-assets:");
        sb.AppendLine("    image: nginx:alpine");
        sb.AppendLine($"    container_name: {containerName}");
        sb.AppendLine("    restart: unless-stopped");
        sb.AppendLine("    volumes:");
        sb.AppendLine($"      - {ArmoryAssetsVolumeKey}:/usr/share/nginx/html:ro");
        sb.AppendLine("    healthcheck:");
        sb.AppendLine("      test: [\"CMD-SHELL\", \"wget -q --spider http://127.0.0.1/ || exit 1\"]");
        sb.AppendLine("      interval: 30s");
        sb.AppendLine("      timeout: 5s");
        sb.AppendLine("      retries: 3");
        sb.AppendLine("      start_period: 10s");
    }

    /// <summary>
    /// Per-stack armory (frontend-armory) service definition. Not part of the AzerothCore base
    /// compose, so it is added here as a new service. It reaches the stack's MySQL over the host's
    /// published DB port (host.docker.internal) using the stack root credentials, and is only
    /// started/stopped explicitly (never by <c>up -d</c> of the whole stack unless requested).
    /// </summary>
    private static void AppendArmoryService(
        StringBuilder sb, string containerName, ArmoryComposeOptions armory, IReadOnlyDictionary<string, string> overrides)
    {
        var pw = armory.DbPassword;
        var publicUrl = string.IsNullOrWhiteSpace(armory.PlatformPublicUrl) ? armory.PlatformApiUrl : armory.PlatformPublicUrl;

        sb.AppendLine("  frontend-armory:");
        sb.AppendLine($"    image: {armory.ImageName}");
        sb.AppendLine($"    container_name: {containerName}");
        sb.AppendLine("    restart: unless-stopped");
        sb.AppendLine("    extra_hosts:");
        sb.AppendLine("      - \"host.docker.internal:host-gateway\"");

        // Bring up the per-stack asset sidecar with the armory and let the armory resolve it by
        // service name on the project's default network (see AppendArmoryAssetsService).
        if (armory.AssetsAvailable)
        {
            sb.AppendLine("    depends_on:");
            sb.AppendLine("      - armory-assets");
        }

        sb.AppendLine("    environment:");
        var defaults = new List<(string, string)>
        {
            ("ACORE_ARMORY_WEBSITE_URL", publicUrl),
            ("ACORE_ARMORY_WEBSITE_NAME", armory.WebsiteName),
            ("ACORE_ARMORY_WEBSITE_ROOT", ""),
            ("ACORE_ARMORY_IFRAME_MODE__ENABLED", "0"),
            ("ACORE_ARMORY_IFRAME_MODE__URL", ""),
            ("ACORE_ARMORY_LOAD_DBCS", "1"),
            ("ACORE_ARMORY_HIDE_GAME_MASTERS", "0"),
            ("ACORE_ARMORY_TRANSMOG_MODULE", "0"),
            ("ACORE_ARMORY_USE_ZAM_CDN", "0"),
            ("ACORE_ARMORY_WORLD_MAP_MODULE", "1"),
            // Per-stack asset sidecar (armory-assets) that serves the heavy 3D model-viewer data
            // (meta/mo3/bone/textures). The armory proxies its /data/* routes to it server-side, so the
            // browser stays same-origin. Blank leaves the viewer to whatever assets exist locally.
            ("ACORE_ARMORY_ASSET_PROXY_URL", armory.AssetProxyUrl),
            ("ACORE_ARMORY_REALMS__0__NAME", armory.RealmName),
            ("ACORE_ARMORY_REALMS__0__REALM_ID", armory.RealmId.ToString()),
            ("ACORE_ARMORY_REALMS__0__AUTH_DATABASE", "acore_auth"),
            ("ACORE_ARMORY_REALMS__0__CHARACTERS_DATABASE__HOST", armory.DbHost),
            ("ACORE_ARMORY_REALMS__0__CHARACTERS_DATABASE__PORT", armory.DbPort.ToString()),
            ("ACORE_ARMORY_REALMS__0__CHARACTERS_DATABASE__USER", armory.DbUser),
            ("ACORE_ARMORY_REALMS__0__CHARACTERS_DATABASE__PASSWORD", pw),
            ("ACORE_ARMORY_REALMS__0__CHARACTERS_DATABASE__DATABASE", "acore_characters"),
            ("ACORE_ARMORY_WORLD_DATABASE__HOST", armory.DbHost),
            ("ACORE_ARMORY_WORLD_DATABASE__PORT", armory.DbPort.ToString()),
            ("ACORE_ARMORY_WORLD_DATABASE__USER", armory.DbUser),
            ("ACORE_ARMORY_WORLD_DATABASE__PASSWORD", pw),
            ("ACORE_ARMORY_WORLD_DATABASE__DATABASE", "acore_world"),
            ("ACORE_ARMORY_DB_QUERY_TIMEOUT", "10000"),
            // Player account login/registration (SRP6 against acore_auth on the same MySQL server).
            ("ACORE_ARMORY_ACCOUNTS__ENABLED", "1"),
            ("ACORE_ARMORY_ACCOUNTS__ALLOW_REGISTRATION", "1"),
            ("ACORE_ARMORY_ACCOUNTS__MIN_PASSWORD_LENGTH", "8"),
            ("ACORE_ARMORY_ACCOUNTS__MAX_PASSWORD_LENGTH", "16"),
            ("ACORE_ARMORY_ACCOUNTS__SESSION_SECRET", armory.SessionSecret),
            ("ACORE_ARMORY_ACCOUNTS__SESSION_HOURS", "24"),
            ("ACORE_ARMORY_ACCOUNTS__EMAIL_CONFIRMATION_ENABLED", armory.EmailConfirmationEnabled ? "1" : "0"),
            ("ACORE_ARMORY_ACCOUNTS__EMAIL_CONFIGURED", armory.EmailConfigured ? "1" : "0"),
            // Consumed directly by the armory (news + launcher download), not via ACORE_ARMORY config.
            ("PLATFORM_API_URL", armory.PlatformApiUrl),
            ("PLATFORM_PUBLIC_URL", publicUrl),
            ("PLATFORM_STACK_ID", armory.StackId),
            // This stack's own client container: the armory serves the launcher exe from here so the
            // download never depends on the central manager.
            ("CLIENT_PORTAL_URL", armory.ClientPortalUrl),
        };

        if (armory.EmailConfirmationEnabled && armory.EmailConfigured && armory.Email is not null)
        {
            var email = armory.Email;
            defaults.AddRange(
            [
                ("ACORE_ARMORY_EMAIL__SMTP_HOST", email.SmtpHost),
                ("ACORE_ARMORY_EMAIL__SMTP_PORT", email.SmtpPort.ToString()),
                ("ACORE_ARMORY_EMAIL__SMTP_SECURITY", email.SmtpSecurity),
                ("ACORE_ARMORY_EMAIL__SMTP_USERNAME", email.SmtpUsername),
                ("ACORE_ARMORY_EMAIL__SMTP_PASSWORD", email.SmtpPassword),
                ("ACORE_ARMORY_EMAIL__FROM_ADDRESS", email.FromAddress),
                ("ACORE_ARMORY_EMAIL__FROM_NAME", email.FromName),
                ("ACORE_ARMORY_EMAIL__VERIFICATION_SUBJECT", email.VerificationSubject),
                ("ACORE_ARMORY_EMAIL__VERIFICATION_BODY_HTML", email.VerificationBodyHtml),
            ]);
        }

        // Operators may tune display/behaviour keys, but connectivity and identity are managed:
        // credentials, realm wiring, session secret and the platform wiring can never be overridden.
        var protectedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "ACORE_ARMORY_WEBSITE_URL",
            "ACORE_ARMORY_ASSET_PROXY_URL",
            "ACORE_ARMORY_REALMS__0__NAME",
            "ACORE_ARMORY_REALMS__0__REALM_ID",
            "ACORE_ARMORY_REALMS__0__AUTH_DATABASE",
            "ACORE_ARMORY_REALMS__0__CHARACTERS_DATABASE__HOST",
            "ACORE_ARMORY_REALMS__0__CHARACTERS_DATABASE__PORT",
            "ACORE_ARMORY_REALMS__0__CHARACTERS_DATABASE__USER",
            "ACORE_ARMORY_REALMS__0__CHARACTERS_DATABASE__PASSWORD",
            "ACORE_ARMORY_REALMS__0__CHARACTERS_DATABASE__DATABASE",
            "ACORE_ARMORY_WORLD_DATABASE__HOST",
            "ACORE_ARMORY_WORLD_DATABASE__PORT",
            "ACORE_ARMORY_WORLD_DATABASE__USER",
            "ACORE_ARMORY_WORLD_DATABASE__PASSWORD",
            "ACORE_ARMORY_WORLD_DATABASE__DATABASE",
            "ACORE_ARMORY_ACCOUNTS__ENABLED",
            "ACORE_ARMORY_ACCOUNTS__SESSION_SECRET",
            "ACORE_ARMORY_ACCOUNTS__EMAIL_CONFIRMATION_ENABLED",
            "ACORE_ARMORY_ACCOUNTS__EMAIL_CONFIGURED",
            "ACORE_ARMORY_EMAIL__SMTP_HOST",
            "ACORE_ARMORY_EMAIL__SMTP_PORT",
            "ACORE_ARMORY_EMAIL__SMTP_SECURITY",
            "ACORE_ARMORY_EMAIL__SMTP_USERNAME",
            "ACORE_ARMORY_EMAIL__SMTP_PASSWORD",
            "ACORE_ARMORY_EMAIL__FROM_ADDRESS",
            "ACORE_ARMORY_EMAIL__FROM_NAME",
            "ACORE_ARMORY_EMAIL__VERIFICATION_SUBJECT",
            "ACORE_ARMORY_EMAIL__VERIFICATION_BODY_HTML",
            "PLATFORM_API_URL",
            "PLATFORM_PUBLIC_URL",
            "PLATFORM_STACK_ID",
            "CLIENT_PORTAL_URL",
        };

        EmitMergedEnv(sb, defaults, overrides, protectedKeys);
        sb.AppendLine("    ports:");
        sb.AppendLine("      - \"${DOCKER_ARMORY_EXTERNAL_PORT}:48733\"");
        sb.AppendLine("    healthcheck:");
        sb.AppendLine("      test: [\"CMD\", \"node\", \"-e\", \"fetch('http://127.0.0.1:48733/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))\"]");
        sb.AppendLine("      interval: 30s");
        sb.AppendLine("      timeout: 5s");
        sb.AppendLine("      retries: 3");
        sb.AppendLine("      start_period: 20s");
    }

    private static void AppendEnv(StringBuilder sb, string key, string value)
    {
        sb.AppendLine($"      {key}: \"{EscapeEnv(value)}\"");
    }

    /// <summary>Returns the env bucket for a service, or an empty map when none was supplied.</summary>
    private static IReadOnlyDictionary<string, string> Bucket(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? serviceEnvironment, string serviceId) =>
        serviceEnvironment is not null
        && serviceEnvironment.TryGetValue(serviceId, out var bucket)
        && bucket is not null
            ? bucket
            : EmptyEnv;

    /// <summary>
    /// Emits a service's <c>environment:</c> entries as unique YAML keys: service defaults first, then
    /// admin overrides layered on top (a user value replaces the default line rather than producing a
    /// duplicate key). Keys in <paramref name="protectedKeys"/> are managed by the platform and can never
    /// be overridden (e.g. DB credentials, secrets, container paths). Insertion order is preserved.
    /// </summary>
    private static void EmitMergedEnv(
        StringBuilder sb,
        IEnumerable<(string Key, string Value)> defaults,
        IReadOnlyDictionary<string, string> overrides,
        ISet<string>? protectedKeys = null)
    {
        var order = new List<string>();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        void Set(string key, string value)
        {
            if (!map.ContainsKey(key))
            {
                order.Add(key);
            }

            map[key] = value;
        }

        foreach (var (key, value) in defaults)
        {
            Set(key, value);
        }

        foreach (var (key, value) in overrides)
        {
            if (string.IsNullOrWhiteSpace(key) || (protectedKeys is not null && protectedKeys.Contains(key)))
            {
                continue;
            }

            Set(key, value);
        }

        foreach (var key in order)
        {
            AppendEnv(sb, key, map[key]);
        }
    }

    /// <summary>
    /// Escapes a value for a YAML double-quoted scalar that also passes through Compose variable
    /// interpolation: backslash and quote are YAML-escaped, and <c>$</c> is doubled so Compose does
    /// not treat it as a variable reference.
    /// </summary>
    private static string EscapeEnv(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            // Doubled so Compose does not interpret it as variable interpolation.
            .Replace("$", "$$")
            // A literal newline would break the double-quoted YAML scalar.
            .Replace("\r", string.Empty)
            .Replace("\n", "\\n");
    }

    /// <summary>
    /// Turns a free-form stack name into a Docker-safe slug: lowercase, only [a-z0-9-], collapsed
    /// dashes, trimmed. Returns an empty string when nothing usable remains.
    /// </summary>
    private static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(name.Length);
        var lastDash = false;
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) && ch < 128)
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        return sb.ToString().Trim('-');
    }

    private static void AppendServiceOverride(StringBuilder sb, string serviceName, string containerName)
    {
        sb.AppendLine($"  {serviceName}:");
        sb.AppendLine($"    container_name: {containerName}");
    }

    /// <summary>
    /// MySQL service override. Beyond the container name, it hardens the server against file-based
    /// exfiltration/injection that operator-supplied SQL could otherwise abuse: <c>secure-file-priv=NULL</c>
    /// disables <c>INTO OUTFILE</c>/<c>LOAD_FILE</c>/<c>LOAD DATA INFILE</c>, <c>local-infile=0</c> blocks
    /// <c>LOAD DATA LOCAL INFILE</c>, and <c>skip-symbolic-links</c> prevents symlink table tricks. The
    /// AzerothCore DB import streams dumps via <c>mysql &lt; file.sql</c>, so none of these break setup.
    /// </summary>
    private static void AppendDatabaseOverride(StringBuilder sb, string containerName)
    {
        sb.AppendLine("  ac-database:");
        sb.AppendLine($"    container_name: {containerName}");
        sb.AppendLine("    command:");
        sb.AppendLine("      - \"--secure-file-priv=NULL\"");
        sb.AppendLine("      - \"--local-infile=0\"");
        sb.AppendLine("      - \"--skip-symbolic-links\"");
    }

    private static void AppendDbImportOverride(StringBuilder sb, string stackId, string containerName, bool external)
    {
        sb.AppendLine("  ac-db-import:");
        sb.AppendLine($"    container_name: {containerName}");
        if (external)
        {
            // Pin the shipped image so the remote engine never tries to build from missing source.
            sb.AppendLine($"    image: acore/ac-wotlk-db-import:{stackId}");
        }
    }

    private static void AppendWorldserverOverride(
        StringBuilder sb,
        string stackId,
        string containerPrefix,
        IReadOnlyDictionary<string, string> customEnvironment,
        bool includeLua,
        bool external)
    {
        sb.AppendLine("  ac-worldserver:");
        sb.AppendLine($"    container_name: {containerPrefix}-worldserver");
        if (external)
        {
            sb.AppendLine($"    image: acore/ac-wotlk-worldserver:{stackId}");
        }

        // Mount the modules directory (pre-seeded named volume) for SQL migrations, critical for modules
        // like Playerbots.
        sb.AppendLine("    volumes:");
        sb.AppendLine("      - modules:/azerothcore/modules:ro");

        // Mount Lua scripts into Eluna's default ScriptPath (relative to the worldserver bin dir).
        // Requires an Eluna module compiled into the image for the scripts to actually run.
        if (includeLua)
        {
            sb.AppendLine("      - lua_scripts:/azerothcore/env/dist/bin/lua_scripts");
        }

        // Always add environment variables section (for SOAP at minimum), then layer the operator's
        // per-service worldserver overrides on top. SOAP is required for remote management, so its keys
        // are protected from being overridden here.
        sb.AppendLine("    environment:");
        EmitMergedEnv(
            sb,
            new[]
            {
                ("AC_SOAP_ENABLED", "1"),
                ("AC_SOAP_IP", "0.0.0.0"),
                ("AC_SOAP_PORT", "7878"),
            },
            customEnvironment,
            protectedKeys: new HashSet<string>(StringComparer.Ordinal)
            {
                "AC_SOAP_ENABLED", "AC_SOAP_IP", "AC_SOAP_PORT",
            });
    }

    private static void AppendAuthserverOverride(
        StringBuilder sb,
        string stackId,
        string containerPrefix,
        IReadOnlyDictionary<string, string> environment,
        bool external)
    {
        sb.AppendLine("  ac-authserver:");
        sb.AppendLine($"    container_name: {containerPrefix}-authserver");
        if (external)
        {
            sb.AppendLine($"    image: acore/ac-wotlk-authserver:{stackId}");
        }
        sb.AppendLine("    ports:");
        sb.AppendLine("      - \"${DOCKER_AUTH_EXTERNAL_PORT}:3724\"");

        // The authserver has no baseline overrides; only emit the block when the operator set some.
        if (environment.Count > 0)
        {
            sb.AppendLine("    environment:");
            EmitMergedEnv(sb, Array.Empty<(string, string)>(), environment);
        }
    }
}

/// <summary>
/// Values needed to render a stack's <c>frontend-armory</c> service in the compose override.
/// </summary>
public sealed record ArmoryComposeOptions
{
    public required string ImageName { get; init; }
    public required string WebsiteName { get; init; }
    public required string RealmName { get; init; }
    public required int RealmId { get; init; }
    public required string DbHost { get; init; }
    public required int DbPort { get; init; }
    public required string DbUser { get; init; }
    public required string DbPassword { get; init; }
    public required string PlatformApiUrl { get; init; }
    public required string PlatformPublicUrl { get; init; }
    public required string StackId { get; init; }

    /// <summary>
    /// URL of this stack's own client-server container on the compose network (e.g.
    /// <c>http://client:8090</c>), used by the armory to serve the launcher download without reaching
    /// the manager. Blank when the stack has no client container (download is then unavailable).
    /// </summary>
    public string ClientPortalUrl { get; init; } = string.Empty;

    /// <summary>Secret used by the armory to sign player session cookies (per-stack, stable).</summary>
    public required string SessionSecret { get; init; }

    /// <summary>
    /// Base URL the armory proxies its <c>/data/*</c> model-viewer routes to (the per-stack
    /// <c>armory-assets</c> sidecar, reached by service name). Blank leaves the armory to serve
    /// whatever assets exist locally (viewer disabled if none).
    /// </summary>
    public string AssetProxyUrl { get; init; } = string.Empty;

    /// <summary>
    /// Whether this stack's 3D model-viewer dataset (<c>frontend-armory/data</c>) is available to seed
    /// into its per-stack armory assets volume. When false the <c>armory-assets</c> sidecar is not
    /// emitted and <see cref="AssetProxyUrl"/> should also be blank.
    /// </summary>
    public bool AssetsAvailable { get; init; }

    public bool EmailConfirmationEnabled { get; init; }

    public bool EmailConfigured { get; init; }

    public ArmoryEmailComposeOptions? Email { get; init; }
}

/// <summary>SMTP + template values injected into the armory container when email confirmation is enabled.</summary>
public sealed record ArmoryEmailComposeOptions
{
    public string SmtpHost { get; init; } = string.Empty;
    public int SmtpPort { get; init; } = 587;
    public string SmtpSecurity { get; init; } = "starttls";
    public string SmtpUsername { get; init; } = string.Empty;
    public string SmtpPassword { get; init; } = string.Empty;
    public string FromAddress { get; init; } = string.Empty;
    public string FromName { get; init; } = string.Empty;
    public string VerificationSubject { get; init; } = string.Empty;
    public string VerificationBodyHtml { get; init; } = string.Empty;
}

/// <summary>
/// Values needed to render a stack's self-contained <c>client</c> file-server service. The mounts are
/// always pre-seeded named volumes (<c>client_base</c>/<c>client_overlay</c>/<c>client_cache</c>) for
/// both local and external stacks.
/// </summary>
public sealed record ClientComposeOptions
{
    public required string ImageName { get; init; }
    public required int ContainerPort { get; init; }

    /// <summary>Comma-separated managed-file prefixes forwarded to the client server.</summary>
    public string ManagedPrefixes { get; init; } = "Data/patch-,Interface/AddOns/";

    /// <summary>Bearer token guarding the client server's /rescan + /force-verify + POST /portal endpoints.</summary>
    public string AuthToken { get; init; } = string.Empty;

    /// <summary>Base64 PKCS#8 ECDSA private key the client server uses to sign its manifest.</summary>
    public string ManifestPrivateKey { get; init; } = string.Empty;

    // ==== Player portal (registry/launcher/login) served by the stack container ====

    /// <summary>Whether the container verifies player logins against the stack auth DB (POST /login).</summary>
    public bool LoginEnabled { get; init; }

    /// <summary>Auth DB host reachable from the client container (host.docker.internal + published port).</summary>
    public string DbHost { get; init; } = "host.docker.internal";
    public int DbPort { get; init; }
    public string DbUser { get; init; } = "root";
    public string DbPassword { get; init; } = string.Empty;

    /// <summary>Stack identity + advertised connection info for the fallback /portal document.</summary>
    public string StackId { get; init; } = string.Empty;
    public string AppName { get; init; } = "Azeroth Platform";
    public string DisplayName { get; init; } = string.Empty;
    public string RealmlistHost { get; init; } = string.Empty;
    public int RealmlistPort { get; init; }
    public int ArmoryPort { get; init; }
    public string Template { get; init; } = string.Empty;
    public string AccentColor { get; init; } = string.Empty;
    public bool RequireLogin { get; init; }
}
