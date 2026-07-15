using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace AzerothPlatform.Api.Services;

/// <summary>
/// Single-admin authentication: validates the configured admin password (constant-time) and issues
/// short-lived JWTs. The HS256 signing key is a random 256-bit secret persisted to the data volume
/// (independent of the admin password), so learning the password does not let an attacker forge tokens
/// and tokens survive restarts. An explicit <c>Auth:JwtKey</c> (>= 32 chars) overrides the persisted key.
/// </summary>
public sealed class AdminAuthService
{
    public const string Issuer = "azeroth-platform";
    public const string Audience = "azeroth-platform-admin";
    private const int DefaultLifetimeHours = 4;

    private readonly string _password;
    private readonly SymmetricSecurityKey _signingKey;
    private readonly TimeSpan _lifetime;

    public AdminAuthService(IConfiguration configuration, ILogger<AdminAuthService> logger)
    {
        var configured = configuration["Admin:Password"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            // No password configured: generate a random one and persist it to a restricted file so it
            // never lands in application logs / log drains (which are often widely readable).
            configured = GenerateRandomSecret();
            var location = PersistGeneratedPassword(configuration, configured, logger);
            logger.LogWarning(
                "No Admin:Password configured. A temporary admin password was generated and written to {Location}. " +
                "Set Admin__Password (env) to a stable value to keep sessions across restarts.",
                location);
        }

        _password = configured;

        var keyMaterial = configuration["Auth:JwtKey"];
        byte[] keyBytes;
        if (!string.IsNullOrWhiteSpace(keyMaterial))
        {
            if (Encoding.UTF8.GetByteCount(keyMaterial) < 32)
            {
                throw new InvalidOperationException("Auth:JwtKey must be at least 32 characters (256 bits) for HS256.");
            }

            keyBytes = Encoding.UTF8.GetBytes(keyMaterial);
        }
        else
        {
            // Random, persisted key decoupled from the admin password.
            keyBytes = LoadOrCreatePersistedKey(configuration, logger);
        }

        _signingKey = new SymmetricSecurityKey(keyBytes);

        var hours = configuration.GetValue("Auth:TokenLifetimeHours", DefaultLifetimeHours);
        _lifetime = TimeSpan.FromHours(hours <= 0 ? DefaultLifetimeHours : hours);
    }

    public SymmetricSecurityKey SigningKey => _signingKey;

    /// <summary>Constant-time comparison of the supplied password against the configured one.</summary>
    public bool ValidatePassword(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(candidate);
        var b = Encoding.UTF8.GetBytes(_password);
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(a),
            SHA256.HashData(b));
    }

    public string CreateToken()
    {
        var now = DateTime.UtcNow;
        var credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: new[]
            {
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, "admin"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            },
            notBefore: now,
            expires: now.Add(_lifetime),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string GenerateRandomSecret()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Loads the persisted 256-bit JWT signing key from the data directory, creating (and locking down)
    /// a fresh random one on first run. Kept out of the admin password's derivation so a leaked password
    /// cannot be used to mint tokens. Regenerates on restart only if the file cannot be persisted.
    /// </summary>
    private static byte[] LoadOrCreatePersistedKey(IConfiguration configuration, ILogger logger)
    {
        try
        {
            var dir = ResolveDataDir(configuration);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "jwt-signing.key");

            if (File.Exists(path))
            {
                var existing = Convert.FromBase64String(File.ReadAllText(path).Trim());
                if (existing.Length >= 32)
                {
                    return existing;
                }
            }

            var key = new byte[32];
            RandomNumberGenerator.Fill(key);
            File.WriteAllText(path, Convert.ToBase64String(key));
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch { /* best effort */ }
            }

            return key;
        }
        catch (Exception ex)
        {
            // If we cannot persist, fall back to an in-memory random key. Tokens will not survive a
            // restart (admins re-login), which is acceptable and still secure.
            logger.LogWarning(ex, "Could not persist the JWT signing key; using an in-memory key for this run.");
            var key = new byte[32];
            RandomNumberGenerator.Fill(key);
            return key;
        }
    }

    /// <summary>Resolves the persistent data directory (data volume), used for the signing key file.</summary>
    private static string ResolveDataDir(IConfiguration configuration)
    {
        var explicitDir = configuration["Auth:KeyDir"];
        if (!string.IsNullOrWhiteSpace(explicitDir))
        {
            return explicitDir;
        }

        // Derive from the SQLite connection string ("Data Source=/app/data/azeroth-platform.db"), which
        // points at the persistent data volume, so the key lives alongside the manager's own state.
        var conn = configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(conn))
        {
            foreach (var part in conn.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0].Trim().Equals("Data Source", StringComparison.OrdinalIgnoreCase))
                {
                    var dbDir = Path.GetDirectoryName(kv[1].Trim());
                    if (!string.IsNullOrWhiteSpace(dbDir))
                    {
                        return dbDir;
                    }
                }
            }
        }

        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// Writes the generated password to a file the operator can read out-of-band, restricting it to the
    /// owner where the OS supports it. Returns a human-readable location (path, or "the application logs"
    /// as a last-resort fallback if the file could not be written).
    /// </summary>
    private static string PersistGeneratedPassword(IConfiguration configuration, string password, ILogger logger)
    {
        try
        {
            var dir = configuration["Admin:GeneratedPasswordDir"];
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = AppContext.BaseDirectory;
            }

            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "admin-password.txt");
            File.WriteAllText(path, password + Environment.NewLine);

            // Best-effort owner-only permissions (POSIX). No-op / ignored on Windows.
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch { /* best effort */ }
            }

            return path;
        }
        catch (Exception ex)
        {
            // Fall back to logging the password only if we truly cannot persist it, so the operator is
            // not locked out. This is the least-bad option and only happens on a broken filesystem.
            logger.LogError(ex, "Could not write the generated admin password to disk.");
            logger.LogWarning("Generated temporary admin password (write failed, shown once): {Password}", password);
            return "the application logs";
        }
    }
}
