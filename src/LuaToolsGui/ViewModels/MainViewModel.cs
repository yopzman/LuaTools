using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;

namespace LuaToolsGui.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AuthService _auth;
    private readonly SteamService _steam;
    private readonly CefInjectorService _cef;
    private readonly DownloadQueue _queue;
    private readonly DispatcherTimer _statusTimer;

    /// <summary>The first-run welcome overlay VM (hosted at the window root, shown via its IsOpen).</summary>
    public OnboardingViewModel Onboarding { get; }

    /// <summary>App version shown in the nav pane footer, e.g. "v1.0.1". Read from the assembly.</summary>
    public string VersionLabel { get; } = $"v{ReadVersion()}";

    private static string ReadVersion()
    {
        // InformationalVersion carries the csproj <Version> (may have a "+commit" suffix. Trim it).
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var ver = info ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
        int plus = ver.IndexOf('+');
        return plus >= 0 ? ver[..plus] : ver;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRealUser))]
    [NotifyPropertyChangedFor(nameof(FooterStatus))]
    private bool _isGuest = true;

    public bool IsRealUser => !IsGuest;

    /// <summary>Bottom-of-pane line: version plus auth state. No username shown (privacy).</summary>
    public string FooterStatus => $"{VersionLabel} · {(IsGuest ? Resources.Strings.Nav_Footer_Guest : Resources.Strings.Nav_Footer_LoggedIn)}";

    [ObservableProperty] private bool _isSigningIn;
    [ObservableProperty] private string? _signInError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveDownloadCountString))]
    private int _activeDownloadCount;

    [ObservableProperty]
    private bool _hasActiveDownloads;

    public string ActiveDownloadCountString => ActiveDownloadCount.ToString();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SteamStatusBrush))]
    [NotifyPropertyChangedFor(nameof(SteamStatusLabel))]
    private bool _isSteamRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CefStatusBrush))]
    [NotifyPropertyChangedFor(nameof(CefStatusLabel))]
    private bool _isCefConnected;

    private static readonly Brush GreenBrush = CreateFrozenBrush(Color.FromRgb(0x34, 0xd3, 0x99));
    private static readonly Brush BlueBrush = CreateFrozenBrush(Color.FromRgb(0x38, 0xbd, 0xf8));
    private static readonly Brush GrayBrush = CreateFrozenBrush(Color.FromRgb(0x9c, 0xa3, 0xaf));

    private static Brush CreateFrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public Brush SteamStatusBrush => IsSteamRunning ? GreenBrush : GrayBrush;
    public string SteamStatusLabel => IsSteamRunning ? "Steam Running" : "Steam Stopped";

    public Brush CefStatusBrush => IsCefConnected ? BlueBrush : GrayBrush;
    public string CefStatusLabel => IsCefConnected ? "Plugin Connected" : "Plugin Standby";

    public MainViewModel(AuthService auth, SteamService steam, CefInjectorService cef, DownloadQueue queue, OnboardingViewModel onboarding)
    {
        _auth = auth;
        _steam = steam;
        _cef = cef;
        _queue = queue;
        Onboarding = onboarding;
        _auth.AuthStateChanged += () => IsGuest = _auth.IsGuest;

        _queue.StateChanged += () => Application.Current?.Dispatcher.InvokeAsync(UpdateDownloadState);
        _queue.Items.CollectionChanged += (_, _) => Application.Current?.Dispatcher.InvokeAsync(UpdateDownloadState);
        UpdateDownloadState();

        _cef.CefStatusChanged += () => Application.Current?.Dispatcher.InvokeAsync(() => IsCefConnected = _cef.IsCefConnected);

        _statusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _statusTimer.Tick += (_, _) => CheckStatus();
        _statusTimer.Start();
        CheckStatus();
    }

    private void UpdateDownloadState()
    {
        ActiveDownloadCount = _queue.ActiveCount;
        HasActiveDownloads = ActiveDownloadCount > 0;
    }

    private void CheckStatus()
    {
        IsSteamRunning = SteamService.IsSteamRunning();
        IsCefConnected = _cef.IsCefConnected;
    }

    public async Task InitializeAsync()
    {
        await _auth.InitializeAsync();
        IsGuest = _auth.IsGuest;
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsSigningIn) return;
        IsSigningIn = true;
        SignInError = null;
        try
        {
            await _auth.SignInAsync();
        }
        catch (Exception ex)
        {
            SignInError = ex.Message;
        }
        finally
        {
            IsSigningIn = false;
        }
    }

    /// <summary>Confirm, then kill + relaunch Steam so newly added/removed luas take effect.</summary>
    [RelayCommand]
    private void RestartSteam()
    {
        var result = MessageBox.Show(
            Resources.Strings.Main_RestartSteam_Ask,
            Resources.Strings.Manage_RestartSteam_Title,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.OK) return;

        if (!_steam.RestartSteam())
            MessageBox.Show(
                Resources.Strings.Manage_RestartSteam_Failed,
                Resources.Strings.Manage_RestartSteam_Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
    }
}
