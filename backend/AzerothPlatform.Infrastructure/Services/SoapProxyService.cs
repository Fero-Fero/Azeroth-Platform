using System.Security;
using System.Text;
using System.Xml.Linq;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Service for executing SOAP commands on AzerothCore worldserver
/// </summary>
public class SoapProxyService : ISoapProxyService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AzerothCoreDbContext _dbContext;
    private readonly ILogger<SoapProxyService> _logger;
    private readonly string _soapHost;

    public SoapProxyService(
        IHttpClientFactory httpClientFactory,
        AzerothCoreDbContext dbContext,
        ILogger<SoapProxyService> logger,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _dbContext = dbContext;
        _logger = logger;
        _soapHost = configuration["SOAP:Host"] ?? "localhost";
    }

    public async Task<string> ExecuteCommandAsync(string stackId, string command, CancellationToken cancellationToken = default)
    {
        // Defense in depth: a SOAP command is a single console line. Reject embedded control characters
        // (notably CR/LF) so a crafted free-text argument (ban reason, mail body, …) cannot smuggle a
        // second GM command onto its own line. Callers additionally sanitize individual tokens.
        if (!string.IsNullOrEmpty(command) && command.Any(char.IsControl))
        {
            throw new ArgumentException("SOAP command must not contain control characters (e.g. newlines).");
        }

        var stack = await _dbContext.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);

        if (stack is null)
        {
            throw new InvalidOperationException($"Stack '{stackId}' not found");
        }

        // Use configured host (localhost for local dev, host.docker.internal for Docker). External
        // stacks run on a remote engine, so target their public host instead.
        var host = stack.DeploymentTarget == Core.Contracts.DeploymentTarget.External
                   && !string.IsNullOrWhiteSpace(stack.ExternalHost)
            ? stack.ExternalHost
            : _soapHost;
        var soapUrl = $"http://{host}:{stack.SoapPort}/";
        var soapEnvelope = BuildSoapEnvelope(command, stack.SoapUsername, stack.SoapPassword);

        _logger.LogInformation("Executing SOAP command on stack {StackId} via {SoapUrl}: {Command}", stackId, soapUrl, command);

        try
        {
            var client = _httpClientFactory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Post, soapUrl)
            {
                Content = new StringContent(soapEnvelope, Encoding.UTF8, "text/xml")
            };
            request.Headers.Add("SOAPAction", "\"urn:AC#executeCommand\"");
            
            // Add HTTP Basic Auth header
            var authBytes = Encoding.UTF8.GetBytes($"{stack.SoapUsername}:{stack.SoapPassword}");
            var authHeader = Convert.ToBase64String(authBytes);
            request.Headers.Add("Authorization", $"Basic {authHeader}");

            var response = await client.SendAsync(request, cancellationToken);
            var responseXml = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // AzerothCore returns HTTP 500 with a SOAP Fault when a command *runs* but reports
                // failure (e.g. "Account name can't be longer than 16 characters"). The reason is in
                // faultstring, so surface it as the command output instead of throwing it away.
                var fault = TryParseSoapFault(responseXml);
                if (fault is not null)
                {
                    _logger.LogInformation("SOAP command on stack {StackId} returned failure: {Fault}", stackId, fault);
                    return fault;
                }

                _logger.LogError("SOAP HTTP {Status} on stack {StackId}: {Body}", (int)response.StatusCode, stackId, responseXml);
                throw new InvalidOperationException(
                    $"Worldserver SOAP returned HTTP {(int)response.StatusCode}. Ensure the world server is running with SOAP enabled and the SOAP credentials are correct.");
            }

            var result = ParseSoapResponse(responseXml);

            _logger.LogInformation("SOAP command executed successfully on stack {StackId}", stackId);
            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to execute SOAP command on stack {StackId}: {Error}", stackId, ex.Message);
            throw new InvalidOperationException($"Failed to connect to worldserver SOAP interface: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing SOAP command on stack {StackId}", stackId);
            throw;
        }
    }

    private static string BuildSoapEnvelope(string command, string username, string password)
    {
        // Use SecurityElement.Escape to prevent XML injection
        var escapedCommand = SecurityElement.Escape(command);
        
        // Note: username/password are sent via HTTP Basic Auth header, not in SOAP body
        // The SOAP envelope only contains the command
        return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<SOAP-ENV:Envelope xmlns:SOAP-ENV=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:ns1=""urn:AC"">
  <SOAP-ENV:Body>
    <ns1:executeCommand>
      <command>{escapedCommand}</command>
    </ns1:executeCommand>
  </SOAP-ENV:Body>
</SOAP-ENV:Envelope>";
    }

    /// <summary>
    /// Extracts the human-readable reason from a SOAP Fault (AzerothCore puts a failed command's
    /// output in faultstring). Returns null if the response isn't a SOAP fault.
    /// </summary>
    private static string? TryParseSoapFault(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
            if (fault is null)
            {
                return null;
            }

            var faultString = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value?.Trim();
            return string.IsNullOrEmpty(faultString) ? "Command failed" : faultString;
        }
        catch
        {
            return null;
        }
    }

    private static string ParseSoapResponse(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var ns = XNamespace.Get("urn:AC");

            // Try to find the result element
            var resultElement = doc.Descendants(ns + "result").FirstOrDefault()
                ?? doc.Descendants(ns + "executeCommandResponse").FirstOrDefault()
                ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "result");

            return resultElement?.Value ?? string.Empty;
        }
        catch (Exception)
        {
            // If XML parsing fails, return the raw response
            return xml;
        }
    }
}
