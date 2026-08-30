using AzerothPlatform.ClientContent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Provides the platform's manifest-signing keypair. The private key is generated once and persisted in
/// the data volume; the same private key is injected into every client-server container so all manifests
/// (manager-served and container-served) verify against the single public key the launcher receives over
/// the trusted (TLS) config channel.
/// </summary>
public interface IManifestSigningKeyProvider
{
    /// <summary>Base64 PKCS#8 private key. Always populated: startup fails closed if it cannot be created.</summary>
    string PrivateKeyPkcs8Base64 { get; }

    /// <summary>Base64 SPKI public key handed to the launcher for verification. Always populated (see above).</summary>
    string PublicKeySpkiBase64 { get; }
}

/// <inheritdoc />
public sealed class ManifestSigningKeyProvider : IManifestSigningKeyProvider
{
    private const string KeyFileName = "manifest-signing.key";

    public string PrivateKeyPkcs8Base64 { get; }
    public string PublicKeySpkiBase64 { get; }

    public ManifestSigningKeyProvider(IConfiguration configuration, ILogger<ManifestSigningKeyProvider> logger)
    {
        try
        {
            var dir = ResolveDataDir(configuration);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, KeyFileName);

            string privateKey;
            if (File.Exists(path))
            {
                privateKey = File.ReadAllText(path).Trim();
                // Validate; regenerate if corrupt.
                _ = ManifestSigner.DerivePublicKey(privateKey);
            }
            else
            {
                (privateKey, _) = ManifestSigner.GenerateKeyPair();
                File.WriteAllText(path, privateKey);
                if (!OperatingSystem.IsWindows())
                {
                    try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                    catch { /* best effort */ }
                }
            }

            PrivateKeyPkcs8Base64 = privateKey;
            PublicKeySpkiBase64 = ManifestSigner.DerivePublicKey(privateKey);
        }
        catch (Exception ex)
        {
            // Fails closed. Falling back to "signing disabled" would silently downgrade the whole
            // client-distribution channel to unauthenticated — launchers skip verification when the
            // server advertises no public key, so a MITM could tamper with client files undetected.
            logger.LogError(ex, "Could not load or create the manifest signing key. Refusing to start with signing disabled.");
            throw new InvalidOperationException(
                "Manifest signing key could not be loaded or created. The server will not start without it, " +
                "because serving unsigned client manifests would allow undetected tampering. Check the data " +
                "directory permissions/space (see Auth:KeyDir / the SQLite data path).", ex);
        }
    }

    private static string ResolveDataDir(IConfiguration configuration)
    {
        var explicitDir = configuration["Auth:KeyDir"];
        if (!string.IsNullOrWhiteSpace(explicitDir))
        {
            return explicitDir;
        }

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
}
