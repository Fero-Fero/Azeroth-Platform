using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services.Cloud;
using AzerothPlatform.Infrastructure.Services.RemoteHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Default <see cref="IRemoteEngineService"/> implementation backed by the docker CLI. For external
/// stacks it uses docker contexts over SSH (key material and an ssh config alias written under
/// <c>~/.ssh</c> in a marker-delimited managed block so multiple stacks can coexist); for local stacks
/// it drives the manager's own daemon with no context. Volume/tool helpers resolve the right context
/// automatically so callers share one code path.
/// </summary>
public sealed class RemoteEngineService : IRemoteEngineService
{
    private const int ConnectionTestConnectTimeoutSeconds = 20;
    private const int SshProbeRetryCount = 6;
    private static readonly TimeSpan SshProbeRetryDelay = TimeSpan.FromSeconds(4);

    private readonly ILogger<RemoteEngineService> _logger;
    private readonly DockerOptions _dockerOptions;
    private readonly ISecretProtector _secretProtector;
    private readonly ConcurrentDictionary<string, string> _verifiedContextEndpoints = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _lastSshConnectionEndpoints = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ManagementTunnel> _managementTunnels = new(StringComparer.Ordinal);

    private sealed class ManagementTunnel
    {
        public required Process Process { get; init; }
        public required int LocalPort { get; init; }
        public required int RemotePort { get; init; }
    }

    public RemoteEngineService(
        ILogger<RemoteEngineService> logger,
        IOptions<DockerOptions> dockerOptions,
        ISecretProtector secretProtector)
    {
        _logger = logger;
        _dockerOptions = dockerOptions.Value;
        _secretProtector = secretProtector;
    }

    public string GetContextName(string stackId) => $"acore-ext-{stackId}";

    public async Task<string> ContextArgAsync(ManagedStackEntity stack, CancellationToken cancellationToken = default)
    {
        if (stack.DeploymentTarget != DeploymentTarget.External)
        {
            return string.Empty;
        }

        var contextName = await EnsureContextAsync(stack, cancellationToken);
        return $"--context {contextName} ";
    }

    public async Task<string> EnsureContextAsync(ManagedStackEntity stack, CancellationToken cancellationToken = default)
    {
        if (stack.DeploymentTarget != DeploymentTarget.External)
        {
            throw new InvalidOperationException("EnsureContextAsync is only valid for external stacks.");
        }

        if (string.IsNullOrWhiteSpace(stack.ExternalHost) || string.IsNullOrWhiteSpace(stack.ExternalSshUser))
        {
            throw new InvalidOperationException("External stack is missing the remote host or SSH user.");
        }

        var contextName = GetContextName(stack.Id);
        var sshPort = stack.ExternalSshPort <= 0 ? 22 : stack.ExternalSshPort;
        var sshEndpoint = $"{stack.ExternalSshUser.Trim()}@{stack.ExternalHost.Trim()}:{sshPort}";
        if (_lastSshConnectionEndpoints.TryGetValue(stack.Id, out var previousEndpoint)
            && !string.Equals(previousEndpoint, sshEndpoint, StringComparison.Ordinal))
        {
            StopManagementTunnels(stack.Id);
        }

        _lastSshConnectionEndpoints[stack.Id] = sshEndpoint;

        // The stored key is encrypted at rest; decrypt just-in-time to write the on-disk identity file.
        var privateKey = _secretProtector.Unprotect(stack.ExternalSshPrivateKey);
        await PrepareSshAsync(contextName, stack.ExternalHost.Trim(), sshPort,
            stack.ExternalSshUser.Trim(), privateKey, cancellationToken);
        await EnsureDockerContextAsync(
            contextName,
            stack.ExternalSshUser.Trim(),
            stack.ExternalHost.Trim(),
            sshPort,
            cancellationToken);
        return contextName;
    }

    public async Task<(bool Available, string? Message)> ProbeRemoteDockerAsync(
        ManagedStackEntity stack,
        CancellationToken cancellationToken = default)
    {
        if (stack.DeploymentTarget != DeploymentTarget.External)
        {
            throw new InvalidOperationException("ProbeRemoteDockerAsync is only valid for external stacks.");
        }

        var contextName = await EnsureContextAsync(stack, cancellationToken);
        var host = stack.ExternalHost.Trim();
        var user = stack.ExternalSshUser.Trim();
        var port = stack.ExternalSshPort <= 0 ? 22 : stack.ExternalSshPort;

        var (exitCode, stdout, stderr) = await RunSshAsync(
            contextName,
            ["docker", "info", "--format", "{{.ServerVersion}}"],
            cancellationToken);

        if (exitCode != 0)
        {
            return (false, FormatRemoteDockerError(stderr, host, user, port));
        }

        var version = stdout.Trim();
        return (true, string.IsNullOrWhiteSpace(version) ? null : $"Docker {version}");
    }

    public async Task<int?> TryResolveRemotePublishedPortAsync(
        ManagedStackEntity stack,
        string containerName,
        int containerPort,
        CancellationToken cancellationToken = default)
    {
        var endpoint = await TryResolveRemotePublishedEndpointAsync(stack, containerName, containerPort, cancellationToken);
        return endpoint?.Port;
    }

    public async Task<(string Host, int Port)?> TryResolveRemotePublishedEndpointAsync(
        ManagedStackEntity stack,
        string containerName,
        int containerPort,
        CancellationToken cancellationToken = default)
    {
        if (stack.DeploymentTarget != DeploymentTarget.External)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(containerName) || containerPort is <= 0 or > 65535)
        {
            return null;
        }

        try
        {
            var contextName = await EnsureContextAsync(stack, cancellationToken);
            var (exitCode, stdout, _) = await RunSshAsync(
                contextName,
                ["docker", "port", containerName, $"{containerPort}/tcp"],
                cancellationToken);

            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                return null;
            }

            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TryParseDockerPublishedEndpoint(line, out var host, out var hostPort))
                {
                    return (host, hostPort);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Could not resolve published port for container {Container} ({ContainerPort}/tcp) on stack {StackId}.",
                containerName,
                containerPort,
                stack.Id);
        }

        return null;
    }

    public void InvalidateManagementTunnel(ManagedStackEntity stack, int remotePort, string remoteHost = "127.0.0.1")
    {
        var tunnelKey = ManagementTunnelKey(stack.Id, NormalizeTunnelRemoteHost(remoteHost), remotePort);
        if (_managementTunnels.TryRemove(tunnelKey, out var tunnel))
        {
            StopManagementTunnel(tunnelKey, tunnel);
        }
    }

    public async Task RemoveContextAsync(ManagedStackEntity stack, CancellationToken cancellationToken = default)
    {
        StopManagementTunnels(stack.Id);
        _lastSshConnectionEndpoints.TryRemove(stack.Id, out _);
        var contextName = GetContextName(stack.Id);
        _verifiedContextEndpoints.TryRemove(contextName, out _);
        await RunAsync("docker", $"context rm -f {contextName}", cancellationToken, throwOnError: false);
        RemoveSshConfigBlock(contextName);
    }

    public async Task<(string Host, int Port)> GetManagementTunnelEndpointAsync(
        ManagedStackEntity stack,
        int remotePort,
        string remoteHost = "127.0.0.1",
        CancellationToken cancellationToken = default)
    {
        if (stack.DeploymentTarget != DeploymentTarget.External)
        {
            throw new InvalidOperationException("Management tunnels are only used for external stacks.");
        }

        if (remotePort is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(remotePort), remotePort, "Remote port must be between 1 and 65535.");
        }

        remoteHost = NormalizeTunnelRemoteHost(remoteHost);

        var tunnelKey = ManagementTunnelKey(stack.Id, remoteHost, remotePort);
        if (_managementTunnels.TryGetValue(tunnelKey, out var existing) && IsTunnelAlive(existing))
        {
            return ("127.0.0.1", existing.LocalPort);
        }

        if (existing is not null)
        {
            StopManagementTunnel(tunnelKey, existing);
        }

        var contextName = await EnsureContextAsync(stack, cancellationToken);
        var localPort = AllocateLocalPort();
        var forward = $"127.0.0.1:{localPort}:{remoteHost}:{remotePort}";
        var sshConfigPath = Path.Combine(GetSshDir(), "config");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-F");
        process.StartInfo.ArgumentList.Add(sshConfigPath);
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add("BatchMode=yes");
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add("ConnectTimeout=15");
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add("ExitOnForwardFailure=yes");
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add("ServerAliveInterval=15");
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add("ServerAliveCountMax=4");
        process.StartInfo.ArgumentList.Add("-N");
        process.StartInfo.ArgumentList.Add("-L");
        process.StartInfo.ArgumentList.Add(forward);
        process.StartInfo.ArgumentList.Add(contextName);

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start SSH tunnel to remote port {remotePort}.");
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.Delay(750, cancellationToken);
        if (process.HasExited)
        {
            var err = (await stderrTask).Trim();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(err)
                    ? $"SSH tunnel to remote port {remotePort} exited immediately."
                    : $"SSH tunnel to remote port {remotePort} failed: {err}");
        }

        var tunnel = new ManagementTunnel
        {
            Process = process,
            LocalPort = localPort,
            RemotePort = remotePort,
        };
        _managementTunnels[tunnelKey] = tunnel;
        _ = stderrTask.ContinueWith(
            t =>
            {
                if (process.HasExited)
                {
                    _managementTunnels.TryRemove(tunnelKey, out _);
                    var err = t.IsCompletedSuccessfully ? t.Result.Trim() : string.Empty;
                    if (!string.IsNullOrWhiteSpace(err))
                    {
                        _logger.LogWarning(
                            "SSH management tunnel for stack {StackId} port {RemotePort} closed: {Err}",
                            stack.Id,
                            remotePort,
                            err);
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        _logger.LogDebug(
            "Opened SSH management tunnel for stack {StackId}: localhost:{LocalPort} -> remote:{RemotePort}",
            stack.Id,
            localPort,
            remotePort);

        return ("127.0.0.1", localPort);
    }

    public async Task<RemoteConnectionTestResultDto> TestConnectionAsync(
        string host,
        int sshPort,
        string user,
        string privateKey,
        RemoteConnectionTestPhase phase = RemoteConnectionTestPhase.Full,
        VpcConnectionTestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user))
        {
            return new RemoteConnectionTestResultDto { Success = false, Message = "Host and SSH user are required." };
        }

        if (string.IsNullOrWhiteSpace(privateKey))
        {
            return new RemoteConnectionTestResultDto { Success = false, Message = "SSH private key is required." };
        }

        host = host.Trim();
        user = user.Trim();
        var port = sshPort <= 0 ? 22 : sshPort;
        var checkSsh = phase is RemoteConnectionTestPhase.Full or RemoteConnectionTestPhase.SshOnly;
        var checkPrerequisites = phase is RemoteConnectionTestPhase.Full or RemoteConnectionTestPhase.PrerequisitesOnly;

        // SSRF guard: refuse to dial loopback / link-local / cloud-metadata targets. This endpoint takes a
        // caller-supplied host, so without this an admin (or a stolen token) could use the manager to reach
        // internal-only services or the 169.254.169.254 metadata endpoint. Private LAN ranges are allowed
        // because legitimate remote Docker engines commonly live on a private network.
        if (await IsDisallowedRemoteHostAsync(host, cancellationToken))
        {
            return new RemoteConnectionTestResultDto
            {
                Success = false,
                Message = "The specified host is not an allowed remote engine target (loopback and " +
                          "link-local/metadata addresses are blocked)."
            };
        }

        // Use a throwaway context name so a pre-create test doesn't collide with a real stack context.
        var contextName = $"acore-ext-test-{Guid.NewGuid():N}";
        var prerequisites = new List<RemotePrerequisiteCheckDto>();
        var bootstrapSecured = options?.BootstrapUserSecured ?? false;
        RemoteHostOs? detectedOs = null;
        try
        {
            await PrepareSshAsync(contextName, host, port, user, privateKey, cancellationToken);
            detectedOs = await DetectRemoteHostOsAsync(contextName, cancellationToken);
            if (detectedOs == RemoteHostOs.Windows)
            {
                prerequisites.Add(new RemotePrerequisiteCheckDto
                {
                    Name = "Operating system",
                    Passed = false,
                    Message = "Windows Server VPC hosts are not supported. Use Ubuntu or Debian.",
                });
                return new RemoteConnectionTestResultDto
                {
                    Success = false,
                    Message = "Windows Server VPC hosts are not supported. Use Ubuntu or Debian.",
                    Prerequisites = prerequisites,
                    BootstrapUserSecured = bootstrapSecured,
                    DetectedOs = detectedOs,
                };
            }

            if (detectedOs is not null)
            {
                prerequisites.Add(new RemotePrerequisiteCheckDto
                {
                    Name = "Operating system",
                    Passed = true,
                    Message = "Host is Linux.",
                });
            }

            if (checkSsh)
            {
                var ssh = await RunVpcSshVerifyAsync(
                    contextName,
                    host,
                    port,
                    user,
                    privateKey,
                    options,
                    prerequisites,
                    cancellationToken);
                bootstrapSecured = ssh.BootstrapUserSecured;
                if (!ssh.Passed)
                {
                    return new RemoteConnectionTestResultDto
                    {
                        Success = false,
                        Message = ssh.Message,
                        Prerequisites = prerequisites,
                        BootstrapUserSecured = bootstrapSecured,
                        DetectedOs = detectedOs,
                    };
                }

                if (phase == RemoteConnectionTestPhase.SshOnly)
                {
                    return new RemoteConnectionTestResultDto
                    {
                        Success = true,
                        Message = "SSH connection successful.",
                        Prerequisites = prerequisites,
                        BootstrapUserSecured = bootstrapSecured,
                        DetectedOs = detectedOs,
                    };
                }
            }

            if (!checkPrerequisites)
            {
                return new RemoteConnectionTestResultDto
                {
                    Success = prerequisites.All(p => p.Passed),
                    Message = prerequisites.All(p => p.Passed) ? "Connection checks passed." : "Connection checks failed.",
                    Prerequisites = prerequisites,
                    BootstrapUserSecured = bootstrapSecured,
                    DetectedOs = detectedOs,
                };
            }

            await PrepareSshAsync(contextName, host, port, user, privateKey, cancellationToken);

            var (sudoPassed, sudoMessage) = await EnsurePasswordlessSudoAsync(
                contextName,
                user,
                cancellationToken);
            prerequisites.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Non-interactive sudo",
                Passed = sudoPassed,
                Message = sudoMessage
            });

            var dockerUser = await ResolveRemoteSshUserAsync(contextName, user, cancellationToken);
            await EnsureRemoteDockerGroupAsync(contextName, dockerUser, cancellationToken);

            var (remoteDockerReady, remoteDockerOut, remoteDockerErr) = await EnsureRemoteDockerForVerifyAsync(
                contextName,
                dockerUser,
                prerequisites,
                cancellationToken);

            if (!remoteDockerReady)
            {
                prerequisites.Add(new RemotePrerequisiteCheckDto
                {
                    Name = "Docker Engine",
                    Passed = false,
                    Message = FormatRemoteDockerError(remoteDockerErr, host, user, port)
                });
                return new RemoteConnectionTestResultDto
                {
                    Success = false,
                    Message = "SSH works, but the remote Docker engine is not available. Verify waits for " +
                              "launch user-data and will install Docker if it is still missing. If this " +
                              "keeps failing, use Repair host setup.",
                    Prerequisites = prerequisites,
                    BootstrapUserSecured = bootstrapSecured,
                    DetectedOs = detectedOs,
                };
            }

            var version = ExtractDockerVersion(remoteDockerOut);
            prerequisites.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Docker Engine",
                Passed = true,
                Message = string.IsNullOrWhiteSpace(version) ? "Docker engine is running." : $"Docker {version}"
            });

            // Compose commands run on the platform manager via `docker --context … compose`; the VPC only
            // needs the Docker Engine API. Report remote compose when present, but do not block reconnect.
            var composeVersion = await TryGetRemoteComposeVersionAsync(contextName, cancellationToken);
            prerequisites.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Docker Compose",
                Passed = true,
                Message = composeVersion is not null
                    ? $"Compose {composeVersion} also available on the VPC (optional)."
                    : "Runs on the platform manager — not required on the VPC host."
            });

            await AppendHostSecurityPrerequisiteChecksAsync(contextName, prerequisites, cancellationToken);

            var allPassed = prerequisites.All(check => check.Passed);
            return new RemoteConnectionTestResultDto
            {
                Success = allPassed,
                ServerVersion = version,
                Message = allPassed
                    ? (string.IsNullOrWhiteSpace(version)
                        ? "Remote host is ready for deployment."
                        : $"Remote host is ready (Docker {version}). Host firewall and OS baselines look good.")
                    : "SSH and Docker work, but host firewall or OS baselines could not be applied. Use Repair host setup.",
                Prerequisites = prerequisites,
                BootstrapUserSecured = bootstrapSecured,
                DetectedOs = detectedOs,
            };
        }
        catch (Exception ex)
        {
            return new RemoteConnectionTestResultDto
            {
                Success = false,
                Message = ex.Message,
                Prerequisites = prerequisites,
                BootstrapUserSecured = bootstrapSecured,
                DetectedOs = detectedOs,
            };
        }
        finally
        {
            RemoveSshConfigBlock(contextName);
        }
    }

    private sealed record VpcSshVerifyResult(bool Passed, string Message, bool BootstrapUserSecured);

    private static readonly string[] BootstrapLoginUsers =
        ["ubuntu", "debian", "azureuser", "ec2-user", "admin", "centos", "fedora", "root"];

    private async Task<VpcSshVerifyResult> RunVpcSshVerifyAsync(
        string contextName,
        string host,
        int port,
        string operatorUser,
        string operatorPrivateKey,
        VpcConnectionTestOptions? options,
        List<RemotePrerequisiteCheckDto> prerequisites,
        CancellationToken cancellationToken)
    {
        try
        {
            SshKeyMaterialHelper.ExtractOpenSshPublicKey(operatorPrivateKey);
            prerequisites.Add(new RemotePrerequisiteCheckDto
            {
                Name = "azp-admin key",
                Passed = true,
                Message = "Operator private key is a valid PEM and matches an OpenSSH public key.",
            });
        }
        catch (Exception ex)
        {
            prerequisites.Add(new RemotePrerequisiteCheckDto
            {
                Name = "azp-admin key",
                Passed = false,
                Message = "The azp-admin private key is not valid. Download it again and Verify certificate in Connect.",
            });
            return new VpcSshVerifyResult(false, ex.Message, options?.BootstrapUserSecured ?? false);
        }

        var alreadySecured = options?.BootstrapUserSecured ?? false;
        var hasBootstrapPem = !string.IsNullOrWhiteSpace(options?.BootstrapPrivateKey);
        var bootstrapKeyMissing = !alreadySecured
            && !hasBootstrapPem
            && !string.IsNullOrWhiteSpace(options?.BootstrapSshKeyId);
        if (bootstrapKeyMissing)
        {
            // Vault key was deleted after a previous lock, but the wizard cache was not saved.
            alreadySecured = true;
        }

        if (alreadySecured)
        {
            prerequisites.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Root SSH",
                Passed = true,
                Message = "Root internet SSH was already locked. That login is not retried.",
            });
            prerequisites.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Lock image-default SSH",
                Passed = true,
                Message = "Image-default users stay closed. Provider console access is unchanged.",
            });
        }
        else if (hasBootstrapPem)
        {
            var bootstrapLogin = await TryBootstrapLoginAsync(
                contextName,
                host,
                port,
                options!.BootstrapPrivateKey!,
                cancellationToken);
            prerequisites.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Root SSH",
                Passed = bootstrapLogin.Ok,
                Message = bootstrapLogin.Ok
                    ? $"Connected as {bootstrapLogin.User} with the manager bootstrap key."
                    : bootstrapLogin.Error,
            });
            if (!bootstrapLogin.Ok)
            {
                return new VpcSshVerifyResult(false, bootstrapLogin.Error, false);
            }

            var ensured = await EnsureOperatorUserAsync(
                contextName,
                operatorUser,
                operatorPrivateKey,
                cancellationToken);
            prerequisites.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Operator user",
                Passed = ensured.Passed,
                Message = ensured.Message,
            });
            if (!ensured.Passed)
            {
                return new VpcSshVerifyResult(false, ensured.Message, false);
            }

            await PrepareSshAsync(contextName, host, port, operatorUser, operatorPrivateKey, cancellationToken);
            var preLock = await ProbeSshEchoAsync(
                contextName,
                cancellationToken,
                ConnectionTestConnectTimeoutSeconds,
                retry: true);
            if (!SshProbe.IsEchoSuccess(preLock.ExitCode, preLock.StdOut, preLock.StdErr))
            {
                prerequisites.Add(new RemotePrerequisiteCheckDto
                {
                    Name = "Operator SSH (before lock)",
                    Passed = false,
                    Message = FormatSshError(
                        preLock.ExitCode,
                        preLock.StdOut,
                        preLock.StdErr,
                        host,
                        operatorUser,
                        port),
                });
                return new VpcSshVerifyResult(
                    false,
                    GetSshSetupFailureSummary(preLock.StdErr),
                    false);
            }

            var harden = await FinalizeSshHardeningAsync(
                host,
                port,
                operatorUser,
                operatorPrivateKey,
                options?.EnableAwsInstanceConnect ?? false,
                cancellationToken);
            prerequisites.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Lock image-default SSH",
                Passed = harden.Success,
                Message = harden.Success
                    ? "Root static keys removed. VPC provider console access is unchanged."
                    : harden.Message,
            });
            if (!harden.Success)
            {
                return new VpcSshVerifyResult(false, harden.Message, false);
            }

            alreadySecured = true;
        }

        await PrepareSshAsync(contextName, host, port, operatorUser, operatorPrivateKey, cancellationToken);
        var (sshExit, sshStdout, sshStderr) = await ProbeSshEchoAsync(
            contextName,
            cancellationToken,
            ConnectionTestConnectTimeoutSeconds,
            retry: true);
        var sshOk = SshProbe.IsEchoSuccess(sshExit, sshStdout, sshStderr);
        prerequisites.Add(new RemotePrerequisiteCheckDto
        {
            Name = "SSH",
            Passed = sshOk,
            Message = sshOk
                ? $"Connected as {operatorUser}."
                : FormatSshError(sshExit, sshStdout, sshStderr, host, operatorUser, port),
        });
        if (!sshOk)
        {
            return new VpcSshVerifyResult(false, GetSshSetupFailureSummary(sshStderr), alreadySecured);
        }

        return new VpcSshVerifyResult(true, $"Connected as {operatorUser}.", alreadySecured);
    }

    private async Task<(bool Ok, string User, string Error)> TryBootstrapLoginAsync(
        string contextName,
        string host,
        int port,
        string privateKey,
        CancellationToken cancellationToken)
    {
        var lastError = "Could not SSH as ubuntu or root with the bootstrap key.";
        foreach (var candidate in BootstrapLoginUsers)
        {
            await PrepareSshAsync(contextName, host, port, candidate, privateKey, cancellationToken);
            var probe = await ProbeSshEchoAsync(
                contextName,
                cancellationToken,
                ConnectionTestConnectTimeoutSeconds,
                retry: candidate is "ubuntu" or "root");
            if (SshProbe.IsEchoSuccess(probe.ExitCode, probe.StdOut, probe.StdErr))
            {
                return (true, candidate, string.Empty);
            }

            lastError = FormatSshError(probe.ExitCode, probe.StdOut, probe.StdErr, host, candidate, port);
        }

        return (false, string.Empty, lastError);
    }

    private async Task<(bool Passed, string Message)> EnsureOperatorUserAsync(
        string bootstrapContext,
        string operatorUser,
        string operatorPrivateKey,
        CancellationToken cancellationToken)
    {
        string publicKey;
        try
        {
            publicKey = SshKeyMaterialHelper.ExtractOpenSshPublicKey(operatorPrivateKey);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        var pubB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(publicKey.Trim() + "\n"));
        var sudoersB64 = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(VpcBootstrapUserData.BuildPasswordlessSudoers(operatorUser)));
        var command = RemotePathSetup
            + "set -e; "
            + "OP=" + ShellQuote(operatorUser) + "; "
            + "if ! id \"$OP\" >/dev/null 2>&1; then sudo -n useradd --create-home --shell /bin/bash \"$OP\"; fi; "
            + "sudo -n usermod -aG sudo \"$OP\" 2>/dev/null || sudo -n usermod -aG wheel \"$OP\" 2>/dev/null || true; "
            + "sudo -n mkdir -p /home/\"$OP\"/.ssh; "
            + "sudo -n chmod 700 /home/\"$OP\"/.ssh; "
            + "echo " + ShellQuote(pubB64) + " | sudo -n base64 -d | sudo -n tee /home/\"$OP\"/.ssh/authorized_keys >/dev/null; "
            + "sudo -n chmod 600 /home/\"$OP\"/.ssh/authorized_keys; "
            + "sudo -n chown -R \"$OP\":\"$OP\" /home/\"$OP\"/.ssh; "
            + "echo " + ShellQuote(sudoersB64) + " | sudo -n base64 -d | sudo -n tee /etc/sudoers.d/99-azeroth-platform >/dev/null; "
            + "sudo -n chmod 440 /etc/sudoers.d/99-azeroth-platform; "
            + "sudo -n usermod -aG docker \"$OP\" 2>/dev/null || true";

        var (exit, stdout, stderr) = await RunSshRemoteShellAsync(
            bootstrapContext,
            command,
            cancellationToken,
            allowTtyRetry: true);
        if (exit != 0)
        {
            return (false, FormatRemoteShellError(stderr, stdout));
        }

        return (true, $"Operator user {operatorUser} exists with the downloaded public key and passwordless sudo.");
    }

    public async Task<RemoteBootstrapResultDto> RunVpcBootstrapScriptAsync(
        string host,
        int sshPort,
        string user,
        string privateKey,
        string? scriptSshUser = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user))
        {
            return new RemoteBootstrapResultDto { Success = false, Message = "Host and SSH user are required." };
        }

        if (string.IsNullOrWhiteSpace(privateKey))
        {
            return new RemoteBootstrapResultDto { Success = false, Message = "SSH private key is required." };
        }

        host = host.Trim();
        user = user.Trim();
        var port = sshPort <= 0 ? 22 : sshPort;

        if (await IsDisallowedRemoteHostAsync(host, cancellationToken))
        {
            return new RemoteBootstrapResultDto
            {
                Success = false,
                Message = "The specified host is not an allowed remote engine target (loopback and " +
                          "link-local/metadata addresses are blocked)."
            };
        }

        var contextName = $"acore-ext-bootstrap-{Guid.NewGuid():N}";
        var steps = new List<RemotePrerequisiteCheckDto>();

        try
        {
            await PrepareSshAsync(contextName, host, port, user, privateKey, cancellationToken);

            var (sshExit, sshStdout, sshStderr) = await ProbeSshEchoAsync(
                contextName,
                cancellationToken,
                connectTimeoutSeconds: 60,
                retry: true);
            if (!SshProbe.IsEchoSuccess(sshExit, sshStdout, sshStderr))
            {
                return new RemoteBootstrapResultDto
                {
                    Success = false,
                    Message = GetSshSetupFailureSummary(sshStderr),
                    Output = sshStderr,
                };
            }

            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = "SSH",
                Passed = true,
                Message = "Connected to the remote host.",
            });

            var (sudoPassed, sudoMessage) = await EnsurePasswordlessSudoAsync(contextName, user, cancellationToken);
            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Configure passwordless sudo",
                Passed = sudoPassed,
                Message = sudoMessage,
            });
            if (!sudoPassed)
            {
                return new RemoteBootstrapResultDto
                {
                    Success = false,
                    Message = "Bootstrap stopped — could not configure passwordless sudo.",
                    Output = FormatBootstrapStepsOutput(steps),
                };
            }

            var dockerReadyBefore = await IsRemoteDockerReadyAsync(contextName, cancellationToken);
            if (dockerReadyBefore.Ready)
            {
                steps.Add(new RemotePrerequisiteCheckDto
                {
                    Name = "Docker Engine",
                    Passed = true,
                    Message = string.IsNullOrWhiteSpace(dockerReadyBefore.Version)
                        ? "Docker is already installed and running."
                        : $"Docker {dockerReadyBefore.Version} is already running.",
                });
            }
            else
            {
                var started = await TryStartRemoteDockerAsync(contextName, user, steps, cancellationToken);
                if (!started)
                {
                    var installFailure = await InstallLinuxDockerAsync(contextName, user, steps, cancellationToken);
                    if (installFailure is not null)
                    {
                        return new RemoteBootstrapResultDto
                        {
                            Success = false,
                            Message = installFailure.Message,
                            Output = FormatBootstrapStepsOutput(steps),
                        };
                    }
                }
            }

            var bootstrapExtras = new (string Label, string Command)[]
            {
                (
                    "Write bootstrap marker",
                    SudoNonInteractive("mkdir -p /var/lib/azeroth-platform")
                        + " && date -u +%Y-%m-%dT%H:%M:%SZ | "
                        + SudoNonInteractive("tee /var/lib/azeroth-platform/bootstrap-ready")
                        + " >/dev/null"
                ),
            };

            foreach (var (label, command) in bootstrapExtras)
            {
                var (exit, stdout, stderr) = await RunSshRemoteShellAsync(
                    contextName,
                    command,
                    cancellationToken,
                    allowTtyRetry: false);
                steps.Add(new RemotePrerequisiteCheckDto
                {
                    Name = label,
                    Passed = exit == 0,
                    Message = exit == 0
                        ? SummarizeRemoteOutput(stdout, label)
                        : FormatRemoteShellError(stderr, stdout),
                });
                if (exit != 0)
                {
                    return new RemoteBootstrapResultDto
                    {
                        Success = false,
                        Message = $"Bootstrap stopped at “{label}”.",
                        Output = FormatBootstrapStepsOutput(steps),
                    };
                }
            }

            var output = FormatBootstrapStepsOutput(steps);
            var (dockerReady, dockerVersion, dockerErr) = await TryRemoteDockerInfoAsync(contextName, cancellationToken);
            if (!dockerReady)
            {
                return new RemoteBootstrapResultDto
                {
                    Success = false,
                    Message = "Bootstrap steps ran but Docker is still not reachable over SSH. " +
                              "Wait for launch user-data, then verify again, or use Repair host setup.",
                    Output = string.IsNullOrWhiteSpace(output)
                        ? TrimRemoteError(dockerErr, string.Empty)
                        : output,
                };
            }

            return new RemoteBootstrapResultDto
            {
                Success = true,
                Message = string.IsNullOrWhiteSpace(dockerVersion)
                    ? "Bootstrap completed — Docker is installed on the remote host."
                    : $"Bootstrap completed — Docker {dockerVersion} is running.",
                Output = output,
                DockerVersion = dockerVersion,
            };
        }
        catch (Exception ex)
        {
            return new RemoteBootstrapResultDto
            {
                Success = false,
                Message = ex.Message,
            };
        }
        finally
        {
            RemoveSshConfigBlock(contextName);
        }
    }

    public Task<RemoteSetupResultDto> ProvisionRemoteHostAsync(
        string host,
        int sshPort,
        string user,
        string privateKey,
        CancellationToken cancellationToken = default)
        => ProvisionRemoteHostAsync(host, sshPort, user, privateKey, new RemoteSetupOptionsDto { SshPort = sshPort <= 0 ? 22 : sshPort }, cancellationToken);

    public async Task<RemoteSetupResultDto> FinalizeSshHardeningAsync(
        string host,
        int sshPort,
        string user,
        string privateKey,
        bool enableAwsInstanceConnect,
        CancellationToken cancellationToken = default,
        RemoteHostOs remoteOs = RemoteHostOs.Linux)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user))
        {
            return new RemoteSetupResultDto { Success = false, Message = "Host and SSH user are required." };
        }

        if (string.IsNullOrWhiteSpace(privateKey))
        {
            return new RemoteSetupResultDto { Success = false, Message = "SSH private key is required." };
        }

        host = host.Trim();
        user = user.Trim();
        var port = sshPort <= 0 ? 22 : sshPort;

        if (VpcBootstrapUserData.IsForbiddenSshUser(user) || VpcBootstrapUserData.IsImageDefaultSshUser(user))
        {
            return new RemoteSetupResultDto
            {
                Success = false,
                Message =
                    $"SSH user '{user}' cannot be used for hardening. Create and connect as a dedicated operator user " +
                    $"such as {VpcBootstrapUserData.DefaultOperatorUser} first.",
            };
        }

        if (await IsDisallowedRemoteHostAsync(host, cancellationToken))
        {
            return new RemoteSetupResultDto
            {
                Success = false,
                Message = "The specified host is not an allowed remote engine target (loopback and " +
                          "link-local/metadata addresses are blocked)."
            };
        }

        var contextName = $"acore-ext-harden-{Guid.NewGuid():N}";
        var steps = new List<RemotePrerequisiteCheckDto>();
        try
        {
            await PrepareSshAsync(contextName, host, port, user, privateKey, cancellationToken);

            var (sshExit, sshStdout, sshStderr) = await ProbeSshEchoAsync(contextName, cancellationToken, retry: true);
            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Verify SSH as operator",
                Passed = SshProbe.IsEchoSuccess(sshExit, sshStdout, sshStderr),
                Message = SshProbe.IsEchoSuccess(sshExit, sshStdout, sshStderr)
                    ? $"Connected as {user}."
                    : FormatSshError(sshExit, sshStdout, sshStderr, host, user, port)
            });
            if (!SshProbe.IsEchoSuccess(sshExit, sshStdout, sshStderr))
            {
                return FailSetup(steps, GetSshSetupFailureSummary(sshStderr));
            }

            var (sudoPassed, sudoMessage) = await EnsurePasswordlessSudoAsync(contextName, user, cancellationToken);
            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Passwordless sudo",
                Passed = sudoPassed,
                Message = sudoMessage,
            });
            if (!sudoPassed)
            {
                return FailSetup(steps, "Hardening stopped — could not use passwordless sudo as the operator user.");
            }

            var writeInstanceConnect = enableAwsInstanceConnect
                && await TryEnsureConsoleSshHelperAsync(contextName, cancellationToken);

            var dropIn = VpcBootstrapUserData.BuildSshHardeningDropIn(user, writeInstanceConnect);
            var dropInB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(dropIn));
            var writeSshd = RemotePathSetup
                + "set -e; "
                + "TMP=$(mktemp); "
                + $"echo {ShellQuote(dropInB64)} | /usr/bin/base64 -d > \"$TMP\"; "
                + "sudo -n mkdir -p /etc/ssh/sshd_config.d; "
                + "sudo -n install -o root -g root -m 644 \"$TMP\" /etc/ssh/sshd_config.d/99-azeroth-platform-hardening.conf; "
                + "rm -f \"$TMP\"";
            var (writeExit, writeOut, writeErr) = await RunSshBashWithSudoPtyFallbacksAsync(
                contextName,
                writeSshd,
                cancellationToken);
            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Write sshd hardening drop-in",
                Passed = writeExit == 0,
                Message = writeExit == 0
                    ? "Root login and password auth disabled."
                    : FormatRemoteShellError(writeErr, writeOut),
            });
            if (writeExit != 0)
            {
                return FailSetup(steps, "Hardening stopped — could not write sshd config.");
            }

            var clearKeys = RemotePathSetup
                + "set -e; "
                + "for src in ubuntu debian azureuser ec2-user admin centos fedora; do "
                + "  if [ \"$src\" != " + ShellQuote(user) + " ] && [ -f \"/home/$src/.ssh/authorized_keys\" ]; then "
                + "    sudo -n truncate -s 0 \"/home/$src/.ssh/authorized_keys\"; "
                + "  fi; "
                + "done; "
                + "if [ -f /root/.ssh/authorized_keys ]; then sudo -n truncate -s 0 /root/.ssh/authorized_keys; fi";
            var (clearExit, clearOut, clearErr) = await RunSshBashWithSudoPtyFallbacksAsync(
                contextName,
                clearKeys,
                cancellationToken);
            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Remove static keys from image-default users",
                Passed = clearExit == 0,
                Message = clearExit == 0
                    ? "Root authorized_keys cleared. VPC provider console access is unchanged."
                    : FormatRemoteShellError(clearErr, clearOut),
            });
            if (clearExit != 0)
            {
                return FailSetup(steps, "Hardening stopped — could not clear default-user SSH keys.");
            }

            var reload = RemotePathSetup
                + "if sudo -n systemctl reload ssh 2>/dev/null || sudo -n systemctl reload sshd 2>/dev/null "
                + "|| sudo -n service ssh reload 2>/dev/null || sudo -n service sshd reload 2>/dev/null; then exit 0; fi; "
                + "echo 'Could not reload sshd' >&2; exit 1";
            var (reloadExit, reloadOut, reloadErr) = await RunSshBashWithSudoPtyFallbacksAsync(
                contextName,
                reload,
                cancellationToken);
            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Reload sshd",
                Passed = reloadExit == 0,
                Message = reloadExit == 0
                    ? "sshd reloaded."
                    : FormatRemoteShellError(reloadErr, reloadOut),
            });
            if (reloadExit != 0)
            {
                return FailSetup(steps, "Hardening stopped — could not reload sshd.");
            }

            var (verifyExit, verifyStdout, verifyErr) = await ProbeSshEchoAsync(contextName, cancellationToken, retry: true);
            var verifyOk = SshProbe.IsEchoSuccess(verifyExit, verifyStdout, verifyErr);
            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Re-test operator SSH",
                Passed = verifyOk,
                Message = verifyOk
                    ? $"Still connected as {user} after hardening."
                    : FormatSshError(verifyExit, verifyStdout, verifyErr, host, user, port),
            });
            if (!verifyOk)
            {
                return FailSetup(steps, "Hardening applied but operator SSH failed afterwards. Use the provider console to recover.");
            }

            var marker = RemotePathSetup
                + "sudo -n mkdir -p /var/lib/azeroth-platform && date -u +%Y-%m-%dT%H:%M:%SZ | "
                + "sudo -n tee /var/lib/azeroth-platform/ssh-hardening-complete >/dev/null";
            await RunSshBashWithSudoPtyFallbacksAsync(contextName, marker, cancellationToken);

            return new RemoteSetupResultDto
            {
                Success = true,
                Message = $"SSH hardening complete. Daily access is {user}. Break-glass is the VPC provider console.",
                Steps = steps,
            };
        }
        catch (Exception ex)
        {
            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Finalize SSH hardening",
                Passed = false,
                Message = ex.Message,
            });
            return FailSetup(steps, ex.Message);
        }
        finally
        {
            RemoveSshConfigBlock(contextName);
        }
    }

    public async Task<RemoteSetupResultDto> ProvisionRemoteHostAsync(
        string host,
        int sshPort,
        string user,
        string privateKey,
        RemoteSetupOptionsDto options,
        CancellationToken cancellationToken = default)
    {
        options ??= new RemoteSetupOptionsDto();
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user))
        {
            return new RemoteSetupResultDto { Success = false, Message = "Host and SSH user are required." };
        }

        if (string.IsNullOrWhiteSpace(privateKey))
        {
            return new RemoteSetupResultDto { Success = false, Message = "SSH private key is required." };
        }

        host = host.Trim();
        user = user.Trim();
        var port = sshPort <= 0 ? 22 : sshPort;
        options.SshPort = options.SshPort <= 0 ? port : options.SshPort;
        user = SanitizeSshToken(user, "user");

        if (await IsDisallowedRemoteHostAsync(host, cancellationToken))
        {
            return new RemoteSetupResultDto
            {
                Success = false,
                Message = "The specified host is not an allowed remote engine target (loopback and " +
                          "link-local/metadata addresses are blocked)."
            };
        }

        var contextName = $"acore-ext-setup-{Guid.NewGuid():N}";
        var steps = new List<RemotePrerequisiteCheckDto>();
        try
        {
            await PrepareSshAsync(contextName, host, port, user, privateKey, cancellationToken);

            var (sshExit, sshStdout, sshStderr) = await ProbeSshEchoAsync(contextName, cancellationToken, retry: true);
            var sshOk = SshProbe.IsEchoSuccess(sshExit, sshStdout, sshStderr);
            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Verify SSH access",
                Passed = sshOk,
                Message = sshOk
                    ? "Connected to the remote host."
                    : FormatSshError(sshExit, sshStdout, sshStderr, host, user, port)
            });
            if (!sshOk)
            {
                return FailSetup(steps, GetSshSetupFailureSummary(sshStderr));
            }

            var (sudoPassed, sudoMessage) = await EnsurePasswordlessSudoAsync(contextName, user, cancellationToken);
            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Configure passwordless sudo",
                Passed = sudoPassed,
                Message = sudoMessage,
            });
            if (!sudoPassed)
            {
                return FailSetup(steps, "Setup stopped — could not configure passwordless sudo.");
            }

            var dockerReady = await IsRemoteDockerReadyAsync(contextName, cancellationToken);
            if (dockerReady.Ready)
            {
                steps.Add(new RemotePrerequisiteCheckDto
                {
                    Name = "Setting up Docker",
                    Passed = true,
                    Message = string.IsNullOrWhiteSpace(dockerReady.Version)
                        ? "Docker is already installed and running."
                        : dockerReady.ComposeVersion is not null
                            ? $"Docker {dockerReady.Version} is already running (Compose {dockerReady.ComposeVersion} on VPC)."
                            : $"Docker {dockerReady.Version} is already running."
                });
            }
            else
            {
                var started = await TryStartRemoteDockerAsync(contextName, user, steps, cancellationToken);
                if (!started)
                {
                    var dockerSteps = await InstallLinuxDockerAsync(contextName, user, steps, cancellationToken);
                    if (dockerSteps is not null)
                    {
                        return dockerSteps;
                    }
                }
            }

            if (options.EnableUnattendedUpgrades)
            {
                await RunLinuxSecurityBaselinesAsync(contextName, steps, cancellationToken);
            }

            if (options.EnableHostFirewall)
            {
                var firewallResult = await ApplyLinuxHostFirewallAsync(contextName, host, user, port, options, steps, cancellationToken);
                if (firewallResult is not null)
                {
                    return firewallResult;
                }
            }

            var finalDocker = await IsRemoteDockerReadyAsync(contextName, cancellationToken);
            if (!finalDocker.Ready)
            {
                return FailSetup(steps, "Docker is not ready after setup.");
            }

            return new RemoteSetupResultDto
            {
                Success = true,
                ServerVersion = finalDocker.Version,
                Message = string.IsNullOrWhiteSpace(finalDocker.Version)
                    ? "Remote host is ready for deployment."
                    : $"Remote host is ready (Docker {finalDocker.Version}).",
                Steps = steps
            };
        }
        catch (Exception ex)
        {
            return new RemoteSetupResultDto
            {
                Success = false,
                Message = ex.Message,
                Steps = steps
            };
        }
        finally
        {
            RemoveSshConfigBlock(contextName);
        }
    }

    public async Task<RemoteSetupResultDto> SyncRemoteHostFirewallAsync(
        string host,
        int sshPort,
        string user,
        string privateKey,
        RemoteSetupOptionsDto options,
        CancellationToken cancellationToken = default)
    {
        options ??= new RemoteSetupOptionsDto();
        host = host.Trim();
        user = user.Trim();
        var port = sshPort <= 0 ? 22 : sshPort;
        user = SanitizeSshToken(user, "user");
        var contextName = $"acore-ext-fw-{Guid.NewGuid():N}";
        var steps = new List<RemotePrerequisiteCheckDto>();
        try
        {
            await PrepareSshAsync(contextName, host, port, user, privateKey, cancellationToken);
            if (!options.EnableHostFirewall)
            {
                return new RemoteSetupResultDto
                {
                    Success = true,
                    Message = "Host firewall updates are disabled.",
                    Steps = steps
                };
            }

            var result = await ApplyLinuxHostFirewallAsync(contextName, host, user, port, options, steps, cancellationToken);
            if (result is not null)
            {
                return result;
            }

            return new RemoteSetupResultDto
            {
                Success = true,
                Message = "Host firewall rules are up to date.",
                Steps = steps
            };
        }
        catch (Exception ex)
        {
            return new RemoteSetupResultDto { Success = false, Message = ex.Message, Steps = steps };
        }
        finally
        {
            RemoveSshConfigBlock(contextName);
        }
    }

    public async Task<VpcFirewallStatusDto> ProbeHostFirewallAsync(
        ManagedStackEntity stack,
        VpcSecurityProfileDto profile,
        CancellationToken cancellationToken = default)
    {
        profile ??= new VpcSecurityProfileDto();
        var result = new VpcFirewallStatusDto();
        if (stack.DeploymentTarget != DeploymentTarget.External)
        {
            result.Message = "Host firewall probes apply to external stacks only.";
            return result;
        }

        if (string.IsNullOrWhiteSpace(stack.ExternalSshPrivateKey))
        {
            result.Message = "SSH credentials are missing — reconnect from the SSH tab.";
            return result;
        }

        var contextName = await EnsureContextAsync(stack, cancellationToken);
        var (ufwInstalledExit, _, _) = await RunSshRemoteShellAsync(
            contextName,
            "command -v ufw >/dev/null 2>&1",
            cancellationToken);
        result.UfwInstalled = ufwInstalledExit == 0;

        var (statusExit, statusOut, statusErr) = await RunSshRemoteShellAsync(
            contextName,
            SudoUfw("status verbose"),
            cancellationToken);
        if (statusExit != 0)
        {
            result.UfwActive = false;
            result.UfwStatusSummary = FormatRemoteShellError(statusErr, statusOut);
            result.Checks.Add(new VpcSecurityCheckDto
            {
                Category = "host-firewall",
                Name = "ufw status",
                Status = result.UfwInstalled ? "warning" : "error",
                Message = result.UfwStatusSummary
            });
        }
        else
        {
            result.UfwActive = statusOut.Contains("Status: active", StringComparison.OrdinalIgnoreCase);
            result.UfwStatusSummary = SummarizeRemoteOutput(statusOut, "ufw status");
        }

        foreach (var rule in profile.HostFirewallRules)
        {
            var check = new VpcSecurityCheckDto
            {
                Category = "host-firewall",
                Name = rule.Description,
                RoleId = rule.RoleId,
                Port = rule.Port
            };

            if (!result.UfwInstalled)
            {
                check.Status = "warning";
                check.Message = "ufw is not installed on the remote host.";
            }
            else if (!result.UfwActive)
            {
                check.Status = "warning";
                check.Message = "ufw is inactive — verify your cloud security group instead.";
            }
            else if (IsUfwPortAllowed(statusOut, rule.Port))
            {
                check.Status = "ok";
                check.Message = $"Port {rule.Port}/tcp is allowed in ufw.";
            }
            else
            {
                check.Status = "error";
                check.Message = $"Port {rule.Port}/tcp is not allowed in ufw — run Sync VPC firewall or allow it manually.";
            }

            result.Checks.Add(check);
        }

        foreach (var rule in profile.DeniedPorts)
        {
            var check = new VpcSecurityCheckDto
            {
                Category = "host-firewall",
                Name = rule.Description,
                RoleId = rule.RoleId,
                Port = rule.Port
            };

            if (!result.UfwInstalled || !result.UfwActive)
            {
                check.Status = "unknown";
                check.Message = "Cannot verify deny rules while ufw is unavailable or inactive.";
            }
            else if (IsUfwPortAllowed(statusOut, rule.Port))
            {
                check.Status = "error";
                check.Message = $"Port {rule.Port}/tcp is allowed publicly in ufw — it should stay manager/VPC-only.";
            }
            else
            {
                check.Status = "ok";
                check.Message = $"Port {rule.Port}/tcp is not opened in ufw.";
            }

            result.Checks.Add(check);
        }

        foreach (var rule in profile.CloudSecurityGroupRules)
        {
            result.Checks.Add(new VpcSecurityCheckDto
            {
                Category = "cloud-sg",
                Name = rule.Description,
                RoleId = rule.RoleId,
                Port = rule.Port,
                Status = "unknown",
                Message = $"Verify inbound TCP {rule.Port} ({rule.Source}) in your cloud provider console."
            });
        }

        result.OverallHealthy = result.Checks.Count == 0
            || result.Checks.All(c =>
                c.Status is "ok" or "unknown" or "not-applicable");
        result.Message = result.OverallHealthy
            ? "Host firewall checks passed. Cloud security group rules must still be verified manually."
            : "One or more host firewall checks failed — review the items below.";
        return result;
    }

    public async Task<VpcSshLogsDto> FetchSshAuthLogsAsync(
        ManagedStackEntity stack,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var result = new VpcSshLogsDto();
        if (stack.DeploymentTarget != DeploymentTarget.External)
        {
            result.Message = "SSH logs are available for external stacks only.";
            return result;
        }

        if (string.IsNullOrWhiteSpace(stack.ExternalSshPrivateKey))
        {
            result.Message = "SSH credentials are missing — reconnect from the SSH tab.";
            return result;
        }

        var contextName = await EnsureContextAsync(stack, cancellationToken);
        var shell = BuildSshAuthLogFetchShell(limit);
        var (exit, stdout, stderr) = await RunSshRemoteShellAsync(contextName, shell, cancellationToken, connectTimeoutSeconds: 45);
        if (exit != 0)
        {
            result.Message = FormatRemoteShellError(stderr, stdout);
            return result;
        }

        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            result.Success = true;
            result.Message = "No recent SSH authentication events were found.";
            return result;
        }

        if (lines[0].StartsWith("SOURCE:", StringComparison.Ordinal))
        {
            result.LogSource = lines[0]["SOURCE:".Length..].Trim();
            lines = lines.Skip(1).ToArray();
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = ParseSshAuthLogLine(line);
            if (entry is not null)
            {
                result.Entries.Add(entry);
            }
        }

        result.Success = true;
        result.Message = result.Entries.Count == 0
            ? "No parseable SSH authentication events were found."
            : $"Showing {result.Entries.Count} recent SSH event(s).";
        return result;
    }

    private static string BuildSshAuthLogFetchShell(int limit)
        => "set -e; " +
           "PATTERN='sshd\\[[0-9]+\\]: (Accepted|Failed|Invalid|Connection closed by authenticating)'; " +
           "collect() { grep -hE \"$PATTERN\" \"$@\" 2>/dev/null || true; }; " +
           "TMP=\"$(mktemp)\"; " +
           "collect /var/log/auth.log /var/log/secure >>\"$TMP\"; " +
           "if command -v journalctl >/dev/null 2>&1; then journalctl -u ssh -u sshd --no-pager -S '14 days ago' 2>/dev/null | grep -E \"$PATTERN\" >>\"$TMP\" || true; fi; " +
           "if [ ! -s \"$TMP\" ]; then rm -f \"$TMP\"; exit 0; fi; " +
           "if [ -r /var/log/auth.log ]; then echo \"SOURCE:/var/log/auth.log\"; " +
           "elif [ -r /var/log/secure ]; then echo \"SOURCE:/var/log/secure\"; " +
           "else echo \"SOURCE:journalctl\"; fi; " +
           $"sort -u \"$TMP\" | tail -n {limit}; rm -f \"$TMP\"";

    private static VpcSshLogEntryDto? ParseSshAuthLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var entry = new VpcSshLogEntryDto { RawLine = line.Trim() };
        var sshIdx = line.IndexOf("sshd[", StringComparison.Ordinal);
        if (sshIdx >= 0)
        {
            var bracketEnd = line.IndexOf("]: ", sshIdx, StringComparison.Ordinal);
            if (bracketEnd < 0)
            {
                return null;
            }

            var payload = line[(bracketEnd + 3)..].Trim();
            if (payload.StartsWith("Accepted ", StringComparison.Ordinal))
            {
                entry.EventType = "accepted";
                ParseSshUserFrom(payload, "Accepted ", " for ", out var user);
                entry.Username = user;
            }
            else if (payload.StartsWith("Failed password for invalid user ", StringComparison.Ordinal))
            {
                entry.EventType = "invalid-user";
                ParseSshUserFrom(payload, "Failed password for invalid user ", " from ", out var user);
                entry.Username = user;
            }
            else if (payload.StartsWith("Failed password for ", StringComparison.Ordinal))
            {
                entry.EventType = "failed";
                ParseSshUserFrom(payload, "Failed password for ", " from ", out var user);
                entry.Username = user;
            }
            else if (payload.StartsWith("Invalid user ", StringComparison.Ordinal))
            {
                entry.EventType = "invalid-user";
                ParseSshUserFrom(payload, "Invalid user ", " from ", out var user);
                entry.Username = user;
            }
            else if (payload.StartsWith("Connection closed by authenticating user ", StringComparison.Ordinal))
            {
                entry.EventType = "closed";
                ParseSshUserFrom(payload, "Connection closed by authenticating user ", " ", out var user);
                entry.Username = user;
            }
            else
            {
                return null;
            }

            var fromIdx = payload.IndexOf(" from ", StringComparison.Ordinal);
            if (fromIdx >= 0)
            {
                var afterFrom = payload[(fromIdx + 6)..].Trim();
                var space = afterFrom.IndexOf(' ');
                entry.SourceIp = space > 0 ? afterFrom[..space] : afterFrom;
            }
        }

        if (DateTimeOffset.TryParse(line.AsSpan(0, Math.Min(line.Length, 32)).Trim(), out var ts))
        {
            entry.Timestamp = ts;
        }

        return string.IsNullOrWhiteSpace(entry.EventType) ? null : entry;
    }

    private static void ParseSshUserFrom(string payload, string prefix, string suffix, out string? username)
    {
        username = null;
        if (!payload.StartsWith(prefix, StringComparison.Ordinal))
        {
            return;
        }

        var rest = payload[prefix.Length..];
        var end = rest.IndexOf(suffix, StringComparison.Ordinal);
        username = end >= 0 ? rest[..end].Trim() : rest.Trim();
    }

    private static bool IsUfwPortAllowed(string ufwStatus, int port)
    {
        if (string.IsNullOrWhiteSpace(ufwStatus))
        {
            return false;
        }

        foreach (var line in ufwStatus.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.Contains("ALLOW", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.StartsWith($"{port}/", StringComparison.Ordinal)
                || line.Contains($" {port}/", StringComparison.Ordinal)
                || line.Contains($":{port}/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// When Docker is installed but the daemon is stopped, start/enable it without a full apt install.
    /// Returns true when the engine responds after these steps.
    /// </summary>
    private async Task<bool> TryStartRemoteDockerAsync(
        string contextName,
        string user,
        List<RemotePrerequisiteCheckDto> steps,
        CancellationToken cancellationToken)
    {
        var (whichExit, _, _) = await RunSshRemoteShellAsync(
            contextName,
            "command -v docker >/dev/null 2>&1",
            cancellationToken);
        if (whichExit != 0)
        {
            return false;
        }

        // A standalone docker CLI (e.g. leftover binary without docker.io) must not skip package install.
        if (!await RemoteDockerServiceUnitExistsAsync(contextName, cancellationToken))
        {
            return false;
        }

        var startCommands = new (string Label, string Command)[]
        {
            ("Grant Docker access to SSH user", SudoNonInteractive($"usermod -aG docker {user}")),
        };

        var (dockerServicePassed, dockerServiceMessage) = await EnsureRemoteDockerServiceAsync(contextName, cancellationToken);
        steps.Add(new RemotePrerequisiteCheckDto
        {
            Name = "Start Docker service",
            Passed = dockerServicePassed,
            Message = dockerServiceMessage,
        });
        if (!dockerServicePassed)
        {
            return false;
        }

        foreach (var (label, command) in startCommands)
        {
            var (exit, stdout, stderr) = await RunSshRemoteShellAsync(
                contextName,
                command,
                cancellationToken,
                allowTtyRetry: true);
            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = label,
                Passed = exit == 0,
                Message = exit == 0 ? SummarizeRemoteOutput(stdout, label) : FormatRemoteShellError(stderr, stdout)
            });
            if (exit != 0 && !label.StartsWith("Grant Docker", StringComparison.Ordinal))
            {
                return false;
            }
        }

        var ready = await IsRemoteDockerReadyAsync(contextName, cancellationToken);
        if (ready.Ready)
        {
            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Verify Docker Engine",
                Passed = true,
                Message = string.IsNullOrWhiteSpace(ready.Version)
                    ? "Docker engine is responding."
                    : ready.ComposeVersion is not null
                        ? $"Docker {ready.Version} is running (Compose {ready.ComposeVersion} on VPC)."
                        : $"Docker {ready.Version} is running."
            });
        }

        return ready.Ready;
    }

    private async Task<RemoteSetupResultDto?> InstallLinuxDockerAsync(
        string contextName,
        string user,
        List<RemotePrerequisiteCheckDto> steps,
        CancellationToken cancellationToken)
    {
        var (aptExit, _, _) = await RunSshRemoteShellAsync(
            contextName,
            "command -v apt-get >/dev/null 2>&1",
            cancellationToken);
        if (aptExit != 0)
        {
            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Setting up Docker",
                Passed = false,
                Message = "Automatic setup supports Ubuntu/Debian hosts (apt-get). Install Docker manually on this OS."
            });
            return FailSetup(steps, "Automatic Docker setup is not supported on this operating system.");
        }

        await WaitForRemoteAptLockAsync(contextName, cancellationToken, TimeSpan.FromSeconds(90));

        var setupCommands = new (string Label, string Command, int TimeoutSeconds)[]
        {
            ("Update package lists", SudoAptGet("update -qq"), 180),
            ("Install Docker Engine", SudoAptGet("install -y docker.io"), 600),
        };

        foreach (var (label, command, timeoutSeconds) in setupCommands)
        {
            var (exit, stdout, stderr) = await RunSshRemoteShellAsync(
                contextName,
                command,
                cancellationToken,
                connectTimeoutSeconds: timeoutSeconds,
                allowTtyRetry: true);
            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = label,
                Passed = exit == 0,
                Message = exit == 0 ? SummarizeAptOutput(stdout, stderr, label) : FormatRemoteShellError(stderr, stdout)
            });
            if (exit != 0)
            {
                if (label == "Install Docker Engine"
                    && LooksLikeMissingDockerIoPackage(stderr, stdout))
                {
                    var (retryPassed, retryMessage) = await TryInstallDockerIoWithUniverseAsync(
                        contextName,
                        steps,
                        cancellationToken);
                    if (!retryPassed)
                    {
                        return FailSetup(steps, retryMessage);
                    }
                }
                else
                {
                    return FailSetup(steps, $"Setup stopped at “{label}”. See the step detail below.");
                }
            }
        }

        if (!await IsRemoteDockerIoPresentAsync(contextName, cancellationToken))
        {
            var (retryPassed, retryMessage) = await TryInstallDockerIoWithUniverseAsync(
                contextName,
                steps,
                cancellationToken);
            if (!retryPassed)
            {
                return FailSetup(steps, retryMessage);
            }
        }

        await RunSshRemoteShellAsync(
            contextName,
            SudoNonInteractive("systemctl daemon-reload"),
            cancellationToken,
            connectTimeoutSeconds: 60,
            allowTtyRetry: false);

        var (composeExit, composeOut, composeErr) = await RunSshRemoteShellAsync(
            contextName,
            SudoAptGet("install -y docker-compose-v2"),
            cancellationToken,
            connectTimeoutSeconds: 300,
            allowTtyRetry: true);
        steps.Add(new RemotePrerequisiteCheckDto
        {
            Name = "Install Docker Compose (optional)",
            Passed = composeExit == 0,
            Message = composeExit == 0
                ? SummarizeAptOutput(composeOut, composeErr, "Install Docker Compose (optional)")
                : "Optional — Compose runs on the platform manager when unavailable on the VPC."
        });

        if (!await RemoteDockerSystemdUnitExistsAsync(contextName, cancellationToken)
            && !await IsRemoteDockerIoPresentAsync(contextName, cancellationToken))
        {
            var diagnostics = await GetRemoteDockerInstallDiagnosticsAsync(contextName, cancellationToken);
            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Verify Docker systemd unit",
                Passed = false,
                Message = string.IsNullOrWhiteSpace(diagnostics)
                    ? "The docker.io package did not install the Docker systemd service."
                    : diagnostics
            });
            return FailSetup(steps, "Docker packages were installed but the Docker service unit is missing.");
        }

        var (usermodExit, usermodOut, usermodErr) = await RunSshRemoteShellAsync(
            contextName,
            SudoNonInteractive($"usermod -aG docker {user}"),
            cancellationToken,
            allowTtyRetry: true);
        steps.Add(new RemotePrerequisiteCheckDto
        {
            Name = "Grant Docker access to SSH user",
            Passed = usermodExit == 0,
            Message = usermodExit == 0
                ? SummarizeRemoteOutput(usermodOut, "Grant Docker access to SSH user")
                : FormatRemoteShellError(usermodErr, usermodOut)
        });
        if (usermodExit != 0)
        {
            return FailSetup(steps, "Setup stopped at “Grant Docker access to SSH user”. See the step detail below.");
        }

        var (dockerServicePassed, dockerServiceMessage) = await EnsureRemoteDockerServiceAsync(contextName, cancellationToken);
        steps.Add(new RemotePrerequisiteCheckDto
        {
            Name = "Start Docker service",
            Passed = dockerServicePassed,
            Message = dockerServiceMessage,
        });
        if (!dockerServicePassed)
        {
            return FailSetup(steps, "Setup stopped at “Start Docker service”. See the step detail below.");
        }

        var (verifyReady, verifyOut, verifyErr) = await TryRemoteDockerInfoAsync(contextName, cancellationToken);
        steps.Add(new RemotePrerequisiteCheckDto
        {
            Name = "Verify Docker Engine",
            Passed = verifyReady,
            Message = verifyReady
                ? (string.IsNullOrWhiteSpace(ExtractDockerVersion(verifyOut))
                    ? "Docker engine is responding."
                    : $"Docker {ExtractDockerVersion(verifyOut)} is running.")
                : FormatRemoteDockerError(verifyErr, string.Empty, user, 22)
        });
        if (!verifyReady)
        {
            return FailSetup(steps, "Docker was installed but the SSH user still cannot reach the engine.");
        }

        var composeVersion = await TryGetRemoteComposeVersionAsync(contextName, cancellationToken);
        steps.Add(new RemotePrerequisiteCheckDto
        {
            Name = "Verify Docker Compose",
            Passed = true,
            Message = composeVersion is not null
                ? $"Compose {composeVersion} available on the VPC (optional)."
                : "Compose runs on the platform manager — not required on the VPC host."
        });

        return null;
    }

    private async Task RunLinuxSecurityBaselinesAsync(
        string contextName,
        List<RemotePrerequisiteCheckDto> steps,
        CancellationToken cancellationToken)
    {
        if (await IsUnattendedUpgradesEnabledAsync(contextName, cancellationToken))
        {
            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = "OS security baselines",
                Passed = true,
                Message = "Automatic security updates are already enabled."
            });
            return;
        }

        await WaitForRemoteAptLockAsync(contextName, cancellationToken, TimeSpan.FromSeconds(60));
        var (exit, stdout, stderr) = await RunSshRemoteShellAsync(
            contextName,
            $"{SudoAptGet("install -y unattended-upgrades")} && " +
            "printf '%s\\n' 'APT::Periodic::Update-Package-Lists \"1\";' 'APT::Periodic::Unattended-Upgrade \"1\";' | " +
            SudoNonInteractive("tee /etc/apt/apt.conf.d/20auto-upgrades") +
            " >/dev/null && " +
            $"{SudoNonInteractive("systemctl enable --now apt-daily.timer apt-daily-upgrade.timer")} || true; " +
            $"{SudoNonInteractive("systemctl enable unattended-upgrades")} || true; true",
            cancellationToken,
            connectTimeoutSeconds: 300,
            allowTtyRetry: true);
        var ready = await IsUnattendedUpgradesEnabledAsync(contextName, cancellationToken);
        steps.Add(new RemotePrerequisiteCheckDto
        {
            Name = "OS security baselines",
            Passed = ready,
            Message = ready
                ? "Unattended upgrades enabled."
                : FormatRemoteShellError(stderr, stdout)
        });
    }

    private async Task<RemoteSetupResultDto?> ApplyLinuxHostFirewallAsync(
        string contextName,
        string host,
        string user,
        int sshPort,
        RemoteSetupOptionsDto options,
        List<RemotePrerequisiteCheckDto> steps,
        CancellationToken cancellationToken)
    {
        var ufwActive = await IsUfwActiveAsync(contextName, cancellationToken);
        if (ufwActive)
        {
            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Configure host firewall (ufw)",
                Passed = true,
                Message = "ufw is already active — verifying SSH and player/web port rules."
            });
        }
        else
        {
            var (installExit, _, installErr) = await RunSshRemoteShellAsync(
                contextName,
                SudoAptGet("install -y ufw"),
                cancellationToken);
            if (installExit != 0)
            {
                steps.Add(new RemotePrerequisiteCheckDto
                {
                    Name = "Configure host firewall (ufw)",
                    Passed = false,
                    Message = FormatRemoteShellError(installErr, string.Empty)
                });
                return FailSetup(steps, "Could not install ufw on the remote host.");
            }

            var baselineCommands = new (string Label, string Command)[]
            {
                ("Set firewall default deny incoming", SetUfwDefaultIncomingPolicyShell()),
                ("Set firewall default allow outgoing", SetUfwDefaultOutgoingPolicyShell()),
                ($"Allow SSH (port {sshPort})", AllowUfwTcpPort(sshPort, "SSH")),
            };

            foreach (var (label, command) in baselineCommands)
            {
                var (exit, stdout, stderr) = await RunSshRemoteShellAsync(contextName, command, cancellationToken);
                steps.Add(new RemotePrerequisiteCheckDto
                {
                    Name = label,
                    Passed = exit == 0,
                    Message = exit == 0 ? SummarizeRemoteOutput(stdout, label) : FormatRemoteShellError(stderr, stdout)
                });
                if (exit != 0)
                {
                    return FailSetup(steps, $"Host firewall setup failed at “{label}”.");
                }
            }
        }

        if (ufwActive)
        {
            var (sshAllowExit, sshAllowOut, sshAllowErr) = await RunSshRemoteShellAsync(
                contextName,
                AllowUfwTcpPort(sshPort, "SSH"),
                cancellationToken);
            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = $"Allow SSH (port {sshPort})",
                Passed = sshAllowExit == 0,
                Message = sshAllowExit == 0
                    ? SummarizeRemoteOutput(sshAllowOut, $"SSH port {sshPort}/tcp allowed")
                    : FormatRemoteShellError(sshAllowErr, sshAllowOut)
            });
            if (sshAllowExit != 0)
            {
                return FailSetup(steps, $"Could not allow SSH port {sshPort} on the host firewall.");
            }
        }

        foreach (var port in CollectPlayerWebPorts(options))
        {
            var label = $"Allow TCP {port} (player/web)";
            var (exit, stdout, stderr) = await RunSshRemoteShellAsync(
                contextName,
                AllowUfwTcpPort(port, "Azeroth player/web"),
                cancellationToken);
            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = label,
                Passed = exit == 0,
                Message = exit == 0 ? $"Port {port}/tcp allowed." : FormatRemoteShellError(stderr, stdout)
            });
            if (exit != 0)
            {
                return FailSetup(steps, $"Could not allow port {port} on the host firewall.");
            }
        }

        if (!ufwActive)
        {
            var (enableExit, enableOut, enableErr) = await RunSshRemoteShellAsync(
                contextName,
                SudoUfw("--force enable"),
                cancellationToken);
            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Enable host firewall (ufw)",
                Passed = enableExit == 0,
                Message = enableExit == 0
                    ? SummarizeRemoteOutput(enableOut, "Firewall enabled")
                    : FormatRemoteShellError(enableErr, enableOut)
            });
            if (enableExit != 0)
            {
                return FailSetup(steps, "Could not enable ufw on the remote host.");
            }
        }

        var (statusExit, statusOut, _) = await RunSshRemoteShellAsync(contextName, SudoUfw("status verbose"), cancellationToken);
        steps.Add(new RemotePrerequisiteCheckDto
        {
            Name = "Configure firewall rules",
            Passed = statusExit == 0,
            Message = statusExit == 0
                ? "Host firewall configured. Management ports (MySQL/SOAP) are not opened — Docker binds them on the VPC interface only."
                : "Firewall configured; status check failed."
        });

        return null;
    }

    private static IEnumerable<int> CollectPlayerWebPorts(RemoteSetupOptionsDto options)
    {
        var ports = new HashSet<int> { options.AuthServerPort, options.WorldServerPort };
        if (options.ArmoryPort > 0)
        {
            ports.Add(options.ArmoryPort);
        }

        if (options.ClientPort > 0)
        {
            ports.Add(options.ClientPort);
        }

        return ports.OrderBy(p => p);
    }

    private static string FormatBootstrapStepsOutput(IEnumerable<RemotePrerequisiteCheckDto> steps)
    {
        var lines = steps
            .Select(step => $"{step.Name}: {step.Message}")
            .ToList();
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var combined = string.Join(Environment.NewLine, lines);
        return combined.Length > 8000 ? combined[..8000] + "…" : combined;
    }

    private static RemoteSetupResultDto FailSetup(List<RemotePrerequisiteCheckDto> steps, string message)
        => new() { Success = false, Message = message, Steps = steps };

    private const string RemoteDockerLocate =
        "export PATH=\"/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/snap/bin\"; "
        + "DOCKER=\"$(command -v docker 2>/dev/null || true)\"; "
        + "if [ -z \"$DOCKER\" ]; then "
        + "for c in /usr/bin/docker /usr/local/bin/docker /snap/bin/docker; do "
        + "[ -x \"$c\" ] && DOCKER=\"$c\" && break; done; fi; "
        + "if [ -z \"$DOCKER\" ]; then echo 'docker: No such file or directory' >&2; exit 127; fi; ";

    private async Task<(bool Ready, string Version, string Error)> EnsureRemoteDockerForVerifyAsync(
        string contextName,
        string user,
        List<RemotePrerequisiteCheckDto> prerequisites,
        CancellationToken cancellationToken)
    {
        var (ready, version, error) = await TryRemoteDockerInfoAsync(contextName, cancellationToken);
        if (ready)
        {
            return (true, version, string.Empty);
        }

        if (await IsRemoteBootstrapStillRunningAsync(contextName, cancellationToken))
        {
            prerequisites.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Launch user-data",
                Passed = true,
                Message = "Launch user-data is still installing packages. Waiting for Docker..."
            });

            await WaitForRemoteDockerWhileBootstrapRunsAsync(
                contextName,
                cancellationToken,
                TimeSpan.FromSeconds(90));
            (ready, version, error) = await TryRemoteDockerInfoAsync(contextName, cancellationToken);
            if (ready)
            {
                return (true, version, string.Empty);
            }
        }

        await WaitForRemoteAptLockAsync(contextName, cancellationToken, TimeSpan.FromSeconds(60));

        var started = await TryStartRemoteDockerAsync(contextName, user, prerequisites, cancellationToken);
        if (!started)
        {
            var installFailure = await InstallLinuxDockerAsync(contextName, user, prerequisites, cancellationToken);
            if (installFailure is not null)
            {
                return (false, string.Empty, installFailure.Message ?? error);
            }
        }

        await EnsureRemoteDockerGroupAsync(contextName, user, cancellationToken);
        return await TryRemoteDockerInfoAsync(contextName, cancellationToken);
    }

    private async Task<bool> IsRemoteBootstrapStillRunningAsync(
        string contextName,
        CancellationToken cancellationToken)
    {
        var (exit, _, _) = await RunSshRemoteShellAsync(
            contextName,
            RemotePathSetup
                + "if test -f /var/lib/azeroth-platform/bootstrap-ready; then exit 1; fi; "
                + "if cloud-init status 2>/dev/null | grep -qi running; then exit 0; fi; "
                + "if command -v fuser >/dev/null 2>&1 "
                + "&& fuser /var/lib/dpkg/lock-frontend >/dev/null 2>&1; then exit 0; fi; "
                + "exit 1",
            cancellationToken,
            connectTimeoutSeconds: 30,
            allowTtyRetry: false);
        return exit == 0;
    }

    private async Task WaitForRemoteDockerWhileBootstrapRunsAsync(
        string contextName,
        CancellationToken cancellationToken,
        TimeSpan maxWait)
    {
        var deadline = DateTime.UtcNow + maxWait;
        while (DateTime.UtcNow < deadline)
        {
            var (ready, _, _) = await TryRemoteDockerInfoAsync(contextName, cancellationToken);
            if (ready || !await IsRemoteBootstrapStillRunningAsync(contextName, cancellationToken))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private async Task WaitForRemoteAptLockAsync(
        string contextName,
        CancellationToken cancellationToken,
        TimeSpan maxWait)
    {
        var deadline = DateTime.UtcNow + maxWait;
        while (DateTime.UtcNow < deadline)
        {
            var (exit, _, _) = await RunSshRemoteShellAsync(
                contextName,
                RemotePathSetup
                    + "if command -v fuser >/dev/null 2>&1 "
                    + "&& { fuser /var/lib/dpkg/lock-frontend >/dev/null 2>&1 "
                    + "|| sudo -n fuser /var/lib/dpkg/lock-frontend >/dev/null 2>&1; }; then exit 1; fi; "
                    + "exit 0",
                cancellationToken,
                connectTimeoutSeconds: 30,
                allowTtyRetry: true);
            if (exit == 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private async Task<(bool Ready, string Version, string Error)> TryRemoteDockerInfoAsync(
        string contextName,
        CancellationToken cancellationToken)
    {
        const string dockerInfo =
            RemoteDockerLocate + "\"$DOCKER\" info --format '{{.ServerVersion}}'";
        var lastError = string.Empty;
        string? dockerError = null;

        foreach (var (command, allowTtyRetry) in new (string Command, bool AllowTtyRetry)[]
                 {
                     (dockerInfo, false),
                     (RemoteDockerLocate + "sg docker -c \"$DOCKER info --format '{{.ServerVersion}}'\"", false),
                     (RemoteDockerLocate + "sudo -n \"$DOCKER\" info --format '{{.ServerVersion}}'", true),
                 })
        {
            var (exit, stdout, stderr) = await RunSshRemoteShellAsync(
                contextName,
                command,
                cancellationToken,
                allowTtyRetry: allowTtyRetry);
            if (exit == 0 && !LooksLikeSudoFailure(stderr, stdout))
            {
                return (true, ExtractDockerVersion(stdout), string.Empty);
            }

            lastError = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            if (!LooksLikeSudoFailure(stderr, stdout) && string.IsNullOrWhiteSpace(dockerError))
            {
                dockerError = lastError;
            }
        }

        return (false, string.Empty, string.IsNullOrWhiteSpace(dockerError) ? lastError : dockerError);
    }

    private async Task<(bool Ready, string? Version, string? ComposeVersion)> IsRemoteDockerReadyAsync(
        string contextName,
        CancellationToken cancellationToken)
    {
        var (ready, version, _) = await TryRemoteDockerInfoAsync(contextName, cancellationToken);
        if (!ready)
        {
            return (false, null, null);
        }

        var composeVersion = await TryGetRemoteComposeVersionAsync(contextName, cancellationToken);
        return (true, version, composeVersion);
    }

    private async Task<string?> TryGetRemoteComposeVersionAsync(
        string contextName,
        CancellationToken cancellationToken)
    {
        var (composeExit, composeOut, _) = await RunSshAsync(
            contextName,
            ["docker", "compose", "version", "--short"],
            cancellationToken);
        if (composeExit == 0 && !string.IsNullOrWhiteSpace(composeOut))
        {
            return composeOut.Trim();
        }

        var (sudoExit, sudoOut, _) = await RunSshRemoteShellAsync(
            contextName,
            "sudo -n docker compose version --short",
            cancellationToken);
        if (sudoExit == 0 && !string.IsNullOrWhiteSpace(sudoOut))
        {
            return sudoOut.Trim();
        }

        return null;
    }

    private async Task AppendHostSecurityPrerequisiteChecksAsync(
        string contextName,
        List<RemotePrerequisiteCheckDto> prerequisites,
        CancellationToken cancellationToken)
    {
        var baselinesReady = await IsUnattendedUpgradesEnabledAsync(contextName, cancellationToken);
        if (!baselinesReady)
        {
            await RunLinuxSecurityBaselinesAsync(contextName, prerequisites, cancellationToken);
        }
        else
        {
            prerequisites.Add(new RemotePrerequisiteCheckDto
            {
                Name = "OS security baselines",
                Passed = true,
                Message = "Automatic security updates are enabled."
            });
        }

        var (_, statusOut, _) = await RunSshRemoteShellAsync(
            contextName,
            SudoUfw("status verbose"),
            cancellationToken);
        var ufwActive = statusOut.Contains("Status: active", StringComparison.OrdinalIgnoreCase);
        prerequisites.Add(new RemotePrerequisiteCheckDto
        {
            Name = "Host firewall (ufw)",
            Passed = ufwActive,
            Message = ufwActive
                ? "ufw is active."
                : "ufw is not active yet. Wait for launch user data, or run Repair host setup."
        });
        if (!ufwActive)
        {
            return;
        }

        var allowPorts = new (int Port, string Label)[]
        {
            (22, "SSH"),
            (3724, "Authserver"),
            (8085, "Worldserver"),
            (StackNetworkDefaults.DefaultArmoryPort, "Armory"),
            (StackNetworkDefaults.DefaultClientPort, "Client files"),
        };
        foreach (var (port, label) in allowPorts)
        {
            var allowed = IsUfwPortAllowed(statusOut, port);
            prerequisites.Add(new RemotePrerequisiteCheckDto
            {
                Name = $"ufw allow {port}/tcp ({label})",
                Passed = allowed,
                Message = allowed
                    ? $"{label} is allowed in ufw."
                    : $"{label} port {port}/tcp is not allowed in ufw."
            });
        }

        foreach (var (port, label) in new (int Port, string Label)[] { (3306, "MySQL"), (7878, "SOAP") })
        {
            var allowed = IsUfwPortAllowed(statusOut, port);
            prerequisites.Add(new RemotePrerequisiteCheckDto
            {
                Name = $"ufw deny {port}/tcp ({label})",
                Passed = !allowed,
                Message = allowed
                    ? $"{label} port {port}/tcp is publicly allowed in ufw — it should stay VPC-only."
                    : $"{label} is not opened in ufw (expected)."
            });
        }
    }

    private async Task<bool> IsUnattendedUpgradesEnabledAsync(
        string contextName,
        CancellationToken cancellationToken)
    {
        // Ubuntu ships unattended-upgrades.service as static (no [Install] section), so
        // `systemctl is-enabled` exits 1 even when the package is installed and apt timers run it.
        var (exit, _, _) = await RunSshRemoteShellAsync(
            contextName,
            RemotePathSetup
                + "if dpkg -s unattended-upgrades >/dev/null 2>&1; then "
                + "if systemctl is-enabled unattended-upgrades >/dev/null 2>&1; then exit 0; fi; "
                + "state=$(systemctl is-enabled unattended-upgrades 2>/dev/null || true); "
                + "[ \"$state\" = static ] && exit 0; "
                + "fi; "
                + "systemctl is-enabled apt-daily-upgrade.timer >/dev/null 2>&1 && "
                + "dpkg -s unattended-upgrades >/dev/null 2>&1 && exit 0; "
                + "grep -RqsE 'Unattended-Upgrade[[:space:]]+\"?1\"?' /etc/apt/apt.conf.d/ 2>/dev/null && exit 0; "
                + "exit 1",
            cancellationToken,
            allowTtyRetry: false);
        return exit == 0;
    }

    private async Task<bool> IsUfwActiveAsync(
        string contextName,
        CancellationToken cancellationToken)
    {
        var (exit, stdout, _) = await RunSshRemoteShellAsync(contextName, SudoUfw("status"), cancellationToken);
        return exit == 0
            && stdout.Contains("Status: active", StringComparison.OrdinalIgnoreCase);
    }

    private const string UfwPathSetup =
        "export PATH=\"/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin\"; " +
        "UFW=\"$(command -v ufw 2>/dev/null || echo /usr/sbin/ufw)\"";

    private const string RemotePathSetup =
        "export PATH=\"/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin\"; ";

    private const string PlatformSudoersFile = "/etc/sudoers.d/99-azeroth-platform";

    private static string SudoNonInteractive(string command)
        => $"sudo -n {command}";

    private static string SudoUfw(string arguments)
        => $"{UfwPathSetup}; sudo -n \"$UFW\" {arguments}";

    /// <summary>
    /// Sets ufw default incoming deny via the ufw CLI, falling back to /etc/default/ufw when the CLI fails
    /// (common on minimal images or when ufw is not yet enabled).
    /// </summary>
    private static string SetUfwDefaultIncomingPolicyShell()
        => $"{UfwPathSetup}; " +
           "if sudo -n \"$UFW\" default deny incoming 2>/dev/null; then exit 0; fi; " +
           "if sudo -n sed -i 's/^DEFAULT_INPUT_POLICY=.*/DEFAULT_INPUT_POLICY=\"DROP\"/' /etc/default/ufw 2>/dev/null " +
           "&& grep -qE '^DEFAULT_INPUT_POLICY=\"DROP\"' /etc/default/ufw; then exit 0; fi; " +
           "echo 'Could not set default incoming policy to deny.' >&2; exit 1";

    private static string SetUfwDefaultOutgoingPolicyShell()
        => $"{UfwPathSetup}; " +
           "if sudo -n \"$UFW\" default allow outgoing 2>/dev/null; then exit 0; fi; " +
           "if sudo -n sed -i 's/^DEFAULT_OUTPUT_POLICY=.*/DEFAULT_OUTPUT_POLICY=\"ACCEPT\"/' /etc/default/ufw 2>/dev/null " +
           "&& grep -qE '^DEFAULT_OUTPUT_POLICY=\"ACCEPT\"' /etc/default/ufw; then exit 0; fi; " +
           "echo 'Could not set default outgoing policy to allow.' >&2; exit 1";

    private static string AllowUfwTcpPort(int port, string comment)
        => SudoUfw($"allow {port}/tcp comment '{comment}'");

    private static string SudoAptGet(string arguments)
        => $"env DEBIAN_FRONTEND=noninteractive {SudoNonInteractive($"apt-get {arguments}")}";

    /// <summary>
    /// Optional console SSH helper used on some cloud images. Missing package must not fail Verify VPC;
    /// break-glass remains the VPC provider console.
    /// </summary>
    private async Task<bool> TryEnsureConsoleSshHelperAsync(
        string contextName,
        CancellationToken cancellationToken)
    {
        const string helperPath = "/usr/share/ec2-instance-connect/eic_run_authorized_keys";
        var probe = RemotePathSetup + $"if test -x {helperPath}; then echo present; else echo missing; fi";

        async Task<bool> HelperPresentAsync()
        {
            var (_, stdout, _) = await RunSshRemoteShellAsync(
                contextName,
                probe,
                cancellationToken,
                connectTimeoutSeconds: 30,
                allowTtyRetry: false);
            return stdout.Contains("present", StringComparison.OrdinalIgnoreCase);
        }

        if (await HelperPresentAsync())
        {
            return true;
        }

        await RunSshRemoteShellAsync(
            contextName,
            SudoAptGet("update -qq"),
            cancellationToken,
            connectTimeoutSeconds: 180,
            allowTtyRetry: true);
        await RunSshRemoteShellAsync(
            contextName,
            SudoAptGet("install -y ec2-instance-connect"),
            cancellationToken,
            connectTimeoutSeconds: 180,
            allowTtyRetry: true);

        return await HelperPresentAsync();
    }

    private static string BuildFullPlatformSudoersContent(string user)
        => VpcBootstrapUserData.BuildPasswordlessSudoers(user);

    private static string BuildPlatformSudoDefaultsContent(string user)
        => $"Defaults !use_pty\nDefaults:{user} !use_pty\nDefaults !requiretty\nDefaults:{user} !requiretty\n";

    private static string BuildMinimalPlatformSudoersContent(string user)
        => $"Defaults !use_pty\nDefaults:{user} !use_pty\n{user} ALL=(ALL) NOPASSWD:ALL\n";

    private static string BuildMinimalPlatformSudoDefaultsContent(string user)
        => $"Defaults !use_pty\nDefaults:{user} !use_pty\n";

    private async Task<string> ResolveRemoteSshUserAsync(
        string contextName,
        string fallbackUser,
        CancellationToken cancellationToken)
    {
        var (exit, stdout, _) = await RunSshRemoteShellAsync(
            contextName,
            RemotePathSetup + "id -un",
            cancellationToken,
            connectTimeoutSeconds: 30,
            allowTtyRetry: false);
        if (exit == 0)
        {
            var line = stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(line))
            {
                try
                {
                    return SanitizeSshToken(line, "user");
                }
                catch (ArgumentException)
                {
                    // Fall back to the configured SSH user when whoami returns an unexpected value.
                }
            }
        }

        return SanitizeSshToken(fallbackUser, "user");
    }

    private async Task<bool> RemoteUserHasPasswordlessSudoRuleAsync(
        string contextName,
        string user,
        CancellationToken cancellationToken)
    {
        var (exit, stdout, _) = await RunSshRemoteShellAsync(
            contextName,
            RemotePathSetup
                + $"/usr/bin/grep -R -- \"{user}\" /etc/sudoers /etc/sudoers.d 2>/dev/null "
                + "| /usr/bin/grep NOPASSWD | /usr/bin/head -1",
            cancellationToken,
            connectTimeoutSeconds: 30,
            allowTtyRetry: false);
        return exit == 0 && !string.IsNullOrWhiteSpace(stdout);
    }

    private async Task<(bool Passed, string Message)> TryInstallPlatformSudoersAsync(
        string contextName,
        string content,
        CancellationToken cancellationToken)
    {
        var sudoersB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        // Validate the fragment as the SSH user first so visudo does not need a TTY.
        // Then copy it with sudo -n. Ubuntu 24.04 Defaults use_pty makes sudo -n fail
        // without a PTY, so retry over ssh -tt and util-linux script(1).
        var installCommand = RemotePathSetup
            + "set -e; "
            + "TMP=$(mktemp); "
            + $"echo {ShellQuote(sudoersB64)} | /usr/bin/base64 -d > \"$TMP\"; "
            + $"sudo -n install -o root -g root -m 440 \"$TMP\" {PlatformSudoersFile}; "
            + "rm -f \"$TMP\"; "
            + $"if ! sudo -n /usr/sbin/visudo -c -f {PlatformSudoersFile}; then "
            + $"sudo -n rm -f {PlatformSudoersFile}; exit 1; fi";

        var (exit, stdout, stderr) = await RunSshBashWithSudoPtyFallbacksAsync(
            contextName,
            installCommand,
            cancellationToken,
            connectTimeoutSeconds: 60);

        if (exit != 0 || LooksLikeSudoFailure(stderr, stdout))
        {
            return (false, FormatRemoteShellError(stderr, stdout));
        }

        return (true, string.Empty);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunSshBashWithSudoPtyFallbacksAsync(
        string contextName,
        string bashCommand,
        CancellationToken cancellationToken,
        int connectTimeoutSeconds = 60)
    {
        var (exit, stdout, stderr) = await RunSshAsync(
            contextName,
            ["bash", "-c", bashCommand],
            cancellationToken,
            connectTimeoutSeconds);
        if (IsSuccessfulRemoteCommand(exit, stdout, stderr))
        {
            return (exit, stdout, stderr);
        }

        (exit, stdout, stderr) = await RunSshAsync(
            contextName,
            ["bash", "-c", bashCommand],
            cancellationToken,
            connectTimeoutSeconds,
            forceTty: true);
        if (IsSuccessfulRemoteCommand(exit, stdout, stderr))
        {
            return (exit, stdout, stderr);
        }

        var scriptCommand = "script -qefc " + ShellQuote("bash -c " + ShellQuote(bashCommand)) + " /dev/null";
        return await RunSshAsync(
            contextName,
            ["bash", "-c", scriptCommand],
            cancellationToken,
            connectTimeoutSeconds);
    }

    private static bool IsSuccessfulRemoteCommand(int exit, string stdout, string stderr)
        => exit == 0 && !LooksLikeSudoFailure(stderr, stdout);

    private async Task EnsureRemoteDockerGroupAsync(
        string contextName,
        string user,
        CancellationToken cancellationToken)
    {
        var (inGroupExit, _, _) = await RunSshRemoteShellAsync(
            contextName,
            RemotePathSetup + "id -nG | grep -qw docker",
            cancellationToken,
            connectTimeoutSeconds: 30,
            allowTtyRetry: false);
        if (inGroupExit == 0)
        {
            return;
        }

        var (groupExit, _, _) = await RunSshRemoteShellAsync(
            contextName,
            RemotePathSetup + "getent group docker >/dev/null",
            cancellationToken,
            connectTimeoutSeconds: 30,
            allowTtyRetry: false);
        if (groupExit != 0)
        {
            return;
        }

        await RunSshRemoteShellAsync(
            contextName,
            SudoNonInteractive($"usermod -aG docker {user}"),
            cancellationToken,
            connectTimeoutSeconds: 60,
            allowTtyRetry: true);
    }

    private Task<(int ExitCode, string StdOut, string StdErr)> TestNonInteractiveSudoAsync(
        string contextName,
        CancellationToken cancellationToken)
        => RunSshRemoteShellAsync(
            contextName,
            RemotePathSetup + "/usr/bin/sudo -n /usr/bin/true",
            cancellationToken,
            connectTimeoutSeconds: 30,
            allowTtyRetry: false);

    /// <summary>
    /// Ensures <c>sudo -n</c> works for subsequent setup commands. Uses one interactive TTY
    /// session only when needed to write <c>/etc/sudoers.d/99-azeroth-platform</c>.
    /// </summary>
    private async Task<(bool Passed, string Message)> EnsurePasswordlessSudoAsync(
        string contextName,
        string user,
        CancellationToken cancellationToken)
    {
        var remoteUser = await ResolveRemoteSshUserAsync(contextName, user, cancellationToken);

        var (testExit, _, _) = await TestNonInteractiveSudoAsync(contextName, cancellationToken);
        if (testExit == 0)
        {
            return (true, "Passwordless sudo is available.");
        }

        var hasExistingRule = await RemoteUserHasPasswordlessSudoRuleAsync(contextName, remoteUser, cancellationToken);
        string[] candidates = hasExistingRule
            ?
            [
                BuildPlatformSudoDefaultsContent(remoteUser),
                BuildMinimalPlatformSudoDefaultsContent(remoteUser),
            ]
            :
            [
                BuildFullPlatformSudoersContent(remoteUser),
                BuildMinimalPlatformSudoersContent(remoteUser),
            ];

        (bool Passed, string Message)? lastFailure = null;
        foreach (var content in candidates)
        {
            var installResult = await TryInstallPlatformSudoersAsync(contextName, content, cancellationToken);
            if (!installResult.Passed)
            {
                lastFailure = installResult;
                continue;
            }

            (testExit, _, _) = await TestNonInteractiveSudoAsync(contextName, cancellationToken);
            if (testExit == 0)
            {
                return (true, hasExistingRule
                    ? "Adjusted sudo defaults for non-interactive automation."
                    : "Configured passwordless sudo for platform setup.");
            }
        }

        var (_, diagOut, diagErr) = await RunSshRemoteShellAsync(
            contextName,
            RemotePathSetup
                + "/usr/bin/sudo -n /usr/bin/true 2>&1; "
                + "/usr/bin/sudo -l -n 2>&1 | /usr/bin/head -5",
            cancellationToken,
            connectTimeoutSeconds: 30,
            allowTtyRetry: false);
        var detail = FormatRemoteShellError(diagErr, diagOut);
        if (lastFailure is { Passed: false, Message: var installMessage }
            && !string.IsNullOrWhiteSpace(installMessage))
        {
            detail = $"{installMessage} {detail}".Trim();
        }

        return (false,
            $"Could not enable non-interactive sudo for '{remoteUser}'. {detail} "
            + $"If needed, SSH in and run: echo '{remoteUser} ALL=(ALL) NOPASSWD:ALL' | sudo tee {PlatformSudoersFile}");
    }

    private static string SummarizeRemoteOutput(string stdout, string fallback)
    {
        var line = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static value => IsUsefulRemoteOutputLine(value));
        return string.IsNullOrWhiteSpace(line) ? $"{fallback} completed." : line;
    }

    private static string SummarizeAptOutput(string stdout, string stderr, string fallback)
    {
        var lines = (stdout + "\n" + stderr)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var setupLine = lines.LastOrDefault(static line =>
            line.Contains("Setting up docker", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Processing triggers", StringComparison.OrdinalIgnoreCase)
            || line.Contains("already the newest version", StringComparison.OrdinalIgnoreCase)
            || line.Contains("is already installed", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(setupLine))
        {
            return setupLine;
        }

        var usefulLine = lines.LastOrDefault(static line => IsUsefulRemoteOutputLine(line));
        return string.IsNullOrWhiteSpace(usefulLine) ? $"{fallback} completed." : usefulLine;
    }

    private static string ExtractDockerVersion(string stdout)
    {
        var lines = (stdout ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines.Reverse())
        {
            if (LooksLikeDockerVersion(line))
            {
                return line;
            }
        }

        return lines.LastOrDefault(IsUsefulRemoteOutputLine) ?? string.Empty;
    }

    private static bool LooksLikeDockerVersion(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 32 || !char.IsAsciiDigit(line[0]))
        {
            return false;
        }

        var dot = line.IndexOf('.');
        return dot > 0 && dot < line.Length - 1 && char.IsAsciiDigit(line[dot + 1]);
    }

    private static bool IsUsefulRemoteOutputLine(string value)
        => !string.IsNullOrWhiteSpace(value)
           && !value.StartsWith("SHELL=", StringComparison.Ordinal)
           && !value.StartsWith("PWD=", StringComparison.Ordinal)
           && !value.StartsWith("HOME=", StringComparison.Ordinal)
           && !value.StartsWith("LOGNAME=", StringComparison.Ordinal)
           && !value.StartsWith("XDG_SESSION_TYPE=", StringComparison.Ordinal)
           && !value.StartsWith("XDG_RUNTIME_DIR=", StringComparison.Ordinal)
           && !value.StartsWith("declare -x ", StringComparison.Ordinal)
           && !value.StartsWith("declare -", StringComparison.Ordinal)
           && !value.StartsWith("DBUS_SESSION_BUS_ADDRESS=", StringComparison.Ordinal)
           && !value.StartsWith("Reading package lists", StringComparison.OrdinalIgnoreCase)
           && !value.StartsWith("Building dependency tree", StringComparison.OrdinalIgnoreCase)
           && !value.StartsWith("Reading state information", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeMissingDockerIoPackage(string stderr, string stdout)
    {
        var message = $"{stderr} {stdout}";
        return message.Contains("Unable to locate package docker.io", StringComparison.OrdinalIgnoreCase)
               || message.Contains("has no installation candidate", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(bool Passed, string Message)> TryInstallDockerIoWithUniverseAsync(
        string contextName,
        List<RemotePrerequisiteCheckDto> steps,
        CancellationToken cancellationToken)
    {
        var enableCommands = new (string Label, string Command)[]
        {
            ("Install prerequisites for universe repo", SudoAptGet("install -y software-properties-common")),
            ("Enable Ubuntu universe repository", SudoNonInteractive("add-apt-repository -y universe")),
            ("Refresh package lists", SudoAptGet("update -qq")),
        };

        foreach (var (label, command) in enableCommands)
        {
            var (exit, stdout, stderr) = await RunSshRemoteShellAsync(
                contextName,
                command,
                cancellationToken,
                connectTimeoutSeconds: 300,
                allowTtyRetry: true);
            if (exit != 0)
            {
                return (false, FormatRemoteShellError(stderr, stdout));
            }
        }

        var (installExit, installOut, installErr) = await RunSshRemoteShellAsync(
            contextName,
            SudoAptGet("install -y docker.io"),
            cancellationToken,
            connectTimeoutSeconds: 600,
            allowTtyRetry: true);
        steps.Add(new RemotePrerequisiteCheckDto
        {
            Name = "Install Docker Engine",
            Passed = installExit == 0,
            Message = installExit == 0
                ? SummarizeAptOutput(installOut, installErr, "Install Docker Engine")
                : FormatRemoteShellError(installErr, installOut)
        });

        if (installExit != 0 || !await IsRemoteDockerIoPresentAsync(contextName, cancellationToken))
        {
            if (LooksLikeDockerIoAlreadyInstalled(installErr, installOut))
            {
                return (true, SummarizeAptOutput(installOut, installErr, "Install Docker Engine"));
            }

            return (false, "Could not install docker.io. Enable the Ubuntu universe repository on this host, then retry.");
        }

        return (true, "Installed docker.io after enabling the universe repository.");
    }

    private static bool LooksLikeDockerIoAlreadyInstalled(string stderr, string stdout)
    {
        var message = $"{stderr} {stdout}";
        return message.Contains("already the newest version", StringComparison.OrdinalIgnoreCase)
               || message.Contains("is already installed", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> RemoteDockerIoPackageInstalledAsync(
        string contextName,
        CancellationToken cancellationToken)
    {
        var (exit, _, _) = await RunSshRemoteShellAsync(
            contextName,
            "dpkg-query -s docker.io 2>/dev/null | grep -q '^Status: install ok installed'",
            cancellationToken,
            connectTimeoutSeconds: 30,
            allowTtyRetry: false);
        return exit == 0;
    }

    private async Task<bool> IsRemoteDockerIoPresentAsync(
        string contextName,
        CancellationToken cancellationToken)
    {
        if (await RemoteDockerIoPackageInstalledAsync(contextName, cancellationToken))
        {
            return true;
        }

        var (cliExit, _, _) = await RunSshRemoteShellAsync(
            contextName,
            "command -v docker >/dev/null 2>&1",
            cancellationToken,
            connectTimeoutSeconds: 30,
            allowTtyRetry: false);
        return cliExit == 0 && await RemoteDockerServiceUnitExistsAsync(contextName, cancellationToken);
    }

    private async Task<bool> RemoteDockerServiceUnitExistsAsync(
        string contextName,
        CancellationToken cancellationToken)
    {
        var (fileExit, _, _) = await RunSshRemoteShellAsync(
            contextName,
            "test -f /usr/lib/systemd/system/docker.service -o -f /lib/systemd/system/docker.service",
            cancellationToken,
            connectTimeoutSeconds: 30,
            allowTtyRetry: false);
        if (fileExit == 0)
        {
            return true;
        }

        var (unitExit, _, _) = await RunSshRemoteShellAsync(
            contextName,
            "systemctl cat docker.service >/dev/null 2>&1 || systemctl cat docker >/dev/null 2>&1",
            cancellationToken,
            connectTimeoutSeconds: 30,
            allowTtyRetry: false);
        return unitExit == 0;
    }

    private async Task<bool> RemoteDockerSocketUnitExistsAsync(
        string contextName,
        CancellationToken cancellationToken)
    {
        var (fileExit, _, _) = await RunSshRemoteShellAsync(
            contextName,
            "test -f /usr/lib/systemd/system/docker.socket -o -f /lib/systemd/system/docker.socket",
            cancellationToken,
            connectTimeoutSeconds: 30,
            allowTtyRetry: false);
        if (fileExit == 0)
        {
            return true;
        }

        var (unitExit, _, _) = await RunSshRemoteShellAsync(
            contextName,
            "systemctl cat docker.socket >/dev/null 2>&1",
            cancellationToken,
            connectTimeoutSeconds: 30,
            allowTtyRetry: false);
        return unitExit == 0;
    }

    private async Task<bool> IsRemoteDockerServiceRunningAsync(
        string contextName,
        CancellationToken cancellationToken)
    {
        const string checkCommand =
            "systemctl is-active docker 2>/dev/null | grep -qx active "
            + "|| systemctl is-active docker.service 2>/dev/null | grep -qx active "
            + "|| systemctl is-active docker.socket 2>/dev/null | grep -qx active";

        foreach (var command in new[] { checkCommand, SudoNonInteractive(checkCommand) })
        {
            var (exit, _, _) = await RunSshRemoteShellAsync(
                contextName,
                command,
                cancellationToken,
                connectTimeoutSeconds: 30,
                allowTtyRetry: false);
            if (exit == 0)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> RemoteDockerSystemdUnitExistsAsync(
        string contextName,
        CancellationToken cancellationToken)
    {
        if (await IsRemoteDockerServiceRunningAsync(contextName, cancellationToken))
        {
            return true;
        }

        return await RemoteDockerServiceUnitExistsAsync(contextName, cancellationToken)
               || await RemoteDockerSocketUnitExistsAsync(contextName, cancellationToken);
    }

    private async Task<string> GetRemoteDockerInstallDiagnosticsAsync(
        string contextName,
        CancellationToken cancellationToken)
    {
        var (exit, stdout, stderr) = await RunSshRemoteShellAsync(
            contextName,
            "dpkg-query -W docker.io 2>&1; "
                + "ls -l /usr/lib/systemd/system/docker.service /lib/systemd/system/docker.service 2>&1; "
                + "systemctl cat docker.service 2>&1 | head -5",
            cancellationToken,
            connectTimeoutSeconds: 60,
            allowTtyRetry: false);
        if (exit == 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            return TrimRemoteError(stderr, stdout);
        }

        return TrimRemoteError(stderr, stdout);
    }

    private static bool IsDockerSystemdUnitActive(string stdout)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Equals("active", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<string> GetRemoteDockerServiceDiagnosticsAsync(
        string contextName,
        CancellationToken cancellationToken)
    {
        var (statusExit, statusOut, statusErr) = await RunSshRemoteShellAsync(
            contextName,
            SudoNonInteractive("systemctl status docker.service docker.socket --no-pager -l 2>&1 | head -25"),
            cancellationToken,
            connectTimeoutSeconds: 60,
            allowTtyRetry: false);
        if (statusExit == 0 && !string.IsNullOrWhiteSpace(statusOut))
        {
            return TrimRemoteError(statusErr, statusOut);
        }

        var (journalExit, journalOut, journalErr) = await RunSshRemoteShellAsync(
            contextName,
            SudoNonInteractive("journalctl -u docker.service -u docker.socket --no-pager -n 15 2>&1"),
            cancellationToken,
            connectTimeoutSeconds: 60,
            allowTtyRetry: false);
        if (journalExit == 0 && !string.IsNullOrWhiteSpace(journalOut))
        {
            return TrimRemoteError(journalErr, journalOut);
        }

        return TrimRemoteError(statusErr, statusOut);
    }

    private static bool IsTransientSshFailure(string stderr, string stdout)
    {
        var message = $"{stderr} {stdout}";
        return message.Contains("Connection closed", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Connection reset", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase)
               || message.Contains("kex_exchange_identification", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Starts and enables the Docker systemd units after package install, then waits until the engine API responds.
    /// Ubuntu's docker.io package uses socket activation — docker.service may stay inactive until first use.
    /// </summary>
    private async Task<(bool Passed, string Message)> EnsureRemoteDockerServiceAsync(
        string contextName,
        CancellationToken cancellationToken)
    {
        var (infoReady, version, _) = await TryRemoteDockerInfoAsync(contextName, cancellationToken);
        if (infoReady)
        {
            return (true, string.IsNullOrWhiteSpace(version)
                ? "Docker engine is already responding."
                : $"Docker {version} is already running.");
        }

        var (_, activeOut, _) = await RunSshRemoteShellAsync(
            contextName,
            "systemctl is-active docker.service docker.socket 2>/dev/null",
            cancellationToken,
            connectTimeoutSeconds: 60,
            allowTtyRetry: false);
        if (IsDockerSystemdUnitActive(activeOut))
        {
            (infoReady, version, _) = await TryRemoteDockerInfoAsync(contextName, cancellationToken);
            if (infoReady)
            {
                return (true, string.IsNullOrWhiteSpace(version)
                    ? "Docker engine is responding."
                    : $"Docker {version} is running.");
            }

            return (true, "Docker service is active (systemctl).");
        }

        if (!await RemoteDockerSystemdUnitExistsAsync(contextName, cancellationToken))
        {
            return (false, "Docker systemd unit is not installed. Install the docker.io package first.");
        }

        await RunSshRemoteShellAsync(
            contextName,
            SudoNonInteractive("systemctl daemon-reload"),
            cancellationToken,
            connectTimeoutSeconds: 60,
            allowTtyRetry: false);

        var hasSocket = await RemoteDockerSocketUnitExistsAsync(contextName, cancellationToken);
        var hasService = await RemoteDockerServiceUnitExistsAsync(contextName, cancellationToken);
        var unitsToEnable = new List<string>();
        if (hasSocket)
        {
            unitsToEnable.Add("docker.socket");
        }

        if (hasService)
        {
            unitsToEnable.Add("docker.service");
        }

        var (enableExit, _, enableErr) = await RunSshRemoteShellAsync(
            contextName,
            SudoNonInteractive($"systemctl enable {string.Join(' ', unitsToEnable)}"),
            cancellationToken,
            connectTimeoutSeconds: 120,
            allowTtyRetry: false);
        if (enableExit != 0)
        {
            return (false, FormatRemoteShellError(enableErr, string.Empty));
        }

        if (hasSocket)
        {
            var (socketStartExit, _, socketStartErr) = await RunSshRemoteShellAsync(
                contextName,
                SudoNonInteractive("systemctl start docker.socket"),
                cancellationToken,
                connectTimeoutSeconds: 120,
                allowTtyRetry: false);
            if (socketStartExit != 0)
            {
                return (false, FormatRemoteShellError(socketStartErr, string.Empty));
            }
        }

        if (hasService)
        {
            await RunSshRemoteShellAsync(
                contextName,
                SudoNonInteractive("systemctl start --no-block docker.service"),
                cancellationToken,
                connectTimeoutSeconds: 120,
                allowTtyRetry: false);
        }

        for (var attempt = 0; attempt < 12; attempt++)
        {
            (infoReady, version, _) = await TryRemoteDockerInfoAsync(contextName, cancellationToken);
            if (infoReady)
            {
                return (true, string.IsNullOrWhiteSpace(version)
                    ? "Docker engine is responding."
                    : $"Docker {version} is running.");
            }

            if (await IsRemoteDockerServiceRunningAsync(contextName, cancellationToken))
            {
                return (true, "Docker service is active (systemctl).");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        if (await IsRemoteDockerServiceRunningAsync(contextName, cancellationToken))
        {
            return (true, "Docker service is active (systemctl).");
        }

        var diagnostics = await GetRemoteDockerServiceDiagnosticsAsync(contextName, cancellationToken);
        return (false, string.IsNullOrWhiteSpace(diagnostics)
            ? "Docker engine did not respond after starting docker.service."
            : $"Docker engine did not respond after start. {diagnostics}");
    }

    private static string TrimRemoteError(string stderr, string stdout)
    {
        var message = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        message = (message ?? string.Empty).Trim();
        if (message.Length > 500)
        {
            message = message[..500] + "…";
        }

        return string.IsNullOrWhiteSpace(message) ? "Command failed." : message;
    }

    private static string FormatRemoteShellError(string stderr, string stdout)
    {
        var message = TrimRemoteError(stderr, stdout);
        if (message.Contains("a password is required", StringComparison.OrdinalIgnoreCase)
            || message.Contains("sorry, you must have a tty", StringComparison.OrdinalIgnoreCase))
        {
            return message + " The SSH user needs passwordless sudo (NOPASSWD) for setup commands to run non-interactively.";
        }

        if (message.Contains("Connection closed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Connection reset", StringComparison.OrdinalIgnoreCase))
        {
            return message + " Close the in-browser terminal if it is open, wait a few seconds, and run setup again.";
        }

        if (message.Contains("usage: sudo", StringComparison.OrdinalIgnoreCase)
            || message.Contains("expected one of these actions", StringComparison.OrdinalIgnoreCase))
        {
            return "sudo rejected the command on this host. Ensure the SSH user has passwordless sudo, or run the equivalent apt/ufw commands manually over SSH.";
        }

        return message;
    }

    private static bool ShellCommandUsesSudo(string command)
        => command.Contains("sudo", StringComparison.Ordinal);

    private static bool LooksLikeSudoFailure(string stderr, string stdout)
    {
        var message = $"{stderr} {stdout}";
        return message.Contains("a password is required", StringComparison.OrdinalIgnoreCase)
               || message.Contains("sorry, you must have a tty", StringComparison.OrdinalIgnoreCase)
               || message.Contains("sudo: a terminal is required", StringComparison.OrdinalIgnoreCase)
               || message.Contains("usage: sudo", StringComparison.OrdinalIgnoreCase)
               || message.Contains("expected one of these actions", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunSshRemoteShellAsync(
        string contextName,
        string shellCommand,
        CancellationToken cancellationToken,
        int connectTimeoutSeconds = 30,
        bool allowTtyRetry = true)
    {
        const int maxAttempts = 3;
        (int ExitCode, string StdOut, string StdErr) lastResult = (1, string.Empty, string.Empty);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            lastResult = await RunSshRemoteShellOnceAsync(
                contextName,
                shellCommand,
                cancellationToken,
                connectTimeoutSeconds,
                allowTtyRetry);

            if (lastResult.ExitCode == 0 || !IsTransientSshFailure(lastResult.StdErr, lastResult.StdOut))
            {
                return lastResult;
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
            }
        }

        return lastResult;
    }

    private async Task<RemoteHostOs?> DetectRemoteHostOsAsync(
        string contextName,
        CancellationToken cancellationToken)
    {
        var uname = await RunSshAsync(
            contextName,
            ["uname", "-s"],
            cancellationToken,
            connectTimeoutSeconds: 12,
            connectionAttempts: 1,
            serverAliveInterval: 5,
            serverAliveCountMax: 2);
        var fromUname = RemoteHostOsProbe.Interpret(uname.StdOut, windowsOsEnv: null);
        if (fromUname is not null)
        {
            return fromUname;
        }

        var fallback = await RunSshPowerShellAsync(
            contextName,
            "Write-Output $env:OS",
            cancellationToken,
            connectTimeoutSeconds: 12);
        if (fallback.ExitCode != 0)
        {
            fallback = await RunSshAsync(
                contextName,
                ["cmd.exe", "/c", "echo %OS%"],
                cancellationToken,
                connectTimeoutSeconds: 12,
                connectionAttempts: 1,
                serverAliveInterval: 5,
                serverAliveCountMax: 2);
        }

        return RemoteHostOsProbe.Interpret(uname.StdOut, fallback.StdOut);
    }

    private Task<(int ExitCode, string StdOut, string StdErr)> RunSshPowerShellAsync(
        string contextName,
        string script,
        CancellationToken cancellationToken,
        int connectTimeoutSeconds = 90)
    {
        var wrapped =
            "$ProgressPreference='SilentlyContinue'; $WarningPreference='SilentlyContinue'; "
            + (script ?? string.Empty);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
        return RunSshAsync(
            contextName,
            ["powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
            cancellationToken,
            connectTimeoutSeconds);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunSshRemoteShellOnceAsync(
        string contextName,
        string shellCommand,
        CancellationToken cancellationToken,
        int connectTimeoutSeconds,
        bool allowTtyRetry)
    {
        var effectiveCommand = ShellCommandUsesSudo(shellCommand)
            ? RemotePathSetup + shellCommand
            : shellCommand;

        var (exit, stdout, stderr) = await RunSshAsync(
            contextName,
            ["bash", "-c", effectiveCommand],
            cancellationToken,
            connectTimeoutSeconds);

        if (exit == 0
            || !allowTtyRetry
            || !ShellCommandUsesSudo(shellCommand)
            || !LooksLikeSudoFailure(stderr, stdout))
        {
            return (exit, stdout, stderr);
        }

        // Ubuntu 24.04 Defaults use_pty: sudo -n needs a PTY. Keep -n so BatchMode never
        // hangs on a password prompt. Do not use this path for systemctl start (it can
        // drop the SSH session when the service comes up).
        return await RunSshAsync(
            contextName,
            ["bash", "-c", effectiveCommand],
            cancellationToken,
            connectTimeoutSeconds,
            forceTty: true);
    }

    /// <summary>
    /// SSRF allowlist check: returns true when <paramref name="host"/> resolves to a loopback or
    /// link-local address (the latter includes the 169.254.169.254 cloud-metadata endpoint). Hostnames are
    /// resolved so a name pointing at a blocked IP (or DNS rebinding) is also caught. Private LAN ranges
    /// are deliberately allowed — real remote Docker engines commonly live there. On resolution failure we
    /// return false and let the subsequent SSH attempt fail normally.
    /// </summary>
    private static async Task<bool> IsDisallowedRemoteHostAsync(string host, CancellationToken cancellationToken)
    {
        IReadOnlyList<IPAddress> addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            }
            catch
            {
                return false;
            }
        }

        foreach (var ip in addresses)
        {
            if (IPAddress.IsLoopback(ip))
            {
                return true;
            }

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254)
                {
                    return true; // IPv4 link-local, incl. 169.254.169.254 cloud metadata
                }
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv6LinkLocal)
            {
                return true;
            }
        }

        return false;
    }

    public async Task ShipImageAsync(ManagedStackEntity stack, string imageTag, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imageTag))
        {
            return;
        }

        var contextName = await EnsureContextAsync(stack, cancellationToken);

        // Stream the image over SSH without a temp file: docker save <tag> | docker --context <ctx> load.
        var command = $"docker save {imageTag} | docker --context {contextName} load";
        var (exit, _, stderr) = await RunShellAsync(command, cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Failed to ship image '{imageTag}' to remote engine: {stderr}");
        }

        _logger.LogInformation("Shipped image {Image} to remote engine for stack {StackId}.", imageTag, stack.Id);
    }

    public async Task<bool> RemoteImageExistsAsync(
        ManagedStackEntity stack,
        string imageTag,
        CancellationToken cancellationToken = default)
    {
        if (stack.DeploymentTarget != DeploymentTarget.External || string.IsNullOrWhiteSpace(imageTag))
        {
            return false;
        }

        var contextName = await EnsureContextAsync(stack, cancellationToken);
        if (await RemoteImageExistsOnContextAsync(contextName, imageTag, cancellationToken))
        {
            return true;
        }

        var slash = imageTag.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
        {
            return false;
        }

        var localhostTag = $"localhost/{imageTag}";
        return await RemoteImageExistsOnContextAsync(contextName, localhostTag, cancellationToken);
    }

    private static async Task<bool> RemoteImageExistsOnContextAsync(
        string contextName,
        string imageTag,
        CancellationToken cancellationToken)
    {
        var quotedTag = imageTag.Contains('"') ? $"\"{imageTag.Replace("\"", "\\\"")}\"" : imageTag;
        var (exitCode, stdout, _) = await RunAsync(
            "docker",
            $"--context {contextName} images -q {quotedTag}",
            cancellationToken,
            throwOnError: false);
        return exitCode == 0 && !string.IsNullOrWhiteSpace(stdout);
    }

    public async Task SeedVolumeFromArchiveStreamAsync(
        ManagedStackEntity stack,
        string volumeName,
        Stream archiveStream,
        CancellationToken cancellationToken = default)
    {
        if (stack.DeploymentTarget != DeploymentTarget.External)
        {
            throw new InvalidOperationException("SeedVolumeFromArchiveStreamAsync is only valid for external stacks.");
        }

        await EnsureVolumeExistsAsync(stack, volumeName, cancellationToken);
        await ClearVolumeContentsAsync(stack, volumeName, cancellationToken);

        var workVolume = $"acore-client-upload-{Guid.NewGuid():N}";
        await EnsureVolumeExistsAsync(stack, workVolume, cancellationToken);

        try
        {
            var contextName = await EnsureContextAsync(stack, cancellationToken);
            _logger.LogInformation(
                "Streaming client archive to remote work volume {WorkVolume} for stack {StackId}.",
                workVolume,
                stack.Id);

            var uploadCommand =
                $"docker run --rm -i -v {workVolume}:/work alpine:3.20 sh -c {ShellQuote("cat > /work/upload.archive")}";
            var (uploadExit, _, uploadErr) = await StreamStdinToRemoteShellAsync(
                contextName, uploadCommand, archiveStream, cancellationToken);
            if (uploadExit != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to stream the client archive to the remote engine: {uploadErr.Trim()}");
            }

            await ExtractClientArchiveOnRemoteAsync(contextName, workVolume, volumeName, cancellationToken);
            var contextArg = await ContextArgAsync(stack, cancellationToken);
            await VerifyVolumeNotEmptyAsync(contextArg, volumeName, cancellationToken);
            _logger.LogInformation(
                "Client archive extracted into remote volume {Volume} for stack {StackId}.",
                volumeName,
                stack.Id);
        }
        finally
        {
            await RemoveVolumeAsync(stack, workVolume, cancellationToken);
        }
    }

    public async Task WriteVolumeFileFromStreamAsync(
        ManagedStackEntity stack,
        string volumeName,
        string relativePath,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var safeRelative = SanitizeVolumeSubdir(relativePath);
        var destFile = $"/dest/{safeRelative}";
        var destDir = Path.GetDirectoryName(destFile.Replace('\\', '/'))?.Replace('\\', '/') ?? "/dest";
        var shell = destDir == "/dest"
            ? $"cat > {destFile}"
            : $"mkdir -p {destDir} && cat > {destFile}";

        if (stack.DeploymentTarget == DeploymentTarget.External)
        {
            var contextName = await EnsureContextAsync(stack, cancellationToken);
            var remoteCommand =
                $"docker run --rm -i -v {volumeName}:/dest alpine:3.20 sh -c {ShellQuote(shell)}";
            var (exit, _, stderr) = await StreamStdinToRemoteShellAsync(
                contextName, remoteCommand, content, cancellationToken);
            if (exit != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to write '{safeRelative}' into remote volume '{volumeName}': {stderr.Trim()}");
            }

            return;
        }

        var contextArg = await ContextArgAsync(stack, cancellationToken);
        var localCommand =
            $"docker {contextArg}run --rm -i -v {volumeName}:/dest alpine:3.20 sh -c {ShellQuote(shell)}";
        var (localExit, _, localErr) = await StreamStdinToShellAsync(localCommand, content, cancellationToken);
        if (localExit != 0)
        {
            throw new InvalidOperationException(
                $"Failed to write '{safeRelative}' into volume '{volumeName}': {localErr.Trim()}");
        }
    }

    public async Task SeedVolumeAsync(ManagedStackEntity stack, string volumeName, string localSourceDir, CancellationToken cancellationToken = default)
    {
        var contextArg = await ContextArgAsync(stack, cancellationToken);
        var local = stack.DeploymentTarget != DeploymentTarget.External;
        var contextName = local ? null : GetContextName(stack.Id);
        await SeedVolumeCoreAsync(contextArg, contextName, local, volumeName, localSourceDir, cancellationToken);
    }

    public Task SeedLocalVolumeAsync(string volumeName, string localSourceDir, CancellationToken cancellationToken = default)
        => SeedVolumeCoreAsync(string.Empty, contextName: null, local: true, volumeName, localSourceDir, cancellationToken);

    private async Task SeedVolumeCoreAsync(
        string contextArg,
        string? contextName,
        bool local,
        string volumeName,
        string localSourceDir,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(localSourceDir))
        {
            return;
        }

        // Ensure the named volume exists on the engine.
        await RunAsync("docker", $"{contextArg}volume create {volumeName}", cancellationToken, throwOnError: false);

        if (!DirectoryHasContent(localSourceDir))
        {
            _logger.LogDebug(
                "Source {Source} is empty; ensured volume {Volume} exists without tar seed.",
                localSourceDir,
                volumeName);
            return;
        }

        // Fast path for the local daemon: when the source lives inside the manager's own data volume, do
        // a daemon-side volume-to-volume copy (no multi-GB streaming through the CLI).
        if (local && await TryDaemonSideCopyAsync(volumeName, localSourceDir, cancellationToken))
        {
            _logger.LogInformation("Seeded local volume {Volume} (daemon-side copy).", volumeName);
            return;
        }

        // Remote engines: pipe tar over SSH into `docker run -i` on the host. Docker context streaming
        // (`tar | docker --context … run -i`) often drops stdin before it reaches the remote container.
        if (!local && !string.IsNullOrWhiteSpace(contextName))
        {
            var (sshExit, _, sshErr) = await SeedVolumeViaSshAsync(contextName, volumeName, localSourceDir, cancellationToken);
            if (sshExit != 0)
            {
                throw new InvalidOperationException($"Failed to seed volume '{volumeName}': {sshErr}");
            }

            await VerifyVolumeNotEmptyAsync(contextArg, volumeName, cancellationToken);
            _logger.LogInformation("Seeded volume {Volume} (SSH stream).", volumeName);
            return;
        }

        // Local fallback: stream tar into a throwaway container on the manager daemon.
        var srcQuoted = ShellQuote(localSourceDir);
        var command =
            $"tar -C {srcQuoted} -cf - . | docker {contextArg}run --rm -i " +
            $"-v {volumeName}:/dest alpine:3.20 sh -c \"cd /dest && tar -xf -\"";

        var (exit, _, stderr) = await RunShellAsync(command, cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Failed to seed volume '{volumeName}': {stderr}");
        }

        await VerifyVolumeNotEmptyAsync(contextArg, volumeName, cancellationToken);

        _logger.LogInformation("Seeded volume {Volume}.", volumeName);
    }

    private static bool DirectoryHasContent(string directory)
        => Directory.EnumerateFileSystemEntries(directory).Any();

    /// <summary>
    /// Streams a local directory tar over SSH into <c>docker run -i</c> on the remote host so stdin
    /// reaches the extract container reliably (unlike piping through <c>docker --context</c>).
    /// </summary>
    private Task<(int ExitCode, string StdOut, string StdErr)> SeedVolumeViaSshAsync(
        string contextName,
        string volumeName,
        string localSourceDir,
        CancellationToken cancellationToken)
    {
        var srcQuoted = ShellQuote(localSourceDir);
        var sshConfigQuoted = ShellQuote(Path.Combine(GetSshDir(), "config"));
        var remoteDocker = ShellQuote(
            $"docker run --rm -i -v {volumeName}:/dest alpine:3.20 sh -c \"cd /dest && tar -xf -\"");
        var command =
            $"tar -C {srcQuoted} -cf - . | ssh -F {sshConfigQuoted} -o BatchMode=yes -o ConnectTimeout=120 {contextName} {remoteDocker}";
        return RunShellAsync(command, cancellationToken);
    }

    private async Task VerifyVolumeNotEmptyAsync(string contextArg, string volumeName, CancellationToken cancellationToken)
    {
        // Guard against tar streams where stdin never reaches the container (empty volume, exit 0).
        var script = "find /dest -mindepth 1 -maxdepth 1 2>/dev/null | head -1 | grep -q .";
        var (exit, _, stderr) = await RunAlpineInVolumeAsync(
            contextArg, volumeName, readOnly: true, mountAt: "/dest", workDir: null, script, cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"Volume '{volumeName}' appears empty after seeding. " +
                "The stack Docker engine may not accept streamed uploads; check connectivity and try again. " +
                stderr.Trim());
        }
    }

    public async Task FetchVolumeAsync(ManagedStackEntity stack, string volumeName, string localDestinationDir, CancellationToken cancellationToken = default)
    {
        var contextArg = await ContextArgAsync(stack, cancellationToken);
        await FetchVolumeCoreAsync(contextArg, volumeName, localDestinationDir, cancellationToken);
    }

    public Task FetchLocalVolumeAsync(string volumeName, string localDestinationDir, CancellationToken cancellationToken = default)
        => FetchVolumeCoreAsync(string.Empty, volumeName, localDestinationDir, cancellationToken);

    public async Task CopyFileToContainerAsync(
        ManagedStackEntity stack,
        string containerName,
        string localSourcePath,
        string containerDestinationPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localSourcePath))
        {
            return;
        }

        var contextArg = await ContextArgAsync(stack, cancellationToken);
        var (psExit, psOut, _) = await RunAsync(
            "docker",
            $"{contextArg}ps --filter name=^{containerName}$ --format {{{{.Names}}}}",
            cancellationToken,
            throwOnError: false);
        if (psExit != 0 || string.IsNullOrWhiteSpace(psOut))
        {
            return;
        }

        var destination = $"{containerName}:{containerDestinationPath}";
        var command = $"docker {contextArg}cp {ShellQuote(localSourcePath)} {ShellQuote(destination)}";
        var (exit, _, stderr) = await RunShellAsync(command, cancellationToken);
        if (exit != 0)
        {
            _logger.LogWarning(
                "Failed to copy {Source} into {Container}:{Path}: {Err}",
                localSourcePath,
                containerName,
                containerDestinationPath,
                stderr);
            return;
        }

        _logger.LogInformation(
            "Copied live armory file {Source} into {Container}:{Path}.",
            localSourcePath,
            containerName,
            containerDestinationPath);
    }

    public async Task RemoveLocalVolumeAsync(string volumeName, CancellationToken cancellationToken = default)
        => await RunAsync("docker", $"volume rm -f {volumeName}", cancellationToken, throwOnError: false);

    public async Task<bool> ExtractImageDirAsync(string image, string imagePath, string localDestinationDir, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(localDestinationDir);

        // Use an explicit, clearly-prefixed name so this throwaway container is never mistaken for a
        // stack container and can always be force-removed (even if a previous run was interrupted).
        var containerName = $"azp-cfg-extract-{Guid.NewGuid():N}";
        await RunAsync("docker", $"rm -f {containerName}", cancellationToken, throwOnError: false);

        var (createExit, _, createErr) = await RunAsync("docker", $"create --name {containerName} {image}", cancellationToken, throwOnError: false);
        if (createExit != 0)
        {
            _logger.LogWarning("docker create {Image} failed while extracting {Path}: {Err}", image, imagePath, createErr);
            return false;
        }

        try
        {
            // "docker cp <name>:<path>/. <dest>" copies the directory *contents* into dest. RunShellAsync
            // so the paths are shell-quoted (dest may contain spaces on some hosts).
            var src = $"{containerName}:{imagePath.TrimEnd('/')}/.";
            var command = $"docker cp {ShellQuote(src)} {ShellQuote(localDestinationDir)}";
            var (cpExit, _, cpErr) = await RunShellAsync(command, cancellationToken);
            if (cpExit != 0)
            {
                _logger.LogWarning("docker cp {Image}:{Path} failed: {Err}", image, imagePath, cpErr);
                return false;
            }

            return true;
        }
        finally
        {
            await RunAsync("docker", $"rm -f {containerName}", cancellationToken, throwOnError: false);
        }
    }

    private async Task FetchVolumeCoreAsync(string contextArg, string volumeName, string localDestinationDir, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(localDestinationDir);

        // Inverse of SeedVolumeAsync: a throwaway container tars the volume to stdout, and we extract it
        // into the local destination.
        var destQuoted = ShellQuote(localDestinationDir);
        var command =
            $"docker {contextArg}run --rm -i -v {volumeName}:/src alpine:3.20 sh -c \"cd /src && tar -cf - .\" " +
            $"| tar -C {destQuoted} -xf -";

        var (exit, _, stderr) = await RunShellAsync(command, cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Failed to fetch volume '{volumeName}': {stderr}");
        }

        _logger.LogInformation("Fetched volume {Volume}.", volumeName);
    }

    public async Task<bool> VolumeExistsAsync(ManagedStackEntity stack, string volumeName, CancellationToken cancellationToken = default)
    {
        var contextArg = await ContextArgAsync(stack, cancellationToken);
        var (exit, _, _) = await RunAsync(
            "docker",
            $"{contextArg}volume inspect {volumeName}",
            cancellationToken,
            throwOnError: false);
        return exit == 0;
    }

    public async Task RemoveVolumeAsync(ManagedStackEntity stack, string volumeName, CancellationToken cancellationToken = default)
    {
        var contextArg = await ContextArgAsync(stack, cancellationToken);
        await RunAsync("docker", $"{contextArg}volume rm -f {volumeName}", cancellationToken, throwOnError: false);
    }

    public async Task DeleteVolumePathsAsync(ManagedStackEntity stack, string volumeName, IEnumerable<string> relativePaths, CancellationToken cancellationToken = default)
        => await DeleteVolumePathsCoreAsync(stack, volumeName, relativePaths, cancellationToken);

    public Task DeleteLocalVolumePathsAsync(string volumeName, IEnumerable<string> relativePaths, CancellationToken cancellationToken = default)
        => DeleteVolumePathsCoreAsync(stack: null, volumeName, relativePaths, cancellationToken);

    private async Task DeleteVolumePathsCoreAsync(
        ManagedStackEntity? stack,
        string volumeName,
        IEnumerable<string> relativePaths,
        CancellationToken cancellationToken)
    {
        // Normalise to safe, volume-relative paths: forward slashes, no leading slash, no traversal.
        var paths = (relativePaths ?? Enumerable.Empty<string>())
            .Select(p => (p ?? string.Empty).Replace('\\', '/').Trim().Trim('/'))
            .Where(p => p.Length > 0 && !p.StartsWith('/') && !p.Split('/').Contains("..") && !ContainsShellMeta(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (paths.Count == 0)
        {
            return;
        }

        var contextArg = stack is null ? string.Empty : await ContextArgAsync(stack, cancellationToken);
        // rm -f each path inside a throwaway container mounting the volume at /dest. Each path is
        // single-quoted so names with spaces are one argument to the container's rm.
        var targets = string.Join(" ", paths.Select(p => "/dest/" + ShellQuote(p)));
        var command = $"docker {contextArg}run --rm -v {volumeName}:/dest alpine:3.20 sh -c \"rm -rf -- {targets}\"";
        var (exit, _, stderr) = await RunShellAsync(command, cancellationToken);
        if (exit != 0)
        {
            _logger.LogWarning("Failed to delete {Count} path(s) from volume {Volume}: {Err}", paths.Count, volumeName, stderr);
        }
        else
        {
            _logger.LogInformation("Deleted {Count} path(s) from volume {Volume}.", paths.Count, volumeName);
        }
    }

    public Task<IReadOnlyList<VolumeFileEntry>> ListVolumeFilesAsync(
        ManagedStackEntity stack,
        string volumeName,
        CancellationToken cancellationToken = default)
        => ListVolumeFilesCoreAsync(stack, volumeName, cancellationToken);

    public Task<IReadOnlyList<VolumeFileEntry>> ListLocalVolumeFilesAsync(
        string volumeName,
        CancellationToken cancellationToken = default)
        => ListVolumeFilesCoreAsync(stack: null, volumeName, cancellationToken);

    private async Task<IReadOnlyList<VolumeFileEntry>> ListVolumeFilesCoreAsync(
        ManagedStackEntity? stack,
        string volumeName,
        CancellationToken cancellationToken)
    {
        var contextArg = stack is null ? string.Empty : await ContextArgAsync(stack, cancellationToken);
        var (inspectExit, _, _) = await RunAsync(
            "docker",
            $"{contextArg}volume inspect {volumeName}",
            cancellationToken,
            throwOnError: false);
        if (inspectExit != 0)
        {
            return [];
        }

        var command =
            $"docker {contextArg}run --rm -v {volumeName}:/src:ro alpine:3.20 " +
            "find /src -type f -printf '%P\\t%s\\n' 2>/dev/null";
        var (exit, output, stderr) = await RunShellAsync(command, cancellationToken);
        if (exit != 0)
        {
            _logger.LogDebug("Failed to list files in volume {Volume}: {Err}", volumeName, stderr);
            return [];
        }

        var files = new List<VolumeFileEntry>();
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tab = raw.IndexOf('\t');
            if (tab <= 0)
            {
                continue;
            }

            var relativePath = raw[..tab].Replace('\\', '/').Trim().TrimStart('/');
            if (string.IsNullOrWhiteSpace(relativePath)
                || relativePath.Split('/').Contains("..", StringComparer.Ordinal))
            {
                continue;
            }

            if (!long.TryParse(raw[(tab + 1)..].Trim(), out var sizeBytes))
            {
                sizeBytes = 0;
            }

            files.Add(new VolumeFileEntry
            {
                RelativePath = relativePath,
                SizeBytes = sizeBytes,
            });
        }

        return files;
    }

    public Task<IReadOnlyList<VolumeDirectoryEntry>> ListVolumeDirectoryAsync(
        ManagedStackEntity? stack,
        string volumeName,
        string relativePath,
        CancellationToken cancellationToken = default)
        => ListVolumeDirectoryCoreAsync(stack, volumeName, relativePath, cancellationToken);

    public Task<VolumeTreeSummary> GetVolumeTreeSummaryAsync(
        ManagedStackEntity? stack,
        string volumeName,
        CancellationToken cancellationToken = default)
        => GetVolumeTreeSummaryCoreAsync(stack, volumeName, cancellationToken);

    public Task<int> CountVolumeFilesAsync(
        ManagedStackEntity? stack,
        string volumeName,
        string relativePath,
        string filePattern,
        CancellationToken cancellationToken = default)
        => CountVolumeFilesCoreAsync(stack, volumeName, relativePath, filePattern, cancellationToken);

    public Task<bool> VolumeSubdirExistsAsync(
        ManagedStackEntity? stack,
        string volumeName,
        string relativePath,
        CancellationToken cancellationToken = default)
        => VolumeSubdirExistsCoreAsync(stack, volumeName, relativePath, cancellationToken);

    public async Task ClearVolumeContentsAsync(
        ManagedStackEntity? stack,
        string volumeName,
        CancellationToken cancellationToken = default)
    {
        var contextArg = stack is null ? string.Empty : await ContextArgAsync(stack, cancellationToken);
        await RunAsync("docker", $"{contextArg}volume create {volumeName}", cancellationToken, throwOnError: false);
        var command =
            $"docker {contextArg}run --rm -v {volumeName}:/dest alpine:3.20 " +
            "sh -c \"find /dest -mindepth 1 -maxdepth 1 -exec rm -rf {} +\"";
        var (exit, _, stderr) = await RunShellAsync(command, cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Failed to clear volume '{volumeName}': {stderr}");
        }
    }

    private async Task<IReadOnlyList<VolumeDirectoryEntry>> ListVolumeDirectoryCoreAsync(
        ManagedStackEntity? stack,
        string volumeName,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var contextArg = stack is null ? string.Empty : await ContextArgAsync(stack, cancellationToken);
        var (inspectExit, _, _) = await RunAsync(
            "docker",
            $"{contextArg}volume inspect {volumeName}",
            cancellationToken,
            throwOnError: false);
        if (inspectExit != 0)
        {
            return [];
        }

        var subdir = SanitizeVolumeSubdir(relativePath);
        var target = string.IsNullOrEmpty(subdir) ? "/dest" : $"/dest/{subdir}";
        var listScript =
            $"if [ ! -d \"{target}\" ]; then exit 2; fi; " +
            $"find \"{target}\" -mindepth 1 -maxdepth 1 2>/dev/null | while IFS= read -r p; do " +
            "n=\"${p##*/}\"; " +
            "if [ -d \"$p\" ]; then printf \"%s\\t4\\t0\\n\" \"$n\"; " +
            "elif [ -f \"$p\" ]; then s=$(stat -c %s \"$p\" 2>/dev/null || echo 0); printf \"%s\\t8\\t%s\\n\" \"$n\" \"$s\"; fi; " +
            "done";
        var (exit, output, _) = await RunAlpineInVolumeAsync(
            contextArg, volumeName, readOnly: true, mountAt: "/dest", workDir: null, listScript, cancellationToken);
        if (exit == 2)
        {
            return [];
        }

        if (exit != 0)
        {
            return [];
        }

        var parent = NormalizeVolumeRelative(relativePath);
        var entries = new List<VolumeDirectoryEntry>();
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split('\t');
            if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[0]))
            {
                continue;
            }

            var name = NormalizeVolumeEntryName(parts[0]);
            if (name is "." or "..")
            {
                continue;
            }

            var isDirectory = parts[1].Trim() == "4";
            long.TryParse(parts[2].Trim(), out var sizeBytes);
            var rel = string.IsNullOrEmpty(parent) ? name : $"{parent}/{name}";
            entries.Add(new VolumeDirectoryEntry
            {
                Name = name,
                RelativePath = rel,
                IsDirectory = isDirectory,
                SizeBytes = isDirectory ? 0 : sizeBytes,
            });
        }

        foreach (var entry in entries.Where(e => e.IsDirectory))
        {
            var childTarget = string.IsNullOrEmpty(subdir) ? $"/dest/{entry.Name}" : $"/dest/{subdir}/{entry.Name}";
            var countScript = $"find \"{childTarget}\" -mindepth 1 -maxdepth 1 2>/dev/null | wc -l";
            var (countExit, countOut, _) = await RunAlpineInVolumeAsync(
                contextArg, volumeName, readOnly: true, mountAt: "/dest", workDir: null, countScript, cancellationToken);
            if (countExit == 0 && int.TryParse(countOut.Trim(), out var count))
            {
                entry.ItemCount = count;
            }
        }

        entries.Sort((a, b) =>
            a.IsDirectory != b.IsDirectory
                ? (a.IsDirectory ? -1 : 1)
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return entries;
    }

    private async Task<VolumeTreeSummary> GetVolumeTreeSummaryCoreAsync(
        ManagedStackEntity? stack,
        string volumeName,
        CancellationToken cancellationToken)
    {
        var summary = new VolumeTreeSummary();
        var contextArg = stack is null ? string.Empty : await ContextArgAsync(stack, cancellationToken);
        var (inspectExit, _, _) = await RunAsync(
            "docker",
            $"{contextArg}volume inspect {volumeName}",
            cancellationToken,
            throwOnError: false);
        if (inspectExit != 0)
        {
            return summary;
        }

        summary.VolumeExists = true;
        // Prefer cheap checks (Wow.exe / Data/*.MPQ / du) over a full-tree file count, which can
        // fail or time out on large (~17 GB) client volumes — especially over a remote docker context.
        const string summaryScript =
            "set +e; " +
            "wow=0; test -f /dest/Wow.exe -o -f /dest/WoW.exe && wow=1; " +
            "mpq=0; if test -d /dest/Data; then ls /dest/Data/*.MPQ >/dev/null 2>&1 && mpq=1; fi; " +
            "bytes=$(du -sb /dest 2>/dev/null | cut -f1); [ -n \"$bytes\" ] || bytes=0; " +
            "files=0; if [ \"$wow\" = \"1\" ] || [ \"$mpq\" = \"1\" ]; then " +
            "files=$(find /dest -type f ! -name .hashcache.json ! -name .manifest.json 2>/dev/null | wc -l | tr -d \" \"); " +
            "else find /dest -type f ! -name .hashcache.json ! -name .manifest.json -print -quit 2>/dev/null | grep -q . && files=1; fi; " +
            "printf \"AZP_SUMMARY:%s\\t%s\\t%s\\t%s\\n\" \"$files\" \"$bytes\" \"$wow\" \"$mpq\"";
        var (exit, output, stderr) = await RunAlpineInVolumeAsync(
            contextArg, volumeName, readOnly: true, mountAt: "/dest", workDir: null, summaryScript, cancellationToken);
        if (exit != 0 || !TryApplyVolumeSummaryLine(output, summary))
        {
            const string fallbackScript =
                "set +e; " +
                "wow=0; test -f /dest/Wow.exe -o -f /dest/WoW.exe && wow=1; " +
                "mpq=0; if test -d /dest/Data; then ls /dest/Data/*.MPQ >/dev/null 2>&1 && mpq=1; fi; " +
                "bytes=$(du -sb /dest 2>/dev/null | cut -f1); [ -n \"$bytes\" ] || bytes=0; " +
                "files=0; find /dest -type f ! -name .hashcache.json ! -name .manifest.json -print -quit 2>/dev/null | grep -q . && files=1; " +
                "printf \"AZP_SUMMARY:%s\\t%s\\t%s\\t%s\\n\" \"$files\" \"$bytes\" \"$wow\" \"$mpq\"";
            var (fallbackExit, fallbackOutput, fallbackErr) = await RunAlpineInVolumeAsync(
                contextArg, volumeName, readOnly: true, mountAt: "/dest", workDir: null, fallbackScript, cancellationToken);
            if (fallbackExit == 0 && TryApplyVolumeSummaryLine(fallbackOutput, summary))
            {
                return summary;
            }

            summary.InspectionFailed = true;
            summary.InspectionError = string.IsNullOrWhiteSpace(fallbackErr)
                ? (string.IsNullOrWhiteSpace(stderr) ? "Volume inspection failed." : stderr.Trim())
                : fallbackErr.Trim();
            _logger.LogWarning(
                "Failed to inspect volume {Volume} for stack {StackId}: {Error}",
                volumeName,
                stack?.Id ?? "(local)",
                summary.InspectionError);
        }

        return summary;
    }

    private static bool TryApplyVolumeSummaryLine(string output, VolumeTreeSummary summary)
    {
        var line = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith("AZP_SUMMARY:", StringComparison.Ordinal));
        if (line is null)
        {
            return false;
        }

        var payload = line["AZP_SUMMARY:".Length..];
        var parts = payload.Split('\t');
        if (parts.Length < 4)
        {
            return false;
        }

        int.TryParse(parts[0].Trim(), out var fileCount);
        long.TryParse(parts[1].Trim(), out var totalBytes);
        summary.FileCount = fileCount;
        summary.TotalBytes = totalBytes;
        summary.HasWowExe = parts[2].Trim() == "1";
        summary.HasDataMpq = parts[3].Trim() == "1";
        return true;
    }

    private async Task<int> CountVolumeFilesCoreAsync(
        ManagedStackEntity? stack,
        string volumeName,
        string relativePath,
        string filePattern,
        CancellationToken cancellationToken)
    {
        var contextArg = stack is null ? string.Empty : await ContextArgAsync(stack, cancellationToken);
        var (inspectExit, _, _) = await RunAsync(
            "docker",
            $"{contextArg}volume inspect {volumeName}",
            cancellationToken,
            throwOnError: false);
        if (inspectExit != 0)
        {
            return 0;
        }

        var subdir = SanitizeVolumeSubdir(relativePath);
        var target = string.IsNullOrEmpty(subdir) ? "/dest" : $"/dest/{subdir}";
        var pattern = string.IsNullOrWhiteSpace(filePattern) ? "*" : filePattern.Trim();
        if (ContainsShellMeta(pattern))
        {
            throw new ArgumentException($"Unsafe file pattern: '{filePattern}'.", nameof(filePattern));
        }

        var countScript =
            $"if [ ! -d \"{target}\" ]; then exit 2; fi; " +
            $"find \"{target}\" -type f -name \"{pattern}\" 2>/dev/null | wc -l | tr -d \" \"";
        var (exit, output, _) = await RunAlpineInVolumeAsync(
            contextArg, volumeName, readOnly: true, mountAt: "/dest", workDir: null, countScript, cancellationToken);
        if (exit != 0)
        {
            return 0;
        }

        return int.TryParse(output.Trim(), out var count) ? count : 0;
    }

    private async Task<bool> VolumeSubdirExistsCoreAsync(
        ManagedStackEntity? stack,
        string volumeName,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var subdir = SanitizeVolumeSubdir(relativePath);
        if (string.IsNullOrEmpty(subdir))
        {
            return false;
        }

        var contextArg = stack is null ? string.Empty : await ContextArgAsync(stack, cancellationToken);
        var (inspectExit, _, _) = await RunAsync(
            "docker",
            $"{contextArg}volume inspect {volumeName}",
            cancellationToken,
            throwOnError: false);
        if (inspectExit != 0)
        {
            return false;
        }

        var script = $"test -d \"/dest/{subdir}\"";
        var (exit, _, _) = await RunAlpineInVolumeAsync(
            contextArg, volumeName, readOnly: true, mountAt: "/dest", workDir: null, script, cancellationToken);
        return exit == 0;
    }

    private static string NormalizeVolumeRelative(string? relativePath)
        => string.IsNullOrWhiteSpace(relativePath)
            ? string.Empty
            : relativePath.Replace('\\', '/').Trim('/');

    public async Task EnsureVolumeExistsAsync(
        ManagedStackEntity? stack,
        string volumeName,
        CancellationToken cancellationToken = default)
    {
        var contextArg = stack is null ? string.Empty : await ContextArgAsync(stack, cancellationToken);
        await RunAsync("docker", $"{contextArg}volume create {volumeName}", cancellationToken, throwOnError: false);
    }

    public async Task SetVolumeOwnershipAsync(ManagedStackEntity stack, string volumeName, int uid, int gid, CancellationToken cancellationToken = default)
    {
        var contextArg = await ContextArgAsync(stack, cancellationToken);
        // Ensure the volume exists, then chown its contents in a throwaway root container. A fresh empty
        // volume keeps this ownership when a service container later auto-populates it from the image.
        await RunAsync("docker", $"{contextArg}volume create {volumeName}", cancellationToken, throwOnError: false);
        var args = $"{contextArg}run --rm -v {volumeName}:/dest alpine:3.20 chown -R {uid}:{gid} /dest";
        var (exit, _, stderr) = await RunAsync("docker", args, cancellationToken, throwOnError: false);
        if (exit != 0)
        {
            _logger.LogWarning("Failed to set ownership {Uid}:{Gid} on volume {Volume}: {Err}", uid, gid, volumeName, stderr);
        }
    }

    public async Task SetVolumeWorldReadableAsync(ManagedStackEntity stack, string volumeName, CancellationToken cancellationToken = default)
    {
        var contextArg = await ContextArgAsync(stack, cancellationToken);
        await RunAsync("docker", $"{contextArg}volume create {volumeName}", cancellationToken, throwOnError: false);
        // a+rX = readable for all, and traversable (+x) on directories only, so nginx (or any uid) can
        // read the served tree even when the source carried restrictive (e.g. 0700) permissions.
        var args = $"{contextArg}run --rm -v {volumeName}:/dest alpine:3.20 chmod -R a+rX /dest";
        var (exit, _, stderr) = await RunAsync("docker", args, cancellationToken, throwOnError: false);
        if (exit != 0)
        {
            _logger.LogWarning("Failed to make volume {Volume} world-readable: {Err}", volumeName, stderr);
        }
    }

    public async Task<(int ExitCode, string StdOut, string StdErr)> RunToolWithWorkVolumeAsync(
        ManagedStackEntity stack,
        string localWorkDir,
        string image,
        string toolArgs,
        CancellationToken cancellationToken = default)
    {
        var contextArg = await ContextArgAsync(stack, cancellationToken);
        var workVolume = $"acore-tool-{Guid.NewGuid():N}";

        try
        {
            // Seed the work volume with the staged inputs, run the tool against /work, then pull the
            // (mutated) work dir back so callers keep operating on their local filesystem as before.
            await SeedVolumeAsync(stack, workVolume, localWorkDir, cancellationToken);

            var args = $"{contextArg}run --rm -v {workVolume}:/work {image} {toolArgs}";
            var result = await RunAsync("docker", args, cancellationToken, throwOnError: false);

            await FetchVolumeAsync(stack, workVolume, localWorkDir, cancellationToken);
            return result;
        }
        finally
        {
            await RunAsync("docker", $"{contextArg}volume rm -f {workVolume}", cancellationToken, throwOnError: false);
        }
    }

    public async Task FetchVolumeSubdirAsync(
        ManagedStackEntity stack,
        string volumeName,
        string subdir,
        string localDestinationDir,
        CancellationToken cancellationToken = default)
    {
        var contextArg = await ContextArgAsync(stack, cancellationToken);
        Directory.CreateDirectory(localDestinationDir);

        var destQuoted = ShellQuote(localDestinationDir);
        var safeSubdir = SanitizeVolumeSubdir(subdir);
        var command =
            $"docker {contextArg}run --rm -i -v {volumeName}:/src alpine:3.20 sh -c \"cd /src/{safeSubdir} && tar -cf - .\" " +
            $"| tar -C {destQuoted} -xf -";

        var (exit, _, stderr) = await RunShellAsync(command, cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Failed to fetch '{subdir}' from volume '{volumeName}': {stderr}");
        }

        _logger.LogInformation("Fetched {Subdir} from volume {Volume} for stack {StackId}.", subdir, volumeName, stack.Id);
    }

    public async Task CopyVolumeSubdirAsync(
        ManagedStackEntity stack,
        string sourceVolume,
        string sourceSubdir,
        string destVolume,
        string destSubdir,
        CancellationToken cancellationToken = default)
    {
        var srcRel = NormalizeVolumeRelative(sourceSubdir);
        var dstRel = NormalizeVolumeRelative(destSubdir);
        if (string.IsNullOrEmpty(srcRel))
        {
            throw new ArgumentException("Source subdirectory is required.", nameof(sourceSubdir));
        }

        if (string.IsNullOrEmpty(dstRel))
        {
            throw new ArgumentException("Destination subdirectory is required.", nameof(destSubdir));
        }

        _ = SanitizeVolumeSubdir(srcRel);
        _ = SanitizeVolumeSubdir(dstRel);

        var contextArg = await ContextArgAsync(stack, cancellationToken);
        var command = string.Equals(sourceVolume, destVolume, StringComparison.Ordinal)
            ? $"docker {contextArg}run --rm -v {sourceVolume}:/w alpine:3.20 " +
              $"sh -c \"mkdir -p /w/{dstRel} && cp -a /w/{srcRel}/. /w/{dstRel}/\""
            : $"docker {contextArg}run --rm -v {sourceVolume}:/src:ro -v {destVolume}:/dest alpine:3.20 " +
              $"sh -c \"mkdir -p /dest/{dstRel} && cp -a /src/{srcRel}/. /dest/{dstRel}/\"";

        var (exit, _, stderr) = await RunShellAsync(command, cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"Failed to copy '{srcRel}' to '{dstRel}' on the stack engine: {stderr}");
        }

        _logger.LogInformation(
            "Copied volume subdir {Source} -> {Dest} ({SourceVolume} -> {DestVolume}) for stack {StackId}.",
            srcRel, dstRel, sourceVolume, destVolume, stack.Id);
    }

    public async Task RunVolumeShellAsync(
        ManagedStackEntity stack,
        string volumeName,
        string shellScript,
        CancellationToken cancellationToken = default)
    {
        var contextArg = await ContextArgAsync(stack, cancellationToken);
        var args = new List<string>();
        AddDockerContextArgs(args, contextArg);
        args.Add("run");
        args.Add("--rm");
        args.Add("-v");
        args.Add($"{volumeName}:/w");
        args.Add("-w");
        args.Add("/w");
        args.Add("alpine:3.20");
        args.Add("sh");
        args.Add("-c");
        args.Add(shellScript);
        var (exit, _, stderr) = await RunProcessAsync("docker", args, cancellationToken, throwOnError: false);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Volume shell command failed: {stderr}");
        }
    }

    public async Task<(int ExitCode, string StdOut, string StdErr)> RunToolInVolumeSubdirAsync(
        ManagedStackEntity stack,
        string volumeName,
        string workSubdir,
        string image,
        string toolArgs,
        CancellationToken cancellationToken = default)
    {
        var sub = SanitizeVolumeSubdir(workSubdir);
        var contextArg = await ContextArgAsync(stack, cancellationToken);
        var args = $"{contextArg}run --rm -v {volumeName}:/w -w /w/{sub} {image} {toolArgs}";
        return await RunAsync("docker", args, cancellationToken, throwOnError: false);
    }

    /// <summary>
    /// Attempts a daemon-side copy of <paramref name="localSourceDir"/> into <paramref name="volumeName"/>
    /// by mounting both the manager's data volume and the target volume in a helper container. Only
    /// possible when a data volume is configured, it exists on the daemon, and the source path lives
    /// under the data-volume mount (the parent of BuildsPath). Returns false to fall back to tar streaming.
    /// </summary>
    private async Task<bool> TryDaemonSideCopyAsync(string volumeName, string localSourceDir, CancellationToken cancellationToken)
    {
        var dataVolume = _dockerOptions.DataVolumeName;
        if (string.IsNullOrWhiteSpace(dataVolume))
        {
            return false;
        }

        if (!TryGetDataVolumeSubpath(localSourceDir, out var relative))
        {
            return false;
        }

        // Confirm the data volume actually exists (it won't for non-containerized dev runs).
        var (inspectExit, _, _) = await RunAsync("docker", $"volume inspect {dataVolume}", cancellationToken, throwOnError: false);
        if (inspectExit != 0)
        {
            return false;
        }

        var srcPath = string.IsNullOrEmpty(relative) ? "/src" : $"/src/{relative}";
        var inner = $"mkdir -p /dest && cp -a {srcPath}/. /dest/";
        var args =
            $"run --rm -v {dataVolume}:/src:ro -v {volumeName}:/dest alpine:3.20 sh -c \"{inner}\"";

        var (exit, _, stderr) = await RunAsync("docker", args, cancellationToken, throwOnError: false);
        if (exit != 0)
        {
            _logger.LogWarning("Daemon-side copy into {Volume} failed ({Stderr}); falling back to tar stream.", volumeName, stderr);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Computes the path of <paramref name="localSourceDir"/> relative to the manager's data-volume mount
    /// (the parent directory of <see cref="DockerOptions.BuildsPath"/>, e.g. <c>/app/data</c>). Returns
    /// false when the source is not under that mount.
    /// </summary>
    private bool TryGetDataVolumeSubpath(string localSourceDir, out string relative)
    {
        relative = string.Empty;
        var buildsPath = _dockerOptions.BuildsPath;
        if (string.IsNullOrWhiteSpace(buildsPath))
        {
            return false;
        }

        var dataMount = Path.GetDirectoryName(Path.GetFullPath(buildsPath).TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(dataMount))
        {
            return false;
        }

        var fullSource = Path.GetFullPath(localSourceDir);
        var normalizedMount = dataMount.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(fullSource, normalizedMount, StringComparison.Ordinal))
        {
            relative = string.Empty;
            return true;
        }

        var prefix = normalizedMount + Path.DirectorySeparatorChar;
        if (!fullSource.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        relative = fullSource[prefix.Length..].Replace('\\', '/').Trim('/');
        return true;
    }

    private static string ShellQuote(string value) => "'" + (value ?? string.Empty).Replace("'", "'\\''") + "'";

    /// <summary>
    /// Runs a shell script in a throwaway Alpine container via the docker CLI directly (not
    /// <see cref="RunShellAsync"/>), so variable expansion inside the script is not broken by a
    /// second wrapping <c>/bin/sh -c</c>. The script is single-quoted for docker; do not embed
    /// single quotes in <paramref name="shellScript"/> (use double quotes for paths/literals).
    /// </summary>
    private static Task<(int ExitCode, string StdOut, string StdErr)> RunAlpineInVolumeAsync(
        string contextArg,
        string volumeName,
        bool readOnly,
        string mountAt,
        string? workDir,
        string shellScript,
        CancellationToken cancellationToken)
    {
        var args = new List<string>();
        AddDockerContextArgs(args, contextArg);
        args.Add("run");
        args.Add("--rm");
        if (!string.IsNullOrEmpty(workDir))
        {
            args.Add("-w");
            args.Add(workDir);
        }

        args.Add("-v");
        args.Add($"{volumeName}:{mountAt}{(readOnly ? ":ro" : string.Empty)}");
        args.Add("alpine:3.20");
        args.Add("sh");
        args.Add("-c");
        args.Add(shellScript);
        return RunProcessAsync("docker", args, cancellationToken, throwOnError: false);
    }

    private static void AddDockerContextArgs(List<string> args, string contextArg)
    {
        var trimmed = (contextArg ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        foreach (var part in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            args.Add(part);
        }
    }

    private static string NormalizeVolumeEntryName(string raw)
    {
        var name = (raw ?? string.Empty).Replace('\\', '/').Trim();
        var slash = name.LastIndexOf('/');
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    // Characters that survive single-quoting only to be re-interpreted by the *outer* host shell that
    // RunShellAsync wraps the whole command in with double quotes (so command-substitution/escapes still
    // fire). Any volume-relative path containing one of these is rejected before it reaches the shell.
    private static readonly char[] ShellMetaChars = { '$', '`', '"', '\\', '\n', '\r', ';', '|', '&', '<', '>' };

    private static bool ContainsShellMeta(string value) => value.IndexOfAny(ShellMetaChars) >= 0;

    /// <summary>
    /// Validates a volume-relative subdirectory used inside a helper container command: forward slashes
    /// only, no leading slash, no <c>..</c> traversal, and no shell metacharacters. Returns the cleaned
    /// value or throws <see cref="ArgumentException"/>.
    /// </summary>
    private static string SanitizeVolumeSubdir(string subdir)
    {
        var value = (subdir ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (value.StartsWith('/')
            || value.Split('/').Contains("..")
            || ContainsShellMeta(value))
        {
            throw new ArgumentException($"Unsafe volume subdirectory: '{subdir}'.");
        }

        return value;
    }

    /// <summary>
    /// Rejects an SSH config token (host/user) that could inject additional <c>ssh_config</c> directives
    /// (e.g. a smuggled <c>ProxyCommand</c>) via embedded whitespace/newlines. Returns the trimmed value.
    /// </summary>
    private static string SanitizeSshToken(string value, string field)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch)))
        {
            throw new ArgumentException($"Invalid SSH {field}: it must be a single token with no whitespace or control characters.");
        }

        return trimmed;
    }

    // ===== ssh config / key management =====

    private void WriteSshConfig(string contextName, string host, int port, string user, string privateKey)
    {
        // Host/User land in ssh_config as directive values; embedded whitespace/newlines could smuggle
        // extra directives (e.g. ProxyCommand → RCE). Reject anything that is not a single clean token.
        host = SanitizeSshToken(host, "host");
        user = SanitizeSshToken(user, "user");

        var sshDir = GetSshDir();
        Directory.CreateDirectory(sshDir);
        TrySetUnixMode(sshDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var keyPath = Path.Combine(sshDir, $"{contextName}.key");
        var knownHostsPath = Path.Combine(sshDir, $"{contextName}.known_hosts");
        var keyContent = NormalizePrivateKey(privateKey);
        File.WriteAllText(keyPath, keyContent);
        TrySetUnixMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (!File.Exists(knownHostsPath))
        {
            File.WriteAllText(knownHostsPath, string.Empty);
        }

        // Match both the internal alias and the real hostname so `ssh alias …` (our probe) and
        // `ssh://user@hostname` (Docker context) resolve to the same key/user settings.
        var block = new StringBuilder()
            .Append(BeginMarker(contextName)).Append('\n')
            .Append($"Host {contextName} {host}\n")
            .Append($"    HostName {host}\n")
            .Append($"    User {user}\n")
            .Append($"    Port {port}\n")
            .Append($"    IdentityFile {SshProbe.FormatConfigPath(keyPath)}\n")
            .Append("    IdentitiesOnly yes\n")
            .Append("    PreferredAuthentications publickey\n")
            .Append("    PubkeyAuthentication yes\n")
            .Append("    PubkeyAcceptedAlgorithms +ssh-rsa,rsa-sha2-256,rsa-sha2-512\n")
            .Append("    HostkeyAlgorithms +ssh-rsa,rsa-sha2-256,rsa-sha2-512,ssh-ed25519,ecdsa-sha2-nistp256\n")
            .Append("    IPQoS none\n")
            .Append("    StrictHostKeyChecking accept-new\n")
            .Append($"    UserKnownHostsFile {SshProbe.FormatConfigPath(knownHostsPath)}\n")
            .Append(EndMarker(contextName)).Append('\n')
            .ToString();

        UpsertSshConfigBlock(contextName, block);
    }

    private void UpsertSshConfigBlock(string contextName, string block)
    {
        var configPath = Path.Combine(GetSshDir(), "config");
        var existing = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
        var without = StripBlock(existing, contextName);
        var separator = string.IsNullOrEmpty(without) || without.EndsWith('\n') ? string.Empty : "\n";
        File.WriteAllText(configPath, without + separator + block);
        TrySetUnixMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private void RemoveSshConfigBlock(string contextName)
    {
        var sshDir = GetSshDir();
        var configPath = Path.Combine(sshDir, "config");
        if (File.Exists(configPath))
        {
            File.WriteAllText(configPath, StripBlock(File.ReadAllText(configPath), contextName));
        }

        foreach (var suffix in new[] { ".key", ".key.pub", ".known_hosts" })
        {
            var path = Path.Combine(sshDir, $"{contextName}{suffix}");
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to delete {Path}", path);
            }
        }
    }

    private static string StripBlock(string content, string contextName)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        var begin = BeginMarker(contextName);
        var end = EndMarker(contextName);
        var startIdx = content.IndexOf(begin, StringComparison.Ordinal);
        if (startIdx < 0)
        {
            return content;
        }

        var endIdx = content.IndexOf(end, startIdx, StringComparison.Ordinal);
        if (endIdx < 0)
        {
            return content[..startIdx].TrimEnd('\n') + "\n";
        }

        endIdx += end.Length;
        // Swallow a trailing newline left behind by the block.
        if (endIdx < content.Length && content[endIdx] == '\n')
        {
            endIdx++;
        }

        var result = content[..startIdx] + content[endIdx..];
        return result;
    }

    private static string BeginMarker(string contextName) => $"# BEGIN {contextName} (managed by AzerothPlatform)";
    private static string EndMarker(string contextName) => $"# END {contextName}";

    private static string GetSshDir()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(home, ".ssh");
    }

    private static string NormalizePrivateKey(string privateKey)
    {
        var normalized = (privateKey ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        // Win32 OpenSSH is unreliable with PKCS#8 "BEGIN PRIVATE KEY" identity files. RSA keys
        // are rewritten as PKCS#1 so ssh.exe will actually offer them.
        try
        {
            using var rsa = System.Security.Cryptography.RSA.Create();
            rsa.ImportFromPem(normalized);
            return rsa.ExportRSAPrivateKeyPem() + "\n";
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return normalized + "\n";
        }
    }

    private async Task PrepareSshAsync(
        string contextName,
        string host,
        int port,
        string user,
        string privateKey,
        CancellationToken cancellationToken)
    {
        WriteSshConfig(contextName, host, port, user, privateKey);
        var keyPath = Path.Combine(GetSshDir(), $"{contextName}.key");
        await NormalizeSshIdentityFileAsync(keyPath, cancellationToken);
        await SeedKnownHostsAsync(host, port, Path.Combine(GetSshDir(), $"{contextName}.known_hosts"), cancellationToken);
    }

    /// <summary>
    /// OpenSSH reports <c>identity file … type -1</c> when it cannot parse the private key (PKCS#8,
    /// permissions, or a non-OpenSSH PEM). Convert with ssh-keygen and pin mode 600.
    /// </summary>
    private async Task NormalizeSshIdentityFileAsync(string keyPath, CancellationToken cancellationToken)
    {
        TrySetUnixMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        int readExit;
        string publicKey;
        string readErr;
        try
        {
            (readExit, publicKey, readErr) = await RunProcessAsync(
                "ssh-keygen",
                ["-y", "-f", keyPath],
                cancellationToken,
                throwOnError: false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ssh-keygen is not available to normalize {Path}", keyPath);
            return;
        }
        if (readExit != 0)
        {
            throw new InvalidOperationException(
                "OpenSSH cannot read the VPC private key (identity type -1). " +
                "The key file is not a usable PEM/OpenSSH identity. " +
                (string.IsNullOrWhiteSpace(readErr) ? "Re-launch so a new key is generated." : readErr.Trim()));
        }

        var pubPath = keyPath + ".pub";
        File.WriteAllText(pubPath, publicKey.Trim() + "\n");
        TrySetUnixMode(pubPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var (convertExit, _, convertErr) = await RunProcessAsync(
            "ssh-keygen",
            ["-p", "-P", "", "-N", "", "-f", keyPath],
            cancellationToken,
            throwOnError: false);
        if (convertExit != 0)
        {
            _logger.LogDebug("ssh-keygen could not rewrite {Path} to OpenSSH format: {Error}", keyPath, convertErr.Trim());
        }

        TrySetUnixMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private async Task SeedKnownHostsAsync(
        string host,
        int port,
        string knownHostsPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var args = new List<string>
            {
                "-4",
                "-T", "5",
                "-p", port.ToString(),
                host,
            };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var (exit, stdout, stderr) = await RunProcessAsync("ssh-keyscan", args, timeout.Token, throwOnError: false);
            if (exit != 0 || string.IsNullOrWhiteSpace(stdout) || !stdout.Contains(" ssh-", StringComparison.Ordinal))
            {
                _logger.LogDebug(
                    "ssh-keyscan did not seed known_hosts for {Host}:{Port} (exit {Exit}): {Error}",
                    host,
                    port,
                    exit,
                    stderr.Trim());
                return;
            }

            File.WriteAllText(knownHostsPath, stdout);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("ssh-keyscan timed out for {Host}:{Port}", host, port);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ssh-keyscan is unavailable; SSH will use accept-new for {Host}", host);
        }
    }

    private async Task EnsureDockerContextAsync(
        string contextName,
        string user,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var endpoint = $"host={BuildDockerSshEndpoint(user, host, port)}";
        if (_verifiedContextEndpoints.TryGetValue(contextName, out var cachedEndpoint)
            && string.Equals(cachedEndpoint, endpoint, StringComparison.Ordinal))
        {
            var (cachedInspectExit, _, _) = await RunAsync(
                "docker",
                $"context inspect {contextName}",
                cancellationToken,
                throwOnError: false);
            if (cachedInspectExit == 0)
            {
                return;
            }

            _verifiedContextEndpoints.TryRemove(contextName, out _);
        }

        var (inspectExit, _, _) = await RunAsync("docker", $"context inspect {contextName}", cancellationToken, throwOnError: false);
        if (inspectExit == 0)
        {
            var (updateExit, _, updateErr) = await RunAsync(
                "docker",
                $"context update {contextName} --docker {endpoint}",
                cancellationToken,
                throwOnError: false);
            if (updateExit != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to update Docker context '{contextName}': {updateErr.Trim()}");
            }
        }
        else
        {
            await RunAsync("docker", $"context create {contextName} --docker {endpoint}", cancellationToken, throwOnError: true);
        }

        _verifiedContextEndpoints[contextName] = endpoint;
    }

    private static string BuildDockerSshEndpoint(string user, string host, int port)
    {
        user = SanitizeSshToken(user, "user");
        host = SanitizeSshToken(host, "host");
        var portSuffix = port is > 0 and not 22 ? $":{port}" : string.Empty;
        return $"ssh://{user}@{host}{portSuffix}";
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> ProbeSshEchoAsync(
        string contextName,
        CancellationToken cancellationToken,
        int connectTimeoutSeconds = 30,
        bool retry = false)
    {
        var attempts = retry ? SshProbeRetryCount : 1;
        var last = (ExitCode: 1, StdOut: string.Empty, StdErr: string.Empty);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            last = await RunSshAsync(
                contextName,
                ["echo", "ok"],
                cancellationToken,
                connectTimeoutSeconds,
                connectionAttempts: 1,
                serverAliveInterval: 5,
                serverAliveCountMax: 2);
            if (SshProbe.IsEchoSuccess(last.ExitCode, last.StdOut, last.StdErr))
            {
                return last;
            }

            _logger.LogInformation(
                "SSH probe attempt {Attempt}/{Attempts} failed with exit {Exit}. stderr={Stderr}",
                attempt,
                attempts,
                last.ExitCode,
                last.StdErr.Trim());

            if (attempt < attempts && SshProbe.ShouldRetry(last.ExitCode, last.StdOut, last.StdErr))
            {
                await Task.Delay(SshProbeRetryDelay, cancellationToken);
                continue;
            }

            break;
        }

        if (!SshProbe.IsEchoSuccess(last.ExitCode, last.StdOut, last.StdErr))
        {
            var verbose = await RunSshAsync(
                contextName,
                ["echo", "ok"],
                cancellationToken,
                connectTimeoutSeconds,
                connectionAttempts: 1,
                serverAliveInterval: 5,
                serverAliveCountMax: 2,
                verbose: true);
            if (SshProbe.IsEchoSuccess(verbose.ExitCode, verbose.StdOut, verbose.StdErr))
            {
                return verbose;
            }

            last = (
                verbose.ExitCode,
                verbose.StdOut,
                MergeSshDiagnostics(last.StdErr, verbose.StdErr));
        }

        return last;
    }

    private static string MergeSshDiagnostics(string probeStderr, string verboseStderr)
    {
        var useful = SshProbe.ExtractUsefulVerbose(verboseStderr);
        if (!string.IsNullOrWhiteSpace(useful))
        {
            return useful;
        }

        var stripped = SshProbe.StripHostKeyWarnings(verboseStderr);
        if (string.IsNullOrWhiteSpace(stripped))
        {
            stripped = SshProbe.StripHostKeyWarnings(probeStderr);
        }

        return stripped;
    }

    private Task<(int ExitCode, string StdOut, string StdErr)> RunSshAsync(
        string contextName,
        IReadOnlyList<string> remoteCommand,
        CancellationToken cancellationToken,
        int connectTimeoutSeconds = 30,
        bool forceTty = false,
        int connectionAttempts = 3,
        int serverAliveInterval = 15,
        int serverAliveCountMax = 10,
        bool verbose = false)
    {
        var sshConfigPath = Path.Combine(GetSshDir(), "config");
        var args = new List<string>
        {
            "-F", sshConfigPath,
            "-o", "BatchMode=yes",
            "-o", "PreferredAuthentications=publickey",
            "-o", "PubkeyAuthentication=yes",
            "-o", "IPQoS=none",
            "-o", "NumberOfPasswordPrompts=0",
            "-o", $"ConnectTimeout={connectTimeoutSeconds}",
            "-o", $"ConnectionAttempts={Math.Clamp(connectionAttempts, 1, 6)}",
            "-o", $"ServerAliveInterval={Math.Clamp(serverAliveInterval, 1, 30)}",
            "-o", $"ServerAliveCountMax={Math.Clamp(serverAliveCountMax, 1, 10)}",
        };
        if (verbose)
        {
            args.Add("-v");
        }

        if (forceTty)
        {
            args.Add("-tt");
        }

        args.Add(contextName);
        args.AddRange(remoteCommand);
        return RunProcessAsync("ssh", args, cancellationToken, throwOnError: false);
    }

    private static string FormatSshError(int exitCode, string stdout, string stderr, string host, string user, int port)
    {
        var message = SshProbe.DescribeFailure(exitCode, stdout, stderr, host, user, port);

        if (message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("invalid format", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no such identity", StringComparison.OrdinalIgnoreCase))
        {
            message += " Verify the private key matches the remote authorized_keys entry.";
        }
        else if (SshProbe.IsConnectivityFailure(message))
        {
            message += " This is usually a network or firewall issue, not bad credentials — confirm the " +
                       "instance is running, the host/IP is correct (EC2 public IPs change after stop/start " +
                       "unless you use an Elastic IP), and SSH port " + port +
                       " is allowed from this manager in your cloud security group.";
        }

        return message;
    }

    private static string GetSshSetupFailureSummary(string stderr)
        => SshProbe.SetupFailureSummary(stderr);

    /// <summary>
    /// Rewrites Docker CLI's misleading <c>http://docker.example.com</c> placeholder (used for SSH
    /// transports) and surfaces nested <c>stderr=</c> details when present.
    /// </summary>
    private static string FormatRemoteDockerError(
        string stderr,
        string host,
        string user,
        int port,
        string? fallback = null)
    {
        var target = port == 22 ? $"ssh://{user}@{host}" : $"ssh://{user}@{host}:{port}";
        var message = string.IsNullOrWhiteSpace(stderr) ? (fallback ?? "Docker is not available on the remote host.") : stderr.Trim();

        message = message
            .Replace("http://docker.example.com", target, StringComparison.OrdinalIgnoreCase)
            .Replace("https://docker.example.com", target, StringComparison.OrdinalIgnoreCase);

        const string stderrPrefix = "stderr=";
        var stderrIdx = message.LastIndexOf(stderrPrefix, StringComparison.OrdinalIgnoreCase);
        if (stderrIdx >= 0)
        {
            var nested = message[(stderrIdx + stderrPrefix.Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(nested))
            {
                message = nested;
            }
        }

        if (message.Contains("a password is required", StringComparison.OrdinalIgnoreCase)
            || message.Contains("sorry, you must have a tty", StringComparison.OrdinalIgnoreCase))
        {
            message += " Non-interactive sudo is not ready yet (common on Ubuntu 24.04). Wait for launch " +
                       "user-data, or use Repair host setup so the platform can write NOPASSWD and disable sudo use_pty.";
        }
        else if (message.Contains("No such file", StringComparison.OrdinalIgnoreCase)
                 || message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            message += " Docker is not installed on the VPC yet. Wait for launch user-data to finish, then " +
                       "verify again. If this host was not launched from the wizard, use Repair host setup.";
        }
        else if (message.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
        {
            message += $" Add '{user}' to the docker group on the remote host " +
                       $"(sudo usermod -aG docker {user}; log out and back in).";
        }
        else if (message.Contains("docker daemon", StringComparison.OrdinalIgnoreCase)
                 || message.Contains("docker.sock", StringComparison.OrdinalIgnoreCase))
        {
            message += " The Docker client is installed but the daemon is not running — on the remote " +
                       "host run: sudo systemctl start docker && sudo systemctl enable docker";
        }

        return message;
    }

    // ===== process helpers =====

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken,
        bool throwOnError)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (throwOnError && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} {arguments} failed ({process.ExitCode}): {stderr}");
        }

        return (process.ExitCode, stdout, stderr);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool throwOnError,
        Stream? stdin = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {fileName}.");
        }

        if (stdin is not null)
        {
            await stdin.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (throwOnError && process.ExitCode != 0)
        {
            var rendered = arguments.Count > 0
                ? $"{fileName} {string.Join(' ', arguments)}"
                : fileName;
            throw new InvalidOperationException($"{rendered} failed ({process.ExitCode}): {stderr}");
        }

        return (process.ExitCode, stdout, stderr);
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool throwOnError)
        => RunProcessAsync(fileName, arguments, cancellationToken, throwOnError, stdin: null);

    private async Task<(int ExitCode, string Stdout, string Stderr)> StreamStdinToRemoteShellAsync(
        string contextName,
        string remoteCommand,
        Stream stdin,
        CancellationToken cancellationToken)
    {
        var sshConfigPath = Path.Combine(GetSshDir(), "config");
        var args = new List<string>
        {
            "-F", sshConfigPath,
            "-o", "BatchMode=yes",
            "-o", "ConnectTimeout=120",
            "-o", "ServerAliveInterval=15",
            "-o", "ServerAliveCountMax=8",
            contextName,
            remoteCommand,
        };
        return await RunProcessAsync("ssh", args, cancellationToken, throwOnError: false, stdin: stdin);
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> StreamStdinToShellAsync(
        string command,
        Stream stdin,
        CancellationToken cancellationToken)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var fileName = isWindows ? "cmd.exe" : "/bin/sh";
        var arguments = isWindows
            ? new List<string> { "/c", command }
            : new List<string> { "-c", command };
        return RunProcessAsync(fileName, arguments, cancellationToken, throwOnError: false, stdin: stdin);
    }

    private async Task ExtractClientArchiveOnRemoteAsync(
        string contextName,
        string workVolume,
        string destinationVolume,
        CancellationToken cancellationToken)
    {
        const string script = """
            set -e
            apk add --no-cache unzip p7zip >/dev/null
            ARCH=/work/upload.archive
            mkdir -p /work/extract
            if unzip -t "$ARCH" >/dev/null 2>&1; then
              unzip -q "$ARCH" -d /work/extract
            elif 7z t "$ARCH" >/dev/null 2>&1; then
              7z x -o/work/extract "$ARCH"
            elif tar -tf "$ARCH" >/dev/null 2>&1; then
              tar -xf "$ARCH" -C /work/extract
            elif tar -tzf "$ARCH" >/dev/null 2>&1; then
              tar -xzf "$ARCH" -C /work/extract
            else
              echo "Unsupported archive format on the remote host." >&2
              exit 1
            fi
            find_root() {
              local base="$1"
              if [ -f "$base/Wow.exe" ] || [ -f "$base/WoW.exe" ]; then echo "$base"; return 0; fi
              if [ -d "$base/Data" ] && ls "$base"/Data/*.MPQ >/dev/null 2>&1; then echo "$base"; return 0; fi
              return 1
            }
            ROOT=""
            if find_root /work/extract; then ROOT=/work/extract; fi
            if [ -z "$ROOT" ]; then
              for d in /work/extract/*/; do
                [ -d "$d" ] || continue
                if find_root "${d%/}"; then ROOT="${d%/}"; break; fi
                for nested in "$d"*/; do
                  [ -d "$nested" ] || continue
                  if find_root "${nested%/}"; then ROOT="${nested%/}"; break 2; fi
                done
              done
            fi
            if [ -z "$ROOT" ]; then
              echo "The uploaded archive does not look like a WoW client (no Wow.exe or Data/*.MPQ found)." >&2
              exit 1
            fi
            cp -a "$ROOT"/. /dest/
            """;

        var remoteCommand =
            $"docker run --rm -v {workVolume}:/work -v {destinationVolume}:/dest alpine:3.20 sh -c {ShellQuote(script)}";
        var (exit, _, stderr) = await RunSshAsync(contextName, ["sh", "-c", remoteCommand], cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"Failed to extract the client archive on the remote engine: {stderr.Trim()}");
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunShellAsync(string command, CancellationToken cancellationToken)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var fileName = isWindows ? "cmd.exe" : "/bin/sh";
        var arguments = isWindows ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"";
        return await RunAsync(fileName, arguments, cancellationToken, throwOnError: false);
    }

    private void TrySetUnixMode(string path, UnixFileMode mode)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to set unix mode on {Path}", path);
        }
    }

    private static string ManagementTunnelKey(string stackId, string remoteHost, int remotePort) =>
        $"{stackId}:{remoteHost}:{remotePort}";

    private static string NormalizeTunnelRemoteHost(string remoteHost)
    {
        var trimmed = (remoteHost ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed == "0.0.0.0" || trimmed == "*" || trimmed == "::")
        {
            return "127.0.0.1";
        }

        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            trimmed = trimmed[1..^1];
            return trimmed == "::" ? "127.0.0.1" : trimmed;
        }

        return trimmed;
    }

    private static bool TryParseDockerPublishedEndpoint(string line, out string host, out int port)
    {
        host = "127.0.0.1";
        port = 0;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (line.StartsWith('['))
        {
            var close = line.IndexOf(']');
            if (close <= 1 || close + 1 >= line.Length || line[close + 1] != ':')
            {
                return false;
            }

            host = NormalizeTunnelRemoteHost(line[..(close + 1)]);
            return int.TryParse(line[(close + 2)..], out port) && port is > 0 and <= 65535;
        }

        var colon = line.LastIndexOf(':');
        if (colon <= 0 || colon >= line.Length - 1)
        {
            return false;
        }

        host = NormalizeTunnelRemoteHost(line[..colon]);
        return int.TryParse(line[(colon + 1)..], out port) && port is > 0 and <= 65535;
    }

    private static bool IsTunnelAlive(ManagementTunnel tunnel) =>
        !tunnel.Process.HasExited;

    private static int AllocateLocalPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private void StopManagementTunnels(string stackId)
    {
        var prefix = stackId + ":";
        foreach (var key in _managementTunnels.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            if (_managementTunnels.TryRemove(key, out var tunnel))
            {
                StopManagementTunnel(key, tunnel);
            }
        }
    }

    private void StopManagementTunnel(string tunnelKey, ManagementTunnel tunnel)
    {
        try
        {
            if (!tunnel.Process.HasExited)
            {
                tunnel.Process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to stop SSH management tunnel {TunnelKey}", tunnelKey);
        }
        finally
        {
            tunnel.Process.Dispose();
        }
    }

    private static readonly TimeSpan InteractiveShellMaxDuration = TimeSpan.FromMinutes(60);

    public async Task RunInteractiveShellAsync(
        string host,
        int sshPort,
        string user,
        string privateKey,
        Func<byte[], Task> onOutput,
        ChannelReader<byte[]> input,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user))
        {
            throw new InvalidOperationException("Host and SSH user are required.");
        }

        if (string.IsNullOrWhiteSpace(privateKey))
        {
            throw new InvalidOperationException("SSH private key is required.");
        }

        host = host.Trim();
        user = user.Trim();
        var port = sshPort <= 0 ? 22 : sshPort;
        user = SanitizeSshToken(user, "user");

        if (await IsDisallowedRemoteHostAsync(host, cancellationToken))
        {
            throw new InvalidOperationException(
                "The specified host is not an allowed remote engine target (loopback and " +
                "link-local/metadata addresses are blocked).");
        }

        using var durationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        durationCts.CancelAfter(InteractiveShellMaxDuration);

        var linkedToken = durationCts.Token;
        var contextName = $"acore-term-{Guid.NewGuid():N}";
        await PrepareSshAsync(contextName, host, port, user, privateKey, linkedToken);

        Process? process = null;
        try
        {
            var sshConfigPath = Path.Combine(GetSshDir(), "config");
            var startInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-F");
            startInfo.ArgumentList.Add(sshConfigPath);
            startInfo.ArgumentList.Add("-tt");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("BatchMode=yes");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("ConnectTimeout=30");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("ServerAliveInterval=15");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("ServerAliveCountMax=4");
            startInfo.ArgumentList.Add(contextName);

            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start ssh.");
            }

            var stdoutTask = PumpShellOutputAsync(process.StandardOutput.BaseStream, onOutput, linkedToken);
            var stderrTask = PumpShellOutputAsync(process.StandardError.BaseStream, onOutput, linkedToken);
            var stdinTask = PumpShellInputAsync(process.StandardInput.BaseStream, input, linkedToken);
            var exitTask = process.WaitForExitAsync(linkedToken);

            var completed = await Task.WhenAny(exitTask, stdoutTask, stderrTask);
            if (completed == exitTask)
            {
                await Task.WhenAll(stdoutTask, stderrTask);
            }

            try
            {
                await stdinTask;
            }
            catch
            {
                // Input pump cancelled when the session ends.
            }
        }
        finally
        {
            if (process is not null && !process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to stop interactive ssh session {Context}", contextName);
                }
            }

            process?.Dispose();
            RemoveSshConfigBlock(contextName);
        }
    }

    private static async Task PumpShellOutputAsync(
        Stream stream,
        Func<byte[], Task> onOutput,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read <= 0)
            {
                break;
            }

            var chunk = new byte[read];
            Buffer.BlockCopy(buffer, 0, chunk, 0, read);
            await onOutput(chunk);
        }
    }

    private static async Task PumpShellInputAsync(
        Stream stream,
        ChannelReader<byte[]> input,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in input.ReadAllAsync(cancellationToken))
            {
                await stream.WriteAsync(chunk, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Session ended.
        }
        catch (ChannelClosedException)
        {
            // Input channel completed.
        }
    }
}
