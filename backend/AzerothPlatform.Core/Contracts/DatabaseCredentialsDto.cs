namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// MySQL root credentials for a managed stack. Returned only from the dedicated, audited reveal
/// endpoint — never from the standard stack list/detail payloads.
/// </summary>
public class DatabaseCredentialsDto
{
    public string Username { get; set; } = "root";
    public string Password { get; set; } = string.Empty;
    public int Port { get; set; }
}
