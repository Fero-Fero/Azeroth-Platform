using System.Text.Json;
using System.Text.Json.Serialization;
using AzerothPlatform.Launcher.Models;

namespace AzerothPlatform.Launcher.Services;

/// <summary>
/// Loads and persists <see cref="LauncherState"/> to the per-user application data directory.
/// </summary>
public sealed class LauncherStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly string _statePath;

    public LauncherStateStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AzerothPlatformLauncher");
        Directory.CreateDirectory(dir);
        _statePath = Path.Combine(dir, "launcher-state.json");
    }

    public string StatePath => _statePath;

    public LauncherState Load()
    {
        if (!File.Exists(_statePath))
        {
            return new LauncherState();
        }

        try
        {
            var json = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize<LauncherState>(json, JsonOptions) ?? new LauncherState();
        }
        catch
        {
            return new LauncherState();
        }
    }

    public void Save(LauncherState state)
    {
        var json = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(_statePath, json);
    }

    /// <summary>
    /// Loads optional distribution defaults from <c>launcher.settings.json</c> placed next to the
    /// executable. Returns empty defaults when the file is missing or invalid.
    /// </summary>
    public LauncherDefaults LoadDefaults()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "launcher.settings.json");
            if (!File.Exists(path))
            {
                return new LauncherDefaults();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<LauncherDefaults>(json, JsonOptions) ?? new LauncherDefaults();
        }
        catch
        {
            return new LauncherDefaults();
        }
    }
}
