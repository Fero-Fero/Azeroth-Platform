using System.Security.Cryptography;
using System.Text;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

internal static class SshKeyMaterialHelper
{
    internal sealed record SshKeyPair(string PrivateKeyPem, string OpenSshPublicKey, string Fingerprint);

    internal static SshKeyPair GenerateKeyPair()
    {
        using var rsa = RSA.Create(4096);
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

    private static string ConvertToOpenSshPublicKey(RSA rsa)
    {
        var parameters = rsa.ExportParameters(false);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteOpenSshString(writer, "ssh-rsa");
        WriteOpenSshMpint(writer, parameters.Exponent ?? Array.Empty<byte>());
        WriteOpenSshMpint(writer, parameters.Modulus ?? Array.Empty<byte>());
        return $"ssh-rsa {Convert.ToBase64String(stream.ToArray())} azeroth-platform";
    }

    private static void WriteOpenSshString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteOpenSshBytes(writer, bytes);
    }

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
