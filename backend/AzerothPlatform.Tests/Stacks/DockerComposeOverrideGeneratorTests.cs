using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Modules;
using AzerothPlatform.Infrastructure.Services;
using Xunit;

namespace AzerothPlatform.Tests.Stacks;

public sealed class DockerComposeOverrideGeneratorTests
{
    [Fact]
    public void GameServices_EnableAutoSetupAndSkipCreatePrompt()
    {
        var yaml = DockerComposeOverrideGenerator.Generate("abc123", "test", serviceEnvironment: null);

        Assert.Contains("ac-db-import:", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("azp-dbimport.sh", yaml, StringComparison.Ordinal);
        Assert.Contains("volumes: !override", yaml, StringComparison.Ordinal);
        Assert.Contains("source: modules", yaml, StringComparison.Ordinal);
        Assert.Contains("target: /azerothcore/modules", yaml, StringComparison.Ordinal);
        Assert.Contains("nocopy: true", yaml, StringComparison.Ordinal);
        Assert.Contains("modules:/azerothcore/modules:ro", yaml, StringComparison.Ordinal);
        Assert.Contains("AC_UPDATES_ENABLE_DATABASES: \"7\"", yaml, StringComparison.Ordinal);
        Assert.Contains("AC_UPDATES_AUTO_SETUP: \"1\"", yaml, StringComparison.Ordinal);
        Assert.Contains("AC_UPDATES_EXCEPTION_SHUTDOWN_DELAY: \"10000\"", yaml, StringComparison.Ordinal);
        Assert.Contains("AC_DISABLE_INTERACTIVE: \"1\"", yaml, StringComparison.Ordinal);
        Assert.Contains("AC_CONSOLE_ENABLE: \"0\"", yaml, StringComparison.Ordinal);
        Assert.Contains("ac-authserver:", yaml, StringComparison.Ordinal);
        Assert.Contains("ac-worldserver:", yaml, StringComparison.Ordinal);

        var importIdx = yaml.IndexOf("ac-db-import:", StringComparison.Ordinal);
        var authIdx = yaml.IndexOf("ac-authserver:", StringComparison.Ordinal);
        var worldIdx = yaml.IndexOf("ac-worldserver:", StringComparison.Ordinal);
        Assert.True(yaml.IndexOf("AC_DISABLE_INTERACTIVE: \"1\"", importIdx, StringComparison.Ordinal) > importIdx);
        Assert.True(yaml.IndexOf("AC_DISABLE_INTERACTIVE: \"1\"", authIdx, StringComparison.Ordinal) > authIdx);
        Assert.True(yaml.IndexOf("AC_DISABLE_INTERACTIVE: \"1\"", worldIdx, StringComparison.Ordinal) > worldIdx);
        Assert.True(yaml.IndexOf("AC_CONSOLE_ENABLE: \"0\"", worldIdx, StringComparison.Ordinal) > worldIdx);
        var enableIdx = yaml.IndexOf("AC_UPDATES_ENABLE_DATABASES: \"7\"", StringComparison.Ordinal);
        Assert.True(enableIdx > importIdx && enableIdx < worldIdx);
        Assert.True(
            yaml.IndexOf("modules:/azerothcore/modules:ro", importIdx, StringComparison.Ordinal) > worldIdx
            && yaml.IndexOf("modules:/azerothcore/modules:ro", importIdx, StringComparison.Ordinal) < authIdx);
        Assert.DoesNotContain(DockerComposeOverrideGenerator.ManagerDataVolumeKey, yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ac-ollama", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("internal: true", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DbImport_mounts_manager_data_subpath_when_provided()
    {
        var yaml = DockerComposeOverrideGenerator.Generate(
            "abc123",
            "test",
            serviceEnvironment: null,
            managerDataVolumeName: "azeroth-platform-data",
            modulesSubpath: "stacks/abc123/azerothcore-wotlk/modules");

        var importIdx = yaml.IndexOf("ac-db-import:", StringComparison.Ordinal);
        var worldIdx = yaml.IndexOf("ac-worldserver:", StringComparison.Ordinal);
        Assert.True(importIdx >= 0 && worldIdx > importIdx);
        var importBlock = yaml[importIdx..worldIdx];
        Assert.Contains($"source: {DockerComposeOverrideGenerator.ManagerDataVolumeKey}", importBlock, StringComparison.Ordinal);
        Assert.Contains("subpath: stacks/abc123/azerothcore-wotlk/modules", importBlock, StringComparison.Ordinal);
        Assert.Contains("nocopy: true", importBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("source: modules", importBlock, StringComparison.Ordinal);
        Assert.Contains("name: azeroth-platform-data", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetDataVolumeSubpath_returns_path_under_builds_parent()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "azp-data-" + Guid.NewGuid().ToString("N"));
        var builds = Path.Combine(dataRoot, "stacks");
        var modules = Path.Combine(builds, "abc123", "azerothcore-wotlk", "modules");
        Directory.CreateDirectory(modules);
        try
        {
            Assert.True(DockerComposeOverrideGenerator.TryGetDataVolumeSubpath(modules, builds, out var relative));
            Assert.Equal("stacks/abc123/azerothcore-wotlk/modules", relative.Replace('\\', '/'));
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public void TryGetDataVolumeSubpath_sees_client_upload_staging_under_data_root()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "azp-data-" + Guid.NewGuid().ToString("N"));
        var builds = Path.Combine(dataRoot, "stacks");
        var staging = Path.Combine(dataRoot, "client", "upload-staging", "abc123", "extract");
        Directory.CreateDirectory(staging);
        try
        {
            Assert.True(DockerComposeOverrideGenerator.TryGetDataVolumeSubpath(staging, builds, out var relative));
            Assert.Equal("client/upload-staging/abc123/extract", relative.Replace('\\', '/'));
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public void OllamaSidecar_uses_hub_image_healthcheck_without_blocking_worldserver()
    {
        var ollama = new OllamaComposeOptions
        {
            Image = "ollama/ollama:latest",
            Model = "llama3.2:1b",
            ModelsVolumeName = "azeroth-platform-ollama-models",
            ModelsVolumeKey = "ollama_models",
            GpuBackend = GpuBackend.Cpu,
        };

        var yaml = DockerComposeOverrideGenerator.Generate(
            "abc123",
            "test",
            serviceEnvironment: null,
            ollama: ollama);

        Assert.Contains("  ollama:", yaml, StringComparison.Ordinal);
        Assert.Contains("  ollama-pull:", yaml, StringComparison.Ordinal);
        Assert.Contains("image: ollama/ollama:latest", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_policy: never", yaml.Substring(yaml.IndexOf("  ollama:", StringComparison.Ordinal)));
        Assert.DoesNotContain("11434:11434", yaml, StringComparison.Ordinal);
        Assert.Contains("OLLAMA_HOST: \"0.0.0.0\"", yaml, StringComparison.Ordinal);
        Assert.Contains("OLLAMA_NO_CLOUD: \"1\"", yaml, StringComparison.Ordinal);
        Assert.Contains("OLLAMA_KEEP_ALIVE: \"-1\"", yaml, StringComparison.Ordinal);
        Assert.Contains("OLLAMA_CONTEXT_LENGTH: \"2048\"", yaml, StringComparison.Ordinal);
        Assert.Contains("internal: true", yaml, StringComparison.Ordinal);
        Assert.Contains("name: acore-abc123-ollama", yaml, StringComparison.Ordinal);
        Assert.Contains("name: azeroth-platform-ollama-models", yaml, StringComparison.Ordinal);
        Assert.Contains("AC_CONSOLE_ENABLE: \"0\"", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("capabilities: [gpu]", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("NVIDIA_VISIBLE_DEVICES", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OLLAMA_VULKAN", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("/dev/kfd", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("/dev/dri", yaml, StringComparison.Ordinal);

        var pullIdx = yaml.IndexOf("  ollama-pull:", StringComparison.Ordinal);
        var serveIdx = yaml.IndexOf("  ollama:", pullIdx + "  ollama-pull:".Length, StringComparison.Ordinal);
        var topNetworksIdx = yaml.LastIndexOf("networks:", StringComparison.Ordinal);
        Assert.True(pullIdx >= 0 && serveIdx > pullIdx && topNetworksIdx > serveIdx);
        var pullBlock = yaml[pullIdx..serveIdx];
        var serveBlock = yaml[serveIdx..topNetworksIdx];
        Assert.Contains("ollama pull llama3.2:1b", pullBlock, StringComparison.Ordinal);
        Assert.Contains("pid=$$!", pullBlock, StringComparison.Ordinal);
        Assert.Contains("- ac-network", pullBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("OLLAMA_NO_CLOUD", pullBlock, StringComparison.Ordinal);
        Assert.Contains("OLLAMA_NO_CLOUD: \"1\"", serveBlock, StringComparison.Ordinal);
        Assert.Contains("OLLAMA_KEEP_ALIVE: \"-1\"", serveBlock, StringComparison.Ordinal);
        Assert.Contains("printf 'ok\\n' | ollama run llama3.2:1b", serveBlock, StringComparison.Ordinal);
        Assert.Contains("wait $$pid", serveBlock, StringComparison.Ordinal);
        Assert.Contains("ollama ps | grep -F llama3.2:1b", serveBlock, StringComparison.Ordinal);
        Assert.Contains("start_period: 120s", serveBlock, StringComparison.Ordinal);
        Assert.Contains("- ac-ollama", serveBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("- ac-network", serveBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("ollama pull", serveBlock, StringComparison.Ordinal);

        var worldIdx = yaml.IndexOf("  ac-worldserver:", StringComparison.Ordinal);
        var authIdx = yaml.IndexOf("  ac-authserver:", StringComparison.Ordinal);
        var worldBlock = yaml[worldIdx..authIdx];
        Assert.Contains("- ac-network", worldBlock, StringComparison.Ordinal);
        Assert.Contains("- ac-ollama", worldBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("      ollama:", worldBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void OllamaSidecar_adds_nvidia_reservation_when_backend_is_nvidia()
    {
        var yaml = GenerateOllama(GpuBackend.Nvidia);

        Assert.Contains("image: ollama/ollama:latest", yaml, StringComparison.Ordinal);
        Assert.Contains("NVIDIA_VISIBLE_DEVICES: \"all\"", yaml, StringComparison.Ordinal);
        Assert.Contains("driver: nvidia", yaml, StringComparison.Ordinal);
        Assert.Contains("capabilities: [gpu]", yaml, StringComparison.Ordinal);
        Assert.Contains("OLLAMA_KEEP_ALIVE: \"-1\"", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OLLAMA_VULKAN", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("/dev/kfd", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("/dev/dri", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void OllamaSidecar_emits_rocm_devices_and_rocm_image()
    {
        var yaml = GenerateOllama(GpuBackend.Rocm, image: OllamaSidecar.RocmImage);

        Assert.Contains("image: ollama/ollama:rocm", yaml, StringComparison.Ordinal);
        Assert.Contains("OLLAMA_KEEP_ALIVE: \"-1\"", yaml, StringComparison.Ordinal);
        Assert.Contains("- /dev/kfd", yaml, StringComparison.Ordinal);
        Assert.Contains("- /dev/dri", yaml, StringComparison.Ordinal);
        Assert.Contains("- video", yaml, StringComparison.Ordinal);
        Assert.Contains("- render", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("NVIDIA_VISIBLE_DEVICES", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("driver: nvidia", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OLLAMA_VULKAN", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void OllamaSidecar_emits_vulkan_device_and_env()
    {
        var yaml = GenerateOllama(GpuBackend.Vulkan);

        Assert.Contains("image: ollama/ollama:latest", yaml, StringComparison.Ordinal);
        Assert.Contains("OLLAMA_VULKAN: \"1\"", yaml, StringComparison.Ordinal);
        Assert.Contains("OLLAMA_KEEP_ALIVE: \"-1\"", yaml, StringComparison.Ordinal);
        Assert.Contains("- /dev/dri", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("/dev/kfd", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("NVIDIA_VISIBLE_DEVICES", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("driver: nvidia", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void FromSidecar_swaps_default_hub_image_for_rocm()
    {
        var options = OllamaComposeOptions.FromSidecar(OllamaSidecar.Default, GpuBackend.Rocm);
        Assert.Equal(OllamaSidecar.RocmImage, options.Image);
        Assert.Equal(GpuBackend.Rocm, options.GpuBackend);
    }

    [Fact]
    public void FromSidecar_keeps_custom_image_on_rocm()
    {
        var sidecar = new ModuleRuntimeSidecar
        {
            ServiceName = OllamaSidecar.ServiceName,
            Image = "ghcr.io/example/ollama:custom",
            Model = OllamaSidecar.Model,
            ModelsVolumeName = OllamaSidecar.ModelsVolumeName,
            ModelsVolumeKey = OllamaSidecar.ModelsVolumeKey,
        };
        var options = OllamaComposeOptions.FromSidecar(sidecar, GpuBackend.Rocm);
        Assert.Equal("ghcr.io/example/ollama:custom", options.Image);
    }

    [Fact]
    public void LlmChatterBridge_joins_the_database_and_ollama_networks_and_mounts_the_conf()
    {
        var yaml = DockerComposeOverrideGenerator.Generate(
            "abc123",
            "test",
            serviceEnvironment: null,
            ollama: new OllamaComposeOptions
            {
                Image = "ollama/ollama:latest",
                Model = "llama3.2:1b",
                ModelsVolumeName = "azeroth-platform-ollama-models",
                ModelsVolumeKey = "ollama_models",
                GpuBackend = GpuBackend.Cpu,
            },
            llmChatterBridge: LlmChatterBridgeComposeOptions.ForStack("abc123"));

        Assert.Contains($"  {LlmChatterBridge.ServiceName}:", yaml, StringComparison.Ordinal);
        Assert.Contains($"image: {LlmChatterBridge.ImageTag("abc123")}", yaml, StringComparison.Ordinal);
        Assert.Contains("-llm-chatter-bridge", yaml, StringComparison.Ordinal);
        Assert.Contains("pull_policy: never", yaml, StringComparison.Ordinal);
        Assert.Contains($"${{DOCKER_VOL_ETC}}:{LlmChatterBridge.ConfMountPath}:ro", yaml, StringComparison.Ordinal);
        Assert.Contains(DockerComposeOverrideGenerator.OllamaNetworkKey, yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void LlmChatterBridge_is_absent_when_the_module_is_not_selected()
    {
        var yaml = DockerComposeOverrideGenerator.Generate("abc123", "test", serviceEnvironment: null);

        Assert.DoesNotContain(LlmChatterBridge.ServiceName, yaml, StringComparison.Ordinal);
    }

    private static string GenerateOllama(GpuBackend backend, string? image = null) =>
        DockerComposeOverrideGenerator.Generate(
            "abc123",
            "test",
            serviceEnvironment: null,
            ollama: new OllamaComposeOptions
            {
                Image = image ?? "ollama/ollama:latest",
                Model = "llama3.2:1b",
                ModelsVolumeName = "azeroth-platform-ollama-models",
                ModelsVolumeKey = "ollama_models",
                GpuBackend = backend,
            });

    [Fact]
    public void GetContainerNameForService_maps_ollama()
    {
        var name = DockerComposeOverrideGenerator.GetContainerNameForService("abc123", "test", "ollama");
        Assert.Equal("acore-test-abc123-ollama", name);
        Assert.Equal(
            "acore-test-abc123-ollama-pull",
            DockerComposeOverrideGenerator.GetContainerNameForService("abc123", "test", "ollama-pull"));
    }

    [Fact]
    public void ClientService_writes_auth_token_from_compose_options()
    {
        var yaml = DockerComposeOverrideGenerator.Generate(
            "abc123",
            "test",
            serviceEnvironment: null,
            client: new ClientComposeOptions
            {
                ImageName = "azeroth-platform-client:local",
                ContainerPort = 8090,
                AuthToken = "client-auth-secret",
                DbPassword = "db-secret",
                StackId = "abc123",
            });

        Assert.Contains("CLIENT_AUTH_TOKEN: \"client-auth-secret\"", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization: Bearer", yaml, StringComparison.Ordinal);
    }
}
