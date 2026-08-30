namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// SOAP admin credentials for a managed stack.
/// </summary>
public class SoapCredentialsDto
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
