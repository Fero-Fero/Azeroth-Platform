using System.Diagnostics;
using System.Security;
using System.Text;
using System.Xml.Linq;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Service for executing SOAP commands on AzerothCore worldserver
/// </summary>
public class SoapProxyService : ISoapProxyService
{
    private const int SoapContainerPort = 7878;
    private const string ManagementTunnelRemoteHost = "127.0.0.1";
    private static readonly TimeSpan ExternalSoapOperationTimeout = TimeSpan.FromSeconds(35);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AzerothCoreDbContext _dbContext;
    private readonly IRemoteEngineService _remoteEngine;
    private readonly ILogger<SoapProxyService> _logger;
    private readonly string _soapHost;

    public SoapProxyService(
        IHttpClientFactory httpClientFactory,
        AzerothCoreDbContext dbContext,
        IRemoteEngineService remoteEngine,
        ILogger<SoapProxyService> logger,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _dbContext = dbContext;
        _remoteEngine = remoteEngine;
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

        var soapEnvelope = BuildSoapEnvelope(command);
        var authHeader = BuildBasicAuthHeader(stack.SoapUsername, stack.SoapPassword);

        _logger.LogInformation("Executing SOAP command on stack {StackId}: {Command}", stackId, command);

        if (stack.DeploymentTarget == DeploymentTarget.External)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ExternalSoapOperationTimeout);
            return await ExecuteExternalAsync(stack, soapEnvelope, authHeader, timeoutCts.Token);
        }

        return await ExecuteViaHttpAsync(
            stack,
            stackId,
            $"http://{_soapHost}:{stack.SoapPort}/",
            soapEnvelope,
            authHeader,
            remotePort: stack.SoapPort,
            remoteHost: ManagementTunnelRemoteHost,
            invalidateTunnelOnFailure: false,
            cancellationToken);
    }

    private async Task<string> ExecuteExternalAsync(
        ManagedStackEntity stack,
        string soapEnvelope,
        string authHeader,
        CancellationToken cancellationToken)
    {
        var containerName = DockerComposeOverrideGenerator.GetContainerNameForService(
            stack.Id,
            stack.StackName,
            "ac-worldserver");
        if (containerName is null)
        {
            throw new InvalidOperationException("Could not resolve the worldserver container name for this stack.");
        }

        // Avoid `docker ps` over SSH (slow on small VPCs). The container name is fixed by our compose override.
        var dockerContext = await _remoteEngine.EnsureContextAsync(stack, cancellationToken);

        var execResult = await TryExecuteViaDockerExecAsync(
            dockerContext,
            containerName,
            soapEnvelope,
            authHeader,
            cancellationToken);
        if (execResult.Success)
        {
            _logger.LogInformation("SOAP command executed successfully on stack {StackId} via docker exec", stack.Id);
            return execResult.Output!;
        }

        if (await IsContainerRunningAsync(dockerContext, containerName, cancellationToken) == false)
        {
            throw new InvalidOperationException(
                "The world server container is not running. SOAP commands require ac-worldserver - " +
                "starting auth and database alone is not enough.");
        }

        _logger.LogDebug(
            "docker exec SOAP failed for stack {StackId} ({Reason}); falling back to SSH tunnel.",
            stack.Id,
            execResult.Error);

        // Data-plane ports publish on loopback on the remote host; skip `docker port` (another SSH round trip).
        var endpoint = await _remoteEngine.GetManagementTunnelEndpointAsync(
            stack,
            stack.SoapPort,
            ManagementTunnelRemoteHost,
            cancellationToken);

        try
        {
            return await ExecuteViaHttpAsync(
                stack,
                stack.Id,
                $"http://{endpoint.Host}:{endpoint.Port}/",
                soapEnvelope,
                authHeader,
                stack.SoapPort,
                ManagementTunnelRemoteHost,
                invalidateTunnelOnFailure: true,
                cancellationToken);
        }
        catch (InvalidOperationException ex) when (execResult.Error?.Contains("curl", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new InvalidOperationException(
                $"{ex.Message} (docker exec fallback: {execResult.Error})",
                ex.InnerException);
        }
    }

    private async Task<(bool Success, string? Output, string? Error)> TryExecuteViaDockerExecAsync(
        string dockerContext,
        string containerName,
        string soapEnvelope,
        string authHeader,
        CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "--context",
            dockerContext,
            "exec",
            containerName,
            "curl",
            "-fsS",
            "--connect-timeout",
            "5",
            "--max-time",
            "25",
            "-X",
            "POST",
            "-H",
            "Content-Type: text/xml",
            "-H",
            "SOAPAction: \"urn:AC#executeCommand\"",
            "-H",
            $"Authorization: Basic {authHeader}",
            "--data-binary",
            soapEnvelope,
            $"http://127.0.0.1:{SoapContainerPort}/",
        };

        var (exitCode, stdout, stderr) = await RunDockerAsync(args, cancellationToken);
        if (exitCode == 0)
        {
            try
            {
                return (true, ParseSoapResult(stdout), null);
            }
            catch (InvalidOperationException ex)
            {
                return (false, null, ex.Message);
            }
        }

        var error = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
        return (false, null, string.IsNullOrWhiteSpace(error) ? $"curl exited with code {exitCode}." : error);
    }

    private static async Task<bool?> IsContainerRunningAsync(
        string dockerContext,
        string containerName,
        CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "--context",
            dockerContext,
            "inspect",
            "-f",
            "{{.State.Running}}",
            containerName,
        };

        var (exitCode, stdout, _) = await RunDockerAsync(args, cancellationToken);
        if (exitCode != 0)
        {
            return null;
        }

        return stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> ExecuteViaHttpAsync(
        ManagedStackEntity stack,
        string stackId,
        string soapUrl,
        string soapEnvelope,
        string authHeader,
        int remotePort,
        string remoteHost,
        bool invalidateTunnelOnFailure,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Executing SOAP command on stack {StackId} via {SoapUrl}", stackId, soapUrl);

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(25);
            var request = new HttpRequestMessage(HttpMethod.Post, soapUrl)
            {
                Content = new StringContent(soapEnvelope, Encoding.UTF8, "text/xml")
            };
            request.Headers.Add("SOAPAction", "\"urn:AC#executeCommand\"");
            request.Headers.Add("Authorization", $"Basic {authHeader}");

            var response = await client.SendAsync(request, cancellationToken);
            var responseXml = await response.Content.ReadAsStringAsync(cancellationToken);

            return ParseSoapResult(responseXml, response.IsSuccessStatusCode, stackId, (int)response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            if (invalidateTunnelOnFailure)
            {
                _remoteEngine.InvalidateManagementTunnel(stack, remotePort, remoteHost);
            }

            var detail = ex.InnerException?.Message;
            var reason = string.IsNullOrWhiteSpace(detail) ? ex.Message : $"{ex.Message} ({detail})";
            _logger.LogError(ex, "Failed to execute SOAP command on stack {StackId}: {Error}", stackId, reason);
            throw new InvalidOperationException(
                $"Failed to connect to worldserver SOAP interface: {reason}. " +
                "Ensure the world server container is running (auth alone is not enough), SOAP is enabled, " +
                "and the SOAP admin account has been initialized for this stack.",
                ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Timed out waiting for the worldserver SOAP interface on the external stack. " +
                "The VPC may be under heavy load - try again when the stack is idle or upgrade instance resources.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing SOAP command on stack {StackId}", stackId);
            throw;
        }
    }

    private string ParseSoapResult(
        string responseXml,
        bool isSuccessStatusCode = true,
        string? stackId = null,
        int? statusCode = null)
    {
        if (!isSuccessStatusCode)
        {
            var fault = TryParseSoapFault(responseXml);
            if (fault is not null)
            {
                if (stackId is not null)
                {
                    _logger.LogInformation("SOAP command on stack {StackId} returned failure: {Fault}", stackId, fault);
                }

                return fault;
            }

            if (stackId is not null && statusCode is not null)
            {
                _logger.LogError("SOAP HTTP {Status} on stack {StackId}: {Body}", statusCode, stackId, responseXml);
            }

            throw new InvalidOperationException(
                $"Worldserver SOAP returned HTTP {statusCode}. Ensure the world server is running with SOAP enabled and the SOAP credentials are correct.");
        }

        return ParseSoapResponse(responseXml);
    }

    private static string BuildBasicAuthHeader(string username, string password)
    {
        var authBytes = Encoding.UTF8.GetBytes($"{username}:{password}");
        return Convert.ToBase64String(authBytes);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunDockerAsync(
        IReadOnlyList<string> argumentList,
        CancellationToken cancellationToken)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (var arg in argumentList)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string BuildSoapEnvelope(string command)
    {
        var escapedCommand = SecurityElement.Escape(command);

        return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<SOAP-ENV:Envelope xmlns:SOAP-ENV=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:ns1=""urn:AC"">
  <SOAP-ENV:Body>
    <ns1:executeCommand>
      <command>{escapedCommand}</command>
    </ns1:executeCommand>
  </SOAP-ENV:Body>
</SOAP-ENV:Envelope>";
    }

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

            var resultElement = doc.Descendants(ns + "result").FirstOrDefault()
                ?? doc.Descendants(ns + "executeCommandResponse").FirstOrDefault()
                ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "result");

            return resultElement?.Value ?? string.Empty;
        }
        catch (Exception)
        {
            return xml;
        }
    }
}
