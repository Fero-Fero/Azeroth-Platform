using System;
using System.Security.Cryptography;
using System.Text;
using AzerothPlatform.Launcher.Models;

namespace AzerothPlatform.Launcher.Services;

/// <summary>
/// Verifies a <see cref="ClientManifest"/>'s ECDSA (P-256/SHA-256) signature against the server public
/// key received over the trusted (TLS) config channel. The canonical byte layout MUST match the server's
/// <c>ManifestSigner.CanonicalBytes</c> exactly, so a MITM cannot present a swapped manifest+hashes even
/// when files are served over plain HTTP by a separate client-server.
/// </summary>
public static class ManifestVerifier
{
    private static byte[] CanonicalBytes(ClientManifest manifest)
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
    /// Verifies the manifest against the given SPKI public key. Returns false when the key is malformed,
    /// the manifest is unsigned, or the signature does not match.
    /// </summary>
    public static bool Verify(ClientManifest manifest, string? publicKeySpkiBase64)
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
    /// Enforces manifest authenticity before any file/hash is trusted. When a public key is present the
    /// manifest MUST carry a matching signature, otherwise a <see cref="InvalidOperationException"/> is
    /// thrown.
    /// <para>
    /// When no key is advertised, behavior depends on <paramref name="requireSignature"/>. For remote
    /// servers this MUST be <c>true</c>: a missing key is then treated as a hard failure so a rogue or
    /// man-in-the-middle manager cannot silently disable integrity checking by stripping the key off the
    /// config response (the server itself fails closed and always advertises a key). Only genuine local
    /// development against loopback should pass <c>false</c> to tolerate an unsigned manifest.
    /// </para>
    /// </summary>
    public static void EnsureTrusted(ClientManifest manifest, string? publicKeySpkiBase64, bool requireSignature)
    {
        if (string.IsNullOrWhiteSpace(publicKeySpkiBase64))
        {
            if (requireSignature)
            {
                throw new InvalidOperationException(
                    "This server did not provide a manifest signing key over the secure channel, so the " +
                    "downloaded client files cannot be authenticated. The connection may have been tampered " +
                    "with (possible man-in-the-middle) or the server is misconfigured. Sync aborted for your safety.");
            }

            return;
        }

        if (!Verify(manifest, publicKeySpkiBase64))
        {
            throw new InvalidOperationException(
                "The client manifest signature is missing or invalid. The server files may have been " +
                "tampered with (possible man-in-the-middle). Sync aborted for your safety.");
        }
    }
}
