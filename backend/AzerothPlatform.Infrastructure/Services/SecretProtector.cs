using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Symmetric authenticated encryption for sensitive values stored at rest (e.g. external-engine SSH
/// private keys in SQLite). Uses AES-256-GCM with a random data key persisted in the protected data
/// volume. Ciphertext is tagged with a version marker so legacy plaintext values keep working and are
/// transparently re-encrypted on their next write.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypts a plaintext value into a marker-prefixed, base64 token. Blank in → blank out.</summary>
    string Protect(string? plaintext);

    /// <summary>Decrypts a token produced by <see cref="Protect"/>. Values without the marker (legacy
    /// plaintext) are returned unchanged so existing data still works.</summary>
    string Unprotect(string? protectedValue);

    /// <summary>True when a value is already in the encrypted-at-rest format.</summary>
    bool IsProtected(string? value);
}

/// <inheritdoc />
public sealed class SecretProtector : ISecretProtector
{
    private const string Marker = "enc:v1:";
    private const string KeyFileName = "secret-protection.key";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public SecretProtector(IConfiguration configuration, ILogger<SecretProtector> logger)
    {
        _key = LoadOrCreateKey(configuration, logger);
    }

    public bool IsProtected(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Marker, StringComparison.Ordinal);

    public string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        // Layout: nonce | tag | ciphertext, base64-encoded after the version marker.
        var combined = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, combined, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, combined, NonceSize + TagSize, cipher.Length);
        return Marker + Convert.ToBase64String(combined);
    }

    public string Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return string.Empty;
        }

        if (!IsProtected(protectedValue))
        {
            return protectedValue; // legacy plaintext
        }

        var combined = Convert.FromBase64String(protectedValue[Marker.Length..]);
        if (combined.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Protected value is malformed.");
        }

        var nonce = combined.AsSpan(0, NonceSize);
        var tag = combined.AsSpan(NonceSize, TagSize);
        var cipher = combined.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] LoadOrCreateKey(IConfiguration configuration, ILogger logger)
    {
        try
        {
            var dir = ResolveDataDir(configuration);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, KeyFileName);

            if (File.Exists(path))
            {
                var existing = Convert.FromBase64String(File.ReadAllText(path).Trim());
                if (existing.Length == 32)
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
            // If we cannot persist a key, fall back to an in-memory one so the app still runs. Values
            // encrypted this run won't be decryptable after a restart, which surfaces as a re-entry prompt
            // rather than a crash.
            logger.LogWarning(ex, "Could not persist the secret-protection key; using an in-memory key for this run.");
            var key = new byte[32];
            RandomNumberGenerator.Fill(key);
            return key;
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
