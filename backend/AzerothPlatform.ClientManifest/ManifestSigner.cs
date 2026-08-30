using System.Security.Cryptography;
using System.Text;
using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.ClientContent;

/// <summary>
/// Signs and verifies <see cref="Core.Contracts.ClientManifest"/> instances with ECDSA (P-256 / SHA-256). This is the
/// single source of truth for the canonical signing payload so the manager, the standalone client-server
/// container, and the launcher all agree on exactly which bytes are covered by the signature.
///
/// ECDSA (P-256) is used instead of Ed25519 because it is available in the .NET BCL on every target
/// (backend and the Avalonia launcher) with no extra dependency; it provides equivalent authenticity
/// guarantees for this use case. Keys are exchanged as base64: PKCS#8 for the private key and
/// SubjectPublicKeyInfo (SPKI) for the public key.
/// </summary>
public static class ManifestSigner
{
    /// <summary>
    /// Builds the deterministic byte payload that is signed/verified. Excludes the signature field
    /// itself. Both signer and verifier must iterate <see cref="Core.Contracts.ClientManifest.Files"/> in the same
    /// order (the builder sorts by <c>RelativePath</c> and that order is preserved through JSON).
    /// </summary>
    public static byte[] CanonicalBytes(Core.Contracts.ClientManifest manifest)
    {
        var sb = new StringBuilder();
        sb.Append(manifest.Version).Append('\n');
        sb.Append(manifest.VerifyToken).Append('\n');
        sb.Append(manifest.TotalSize).Append('\n');
        foreach (var file in manifest.Files)
        {
            sb.Append(file.RelativePath).Append(':')
              .Append(file.Size).Append(':')
              .Append(file.Sha256).Append(':')
              .Append((int)file.Group).Append('\n');
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Signs the manifest in place, setting <see cref="Core.Contracts.ClientManifest.Signature"/>. No-op when
    /// <paramref name="privateKeyPkcs8Base64"/> is blank (signing disabled).
    /// </summary>
    public static void Sign(Core.Contracts.ClientManifest manifest, string? privateKeyPkcs8Base64)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPkcs8Base64))
        {
            manifest.Signature = string.Empty;
            return;
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyPkcs8Base64), out _);
        var signature = ecdsa.SignData(CanonicalBytes(manifest), HashAlgorithmName.SHA256);
        manifest.Signature = Convert.ToBase64String(signature);
    }

    /// <summary>
    /// Verifies <paramref name="manifest"/>'s signature against the given SPKI public key. Returns false
    /// when the manifest is unsigned, the key is malformed, or the signature does not match.
    /// </summary>
    public static bool Verify(Core.Contracts.ClientManifest manifest, string? publicKeySpkiBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeySpkiBase64) || string.IsNullOrWhiteSpace(manifest.Signature))
        {
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
            var signature = Convert.FromBase64String(manifest.Signature);
            return ecdsa.VerifyData(CanonicalBytes(manifest), signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Generates a fresh P-256 keypair as base64 (PKCS#8 private, SPKI public).
    /// </summary>
    public static (string PrivateKeyPkcs8Base64, string PublicKeySpkiBase64) GenerateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var priv = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
        var pub = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        return (priv, pub);
    }

    /// <summary>Derives the SPKI public key (base64) from a PKCS#8 private key (base64).</summary>
    public static string DerivePublicKey(string privateKeyPkcs8Base64)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyPkcs8Base64), out _);
        return Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
    }
}
