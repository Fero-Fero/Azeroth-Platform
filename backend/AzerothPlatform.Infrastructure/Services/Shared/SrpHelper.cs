using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// WoW SRP6 credential helper - shared between services that need to create game accounts.
/// </summary>
internal static class SrpHelper
{
    // SRP6 prime N used by WoW 3.3.5a
    private static readonly BigInteger N = BigInteger.Parse(
        "0894B645E89E1535BBDAD5B8B290650530801B18EBFBF5E8FAB3C82872A3E9BB7",
        System.Globalization.NumberStyles.HexNumber);

    private static readonly BigInteger G = new(7);

    internal static (string saltHex, string verifierHex) CalculateCredentials(string username, string password)
    {
        var salt = new byte[32];
        RandomNumberGenerator.Fill(salt);

        var verifierResult = ComputeVerifier(username, password, salt);
        return (Convert.ToHexString(salt), Convert.ToHexString(verifierResult));
    }

    /// <summary>
    /// Recomputes the 32-byte SRP6 verifier for the given identity and stored salt and compares it
    /// (in constant time) against the account's stored verifier. This is how a game account password
    /// is validated without ever storing or transmitting the password: the verifier is deterministic
    /// for a given (salt, username, password), so matching it proves the password is correct.
    /// </summary>
    internal static bool VerifyPassword(string username, string password, byte[] salt, byte[] storedVerifier)
    {
        if (salt.Length == 0 || storedVerifier.Length == 0)
        {
            return false;
        }

        var computed = ComputeVerifier(username, password, salt);

        // Normalize the stored verifier to the same fixed 32-byte width so a leading/trailing zero
        // byte difference in how it was persisted doesn't cause a false mismatch.
        var stored = new byte[32];
        Buffer.BlockCopy(storedVerifier, 0, stored, 0, Math.Min(storedVerifier.Length, 32));

        return CryptographicOperations.FixedTimeEquals(computed, stored);
    }

    /// <summary>Computes the little-endian 32-byte SRP6 verifier <c>g^x mod N</c> for a given salt.</summary>
    private static byte[] ComputeVerifier(string username, string password, byte[] salt)
    {
        var identity = $"{username.ToUpperInvariant()}:{password.ToUpperInvariant()}";

        using var sha1 = SHA1.Create();
        var identityHash = sha1.ComputeHash(Encoding.UTF8.GetBytes(identity));

        var saltAndHash = new byte[salt.Length + identityHash.Length];
        Buffer.BlockCopy(salt, 0, saltAndHash, 0, salt.Length);
        Buffer.BlockCopy(identityHash, 0, saltAndHash, salt.Length, identityHash.Length);

        var x = new BigInteger(sha1.ComputeHash(saltAndHash), isUnsigned: true, isBigEndian: false);
        var verifier = BigInteger.ModPow(G, x, N);

        var verifierBytes = verifier.ToByteArray(isUnsigned: true, isBigEndian: false);
        var verifierResult = new byte[32];
        Buffer.BlockCopy(verifierBytes, 0, verifierResult, 0, Math.Min(verifierBytes.Length, 32));
        return verifierResult;
    }
}
