using System.Security.Cryptography;
using System.Text;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

internal static class SshKeyMaterialHelper
{
    internal sealed record SshKeyPair(string PrivateKeyPem, string OpenSshPublicKey, string Fingerprint);

    internal static SshKeyPair GenerateKeyPair()
    {
        using var rsa = RSA.Create(2048);
        var privatePem = rsa.ExportPkcs8PrivateKeyPem();
        var publicKey = ConvertToOpenSshPublicKey(rsa);
        return new SshKeyPair(privatePem, publicKey, ComputeFingerprint(privatePem));
    }

    internal static string ExtractOpenSshPublicKey(string privateKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        return ConvertToOpenSshPublicKey(rsa);
    }

    /// <summary>
    /// EC2 ImportKeyPair expects the OpenSSH public key as UTF-8 bytes, then Base64.
    /// The AWS SDK does not encode this blob for us.
    /// </summary>
    internal static string ToAwsImportPublicKeyMaterial(string openSshPublicKey)
    {
        var line = NormalizeOpenSshPublicKey(openSshPublicKey);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(line));
    }

    internal static string NormalizeOpenSshPublicKey(string openSshPublicKey)
    {
        var line = (openSshPublicKey ?? string.Empty)
            .Replace("\r", string.Empty)
            .Replace("\n", " ")
            .Trim();
        if (line.Length == 0)
        {
            throw new ArgumentException("SSH public key is empty.");
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && IsOpenSshKeyType(parts[0]))
        {
            line = $"{parts[0]} {parts[1]}";
        }

        if (line.Any(ch => ch > 127))
        {
            throw new ArgumentException(
                "SSH public key contains non-ASCII characters. Remove the comment or paste an OpenSSH .pub key.");
        }

        return line;
    }

    private static bool IsOpenSshKeyType(string value)
        => value is "ssh-rsa"
            or "ssh-ed25519"
            or "ecdsa-sha2-nistp256"
            or "ecdsa-sha2-nistp384"
            or "ecdsa-sha2-nistp521";

    private static string ConvertToOpenSshPublicKey(RSA rsa)
    {
        var parameters = rsa.ExportParameters(false);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteOpenSshString(writer, "ssh-rsa");
            WriteOpenSshMpint(writer, parameters.Exponent ?? []);
            WriteOpenSshMpint(writer, parameters.Modulus ?? []);
            writer.Flush();
        }

        return $"ssh-rsa {Convert.ToBase64String(stream.ToArray())}";
    }

    private static void WriteOpenSshString(BinaryWriter writer, string value)
        => WriteOpenSshBytes(writer, Encoding.UTF8.GetBytes(value));

    private static void WriteOpenSshMpint(BinaryWriter writer, byte[] value)
    {
        if (value.Length > 0 && value[0] >= 0x80)
        {
            value = [0, .. value];
        }

        WriteOpenSshBytes(writer, value);
    }

    private static void WriteOpenSshBytes(BinaryWriter writer, byte[] value)
    {
        var lengthBytes = BitConverter.GetBytes((uint)value.Length);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(lengthBytes);
        }

        writer.Write(lengthBytes);
        writer.Write(value);
    }

    private static string ComputeFingerprint(string pem)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(pem));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
