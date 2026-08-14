using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data.Entities;
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
    private readonly ILogger<RemoteEngineService> _logger;
    private readonly DockerOptions _dockerOptions;
    private readonly ISecretProtector _secretProtector;
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
        // The stored key is encrypted at rest; decrypt just-in-time to write the on-disk identity file.
        var privateKey = _secretProtector.Unprotect(stack.ExternalSshPrivateKey);
        WriteSshConfig(contextName, stack.ExternalHost.Trim(), stack.ExternalSshPort <= 0 ? 22 : stack.ExternalSshPort,
            stack.ExternalSshUser.Trim(), privateKey);
        await EnsureDockerContextAsync(
            contextName,
            stack.ExternalSshUser.Trim(),
            stack.ExternalHost.Trim(),
            stack.ExternalSshPort <= 0 ? 22 : stack.ExternalSshPort,
            cancellationToken);
        return contextName;
    }

    public async Task RemoveContextAsync(ManagedStackEntity stack, CancellationToken cancellationToken = default)
    {
        StopManagementTunnels(stack.Id);
        var contextName = GetContextName(stack.Id);
        await RunAsync("docker", $"context rm -f {contextName}", cancellationToken, throwOnError: false);
        RemoveSshConfigBlock(contextName);
    }

    public async Task<(string Host, int Port)> GetManagementTunnelEndpointAsync(
        ManagedStackEntity stack,
        int remotePort,
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

        var tunnelKey = ManagementTunnelKey(stack.Id, remotePort);
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
        var forward = $"127.0.0.1:{localPort}:127.0.0.1:{remotePort}";
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
        try
        {
            WriteSshConfig(contextName, host, port, user, privateKey);
            if (checkPrerequisites)
            {
                await EnsureDockerContextAsync(contextName, user, host, port, cancellationToken);
            }

            if (checkSsh)
            {
                var (sshExit, _, sshStderr) = await RunSshAsync(contextName, ["echo", "ok"], cancellationToken);
                if (sshExit != 0)
                {
                    prerequisites.Add(new RemotePrerequisiteCheckDto
                    {
                        Name = "SSH",
                        Passed = false,
                        Message = FormatSshError(sshStderr, host, user, port)
                    });
                    return new RemoteConnectionTestResultDto
                    {
                        Success = false,
                        Message = "SSH connection failed from the platform. Your key may work in a local terminal " +
                                  "but the manager process could not reach the host (check host/user/port, key paste, " +
                                  "and outbound SSH from the platform container).",
                        Prerequisites = prerequisites
                    };
                }

                prerequisites.Add(new RemotePrerequisiteCheckDto
                {
                    Name = "SSH",
                    Passed = true,
                    Message = "Connected to the remote host."
                });

                if (phase == RemoteConnectionTestPhase.SshOnly)
                {
                    return new RemoteConnectionTestResultDto
                    {
                        Success = true,
                        Message = "SSH connection successful.",
                        Prerequisites = prerequisites
                    };
                }
            }

            if (!checkPrerequisites)
            {
                return new RemoteConnectionTestResultDto
                {
                    Success = prerequisites.All(p => p.Passed),
                    Message = prerequisites.All(p => p.Passed) ? "Connection checks passed." : "Connection checks failed.",
                    Prerequisites = prerequisites
                };
            }

            // Probe Docker on the remote host over the same SSH session (matches `ssh … docker info` on EC2).
            var (remoteDockerExit, remoteDockerOut, remoteDockerErr) = await RunSshAsync(
                contextName,
                ["docker", "info", "--format", "{{.ServerVersion}}"],
                cancellationToken);

            if (remoteDockerExit != 0)
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
                    Message = "SSH works, but the remote Docker engine is not available. On a fresh EC2 Ubuntu " +
                              "instance install Docker and add the SSH user to the docker group " +
                              $"(sudo usermod -aG docker {user}; log out and back in), or run First Time Setup.",
                    Prerequisites = prerequisites
                };
            }

            var version = remoteDockerOut.Trim();
            prerequisites.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Docker Engine",
                Passed = true,
                Message = string.IsNullOrWhiteSpace(version) ? "Docker engine is running." : $"Docker {version}"
            });

            var (composeExit, composeStdout, composeStderr) = await RunSshAsync(
                contextName,
                ["docker", "compose", "version", "--short"],
                cancellationToken);

            if (composeExit != 0)
            {
                var composeMessage = FormatRemoteDockerError(
                    composeStderr,
                    host,
                    user,
                    port,
                    fallback: "Docker Compose plugin is not installed on the remote host.");
                prerequisites.Add(new RemotePrerequisiteCheckDto
                {
                    Name = "Docker Compose",
                    Passed = false,
                    Message = composeMessage
                });
                return new RemoteConnectionTestResultDto
                {
                    Success = false,
                    ServerVersion = version,
                    Message = "Remote host has Docker but Docker Compose is missing.",
                    Prerequisites = prerequisites
                };
            }

            var composeVersion = composeStdout.Trim();
            prerequisites.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Docker Compose",
                Passed = true,
                Message = string.IsNullOrWhiteSpace(composeVersion) ? "Docker Compose is available." : $"Compose {composeVersion}"
            });

            return new RemoteConnectionTestResultDto
            {
                Success = true,
                ServerVersion = version,
                Message = string.IsNullOrWhiteSpace(version)
                    ? "Remote host is ready for deployment."
                    : $"Remote host is ready (Docker {version}).",
                Prerequisites = prerequisites
            };
        }
        catch (Exception ex)
        {
            return new RemoteConnectionTestResultDto
            {
                Success = false,
                Message = ex.Message,
                Prerequisites = prerequisites
            };
        }
        finally
        {
            await RunAsync("docker", $"context rm -f {contextName}", cancellationToken, throwOnError: false);
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

    public async Task<RemoteSetupResultDto> ProvisionRemoteHostAsync(
        string host,
        int sshPort,
        string user,
        string privateKey,
        RemoteSetupOptionsDto options,
        CancellationToken cancellationToken = default)
    {
        options ??= new RemoteSetupOptionsDto();
        if (options.RemoteOs == RemoteHostOs.Windows)
        {
            return new RemoteSetupResultDto
            {
                Success = false,
                Message = "Automated setup for Windows remote hosts is not supported yet. Use Linux (Ubuntu/Debian)."
            };
        }

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
            WriteSshConfig(contextName, host, port, user, privateKey);

            var (sshExit, _, sshStderr) = await RunSshAsync(contextName, ["echo", "ok"], cancellationToken);
            steps.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Verify SSH access",
                Passed = sshExit == 0,
                Message = sshExit == 0
                    ? "Connected to the remote host."
                    : FormatSshError(sshStderr, host, user, port)
            });
            if (sshExit != 0)
            {
                return FailSetup(steps, "SSH connection failed. Fix credentials before running setup.");
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
                        : $"Docker {dockerReady.Version} is already running (Compose {dockerReady.ComposeVersion})."
                });
            }
            else
            {
                var dockerSteps = await InstallLinuxDockerAsync(contextName, user, steps, cancellationToken);
                if (dockerSteps is not null)
                {
                    return dockerSteps;
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
        if (options.RemoteOs == RemoteHostOs.Windows)
        {
            return new RemoteSetupResultDto { Success = false, Message = "Windows host firewall sync is not supported yet." };
        }

        host = host.Trim();
        user = user.Trim();
        var port = sshPort <= 0 ? 22 : sshPort;
        user = SanitizeSshToken(user, "user");
        var contextName = $"acore-ext-fw-{Guid.NewGuid():N}";
        var steps = new List<RemotePrerequisiteCheckDto>();
        try
        {
            WriteSshConfig(contextName, host, port, user, privateKey);
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

        var setupCommands = new (string Label, string Command)[]
        {
            ("Update package lists", SudoAptGet("update -qq")),
            ("Install Docker Engine & Compose", SudoAptGet("install -y docker.io docker-compose-v2")),
            ("Start Docker service", SudoNonInteractive("systemctl start docker")),
            ("Enable Docker on boot", SudoNonInteractive("systemctl enable docker")),
            ("Grant Docker access to SSH user", SudoNonInteractive($"usermod -aG docker {user}")),
        };

        foreach (var (label, command) in setupCommands)
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
                return FailSetup(steps, $"Setup stopped at “{label}”. See the step detail below.");
            }
        }

        var (verifyExit, verifyOut, verifyErr) = await RunSshAsync(
            contextName,
            ["docker", "info", "--format", "{{.ServerVersion}}"],
            cancellationToken);
        steps.Add(new RemotePrerequisiteCheckDto
        {
            Name = "Verify Docker Engine",
            Passed = verifyExit == 0,
            Message = verifyExit == 0
                ? (string.IsNullOrWhiteSpace(verifyOut.Trim()) ? "Docker engine is responding." : $"Docker {verifyOut.Trim()} is running.")
                : FormatRemoteDockerError(verifyErr, string.Empty, user, 22)
        });
        if (verifyExit != 0)
        {
            return FailSetup(steps, "Docker was installed but the SSH user still cannot reach the engine.");
        }

        var (composeExit, composeOut, composeErr) = await RunSshAsync(
            contextName,
            ["docker", "compose", "version", "--short"],
            cancellationToken);
        steps.Add(new RemotePrerequisiteCheckDto
        {
            Name = "Verify Docker Compose",
            Passed = composeExit == 0,
            Message = composeExit == 0
                ? (string.IsNullOrWhiteSpace(composeOut.Trim()) ? "Docker Compose is available." : $"Compose {composeOut.Trim()}.")
                : FormatRemoteDockerError(composeErr, string.Empty, user, 22, fallback: "Docker Compose plugin is missing.")
        });
        if (composeExit != 0)
        {
            return FailSetup(steps, "Docker Engine is running but Docker Compose is not available.");
        }

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

        var (exit, stdout, stderr) = await RunSshRemoteShellAsync(
            contextName,
            $"{SudoAptGet("install -y unattended-upgrades")} && {SudoNonInteractive("systemctl enable unattended-upgrades")}",
            cancellationToken);
        steps.Add(new RemotePrerequisiteCheckDto
        {
            Name = "OS security baselines",
            Passed = exit == 0,
            Message = exit == 0
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

    private static RemoteSetupResultDto FailSetup(List<RemotePrerequisiteCheckDto> steps, string message)
        => new() { Success = false, Message = message, Steps = steps };

    private async Task<(bool Ready, string? Version, string? ComposeVersion)> IsRemoteDockerReadyAsync(
        string contextName,
        CancellationToken cancellationToken)
    {
        var (dockerExit, dockerOut, _) = await RunSshAsync(
            contextName,
            ["docker", "info", "--format", "{{.ServerVersion}}"],
            cancellationToken);
        if (dockerExit != 0)
        {
            return (false, null, null);
        }

        var (composeExit, composeOut, _) = await RunSshAsync(
            contextName,
            ["docker", "compose", "version", "--short"],
            cancellationToken);
        if (composeExit != 0)
        {
            return (false, dockerOut.Trim(), null);
        }

        return (true, dockerOut.Trim(), composeOut.Trim());
    }

    private async Task<bool> IsUnattendedUpgradesEnabledAsync(
        string contextName,
        CancellationToken cancellationToken)
    {
        var (exit, _, _) = await RunSshRemoteShellAsync(
            contextName,
            "dpkg -s unattended-upgrades >/dev/null 2>&1 && systemctl is-enabled unattended-upgrades >/dev/null 2>&1",
            cancellationToken);
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

    private static string SummarizeRemoteOutput(string stdout, string fallback)
    {
        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? $"{fallback} completed." : line;
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

        if (message.Contains("usage: sudo", StringComparison.OrdinalIgnoreCase)
            || message.Contains("expected one of these actions", StringComparison.OrdinalIgnoreCase))
        {
            return "sudo rejected the command on this host. Ensure the SSH user has passwordless sudo, or run the equivalent apt/ufw commands manually over SSH.";
        }

        return message;
    }

    private Task<(int ExitCode, string StdOut, string StdErr)> RunSshRemoteShellAsync(
        string contextName,
        string shellCommand,
        CancellationToken cancellationToken)
        => RunSshAsync(contextName, ["bash", "-lc", shellCommand], cancellationToken);

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
        var summaryScript =
            "set +e; " +
            "files=$(find /dest -type f ! -name .hashcache.json ! -name .manifest.json 2>/dev/null | wc -l | tr -d \" \"); " +
            "bytes=$(du -sb /dest 2>/dev/null | cut -f1); " +
            "[ -n \"$bytes\" ] || bytes=0; " +
            "wow=0; test -f /dest/Wow.exe -o -f /dest/WoW.exe && wow=1; " +
            "mpq=0; if test -d /dest/Data; then find /dest/Data -maxdepth 1 -name \"*.MPQ\" -print -quit 2>/dev/null | grep -q . && mpq=1; fi; " +
            "printf \"AZP_SUMMARY:%s\\t%s\\t%s\\t%s\\n\" \"$files\" \"$bytes\" \"$wow\" \"$mpq\"";
        var (exit, output, _) = await RunAlpineInVolumeAsync(
            contextArg, volumeName, readOnly: true, mountAt: "/dest", workDir: null, summaryScript, cancellationToken);
        if (exit != 0)
        {
            return summary;
        }

        var line = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith("AZP_SUMMARY:", StringComparison.Ordinal));
        if (line is null)
        {
            return summary;
        }

        var payload = line["AZP_SUMMARY:".Length..];
        var parts = payload.Split('\t');
        if (parts.Length >= 4)
        {
            int.TryParse(parts[0].Trim(), out var fileCount);
            long.TryParse(parts[1].Trim(), out var totalBytes);
            summary.FileCount = fileCount;
            summary.TotalBytes = totalBytes;
            summary.HasWowExe = parts[2].Trim() == "1";
            summary.HasDataMpq = parts[3].Trim() == "1";
        }

        return summary;
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

        // Match both the internal alias and the real hostname so `ssh alias …` (our probe) and
        // `ssh://user@hostname` (Docker context) resolve to the same key/user settings.
        var block = new StringBuilder()
            .Append(BeginMarker(contextName)).Append('\n')
            .Append($"Host {contextName} {host}\n")
            .Append($"    HostName {host}\n")
            .Append($"    User {user}\n")
            .Append($"    Port {port}\n")
            .Append($"    IdentityFile {keyPath}\n")
            .Append("    IdentitiesOnly yes\n")
            .Append("    StrictHostKeyChecking accept-new\n")
            .Append($"    UserKnownHostsFile {knownHostsPath}\n")
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

        foreach (var suffix in new[] { ".key", ".known_hosts" })
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

        return normalized + "\n";
    }

    private async Task EnsureDockerContextAsync(
        string contextName,
        string user,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var (inspectExit, _, _) = await RunAsync("docker", $"context inspect {contextName}", cancellationToken, throwOnError: false);
        // Use the same ubuntu@ec2-host form as manual SSH; IdentityFile comes from the Host block above.
        var endpoint = $"host={BuildDockerSshEndpoint(user, host, port)}";
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
    }

    private static string BuildDockerSshEndpoint(string user, string host, int port)
    {
        user = SanitizeSshToken(user, "user");
        host = SanitizeSshToken(host, "host");
        var portSuffix = port is > 0 and not 22 ? $":{port}" : string.Empty;
        return $"ssh://{user}@{host}{portSuffix}";
    }

    private Task<(int ExitCode, string StdOut, string StdErr)> RunSshAsync(
        string contextName,
        IReadOnlyList<string> remoteCommand,
        CancellationToken cancellationToken)
    {
        var sshConfigPath = Path.Combine(GetSshDir(), "config");
        var args = new List<string>
        {
            "-F", sshConfigPath,
            "-o", "BatchMode=yes",
            "-o", "ConnectTimeout=30",
            "-o", "ServerAliveInterval=15",
            "-o", "ServerAliveCountMax=4",
            contextName
        };
        args.AddRange(remoteCommand);
        return RunProcessAsync("ssh", args, cancellationToken, throwOnError: false);
    }

    private static string FormatSshError(string stderr, string host, string user, int port)
    {
        var message = string.IsNullOrWhiteSpace(stderr)
            ? $"Could not connect to {user}@{host}:{port} over SSH."
            : stderr.Trim();

        if (message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            message += " Verify the private key matches the remote authorized_keys entry.";
        }

        return message;
    }

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

        if (message.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
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

    private static string ManagementTunnelKey(string stackId, int remotePort) => $"{stackId}:{remotePort}";

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
}
