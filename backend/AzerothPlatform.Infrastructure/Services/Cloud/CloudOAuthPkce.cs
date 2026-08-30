using System.Security.Cryptography;
using System.Text;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

internal static class CloudOAuthPkce
{
    public static string CreateCodeVerifier()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    public static string CreateS256Challenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    public static string CreateState()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}