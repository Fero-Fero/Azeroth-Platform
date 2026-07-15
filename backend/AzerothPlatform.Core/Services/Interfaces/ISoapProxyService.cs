namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Service for executing SOAP commands on AzerothCore worldserver
/// </summary>
public interface ISoapProxyService
{
    /// <summary>
    /// Execute a SOAP command on the worldserver for the specified stack
    /// </summary>
    /// <param name="stackId">Stack identifier</param>
    /// <param name="command">Command to execute (e.g., "account create user pass")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Raw response from the server</returns>
    Task<string> ExecuteCommandAsync(string stackId, string command, CancellationToken cancellationToken = default);
}
