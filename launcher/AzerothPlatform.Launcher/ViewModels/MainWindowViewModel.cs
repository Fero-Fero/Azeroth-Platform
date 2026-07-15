using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AzerothPlatform.Launcher.Models;
using AzerothPlatform.Launcher.Services;

namespace AzerothPlatform.Launcher.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly LauncherStateStore _stateStore;
    private readonly LauncherState _state;
    private readonly LauncherDefaults _defaults;
    private readonly ProfileContentService _profileContent = new();
    private readonly RegistryReconciler _reconciler = new();

    // The launcher is stack-hosted: it talks only to each stack's own container (/portal + /manifest +
    // /files + /login + /launcher) and reconciles the replicated registry across all known stacks. The
    // manager is never in the player path — it only builds+pushes the registry to the stacks.

    // The baked manifest signing pubkey used to verify client manifests that arrive over the stack's
    // plain-HTTP channel (trust no longer depends on a manager TLS channel).
    private string? _manifestPublicKey;

    // Portal URL of the healthy stack serving the newest launcher build (self-update source).
    private string? _bestLauncherPortalUrl;

    private CancellationTokenSource? _cts;
    // Monotonic token identifying the current sync operation. Progress<T> posts its callbacks to the
    // UI thread asynchronously, so a plan pass that never yields (up-to-date install / warm hash cache)
    // can flush its per-file progress *after* we've already set the final status - which would clobber
    // "Ready to play" back to "Verifying files (x/x)". Reports whose captured token no longer matches
    // are dropped so the resolved end state stays authoritative.
    private int _progressGeneration;
    private LauncherProfilesResponse? _profilesDoc;
    private ClientManifest? _pendingBaseManifest;
    private LauncherConfig? _pendingProfileConfig;
    private SyncPlan? _pendingBasePlan;
    private ClientManifest? _pendingOverlayManifest;
    private bool _overlayNeedsSync;
    // Set during a full verify so the pending overlay sync re-hashes files from disk (not the cache),
    // catching corrupt overlay content. Consumed and cleared by the subsequent update.
    private bool _forceOverlayRevalidate;
    private bool _suppressProfileReload;
    private int _profileLoadGeneration;
    private readonly CancellationTokenSource _shutdownCts = new();
    private CancellationTokenSource? _registryRefreshCts;

    private const int PlayTab = 0;
    private const int AddonsTab = 1;
    private const int SettingsTab = 2;

    public MainWindowViewModel(LauncherStateStore stateStore)
    {
        _stateStore = stateStore;
        _state = stateStore.Load();
        _defaults = stateStore.LoadDefaults();

        // Normalize whatever we load so a URL saved without a scheme (e.g. "192.168.1.50:8080")
        // from an older build or hand-edited state file doesn't crash the first fetch with
        // "Invalid URI: The format of the URI could not be determined."
        _serverUrl = NormalizePortalUrl(_state.ServerUrl)
            ?? NormalizePortalUrl(_defaults.ServerUrl)
            ?? "http://localhost:8101";

        // Seed the effective portal URL back onto the in-memory state so reconciliation has a starting
        // stack out of the box before the user has saved anything. Not persisted until the user clicks
        // Save, so the launcher is plug-and-play while still letting a distribution override the default.
        _state.ServerUrl = _serverUrl;

        _installDirectory = !string.IsNullOrWhiteSpace(_state.InstallDirectory)
            ? _state.InstallDirectory!
            : SuggestInstallDirectory(_defaults);

        _brandingTitle = !string.IsNullOrWhiteSpace(_defaults.BrandingTitle)
            ? _defaults.BrandingTitle!
            : "Azeroth Platform Launcher";

        RequireLogin = _defaults.RequireLogin;

        _manifestPublicKey = _defaults.SigningPublicKey;

        // Seed the known-servers list from the baked portal URL on first run so reconciliation has a
        // starting point; it grows itself from the registry on each reconcile.
        if (_state.KnownServers.Count == 0 && !string.IsNullOrWhiteSpace(_serverUrl))
        {
            _state.KnownServers.Add(_serverUrl);
        }

        _stateFilePath = stateStore.StatePath;
        // With a server URL always available we can browse published servers + news immediately, so
        // open on the Play tab. Downloading/playing still requires choosing an install folder.
        _selectedTabIndex = !string.IsNullOrWhiteSpace(_serverUrl) ? PlayTab : SettingsTab;
        _statusText = _state.IsConfigured
            ? "Checking for updates..."
            : "Pick an install folder in Settings to download and play.";
    }

    private static string SuggestInstallDirectory(LauncherDefaults defaults)
    {
        if (!string.IsNullOrWhiteSpace(defaults.DefaultInstallDirectory))
        {
            return defaults.DefaultInstallDirectory!.Trim();
        }

        var folderName = !string.IsNullOrWhiteSpace(defaults.AppName) ? defaults.AppName!
            : !string.IsNullOrWhiteSpace(defaults.DefaultInstallSubfolder) ? defaults.DefaultInstallSubfolder!
            : "Azeroth Platform";

        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = AppContext.BaseDirectory;
        }

        return Path.Combine(baseDir, folderName);
    }

    public Func<Task<string?>>? PickFolderAsync { get; set; }

    // ----- Collections -----

    public ObservableCollection<LauncherProfile> Profiles { get; } = new();
    public ObservableCollection<AddonToggle> Addons { get; } = new();
    public ObservableCollection<NewsItem> News { get; } = new();

    /// <summary>The first few articles shown as cards in the List view.</summary>
    public ObservableCollection<NewsItem> TopNews { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfiles))]
    [NotifyPropertyChangedFor(nameof(PlayEnabled))]
    [NotifyPropertyChangedFor(nameof(UpdateEnabled))]
    [NotifyPropertyChangedFor(nameof(SelectedServerName))]
    [NotifyPropertyChangedFor(nameof(CanOpenArmory))]
    [NotifyPropertyChangedFor(nameof(LoginEnabled))]
    [NotifyPropertyChangedFor(nameof(CanRegister))]
    private LauncherProfile? _selectedProfile;

    public bool HasProfiles => Profiles.Count > 0;

    /// <summary>Display name of the selected server, shown under the server picker.</summary>
    public string SelectedServerName => SelectedProfile?.DisplayName ?? string.Empty;

    /// <summary>True when the selected server exposes an armory the "View all news" card can open.</summary>
    public bool CanOpenArmory => SelectedProfile is { ArmoryPort: > 0 };
    public bool HasNews => News.Count > 0;
    public bool HasMoreNews => News.Count > TopNews.Count;
    public bool HasAddons => Addons.Count > 0;

    // ----- News views -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewsList))]
    [NotifyPropertyChangedFor(nameof(IsNewsReading))]
    [NotifyPropertyChangedFor(nameof(IsNewsGrid))]
    private NewsViewMode _newsViewMode = NewsViewMode.List;

    public bool IsNewsList => NewsViewMode == NewsViewMode.List;
    public bool IsNewsReading => NewsViewMode == NewsViewMode.Reading;
    public bool IsNewsGrid => NewsViewMode == NewsViewMode.Grid;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadingTitle))]
    [NotifyPropertyChangedFor(nameof(ReadingDate))]
    private NewsItem? _selectedNews;

    /// <summary>Full HTML document for the reading view; the view navigates the WebView to it.</summary>
    [ObservableProperty]
    private string? _readingHtml;

    public string ReadingTitle => SelectedNews?.Title ?? string.Empty;
    public string ReadingDate => SelectedNews?.Date ?? string.Empty;

    /// <summary>Accent color (hex) currently applied; used to theme the reading-view HTML.</summary>
    private string _accentHex = "#F0C869";

    /// <summary>Accent color as a brush, used for the nav underline and other accented UI bits.</summary>
    [ObservableProperty]
    private IBrush _accentBrush = new SolidColorBrush(Color.Parse("#F0C869"));

    // ----- Tabs / configuration -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlayTab))]
    [NotifyPropertyChangedFor(nameof(IsAddonsTab))]
    [NotifyPropertyChangedFor(nameof(IsSettingsTab))]
    [NotifyPropertyChangedFor(nameof(ShowLoginScreen))]
    private int _selectedTabIndex;

    public bool IsPlayTab => SelectedTabIndex == PlayTab;
    public bool IsAddonsTab => SelectedTabIndex == AddonsTab;
    public bool IsSettingsTab => SelectedTabIndex == SettingsTab;

    /// <summary>Switches the top-left nav between Play/Addons/Settings (parameter is the tab index).</summary>
    [RelayCommand]
    private void SelectTab(string index)
    {
        if (int.TryParse(index, out var i))
        {
            SelectedTabIndex = i;
        }
    }

    [ObservableProperty]
    private string _serverUrl;

    [ObservableProperty]
    private string _installDirectory;

    [ObservableProperty]
    private string _stateFilePath = string.Empty;

    // ----- Branding / status -----

    [ObservableProperty]
    private string _brandingTitle;

    [ObservableProperty]
    private string _clientVersion = string.Empty;

    [ObservableProperty]
    private string _realmlistInfo = string.Empty;

    /// <summary>
    /// Editable realmlist address written into realmlist.wtf on play/update. Auto-populated with the
    /// selected server's realmlist whenever the profile changes; the player can override it here.
    /// </summary>
    [ObservableProperty]
    private string _realmlistOverride = string.Empty;

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _progressIndeterminate;

    [ObservableProperty]
    private Bitmap? _backgroundImage;

    [ObservableProperty]
    private Bitmap? _logoImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LauncherUpdateAvailable))]
    private string? _launcherUpdateVersion;

    public bool LauncherUpdateAvailable => !string.IsNullOrEmpty(LauncherUpdateVersion);

    // ----- Command enablement -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    [NotifyPropertyChangedFor(nameof(UpdateEnabled))]
    [NotifyPropertyChangedFor(nameof(PlayEnabled))]
    [NotifyPropertyChangedFor(nameof(CancelVisible))]
    [NotifyPropertyChangedFor(nameof(ShowPlay))]
    [NotifyPropertyChangedFor(nameof(ShowUpdate))]
    [NotifyPropertyChangedFor(nameof(CanSaveSettings))]
    private bool _isBusy;

    public bool CancelVisible => IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayEnabled))]
    private bool _canPlay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateEnabled))]
    [NotifyPropertyChangedFor(nameof(ShowPlay))]
    [NotifyPropertyChangedFor(nameof(ShowUpdate))]
    private bool _needsUpdate;

    /// <summary>
    /// Whether the selected profile's game executable actually exists in the install folder. When it
    /// doesn't, the footer's primary action becomes "Install" instead of "Play" so we never try to
    /// launch a client that hasn't been downloaded yet.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayEnabled))]
    [NotifyPropertyChangedFor(nameof(UpdateEnabled))]
    [NotifyPropertyChangedFor(nameof(ShowPlay))]
    [NotifyPropertyChangedFor(nameof(ShowUpdate))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionLabel))]
    private bool _isInstalled;

    /// <summary>Set once a base sync plan has been computed, so the Install/Update button only acts when there's something to do.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateEnabled))]
    private bool _planReady;

    public bool CanInteract => !IsBusy;
    public bool CanSaveSettings => !IsSavingSettings && (!IsBusy || IsConnecting);
    public bool UpdateEnabled => !IsBusy && PlanReady && (NeedsUpdate || !IsInstalled) && SelectedProfile is not null;
    public bool PlayEnabled => !IsBusy && CanPlay && IsInstalled && SelectedProfile is not null && (!RequireLogin || IsLoggedIn);

    // ----- Login (only used when the build was compiled with "require login") -----

    /// <summary>Whether this build requires the player to log in before downloading/playing.</summary>
    public bool RequireLogin { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoginScreen))]
    [NotifyPropertyChangedFor(nameof(PlayEnabled))]
    private bool _isLoggedIn;

    /// <summary>True while a login request is in flight (disables the Login button, shows a spinner).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoginEnabled))]
    private bool _isLoggingIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoginEnabled))]
    private string _loginUsername = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoginEnabled))]
    private string _loginPassword = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoginError))]
    private string _loginError = string.Empty;

    public bool HasLoginError => !string.IsNullOrWhiteSpace(LoginError);

    /// <summary>
    /// Whether the blocking login overlay is shown. Only when this build requires login and the player
    /// isn't signed in. Yields to the maintenance overlay, and steps aside on the Settings tab so the
    /// server URL can always be corrected (playing is still gated separately on <see cref="IsLoggedIn"/>).
    /// </summary>
    public bool ShowLoginScreen => RequireLogin && !IsLoggedIn && !IsServerUnavailable && !IsSettingsTab;

    // ----- Server availability (maintenance) -----

    /// <summary>
    /// True when the launcher can't reach the backend at all (server offline / undergoing maintenance).
    /// Drives a blocking overlay that replaces the disabled Play/Install buttons with a clear message
    /// and a Retry action, instead of leaving the user stuck on a greyed-out main page.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoginScreen))]
    private bool _isServerUnavailable;

    /// <summary>The underlying connection error, shown small under the maintenance notice.</summary>
    [ObservableProperty]
    private string _serverUnavailableDetail = string.Empty;

    /// <summary>
    /// True while the initial (or a retried) reconcile is contacting the servers and we don't yet know
    /// whether any are reachable. Starts true so the very first paint shows a "connecting" state rather
    /// than briefly flashing "server not available" before the first /portal response arrives.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectingMessage))]
    [NotifyPropertyChangedFor(nameof(ShowNoServerMessage))]
    [NotifyPropertyChangedFor(nameof(CanSaveSettings))]
    private bool _isConnecting = true;

    /// <summary>Login overlay: show the "connecting…" state while the reconcile is still in flight.</summary>
    public bool ShowConnectingMessage => IsConnecting && !HasProfiles;

    /// <summary>Login overlay: show "no server" only once connecting finished without finding a profile.</summary>
    public bool ShowNoServerMessage => !IsConnecting && !HasProfiles;

    /// <summary>The Login button is enabled once a server is selected and both fields are filled.</summary>
    public bool LoginEnabled => !IsLoggingIn && SelectedProfile is not null
        && !string.IsNullOrWhiteSpace(LoginUsername) && !string.IsNullOrWhiteSpace(LoginPassword);

    /// <summary>Register is only offered when the selected server exposes an armory to register on.</summary>
    public bool CanRegister => CanOpenArmory;

    // Footer primary action: exactly one of Play / Install-or-Update shows when idle; Cancel replaces
    // them when busy. When the client isn't present in the install folder the download button reads
    // "Install"; once installed it reads "Update" and only shows when files are out of date.
    public bool ShowUpdate => !IsBusy && (NeedsUpdate || !IsInstalled);
    public bool ShowPlay => !IsBusy && !NeedsUpdate && IsInstalled;

    /// <summary>Label for the footer download button: "Install" before the client exists, else "Update".</summary>
    public string PrimaryActionLabel => IsInstalled ? "Update" : "Install";

    /// <summary>Requests app shutdown so a downloaded launcher update can replace the exe. Set by the view.</summary>
    public Action? RequestShutdown { get; set; }

    public void StopBackgroundWork()
    {
        _shutdownCts.Cancel();
        _registryRefreshCts?.Cancel();
        _cts?.Cancel();
    }

    public async Task InitializeAsync()
    {
        // Plug-and-play: as long as we have a server URL (defaults to the local platform at
        // http://localhost:8080) load its published servers + news right away, without requiring the
        // user to save settings first. Downloading/playing is still gated on a chosen install folder.
        if (string.IsNullOrWhiteSpace(_state.ServerUrl))
        {
            IsConnecting = false;
            return;
        }

        await LoadProfilesAsync();
    }

    // ----- Profiles -----

    /// <summary>
    /// Stack-portal reconciliation: query <c>/portal</c> from every known stack, merge the replicated
    /// registry (newest revision per stack), health-ping, and populate the profile list — all without
    /// the manager. Self-heals the known-servers list and picks the newest launcher for self-update.
    /// </summary>
    private async Task LoadProfilesAsync()
    {
        var generation = ++_profileLoadGeneration;
        CancelBackgroundRegistryRefresh();
        IsBusy = true;
        IsConnecting = true;
        IsServerUnavailable = false;
        StatusText = "Finding servers...";
        try
        {
            // Always include the effective/saved server URL alongside the known-servers list so a URL the
            // player set in Settings is tried even if the persisted list still holds stale entries.
            var seeds = new List<string>();
            if (!string.IsNullOrWhiteSpace(_state.ServerUrl))
            {
                seeds.Add(_state.ServerUrl!);
            }
            seeds.AddRange(_state.KnownServers);
            if (seeds.Count == 0)
            {
                seeds.Add(ServerUrl);
            }

            var currentServer = seeds.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(currentServer))
            {
                IsBusy = false;
                IsConnecting = false;
                return;
            }

            var result = await _reconciler.ReconcileCurrentAsync(currentServer, _shutdownCts.Token);
            if (generation != _profileLoadGeneration)
            {
                return;
            }

            ApplyProfileResult(result, preserveSelection: false);

            if (!result.AnyReachable)
            {
                IsBusy = false;
                IsServerUnavailable = true;
                ServerUnavailableDetail = "None of the known servers responded.";
                StatusText = "Undergoing maintenance";
                DetailText = "No known server is reachable. Add a server by IP in Settings to recover.";
                return;
            }

            if (SelectedProfile is null)
            {
                StatusText = "No servers are published yet. Check back later.";
                IsBusy = false;
                return;
            }

            IsBusy = false;
            await OnProfileSelectedAsync();
            _ = CheckLauncherUpdateAsync(ArtifactSource());
            StartBackgroundRegistryRefresh(_state.KnownServers, generation);
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            IsBusy = false;
        }
        catch (Exception ex)
        {
            IsBusy = false;
            IsServerUnavailable = true;
            ServerUnavailableDetail = ex.Message;
            StatusText = "Undergoing maintenance";
            DetailText = "The server is offline or unreachable.";
        }
        finally
        {
            if (generation == _profileLoadGeneration)
            {
                // Whatever the outcome, the initial reconcile is done: stop showing the "connecting" state so
                // the login overlay resolves to either the sign-in form (servers found) or the clear
                // "no server" message (none found), never flashing the error mid-connect.
                IsConnecting = false;
            }
        }
    }

    private void ApplyProfileResult(ReconcileResult result, bool preserveSelection)
    {
        _profilesDoc = result.Profiles;
        _state.KnownServers = result.KnownServers;
        _bestLauncherPortalUrl = result.BestLauncherPortalUrl;
        _stateStore.Save(_state);

        if (!string.IsNullOrWhiteSpace(_profilesDoc.BrandingTitle))
        {
            BrandingTitle = _profilesDoc.BrandingTitle;
        }

        ApplyAccentColor(_profilesDoc.AccentColor);

        var selectedStackId = preserveSelection
            ? SelectedProfile?.StackId ?? _state.SelectedProfileId
            : _state.SelectedProfileId;

        _suppressProfileReload = true;
        Profiles.Clear();
        foreach (var profile in _profilesDoc.Profiles.OrderBy(p => p.SortOrder))
        {
            Profiles.Add(profile);
        }
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(ShowConnectingMessage));
        OnPropertyChanged(nameof(ShowNoServerMessage));

        SelectedProfile = Profiles.FirstOrDefault(p => p.StackId == selectedStackId)
            ?? Profiles.FirstOrDefault();
        _suppressProfileReload = false;
    }

    private void StartBackgroundRegistryRefresh(IEnumerable<string> seeds, int generation)
    {
        CancelBackgroundRegistryRefresh();
        _registryRefreshCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        var token = _registryRefreshCts.Token;
        var seedList = seeds
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _ = RefreshRegistryInBackgroundAsync(seedList, generation, token);
    }

    private async Task RefreshRegistryInBackgroundAsync(List<string> seeds, int generation, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _reconciler.ReconcileAsync(seeds, cancellationToken);
            if (cancellationToken.IsCancellationRequested || generation != _profileLoadGeneration || !result.AnyReachable)
            {
                return;
            }

            ApplyProfileResult(result, preserveSelection: true);
            _ = CheckLauncherUpdateAsync(ArtifactSource());
        }
        catch (OperationCanceledException)
        {
            // The launcher is closing or a newer foreground load superseded this refresh.
        }
        catch
        {
            // Background discovery must never interrupt the currently usable server.
        }
    }

    private void CancelBackgroundRegistryRefresh()
    {
        _registryRefreshCts?.Cancel();
        _registryRefreshCts?.Dispose();
        _registryRefreshCts = null;
    }

    /// <summary>
    /// Manually adds a stack by host/IP (the recovery fallback when every known server is unreachable):
    /// fetches a fresh <c>/portal</c> from it, prioritizes it as a seed, and re-reconciles from there.
    /// </summary>
    [RelayCommand]
    private async Task AddServerByHostAsync(string? hostOrUrl)
    {
        var normalized = NormalizePortalUrl(hostOrUrl);
        if (normalized is null)
        {
            StatusText = "Enter a valid server host or URL.";
            return;
        }

        StatusText = "Contacting server...";
        var doc = await new PortalClient(normalized, PortalClient.ProbeTimeout).GetPortalAsync(CancellationToken.None);
        if (doc is null)
        {
            StatusText = "Could not reach that server.";
            DetailText = "No portal responded at that address. Check the host/IP and port.";
            return;
        }

        // Prioritize the manually-added server: put it first so its (fresh) view seeds reconciliation.
        _state.ServerUrl = normalized;
        ServerUrl = normalized;
        _state.KnownServers.Remove(normalized);
        _state.KnownServers.Insert(0, normalized);
        _stateStore.Save(_state);

        await LoadProfilesAsync();
    }

    /// <summary>
    /// Normalizes a portal host/URL for the known-servers list. Unlike the manager URL, a stack portal
    /// is reached over plain HTTP (LAN/VPC), so a bare host defaults to <c>http://</c>.
    /// </summary>
    private static string? NormalizePortalUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var candidate = raw.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "http://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return candidate.TrimEnd('/');
    }

    /// <summary>The self-update artifact source: the healthy stack advertising the newest launcher build.</summary>
    private ILauncherArtifactSource? ArtifactSource() =>
        string.IsNullOrWhiteSpace(_bestLauncherPortalUrl) ? null : new PortalClient(_bestLauncherPortalUrl);

    /// <summary>Re-attempts the connection from the maintenance overlay.</summary>
    [RelayCommand]
    private async Task RetryConnectionAsync() => await LoadProfilesAsync();

    /// <summary>
    /// From the maintenance overlay: dismiss it and open Settings so the player can correct the server
    /// URL. Saving in Settings re-runs the connection, which re-shows the overlay if it still fails.
    /// </summary>
    [RelayCommand]
    private void OpenSettingsFromMaintenance()
    {
        IsServerUnavailable = false;
        SelectedTabIndex = SettingsTab;
    }

    /// <summary>Applies the selected style template's accent color to the Fluent theme at runtime.</summary>
    private void ApplyAccentColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || !Color.TryParse(hex, out var color))
        {
            return;
        }

        _accentHex = hex.Trim();
        AccentBrush = new SolidColorBrush(color);

        if (Application.Current is not { } app)
        {
            return;
        }

        app.Resources["SystemAccentColor"] = color;
        app.Resources["SystemAccentColorLight1"] = Blend(color, Colors.White, 0.25);
        app.Resources["SystemAccentColorLight2"] = Blend(color, Colors.White, 0.50);
        app.Resources["SystemAccentColorLight3"] = Blend(color, Colors.White, 0.75);
        app.Resources["SystemAccentColorDark1"] = Blend(color, Colors.Black, 0.25);
        app.Resources["SystemAccentColorDark2"] = Blend(color, Colors.Black, 0.50);
        app.Resources["SystemAccentColorDark3"] = Blend(color, Colors.Black, 0.75);
    }

    private static Color Blend(Color a, Color b, double t)
    {
        byte Mix(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromArgb(a.A, Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B));
    }

    partial void OnSelectedProfileChanged(LauncherProfile? value)
    {
        _state.SelectedProfileId = value?.StackId;
        _stateStore.Save(_state);

        // Game accounts are per-server, so a session on one server doesn't authorize another. Rather
        // than blanket-dropping the login on every switch, restore whatever we've remembered for the
        // newly-selected profile: switching back to a server you already signed into (this run or a
        // previous launch) keeps you logged in, while a server you haven't authenticated with still
        // shows the login screen.
        if (RequireLogin)
        {
            var savedUsername = value is null ? null : _state.GetProfile(value.StackId).LoggedInUsername;
            IsLoggedIn = !string.IsNullOrWhiteSpace(savedUsername);
            LoginUsername = savedUsername ?? string.Empty;
            LoginPassword = string.Empty;
            LoginError = string.Empty;
        }

        if (_suppressProfileReload || value is null)
        {
            return;
        }

        _ = OnProfileSelectedAsync();
    }

    private async Task OnProfileSelectedAsync()
    {
        var profile = SelectedProfile;
        if (profile is null)
        {
            return;
        }

        // Per-profile accent (per-stack template override) beats the global template accent.
        ApplyAccentColor(string.IsNullOrWhiteSpace(profile.AccentColor) ? _profilesDoc?.AccentColor : profile.AccentColor);

        RealmlistInfo = string.IsNullOrWhiteSpace(profile.RealmlistHost)
            ? string.Empty
            : $"Realm: {profile.RealmlistHost}:{profile.RealmlistPort}";

        // Reflect the selected server's realmlist immediately; ApplyConfigToUi refines it once the
        // server config is fetched (which also carries the exact realmlist.wtf address).
        RealmlistOverride = profile.RealmlistHost ?? string.Empty;

        await LoadBrandingAsync(profile);
        LoadAddons(profile);
        await CheckForUpdatesAsync(fullVerify: false);
    }

    private void LoadAddons(LauncherProfile profile)
    {
        Addons.Clear();
        var profileState = _state.GetProfile(profile.StackId);
        foreach (var addon in profileState.DownloadedAddons.OrderBy(a => a))
        {
            var enabled = profileState.EnabledAddons.Contains(addon, StringComparer.OrdinalIgnoreCase);
            Addons.Add(new AddonToggle(addon, enabled, OnAddonToggled));
        }

        OnPropertyChanged(nameof(HasAddons));
    }

    private void OnAddonToggled(AddonToggle toggle, bool enabled)
    {
        var profile = SelectedProfile;
        if (profile is null)
        {
            return;
        }

        try
        {
            _profileContent.SetAddonEnabled(_state.InstallDirectory!, profile, _state, toggle.Name, enabled);
            _stateStore.Save(_state);
        }
        catch (Exception ex)
        {
            StatusText = "Could not toggle addon.";
            DetailText = ex.Message;
        }
    }

    // Shared client for fetching branding images from a stack's own /branding/* endpoints.
    private static readonly System.Net.Http.HttpClient BrandingHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    /// <summary>
    /// Loads the selected profile's branding. The wallpaper/logo are hosted by the stack's own client
    /// container (advertised as absolute URLs during reconciliation) and fetched directly — the manager
    /// is not in the player path. Missing/unreachable assets simply leave the launcher unbranded.
    /// </summary>
    // Monotonic token for branding/news loads. Fetches post their results asynchronously, so a slow or
    // failed load from a previously-selected profile (or an overlapping retry) could otherwise finish late
    // and clobber the current profile's wallpaper - making it flicker or vanish. Results whose captured
    // token no longer matches the latest load are dropped.
    private int _brandingGeneration;

    // Base URL of the currently-selected stack, used to resolve relative /news-image/* URLs embedded in
    // news article bodies when rendering the reading view.
    private string _newsBaseUrl = string.Empty;

    // URLs of the images currently shown, so a reconcile/retry for the unchanged profile doesn't re-decode
    // and reassign the same wallpaper/logo (which itself briefly flickers the image).
    private string? _loadedBackgroundUrl;
    private string? _loadedLogoUrl;

    private static readonly JsonSerializerOptions NewsJsonOptions = new(JsonSerializerDefaults.Web);

    private async Task LoadBrandingAsync(LauncherProfile? profile)
    {
        var generation = ++_brandingGeneration;

        // News belongs to the previous profile; clear it immediately and let LoadNewsAsync repopulate.
        News.Clear();
        TopNews.Clear();
        SelectedNews = null;
        ReadingHtml = null;
        NewsViewMode = NewsViewMode.List;
        _newsBaseUrl = profile?.PortalUrl?.TrimEnd('/') ?? string.Empty;
        OnPropertyChanged(nameof(HasNews));
        OnPropertyChanged(nameof(HasMoreNews));

        await ApplyBrandingImageAsync(profile?.BackgroundUrl, generation, isBackground: true);
        await ApplyBrandingImageAsync(profile?.LogoUrl, generation, isBackground: false);

        await LoadNewsAsync(profile, generation);
    }

    /// <summary>
    /// Loads one branding image without flicker: a blank URL means the profile genuinely has none (so we
    /// clear it), but a failed fetch or a stale/superseded load keeps whatever is already shown instead of
    /// blanking it. This stops the wallpaper from disappearing on a transient error or overlapping reload.
    /// </summary>
    private async Task ApplyBrandingImageAsync(string? url, int generation, bool isBackground)
    {
        var current = isBackground ? BackgroundImage : LogoImage;
        var lastUrl = isBackground ? _loadedBackgroundUrl : _loadedLogoUrl;

        if (string.IsNullOrWhiteSpace(url))
        {
            if (generation == _brandingGeneration)
            {
                if (isBackground) { BackgroundImage = null; _loadedBackgroundUrl = null; }
                else { LogoImage = null; _loadedLogoUrl = null; }
            }
            return;
        }

        // Same image already shown for this slot: don't refetch/reassign (a needless re-decode makes the
        // wallpaper flicker on every reconcile/retry for an unchanged profile).
        if (current is not null && string.Equals(url, lastUrl, StringComparison.Ordinal))
        {
            return;
        }

        var image = await FetchImageAsync(url);

        // Superseded by a newer load, or the fetch failed: leave the current image untouched.
        if (generation != _brandingGeneration || image is null)
        {
            return;
        }

        if (isBackground) { BackgroundImage = image; _loadedBackgroundUrl = url; }
        else { LogoImage = image; _loadedLogoUrl = url; }
    }

    /// <summary>
    /// Fetches the selected stack's news feed from its own container (<c>/news</c>, advertised during
    /// reconciliation) and populates the news list + cover thumbnails. The manager is not in the player
    /// path: news, like branding, is served by each stack. A blank feed URL or a fetch failure simply
    /// leaves the launcher without news rather than throwing.
    /// </summary>
    private async Task LoadNewsAsync(LauncherProfile? profile, int generation)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.NewsUrl))
        {
            return;
        }

        List<LauncherNewsDto>? dtos;
        try
        {
            var json = await BrandingHttp.GetStringAsync(profile.NewsUrl);
            dtos = JsonSerializer.Deserialize<List<LauncherNewsDto>>(json, NewsJsonOptions);
        }
        catch
        {
            return;
        }

        if (dtos is null || dtos.Count == 0 || generation != _brandingGeneration)
        {
            return;
        }

        var baseUrl = profile.PortalUrl?.TrimEnd('/') ?? string.Empty;
        var built = dtos
            .OrderByDescending(d => d.SortOrder)
            .Select(d => new NewsItem
            {
                Id = d.Id,
                Title = d.Title,
                Date = d.Date,
                Html = d.Html,
                ImageUrl = ResolveNewsAsset(baseUrl, d.HasImage ? d.ImageUrl : null),
            })
            .ToList();

        // Download cover thumbnails before publishing so the cards render with artwork in one pass.
        foreach (var item in built)
        {
            if (!string.IsNullOrWhiteSpace(item.ImageUrl))
            {
                item.Cover = await FetchImageAsync(item.ImageUrl);
            }
        }

        if (generation != _brandingGeneration)
        {
            return;
        }

        News.Clear();
        TopNews.Clear();
        foreach (var item in built)
        {
            News.Add(item);
        }

        RebuildTopNews();
        OnPropertyChanged(nameof(HasNews));
        OnPropertyChanged(nameof(HasMoreNews));
    }

    /// <summary>Resolves a news asset URL to absolute against the stack's portal base, passing through absolute URLs.</summary>
    private static string? ResolveNewsAsset(string baseUrl, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return string.IsNullOrWhiteSpace(baseUrl) ? url : $"{baseUrl}/{url.TrimStart('/')}";
    }

    /// <summary>Downloads and decodes a branding image, or returns null when the URL is blank/unreachable.</summary>
    private static async Task<Bitmap?> FetchImageAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        try
        {
            await using var stream = await BrandingHttp.GetStreamAsync(url);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            buffer.Position = 0;
            return new Bitmap(buffer);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True only for genuine loopback hosts (localhost, *.localhost, 127.0.0.0/8, ::1). A loopback stack
    /// is a local dev stack, for which an unsigned manifest is tolerated; every other host (LAN/VPC/
    /// public) must serve a manifest signed by the baked key.
    /// </summary>
    private static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip);
    }

    private void RebuildTopNews()
    {
        TopNews.Clear();
        foreach (var item in News.Take(4))
        {
            TopNews.Add(item);
        }

        OnPropertyChanged(nameof(HasMoreNews));
    }

    // ----- News commands -----

    [RelayCommand]
    private void OpenNews(NewsItem? item)
    {
        if (item is null)
        {
            return;
        }

        SelectedNews = item;
        ReadingHtml = BuildReadingHtml(item);
        NewsViewMode = NewsViewMode.Reading;
    }

    [RelayCommand]
    private void ShowAllNews() => NewsViewMode = NewsViewMode.Grid;

    /// <summary>
    /// Verifies the entered credentials against the selected server's auth database via the backend.
    /// On success the blocking login overlay disappears and the player can download/play.
    /// </summary>
    [RelayCommand]
    private async Task LoginAsync()
    {
        var profile = SelectedProfile;
        if (profile is null)
        {
            LoginError = "Select a server to log in to.";
            return;
        }

        if (string.IsNullOrWhiteSpace(LoginUsername) || string.IsNullOrWhiteSpace(LoginPassword))
        {
            LoginError = "Enter your username and password.";
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.PortalUrl))
        {
            LoginError = "This server is unavailable right now. Try again once it's reachable.";
            return;
        }

        IsLoggingIn = true;
        LoginError = string.Empty;
        try
        {
            // Login is verified against the selected stack's own auth DB via its portal container.
            var result = await new PortalClient(profile.PortalUrl)
                .LoginAsync(LoginUsername.Trim(), LoginPassword, CancellationToken.None);

            if (result.Success)
            {
                IsLoggedIn = true;
                LoginPassword = string.Empty;
                LoginError = string.Empty;

                // Remember this profile's sign-in so switching servers and future launches don't
                // re-prompt for a server we've already authenticated with.
                _state.GetProfile(profile.StackId).LoggedInUsername = LoginUsername.Trim();
                _stateStore.Save(_state);
            }
            else
            {
                IsLoggedIn = false;
                LoginError = result.Error ?? "Invalid username or password.";
            }
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    /// <summary>
    /// Signs out of the selected server, forgetting its remembered credentials so the login screen
    /// is shown again for it. Other servers keep their own sessions.
    /// </summary>
    [RelayCommand]
    private void Logout()
    {
        var profile = SelectedProfile;
        if (profile is not null)
        {
            _state.GetProfile(profile.StackId).LoggedInUsername = null;
            _stateStore.Save(_state);
        }

        IsLoggedIn = false;
        LoginUsername = string.Empty;
        LoginPassword = string.Empty;
        LoginError = string.Empty;
    }

    /// <summary>
    /// Closes the launcher. Exposed on the login overlay so the player is never trapped on the
    /// sign-in screen (e.g. when the selected server is offline and can't be logged into).
    /// </summary>
    [RelayCommand]
    private void Quit() => RequestShutdown?.Invoke();

    /// <summary>
    /// Opens the armory registration page in the default browser. Prefers the selected server's armory;
    /// falls back to any published server that has an armory so the login-screen button always does
    /// something useful. Shows a short hint when no armory is reachable rather than silently no-op'ing.
    /// </summary>
    [RelayCommand]
    private void Register()
    {
        var profile = SelectedProfile is { ArmoryPort: > 0 }
            ? SelectedProfile
            : Profiles.FirstOrDefault(p => p.ArmoryPort > 0);

        if (profile is null)
        {
            StatusText = "Registration isn't available yet.";
            DetailText = "No server with an armory is published to the launcher.";
            return;
        }

        OpenArmoryPathForProfile(profile, "/register");
    }

    /// <summary>Opens the selected server's armory front page in the default browser.</summary>
    [RelayCommand]
    private void OpenArmory() => OpenArmoryPath("/");

    /// <summary>Opens the selected server's armory news page in the default browser.</summary>
    [RelayCommand]
    private void OpenArmoryNews() => OpenArmoryPath("/news");

    /// <summary>
    /// Opens a path on the selected server's armory web app in the default browser. The armory runs on
    /// the same host as the configured server URL, on the port advertised by the profiles document.
    /// </summary>
    private void OpenArmoryPath(string path)
    {
        var profile = SelectedProfile;
        if (profile is null || profile.ArmoryPort <= 0)
        {
            return;
        }

        OpenArmoryPathForProfile(profile, path);
    }

    /// <summary>Opens a path on a specific profile's armory web app in the default browser.</summary>
    private void OpenArmoryPathForProfile(LauncherProfile profile, string path)
    {
        if (profile.ArmoryPort <= 0)
        {
            return;
        }

        try
        {
            // The armory runs on the selected stack's own host (same host as its portal).
            var host = Uri.TryCreate(profile.PortalUrl, UriKind.Absolute, out var portalUri)
                ? portalUri.Host
                : "localhost";
            OpenUrl($"http://{host}:{profile.ArmoryPort}{path}");
        }
        catch (Exception ex)
        {
            StatusText = "Could not open the armory.";
            DetailText = ex.Message;
        }
    }

    private static void OpenUrl(string url)
    {
        var psi = new ProcessStartInfo { FileName = url, UseShellExecute = true };
        if (OperatingSystem.IsMacOS())
        {
            psi = new ProcessStartInfo("open", url);
        }
        else if (OperatingSystem.IsLinux())
        {
            psi = new ProcessStartInfo("xdg-open", url);
        }

        Process.Start(psi);
    }

    [RelayCommand]
    private void BackFromNews()
    {
        NewsViewMode = NewsViewMode.List;
        SelectedNews = null;
    }

    /// <summary>
    /// Builds a full, self-contained HTML document for the reading view: cover + title + date +
    /// sanitized body, styled with the shared <c>.news-content</c> rules and the current accent so it
    /// matches the website's reading-view preview.
    /// </summary>
    private string BuildReadingHtml(NewsItem item)
    {
        var cover = item.ImageUrl is null
            ? string.Empty
            : $"<img class=\"cover\" src=\"{WebUtility.HtmlEncode(item.ImageUrl)}\" alt=\"\"/>";
        var title = WebUtility.HtmlEncode(item.Title);
        var date = WebUtility.HtmlEncode(item.Date);

        // Resolve any relative /news-image/* URLs embedded in the article body against the selected stack.
        var baseTag = string.IsNullOrWhiteSpace(_newsBaseUrl)
            ? string.Empty
            : $"<base href=\"{WebUtility.HtmlEncode(_newsBaseUrl)}/\">";

        return $$"""
<!DOCTYPE html>
<html><head><meta charset="utf-8"><meta name="color-scheme" content="dark">{{baseTag}}
<style>
:root { --news-accent: {{_accentHex}}; }
html, body { margin: 0; padding: 0; background: #12151c; }
body { color: #e5e7eb; font-family: 'Segoe UI', system-ui, sans-serif; line-height: 1.6; font-size: 15px; }
.cover { width: 100%; max-height: 260px; object-fit: cover; display: block; }
.wrap { padding: 20px 26px 48px; max-width: 820px; margin: 0 auto; }
h1.title { color: #fff; font-size: 1.9em; margin: 0 0 4px; }
.date { color: #94a3b8; font-size: 0.85em; margin-bottom: 18px; }
.news-content { overflow-wrap: anywhere; }
.news-content > :first-child { margin-top: 0; }
.news-content h1 { font-size: 1.6em; font-weight: 700; margin: 0.6em 0 0.3em; color: #fff; }
.news-content h2 { font-size: 1.3em; font-weight: 700; margin: 0.7em 0 0.3em; color: #fff; border-bottom: 1px solid rgba(255,255,255,0.12); padding-bottom: 0.2em; }
.news-content h3 { font-size: 1.1em; font-weight: 600; margin: 0.5em 0 0.25em; color: #fff; }
.news-content p { margin: 0.5em 0; }
.news-content ul, .news-content ol { margin: 0.5em 0; padding-left: 1.4em; }
.news-content li { margin: 0.2em 0; }
.news-content a { color: var(--news-accent); text-decoration: none; }
.news-content a:hover { text-decoration: underline; }
.news-content blockquote { margin: 0.8em 0; padding: 0.4em 1em; font-style: italic; color: #cbd5e1; border-left: 3px solid var(--news-accent); background: rgba(255,255,255,0.05); }
.news-content hr { border: none; border-top: 1px solid rgba(255,255,255,0.15); margin: 1em 0; }
.news-content img { max-width: 100%; height: auto; border-radius: 6px; }
.news-content code { background: rgba(255,255,255,0.1); padding: 0.1em 0.3em; border-radius: 3px; font-size: 0.9em; }
.news-content strong { color: #fff; }
.news-content table { border-collapse: collapse; width: 100%; margin: 0.6em 0; }
.news-content th, .news-content td { border: 1px solid rgba(255,255,255,0.15); padding: 4px 8px; text-align: left; }
</style></head>
<body>{{cover}}<div class="wrap"><h1 class="title">{{title}}</h1><div class="date">{{date}}</div>
<div class="news-content">{{item.Html}}</div></div></body></html>
""";
    }

    private async Task CheckLauncherUpdateAsync(ILauncherArtifactSource? source)
    {
        if (source is null)
        {
            LauncherUpdateVersion = null;
            return;
        }

        try
        {
            var selfUpdate = new SelfUpdateService(source, _defaults.Version);
            LauncherUpdateVersion = await selfUpdate.CheckAsync(CancellationToken.None);
        }
        catch
        {
            LauncherUpdateVersion = null;
        }
    }

    // ----- Settings -----

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (PickFolderAsync is null)
        {
            return;
        }

        var folder = await PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            InstallDirectory = folder;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveSettings))]
    private bool _isSavingSettings;

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (IsSavingSettings)
        {
            return;
        }

        IsSavingSettings = true;
        try
        {
            // The "server URL" is a stack portal reached over HTTP on the LAN/VPC (a bare host defaults to
            // http://). This is the stack the launcher connects to first; it then learns every other stack
            // from that stack's replicated registry.
            var normalizedServerUrl = NormalizePortalUrl(ServerUrl);
            if (normalizedServerUrl is null)
            {
                StatusText = "Please enter a valid server URL.";
                return;
            }

            // Reflect the normalized value (with scheme, no trailing slash) back into the field so the
            // user sees exactly what will be used.
            ServerUrl = normalizedServerUrl;

            if (string.IsNullOrWhiteSpace(InstallDirectory))
            {
                StatusText = "Please choose an install folder.";
                return;
            }

            try
            {
                Directory.CreateDirectory(InstallDirectory);
            }
            catch (Exception ex)
            {
                StatusText = $"Cannot use install folder: {ex.Message}";
                return;
            }

            _state.ServerUrl = normalizedServerUrl;
            _state.InstallDirectory = InstallDirectory.Trim();

            // Reconciliation reads the known-servers list, not ServerUrl. Prioritize the URL the player just
            // entered so editing it here actually takes effect (this is how a player points the launcher at a
            // stack, or recovers when every previously-known stack is unreachable).
            _state.KnownServers.Remove(normalizedServerUrl);
            _state.KnownServers.Insert(0, normalizedServerUrl);

            _stateStore.Save(_state);

            SelectedTabIndex = PlayTab;
            await LoadProfilesAsync();
        }
        finally
        {
            IsSavingSettings = false;
        }
    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        // The launcher itself takes priority: if a newer launcher build exists, update it first
        // (on Windows that downloads + swaps + restarts). Only then check the game client files.
        if (await TryApplyLauncherUpdateAsync(announceUpToDate: true))
        {
            return;
        }

        await CheckForUpdatesAsync(fullVerify: false);

        // "Check for updates" should also pull the latest news/branding for the selected server, so a
        // player who leaves the launcher open sees new articles without restarting.
        await RefreshSelectedProfileFeedsAsync();
    }

    [RelayCommand]
    private Task VerifyFiles() => CheckForUpdatesAsync(fullVerify: true);

    /// <summary>
    /// Re-fetches the selected server's <c>/portal</c> and refreshes this profile's branding + news URLs,
    /// then reloads the wallpaper/logo and news feed. This lets a manual "Check for updates" pick up news
    /// (or branding) published after the launcher started — including a feed that didn't yet exist at
    /// launch (when <c>NewsUrl</c> would have been blank). Best-effort: current values are kept on failure.
    /// </summary>
    private async Task RefreshSelectedProfileFeedsAsync()
    {
        var profile = SelectedProfile;
        if (profile is null || string.IsNullOrWhiteSpace(profile.PortalUrl))
        {
            return;
        }

        try
        {
            var doc = await new PortalClient(profile.PortalUrl).GetPortalAsync(CancellationToken.None);
            var self = doc?.Registry.FirstOrDefault(e =>
                string.Equals(e.StackId, profile.StackId, StringComparison.Ordinal));
            if (self is not null)
            {
                profile.BackgroundUrl = ResolveNewsAsset(profile.PortalUrl, self.BackgroundUrl);
                profile.LogoUrl = ResolveNewsAsset(profile.PortalUrl, self.LogoUrl);
                profile.NewsUrl = ResolveNewsAsset(profile.PortalUrl, self.NewsUrl);
            }
        }
        catch
        {
            // Unreachable/malformed portal: keep whatever URLs we already have and still try a reload below.
        }

        var generation = ++_brandingGeneration;
        await ApplyBrandingImageAsync(profile.BackgroundUrl, generation, isBackground: true);
        await ApplyBrandingImageAsync(profile.LogoUrl, generation, isBackground: false);
        await LoadNewsAsync(profile, generation);
    }

    /// <summary>
    /// Clears the local hash cache and runs a full verify. Because the cache (which trusts a file's size
    /// + mtime) is discarded, every file is re-hashed from disk and checked against the server — the
    /// strongest way to detect and repair corrupt/broken files. Sync markers (last manifest/overlay
    /// version) are kept so a clean verify does not falsely demand an update.
    /// </summary>
    [RelayCommand]
    private async Task InvalidateCache()
    {
        if (IsBusy)
        {
            return;
        }

        _state.HashCache.Clear();
        _stateStore.Save(_state);

        StatusText = "Cache cleared — re-verifying all files...";
        DetailText = string.Empty;
        await CheckForUpdatesAsync(fullVerify: true, pruneUnknown: true);
    }

    /// <summary>
    /// Install-relative directories that a server-mirror prune must never touch: per-profile overlay
    /// stashes (<c>Data/{FolderName}</c>) and addon caches (<c>_acl</c>), plus the user/runtime folders
    /// WoW owns (settings, screenshots, caches, logs). Everything else not in the server manifest is
    /// removed by <see cref="SyncService.PruneToServerManifest"/>.
    /// </summary>
    private List<string> ProtectedInstallDirs()
    {
        var dirs = new List<string> { "WTF", "Cache", "Screenshots", "Logs", "Errors", "WDB", "_acl" };
        foreach (var profile in Profiles)
        {
            dirs.Add($"Data/{profile.FolderName}");
        }
        return dirs;
    }

    /// <summary>
    /// Checks for a newer launcher build and, on Windows, downloads + applies it (restarting the app).
    /// Returns true when a launcher update was found (so the caller should stop and not fall through to
    /// a game-client update). When <paramref name="announceUpToDate"/> is set, reports "up to date" via
    /// the status line when nothing newer is available.
    /// </summary>
    private async Task<bool> TryApplyLauncherUpdateAsync(bool announceUpToDate)
    {
        var source = ArtifactSource();
        if (source is null)
        {
            return false;
        }

        try
        {
            var selfUpdate = new SelfUpdateService(source, _defaults.Version);
            var newVersion = await selfUpdate.CheckAsync(CancellationToken.None);

            if (string.IsNullOrEmpty(newVersion))
            {
                if (announceUpToDate)
                {
                    StatusText = "The launcher is up to date.";
                    DetailText = string.Empty;
                }
                return false;
            }

            LauncherUpdateVersion = newVersion;

            if (!OperatingSystem.IsWindows())
            {
                StatusText = $"A newer launcher ({newVersion}) is available.";
                DetailText = "Automatic launcher updates are only supported on Windows.";
                return true;
            }

            StatusText = "Downloading launcher update...";
            DetailText = string.Empty;
            await selfUpdate.ApplyUpdateAsync(CancellationToken.None);
            StatusText = "Restarting to finish the launcher update...";
            RequestShutdown?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            StatusText = "Could not check for a launcher update.";
            DetailText = ex.Message;
            return false;
        }
    }

    /// <summary>Opens the folder that holds the launcher's settings file in the OS file manager.</summary>
    [RelayCommand]
    private void OpenSettingsLocation() => RevealInFileManager(Path.GetDirectoryName(StateFilePath));

    /// <summary>Opens the configured client install folder in the OS file manager.</summary>
    [RelayCommand]
    private void OpenInstallLocation() =>
        RevealInFileManager(string.IsNullOrWhiteSpace(InstallDirectory) ? null : InstallDirectory);

    private static void RevealInFileManager(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", new[] { path });
            }
            else if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            else
            {
                Process.Start("xdg-open", new[] { path });
            }
        }
        catch
        {
            // Opening a file manager is a convenience; never let it crash the launcher.
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private async Task UpdateLauncherAsync()
    {
        if (!LauncherUpdateAvailable)
        {
            return;
        }

        try
        {
            var source = ArtifactSource();
            if (source is null)
            {
                StatusText = "No server is available to download the update from.";
                return;
            }

            StatusText = "Downloading launcher update...";
            var selfUpdate = new SelfUpdateService(source, _defaults.Version);
            await selfUpdate.ApplyUpdateAsync(CancellationToken.None);
            StatusText = "Restarting to finish the update...";
            RequestShutdown?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = "Launcher update failed.";
            DetailText = ex.Message;
        }
    }

    private async Task CheckForUpdatesAsync(bool fullVerify, bool pruneUnknown = false)
    {
        var profile = SelectedProfile;
        if (IsBusy || profile is null)
        {
            return;
        }

        // Servers + news are already visible, but downloading/playing needs an install folder. Guide
        // the user to Settings instead of silently doing nothing.
        if (!_state.IsConfigured)
        {
            PlanReady = false;
            StatusText = "Pick an install folder in Settings to download and play.";
            DetailText = string.Empty;
            return;
        }

        IsBusy = true;
        CanPlay = false;
        NeedsUpdate = false;
        PlanReady = false;
        ResetProgress();
        _cts = new CancellationTokenSource();
        var gen = ++_progressGeneration;

        try
        {
            var progress = new Progress<SyncProgress>(p => { if (gen == _progressGeneration) OnProgress(p); });
            var hashService = new HashService(_state.HashCache);

            StatusText = "Contacting server...";
            // Config comes from the stack itself: content + manifest + files live on its container and the
            // realmlist/trust key come from the reconciled portal. The manager is never contacted.
            _pendingProfileConfig = BuildPortalConfig(profile);

            // A single merged manifest is served by this stack's client container: Base files (shared
            // install, size-checked) and Managed files (per-profile overlay, hash-verified). Splitting it
            // preserves the existing base-sync vs overlay-stash semantics from one source of truth.
            var contentClient = ContentClientFor(_pendingProfileConfig, profile);
            var sync = new SyncService(contentClient, hashService);

            var fullManifest = await contentClient.GetManifestAsync(_cts.Token);

            // Verify the manifest signature against the key baked into the launcher at build time before
            // trusting any file/hash. This defeats a MITM that swaps the manifest, hashes and files
            // together on the stack's plain-HTTP content channel. For any remote stack we also require a
            // signing key was advertised, so a rogue stack cannot downgrade to "unsigned" by stripping the
            // key; only a loopback (dev) stack tolerates an unsigned manifest.
            var trustHost = !string.IsNullOrWhiteSpace(_pendingProfileConfig.ClientContentBaseUrl)
                ? _pendingProfileConfig.ClientContentBaseUrl
                : profile.PortalUrl;
            var requireSignature = true;
            if (Uri.TryCreate(trustHost, UriKind.Absolute, out var serverUri))
            {
                requireSignature = !IsLoopbackHost(serverUri.Host);
            }

            ManifestVerifier.EnsureTrusted(
                fullManifest, _pendingProfileConfig.ClientManifestPublicKey, requireSignature);

            (_pendingBaseManifest, _pendingOverlayManifest) = ManifestSplitter.Split(fullManifest);

            // Invalidate + re-verify also purges the install of anything the server no longer provides
            // (leaving profile stashes and user/runtime folders intact). Only when the manifest actually
            // returned files, so a transient empty response can never wipe a good install.
            if (pruneUnknown && fullManifest.Files.Count > 0)
            {
                StatusText = "Removing unrecognized files...";
                DetailText = string.Empty;
                var serverPaths = fullManifest.Files.Select(f => f.RelativePath).ToList();
                var protectedDirs = ProtectedInstallDirs();
                var installDir = _state.InstallDirectory!;
                var pruneToken = _cts.Token;
                // The hash cache was already cleared by InvalidateCache, so nothing to reconcile here.
                await Task.Run(
                    () => sync.PruneToServerManifest(installDir, serverPaths, protectedDirs, pruneToken),
                    pruneToken);
            }

            ApplyConfigToUi(_pendingProfileConfig);
            IsInstalled = IsGameInstalled(_pendingProfileConfig.GameExecutable);

            // A changed verify token (bumped by an operator via "Force re-validate") forces a one-off
            // full verify: re-hash every base file (not just size-check) and re-sync the overlay, even
            // when the manifest version is otherwise unchanged. The token is recorded after the update.
            var profileState = _state.GetProfile(profile.StackId);
            var forceVerify = !string.Equals(
                profileState.LastVerifyToken ?? string.Empty,
                _pendingOverlayManifest.VerifyToken ?? string.Empty,
                StringComparison.Ordinal);
            var effectiveFullVerify = fullVerify || forceVerify;

            _pendingBasePlan = await sync.PlanAsync(
                _pendingBaseManifest,
                _state.InstallDirectory!,
                _state.LastManagedPaths,
                effectiveFullVerify,
                progress,
                _cts.Token);

            // A full verify (manual "Verify files" or an operator-forced token) re-validates the overlay
            // against the server too, re-hashing from disk to catch corruption the version check misses.
            _forceOverlayRevalidate = effectiveFullVerify;
            var overlayDownloadsNeeded = await _profileContent.CountOverlayDownloadsNeededAsync(
                _pendingOverlayManifest,
                _state.InstallDirectory!,
                profile,
                hashService,
                effectiveFullVerify,
                _cts.Token);
            _overlayNeedsSync = _profileContent.NeedsSync(
                    profileState,
                    _pendingOverlayManifest,
                    _state.InstallDirectory!,
                    profile,
                    _state.ActiveProfileId)
                || forceVerify
                || overlayDownloadsNeeded > 0;
            var notActive = _state.ActiveProfileId != profile.StackId;
            var missingOverlayFiles = _profileContent.CountMissingOverlayDataFiles(
                _state.InstallDirectory!,
                profile,
                _pendingOverlayManifest,
                _state.ActiveProfileId);

            _stateStore.Save(_state);

            var baseUpToDate = _pendingBasePlan.IsUpToDate;
            var pendingDownloads = _pendingBasePlan.Downloads.Count + overlayDownloadsNeeded;
            PlanReady = true;

            if (baseUpToDate && !_overlayNeedsSync && !notActive && IsInstalled)
            {
                if (effectiveFullVerify)
                {
                    _state.LastManifestVersion = _pendingBaseManifest.Version;
                    profileState.LastOverlayVersion = _pendingOverlayManifest.Version;
                    profileState.LastVerifyToken = _pendingOverlayManifest.VerifyToken;
                    _stateStore.Save(_state);
                }

                await ApplyClientSettingsAsync(_cts.Token);
                CanPlay = true;
                ResetProgress(complete: true);
                StatusText = effectiveFullVerify ? "All files verified" : "Ready to play";
                DetailText = $"Up to date ({FormatBytes(_pendingBaseManifest.TotalSize)} base).";
            }
            else if (baseUpToDate && pendingDownloads == 0 && !notActive && IsInstalled)
            {
                // Profile switch or overlay bookkeeping only — no files to download.
                NeedsUpdate = true;
                ResetProgress();
                StatusText = notActive ? "Update available" : "Server content changes are pending.";
                DetailText = notActive
                    ? $"Switching to {profile.DisplayName}."
                    : "Click Update to apply profile changes.";
            }
            else
            {
                NeedsUpdate = true;
                ResetProgress();
                var count = _pendingBasePlan.Downloads.Count;

                if (!IsInstalled)
                {
                    StatusText = "Ready to install";
                    DetailText = count == 0
                        ? "No client files are available on the server yet."
                        : $"{count} file(s) to install ({FormatBytes(_pendingBasePlan.BytesToDownload)}).";
                }
                else
                {
                    StatusText = "Update available";
                    DetailText = notActive
                        ? $"Switching to {profile.DisplayName}."
                        : pendingDownloads > 0
                            ? $"{pendingDownloads} file(s) to download ({FormatBytes(_pendingBasePlan.BytesToDownload)})."
                        : "Server content changes are pending.";
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
            DetailText = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText = "Could not reach the server.";
            DetailText = ex.Message;
        }
        finally
        {
            // Invalidate any progress reports still queued from this run so they can't overwrite the
            // final status once the UI message loop drains them.
            _progressGeneration++;
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private async Task UpdateAsync()
    {
        var profile = SelectedProfile;
        if (IsBusy || profile is null || _pendingBasePlan is null || _pendingBaseManifest is null
            || _pendingOverlayManifest is null || _pendingProfileConfig is null)
        {
            return;
        }

        IsBusy = true;
        ResetProgress();
        _cts = new CancellationTokenSource();
        var gen = ++_progressGeneration;

        try
        {
            var progress = new Progress<SyncProgress>(p => { if (gen == _progressGeneration) OnProgress(p); });
            var contentClient = ContentClientFor(_pendingProfileConfig, profile);
            var hashService = new HashService(_state.HashCache);
            var sync = new SyncService(contentClient, hashService);

            var baseDownloads = _pendingBasePlan.Downloads.Count;
            StatusText = baseDownloads > 0
                ? "Downloading base client..."
                : "Downloading server patch files...";
            DetailText = baseDownloads > 0
                ? $"{baseDownloads} base file(s)"
                : string.Empty;
            var baseProgress = baseDownloads > 0 ? progress : null;
            await sync.ExecuteAsync(_pendingBasePlan, _state.InstallDirectory!, baseProgress, _cts.Token);
            // Drop any late base-sync progress callbacks before overlay work begins.
            gen = ++_progressGeneration;
            _state.LastManifestVersion = _pendingBaseManifest.Version;
            _state.LastManagedPaths = _pendingBasePlan.ManagedPaths;
            _stateStore.Save(_state);

            // Normalize the install: move the active profile's overlay content back into stashes/caches.
            _profileContent.Deactivate(_state.InstallDirectory!, _state, Profiles.ToList());
            _profileContent.RestoreMisplacedSharedBaseMpqs(_state.InstallDirectory!, profile);

            if (_overlayNeedsSync)
            {
                var overlayFiles = _pendingOverlayManifest.Files.Count;
                StatusText = "Downloading server patch files...";
                DetailText = overlayFiles > 0 ? $"{overlayFiles} server file(s)" : string.Empty;
                var profileState = _state.GetProfile(profile.StackId);
                await _profileContent.SyncOverlayAsync(
                    contentClient, _pendingOverlayManifest, _state.InstallDirectory!, profile, profileState,
                    hashService, progress, _cts.Token, forceRecompute: _forceOverlayRevalidate);
            }
            else
            {
                _profileContent.ReconcileOverlayState(
                    _state.InstallDirectory!, _pendingOverlayManifest, _state.GetProfile(profile.StackId));
            }

            StatusText = "Applying profile...";
            _profileContent.Activate(_state.InstallDirectory!, profile, _state);
            await ApplyClientSettingsAsync(_cts.Token);
            // Record the acknowledged verify token so an operator-forced re-validate runs exactly once.
            _state.GetProfile(profile.StackId).LastVerifyToken = _pendingOverlayManifest.VerifyToken;
            _stateStore.Save(_state);

            LoadAddons(profile);
            _overlayNeedsSync = false;
            _forceOverlayRevalidate = false;
            NeedsUpdate = false;
            IsInstalled = IsGameInstalled(_pendingProfileConfig.GameExecutable);
            CanPlay = IsInstalled;
            ResetProgress(complete: true);
            if (IsInstalled)
            {
                StatusText = "Ready to play";
                DetailText = $"{profile.DisplayName} is ready.";
            }
            else
            {
                StatusText = "Install incomplete";
                DetailText = "No client files are available on the server yet.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Update cancelled.";
            _stateStore.Save(_state);
        }
        catch (Exception ex)
        {
            StatusText = "Update failed.";
            DetailText = ex.Message;
        }
        finally
        {
            // Invalidate any progress reports still queued from this run so they can't overwrite the
            // final status once the UI message loop drains them.
            _progressGeneration++;
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        var profile = SelectedProfile;
        if (IsBusy || !CanPlay || profile is null || _pendingProfileConfig is null)
        {
            return;
        }

        // Guard against a missing client (e.g. the install folder was moved/deleted after the last
        // check): flip back to the Install action instead of throwing "game executable not found".
        if (!IsGameInstalled(_pendingProfileConfig.GameExecutable))
        {
            IsInstalled = false;
            CanPlay = false;
            NeedsUpdate = true;
            StatusText = "The client isn't installed yet.";
            DetailText = "Click Install to download it first.";
            return;
        }

        try
        {
            var profileState = _state.GetProfile(profile.StackId);
            if (_state.ActiveProfileId != profile.StackId)
            {
                _profileContent.Deactivate(_state.InstallDirectory!, _state, Profiles.ToList());
                _profileContent.Activate(_state.InstallDirectory!, profile, _state);
                _stateStore.Save(_state);
            }
            else
            {
                // Profile already active: still reconcile duplicate stash/live MPQs (common after manual
                // edits under Data/) and drop overlay MPQs the server no longer publishes.
                _profileContent.RestoreMisplacedSharedBaseMpqs(_state.InstallDirectory!, profile);
                _profileContent.ReconcileOverlayDuplicates(
                    _state.InstallDirectory!, profile, profileState.OverlayMpqs, profileIsActive: true);
                if (_pendingOverlayManifest is not null)
                {
                    _profileContent.ReconcileOverlayState(
                        _state.InstallDirectory!, _pendingOverlayManifest, profileState);
                }
            }

            await ApplyClientSettingsAsync(CancellationToken.None);
            // Always clear the WoW client Cache before launch so MPQ indexing is rebuilt from the
            // current overlay files (Activate also clears it on profile switch, but same-profile
            // launches must not reuse a stale cache from a prior session).
            _profileContent.ClearClientCache(_state.InstallDirectory!);
            GameLauncher.Launch(_state.InstallDirectory!, _pendingProfileConfig.GameExecutable, _pendingProfileConfig.LaunchArguments);
            StatusText = "Game launched. Closing launcher...";

            // The game runs as a detached process, so the launcher has nothing left to do; close it.
            // Let the status render briefly first, then request a clean shutdown (same path as self-update).
            await Task.Delay(600, CancellationToken.None);
            RequestShutdown?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = "Could not start the game.";
            DetailText = ex.Message;
        }
    }

    /// <summary>
    /// The client used to fetch the merged manifest and download files for this profile, served by the
    /// selected stack's own client-server container (<see cref="LauncherConfig.ClientContentBaseUrl"/>).
    /// </summary>
    private static ManifestClient ContentClientFor(LauncherConfig config, LauncherProfile profile) =>
        ManifestClient.ForContent(config.ClientContentBaseUrl);

    /// <summary>
    /// Builds the launcher config from a reconciled portal profile (stack-portal mode) instead of the
    /// manager's <c>/config</c>: content/manifest/files come from the stack's own container, the manifest
    /// is verified against the baked signing key, and the realmlist comes from the registry. The stack
    /// ships no Config.wtf template, so <see cref="SettingsWriter.ApplyRealmlistAsync"/> writes just the
    /// realmlist line (creating a minimal Config.wtf on first install).
    /// </summary>
    private LauncherConfig BuildPortalConfig(LauncherProfile profile) => new()
    {
        ClientContentBaseUrl = profile.PortalUrl,
        ClientManifestPublicKey = _manifestPublicKey ?? string.Empty,
        RealmlistHost = profile.RealmlistHost,
        RealmlistPort = profile.RealmlistPort,
        BrandingTitle = _profilesDoc?.BrandingTitle ?? BrandingTitle,
        ClientVersion = profile.ClientVersion,
    };

    /// <summary>
    /// True when the selected profile's game executable exists in the install folder. Used to decide
    /// between the "Play" and "Install" primary actions and to guard the launch itself.
    /// </summary>
    private bool IsGameInstalled(string? executable)
    {
        if (string.IsNullOrWhiteSpace(_state.InstallDirectory) || string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        var normalized = executable.Replace('/', Path.DirectorySeparatorChar);
        return File.Exists(Path.Combine(_state.InstallDirectory!, normalized));
    }

    private void ApplyConfigToUi(LauncherConfig config)
    {
        ClientVersion = config.ClientVersion;
        if (SelectedProfile is not null && string.IsNullOrWhiteSpace(RealmlistInfo) && !string.IsNullOrWhiteSpace(config.RealmlistHost))
        {
            RealmlistInfo = $"Realm: {config.RealmlistHost}:{config.RealmlistPort}";
        }

        // Auto-fill the editable realmlist with the server's value on every profile switch, so the box
        // always reflects the selected server unless the player deliberately overrides it before playing.
        // The 3.3.5a client reads the realmlist from WTF/Config.wtf's `set realmList "host:port"` line
        // (the Data/<locale>/realmlist.wtf files are ignored), so we source and display host:port.
        RealmlistOverride = EnsureRealmlistPort(
            ExtractServerRealmlist(config) ?? config.RealmlistHost, config.RealmlistPort);
    }

    private const string ConfigFileSuffix = "Config.wtf";
    private const string DefaultConfigRelativePath = "WTF/Config.wtf";

    // Matches the client's `set realmList "host:port"` line in Config.wtf, capturing the address with
    // or without surrounding quotes. Case-insensitive so `SET realmList`, `set realmlist`, etc. all hit.
    private static readonly Regex ConfigRealmlistRegex =
        new("^\\s*set\\s+realmlist\\s+\"?(?<addr>[^\"\\r\\n]+?)\"?\\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Reads the realmlist address from the server-provided Config.wtf settings file, if any.</summary>
    private static string? ExtractServerRealmlist(LauncherConfig config)
    {
        var setting = config.Settings.FirstOrDefault(
            s => s.TargetRelativePath.EndsWith(ConfigFileSuffix, StringComparison.OrdinalIgnoreCase));
        if (setting is null)
        {
            return null;
        }

        var match = ConfigRealmlistRegex.Match(setting.Content);
        return match.Success ? match.Groups["addr"].Value.Trim() : null;
    }

    /// <summary>
    /// Ensures a realmlist address carries a port. The client's Config.wtf realmList must be
    /// "host:port"; when the player entered a bare host we append the selected server's auth port.
    /// </summary>
    private static string EnsureRealmlistPort(string? address, int port)
    {
        var value = address?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return !value.Contains(':') && port > 0 ? $"{value}:{port}" : value;
    }

    /// <summary>The realmlist address (host:port) to write, honoring the player's override.</summary>
    private string EffectiveRealmlist()
    {
        var config = _pendingProfileConfig;
        var value = string.IsNullOrWhiteSpace(RealmlistOverride) ? config?.RealmlistHost : RealmlistOverride;
        return EnsureRealmlistPort(value, config?.RealmlistPort ?? 0);
    }

    /// <summary>The install-relative path of the Config.wtf the realmlist is written into.</summary>
    private string ConfigRelativePath() =>
        _pendingProfileConfig?.Settings
            .FirstOrDefault(s => s.TargetRelativePath.EndsWith(ConfigFileSuffix, StringComparison.OrdinalIgnoreCase))
            ?.TargetRelativePath
        ?? DefaultConfigRelativePath;

    /// <summary>
    /// Writes the profile's settings files. Config.wtf is treated as player-owned:
    /// <list type="bullet">
    /// <item>On first install (the file is missing) the admin's Config.wtf template is written once, so
    /// its defaults seed the client's initial config.</item>
    /// <item>On every later launch only the single <c>SET realmList</c> line is patched; the template's
    /// other keys are never re-pushed, so the player's in-game settings (resolution, graphics, sound)
    /// persist instead of being reset every update.</item>
    /// </list>
    /// The launcher's editable realmlist always takes final priority for the realmlist line.
    /// </summary>
    private async Task ApplyClientSettingsAsync(CancellationToken cancellationToken)
    {
        var config = _pendingProfileConfig;
        if (config is null)
        {
            return;
        }

        var installDir = _state.InstallDirectory!;
        var configWtf = config.Settings.FirstOrDefault(
            s => s.TargetRelativePath.EndsWith(ConfigFileSuffix, StringComparison.OrdinalIgnoreCase));

        // Every other settings file keeps its own overwrite semantics; only Config.wtf is special.
        var others = config.Settings.Where(s => !ReferenceEquals(s, configWtf));
        await SettingsWriter.ApplyAsync(others, installDir, cancellationToken);

        var configRelativePath = string.IsNullOrWhiteSpace(configWtf?.TargetRelativePath)
            ? ConfigRelativePath()
            : configWtf!.TargetRelativePath;
        var configPath = Path.Combine(
            installDir, configRelativePath.Replace('/', Path.DirectorySeparatorChar));

        // First install (Config.wtf missing): seed the admin's template once so its defaults apply.
        // Thereafter Config.wtf is player-owned — only the realmlist line is ever patched below.
        if (configWtf is not null && !File.Exists(configPath))
        {
            await SettingsWriter.MergeConfigWtfAsync(
                installDir, configRelativePath, configWtf.Content, EffectiveRealmlist(), cancellationToken);
            return;
        }

        await SettingsWriter.ApplyRealmlistAsync(
            installDir, configRelativePath, EffectiveRealmlist(), cancellationToken);
    }

    private void OnProgress(SyncProgress progress)
    {
        StatusText = progress.Status;

        if (progress.Fraction.HasValue)
        {
            ProgressIndeterminate = false;
            ProgressValue = Math.Clamp(progress.Fraction.Value * 100.0, 0, 100);
        }
        else
        {
            ProgressIndeterminate = true;
        }

        DetailText = progress.BytesTotal > 0
            ? $"{FormatBytes(progress.BytesCompleted)} / {FormatBytes(progress.BytesTotal)}"
            : progress.FilesTotal > 0
                ? $"{progress.FilesCompleted} / {progress.FilesTotal} files"
                : string.Empty;
    }

    private void ResetProgress(bool complete = false)
    {
        ProgressIndeterminate = false;
        ProgressValue = complete ? 100 : 0;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
