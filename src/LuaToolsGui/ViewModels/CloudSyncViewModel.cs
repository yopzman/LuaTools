using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;
using LuaToolsGui.Services.CloudSync;
using Microsoft.Win32;

namespace LuaToolsGui.ViewModels;

public sealed partial class CloudSyncViewModel : ObservableObject
{
    private readonly CloudSyncService _cloudSync;
    private readonly ToastService _toast;

    public record ProviderItem(string Key, string DisplayName);

    public static readonly ProviderItem[] AvailableProviders =
    [
        new("folder", "Local Folder / Network Drive"),
        new("workshop", "Steam Workshop"),
        new("gdrive", "Google Drive"),
        new("onedrive", "OneDrive"),
        new("r2", "Cloudflare R2"),
        new("s3", "S3-Compatible")
    ];

    public ObservableCollection<ProviderItem> Providers { get; } = new(AvailableProviders);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProviderTab))]
    [NotifyPropertyChangedFor(nameof(IsAppsTab))]
    [NotifyPropertyChangedFor(nameof(IsCleanupTab))]
    [NotifyPropertyChangedFor(nameof(IsMigrationTab))]
    [NotifyPropertyChangedFor(nameof(IsManifestTab))]
    private int _selectedTabIndex;

    public bool IsProviderTab
    {
        get => SelectedTabIndex == 0;
        set { if (value) SelectedTabIndex = 0; }
    }
    public bool IsAppsTab
    {
        get => SelectedTabIndex == 1;
        set { if (value) SelectedTabIndex = 1; }
    }
    public bool IsCleanupTab
    {
        get => SelectedTabIndex == 2;
        set { if (value) SelectedTabIndex = 2; }
    }
    public bool IsMigrationTab
    {
        get => SelectedTabIndex == 3;
        set { if (value) SelectedTabIndex = 3; }
    }
    public bool IsManifestTab
    {
        get => SelectedTabIndex == 4;
        set { if (value) SelectedTabIndex = 4; }
    }

    // ── Provider Configuration ─────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOAuthProvider))]
    [NotifyPropertyChangedFor(nameof(IsFolderProvider))]
    [NotifyPropertyChangedFor(nameof(IsWorkshopProvider))]
    [NotifyPropertyChangedFor(nameof(IsR2Provider))]
    [NotifyPropertyChangedFor(nameof(IsS3Provider))]
    [NotifyPropertyChangedFor(nameof(RequiresPathRow))]
    private ProviderItem _selectedProvider;

    public bool IsOAuthProvider => SelectedProvider.Key is "gdrive" or "onedrive";
    public bool IsFolderProvider => SelectedProvider.Key == "folder";
    public bool IsWorkshopProvider => SelectedProvider.Key == "workshop";
    public bool IsR2Provider => SelectedProvider.Key == "r2";
    public bool IsS3Provider => SelectedProvider.Key == "s3";
    public bool RequiresPathRow => IsOAuthProvider || IsFolderProvider || IsWorkshopProvider;

    [ObservableProperty]
    private string _syncPath = "";

    [ObservableProperty]
    private string _tokenPath = "";

    [ObservableProperty]
    private int _uploadInflightMb = 24;

    // Workshop
    [ObservableProperty]
    private int _workshopAppId = 480;

    [ObservableProperty]
    private int _workshopVisibilityIndex = 2; // 0=Public, 1=FriendsOnly, 2=Private/Unlisted

    // R2
    [ObservableProperty]
    private string _r2AccountId = "";
    [ObservableProperty]
    private string _r2AccessKey = "";
    [ObservableProperty]
    private string _r2SecretKey = "";
    [ObservableProperty]
    private string _r2Bucket = "";
    [ObservableProperty]
    private string _r2KeyPrefix = "";
    [ObservableProperty]
    private string _r2Endpoint = "";

    // S3
    [ObservableProperty]
    private string _s3Endpoint = "";
    [ObservableProperty]
    private string _s3AccessKey = "";
    [ObservableProperty]
    private string _s3SecretKey = "";
    [ObservableProperty]
    private string _s3Bucket = "";
    [ObservableProperty]
    private string _s3Region = "";
    [ObservableProperty]
    private string _s3KeyPrefix = "";
    [ObservableProperty]
    private bool _s3SignPayload;
    [ObservableProperty]
    private bool _s3InsecureHttp;
    [ObservableProperty]
    private bool _s3InsecureTls;
    [ObservableProperty]
    private string _s3CaCertPath = "";

    // Auth status
    [ObservableProperty]
    private string _authStatusText = "Unknown";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuthStatusBrush))]
    private bool _isAuthenticated;
    [ObservableProperty]
    private bool _isAuthenticating;
    [ObservableProperty]
    private bool _isCheckingAuth;
    [ObservableProperty]
    private string _authLog = "";

    private static readonly System.Windows.Media.SolidColorBrush GreenBrush = FreezeBrush("#22c55e");
    private static readonly System.Windows.Media.SolidColorBrush GrayBrush = FreezeBrush("#6b7280");

    private static System.Windows.Media.SolidColorBrush FreezeBrush(string hex)
    {
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var b = new System.Windows.Media.SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public System.Windows.Media.Brush AuthStatusBrush => IsAuthenticated ? GreenBrush : GrayBrush;

    // ── Apps Section ───────────────────────────────────────────────
    public ObservableCollection<string> SteamAccounts { get; } = [];

    [ObservableProperty]
    private string _selectedAccountId = "";

    public ObservableCollection<CloudSyncAppItemViewModel> SyncedApps { get; } = [];
    public ObservableCollection<CloudSyncAppItemViewModel> DisplayedApps { get; } = [];

    [ObservableProperty]
    private string _appSearchQuery = "";

    [ObservableProperty]
    private bool _isLoadingApps;

    [ObservableProperty]
    private string _appsSummaryText = "";

    // ── Cleanup & 760 Section ──────────────────────────────────────
    public ObservableCollection<Steam760FileItemViewModel> Files760 { get; } = [];

    [ObservableProperty]
    private bool _isScanning760;

    [ObservableProperty]
    private string _summary760Text = "No scan performed.";

    // ── Migration Section ──────────────────────────────────────────
    [ObservableProperty]
    private ProviderItem _migrationSource;

    [ObservableProperty]
    private ProviderItem _migrationDest;

    [ObservableProperty]
    private string _migrationLog = "";

    [ObservableProperty]
    private bool _isMigrating;

    // ── Manifest Pinning Section ───────────────────────────────────
    [ObservableProperty]
    private bool _manifestPinningEnabled;

    [ObservableProperty]
    private bool _autoCommentEnabled;

    [ObservableProperty]
    private string _pinnedAppIdsText = "";

    public CloudSyncViewModel(CloudSyncService cloudSync, ToastService toast)
    {
        _cloudSync = cloudSync;
        _toast = toast;

        _selectedProvider = Providers[0]; // folder default
        _migrationSource = Providers[0];
        _migrationDest = Providers[1];
    }

    public async Task InitializeAsync()
    {
        try
        {
            LoadConfig();
            LoadAccounts();
            LoadManifestPinning();
            await CheckAuthAsync();
            if (!string.IsNullOrEmpty(SelectedAccountId))
            {
                await RefreshAppsAsync();
            }
        }
        catch (Exception ex)
        {
            AuthStatusText = $"Init error: {ex.Message}";
        }
    }

    private void LoadAccounts()
    {
        SteamAccounts.Clear();
        var accounts = _cloudSync.GetSteamAccountIds();
        foreach (var acc in accounts) SteamAccounts.Add(acc);
        if (SteamAccounts.Count > 0 && string.IsNullOrEmpty(SelectedAccountId))
        {
            SelectedAccountId = SteamAccounts[0];
        }
    }

    private void LoadConfig()
    {
        var cfg = CloudRedirectConfigHelper.ReadConfig();
        var match = Providers.FirstOrDefault(p => p.Key.Equals(cfg.Provider, StringComparison.OrdinalIgnoreCase));
        SelectedProvider = match ?? Providers[0];

        SyncPath = cfg.SyncPath ?? (SelectedProvider.Key == "workshop"
            ? CloudRedirectConfigHelper.GetDefaultWorkshopFolder()
            : CloudRedirectConfigHelper.GetDefaultSyncFolder());

        TokenPath = cfg.TokenPath ?? (cfg.TokenPaths.TryGetValue(SelectedProvider.Key, out var tp)
            ? tp
            : CloudRedirectConfigHelper.GetDefaultTokenPath(SelectedProvider.Key));

        UploadInflightMb = cfg.UploadInflightMb > 0 ? cfg.UploadInflightMb : 24;
        WorkshopAppId = cfg.WorkshopAppId > 0 ? cfg.WorkshopAppId : 480;
        WorkshopVisibilityIndex = cfg.WorkshopVisibility >= 0 && cfg.WorkshopVisibility <= 2 ? cfg.WorkshopVisibility : 2;

        var r2 = CloudRedirectConfigHelper.LoadR2Credentials();
        R2AccountId = r2.AccountId;
        R2AccessKey = r2.AccessKeyId;
        R2SecretKey = r2.SecretAccessKey;
        R2Bucket = r2.Bucket;
        R2KeyPrefix = r2.KeyPrefix ?? "";
        R2Endpoint = r2.Endpoint ?? "";

        var s3 = CloudRedirectConfigHelper.LoadS3Credentials();
        S3Endpoint = s3.Endpoint;
        S3AccessKey = s3.AccessKeyId;
        S3SecretKey = s3.SecretAccessKey;
        S3Bucket = s3.Bucket;
        S3Region = s3.Region;
        S3KeyPrefix = s3.KeyPrefix ?? "";
        S3SignPayload = s3.SignPayload;
        S3InsecureHttp = s3.InsecureHttp;
        S3InsecureTls = s3.InsecureTls;
        S3CaCertPath = s3.CaCertPath ?? "";
    }

    partial void OnSelectedProviderChanged(ProviderItem value)
    {
        var cfg = CloudRedirectConfigHelper.ReadConfig();
        if (value.Key is "folder" or "workshop")
        {
            SyncPath = cfg.SyncPath ?? (value.Key == "workshop"
                ? CloudRedirectConfigHelper.GetDefaultWorkshopFolder()
                : CloudRedirectConfigHelper.GetDefaultSyncFolder());
        }
        else
        {
            TokenPath = cfg.TokenPaths.TryGetValue(value.Key, out var tp) && !string.IsNullOrEmpty(tp)
                ? tp
                : CloudRedirectConfigHelper.GetDefaultTokenPath(value.Key);
        }

        _ = CheckAuthAsync();
    }

    [RelayCommand]
    public void BrowseSyncPath()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Sync Folder",
            InitialDirectory = Directory.Exists(SyncPath) ? SyncPath : ""
        };
        if (dialog.ShowDialog() == true)
        {
            SyncPath = dialog.FolderName;
        }
    }

    [RelayCommand]
    public void BrowseS3CaCert()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select CA Certificate",
            Filter = "Certificate files (*.pem;*.crt;*.cer)|*.pem;*.crt;*.cer|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true)
        {
            S3CaCertPath = dialog.FileName;
        }
    }

    [RelayCommand]
    public async Task SaveProviderSettingsAsync()
    {
        var cfg = CloudRedirectConfigHelper.ReadConfig();
        cfg.Provider = SelectedProvider.Key;
        cfg.UploadInflightMb = UploadInflightMb;

        if (IsFolderProvider || IsWorkshopProvider)
        {
            cfg.SyncPath = SyncPath;
        }
        else
        {
            cfg.TokenPath = TokenPath;
            if (!string.IsNullOrEmpty(TokenPath))
                cfg.TokenPaths[SelectedProvider.Key] = TokenPath;
        }

        if (IsWorkshopProvider)
        {
            cfg.WorkshopAppId = WorkshopAppId;
            cfg.WorkshopVisibility = WorkshopVisibilityIndex;
            string? tool = _cloudSync.Cli.EnsureWorkshopTools();
            if (!string.IsNullOrEmpty(tool)) cfg.WorkshopToolPath = tool;
        }

        CloudRedirectConfigHelper.SaveConfig(cfg);

        if (IsR2Provider)
        {
            CloudRedirectConfigHelper.SaveR2Credentials(new R2Credentials
            {
                AccountId = R2AccountId,
                AccessKeyId = R2AccessKey,
                SecretAccessKey = R2SecretKey,
                Bucket = R2Bucket,
                KeyPrefix = R2KeyPrefix,
                Endpoint = R2Endpoint
            });
        }
        else if (IsS3Provider)
        {
            CloudRedirectConfigHelper.SaveS3Credentials(new S3Credentials
            {
                Endpoint = S3Endpoint,
                AccessKeyId = S3AccessKey,
                SecretAccessKey = S3SecretKey,
                Bucket = S3Bucket,
                Region = S3Region,
                KeyPrefix = R2KeyPrefix,
                SignPayload = S3SignPayload,
                InsecureHttp = S3InsecureHttp,
                InsecureTls = S3InsecureTls,
                CaCertPath = S3CaCertPath
            });
        }

        _toast.Show("Cloud Sync", "Provider settings saved successfully.");
        await CheckAuthAsync();
    }

    [RelayCommand]
    public async Task CheckAuthAsync()
    {
        if (IsCheckingAuth) return;
        IsCheckingAuth = true;
        AuthStatusText = "Checking...";
        try
        {
            var res = await _cloudSync.Cli.CheckAuthStatusAsync(SelectedProvider.Key);
            IsAuthenticated = res.Authenticated;
            AuthStatusText = res.Authenticated
                ? "Authenticated"
                : string.IsNullOrEmpty(res.Error) ? "Not Authenticated" : $"Error: {res.Error}";
        }
        catch (Exception ex)
        {
            IsAuthenticated = false;
            AuthStatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsCheckingAuth = false;
        }
    }

    [RelayCommand]
    public async Task SignInOAuthAsync()
    {
        if (IsAuthenticating) return;
        IsAuthenticating = true;
        AuthLog = "";
        try
        {
            string tp = !string.IsNullOrEmpty(TokenPath) ? TokenPath : CloudRedirectConfigHelper.GetDefaultTokenPath(SelectedProvider.Key);
            bool ok = await _cloudSync.OAuth.AuthorizeAsync(
                SelectedProvider.Key,
                tp,
                line => AuthLog += line + "\n");

            if (ok)
            {
                _toast.Show("Cloud Sync", "Authorization successful!");
                await SaveProviderSettingsAsync();
            }
            else
            {
                _toast.Show("Cloud Sync", "Authorization failed.", error: true);
            }
        }
        finally
        {
            IsAuthenticating = false;
            await CheckAuthAsync();
        }
    }

    [RelayCommand]
    public void CancelOAuth()
    {
        _cloudSync.OAuth.Cancel();
        IsAuthenticating = false;
    }

    // ── Apps Tab ───────────────────────────────────────────────────

    partial void OnSelectedAccountIdChanged(string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _ = RefreshAppsAsync();
            _ = Scan760Async();
        }
    }

    partial void OnAppSearchQueryChanged(string value)
    {
        ApplyAppFilter();
    }

    private void ApplyAppFilter()
    {
        DisplayedApps.Clear();
        string q = AppSearchQuery?.Trim().ToLowerInvariant() ?? "";
        foreach (var app in SyncedApps)
        {
            if (string.IsNullOrEmpty(q) ||
                app.Name.ToLowerInvariant().Contains(q) ||
                app.AppId.ToString().Contains(q))
            {
                DisplayedApps.Add(app);
            }
        }
        AppsSummaryText = $"Found {DisplayedApps.Count} game(s)";
    }

    [RelayCommand]
    public async Task RefreshAppsAsync()
    {
        if (IsLoadingApps || string.IsNullOrEmpty(SelectedAccountId)) return;
        IsLoadingApps = true;
        try
        {
            var apps = await _cloudSync.GetAllAppsAsync(SelectedProvider.Key, SelectedAccountId);
            SyncedApps.Clear();
            foreach (var a in apps) SyncedApps.Add(new CloudSyncAppItemViewModel(a));
            ApplyAppFilter();
        }
        finally
        {
            IsLoadingApps = false;
        }
    }

    [RelayCommand]
    public async Task RestoreAppAsync(CloudSyncAppItemViewModel app)
    {
        if (app.IsBusy) return;
        app.IsBusy = true;
        try
        {
            bool ok = await _cloudSync.RestoreAppSavesAsync(SelectedProvider.Key, SelectedAccountId, app.AppId);
            if (ok) _toast.Show("Restore Saves", $"Saves for {app.Name} restored successfully.");
            else _toast.Show("Restore Saves", $"Failed to restore saves for {app.Name}.", error: true);
        }
        finally
        {
            app.IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task DeleteAppCloudDataAsync(CloudSyncAppItemViewModel app)
    {
        if (app.IsBusy) return;
        app.IsBusy = true;
        try
        {
            bool ok = await _cloudSync.Cli.DeleteRemoteAppAsync(SelectedProvider.Key, SelectedAccountId, app.AppId);
            if (ok)
            {
                _toast.Show("Delete Saves", $"Cloud saves for {app.Name} deleted.");
                await RefreshAppsAsync();
            }
            else
            {
                _toast.Show("Delete Saves", $"Failed to delete saves for {app.Name}.", error: true);
            }
        }
        finally
        {
            app.IsBusy = false;
        }
    }

    // ── Cleanup & 760 ──────────────────────────────────────────────

    [RelayCommand]
    public Task Scan760Async()
    {
        if (IsScanning760 || string.IsNullOrEmpty(SelectedAccountId)) return Task.CompletedTask;
        IsScanning760 = true;
        try
        {
            Files760.Clear();
            var files = _cloudSync.Scan760Files(SelectedAccountId);
            long totalBytes = 0;
            foreach (var f in files)
            {
                Files760.Add(new Steam760FileItemViewModel(f));
                totalBytes += f.Size;
            }
            string formattedTotal = totalBytes < 1024 * 1024
                ? $"{totalBytes / 1024.0:F1} KB"
                : $"{totalBytes / (1024.0 * 1024.0):F1} MB";
            Summary760Text = $"{Files760.Count} file(s) found in AppID 760 ({formattedTotal})";
        }
        finally
        {
            IsScanning760 = false;
        }
        return Task.CompletedTask;
    }

    [RelayCommand]
    public async Task DeleteSelected760Async()
    {
        var selected = Files760.Where(f => f.IsSelected).Select(f => new CloudSyncService.Steam760File(f.RelativePath, f.FullPath, f.Size)).ToList();
        if (selected.Count == 0) return;

        int count = _cloudSync.Delete760Files(selected);
        _toast.Show("Clean Up 760", $"Deleted {count} file(s).");
        await Scan760Async();
    }

    [RelayCommand]
    public async Task ClearAll760Async()
    {
        var all = Files760.Select(f => new CloudSyncService.Steam760File(f.RelativePath, f.FullPath, f.Size)).ToList();
        if (all.Count == 0) return;

        int count = _cloudSync.Delete760Files(all);
        _toast.Show("Clean Up 760", $"Deleted all {count} file(s).");
        await Scan760Async();
    }

    // ── Migration ──────────────────────────────────────────────────

    [RelayCommand]
    public async Task StartMigrationAsync()
    {
        if (IsMigrating) return;
        if (MigrationSource.Key == MigrationDest.Key)
        {
            _toast.Show("Migration", "Source and Destination providers cannot be the same.", error: true);
            return;
        }

        IsMigrating = true;
        MigrationLog = $"Starting migration from {MigrationSource.DisplayName} to {MigrationDest.DisplayName}...\n";
        try
        {
            var res = await _cloudSync.Cli.MigrateAsync(
                MigrationSource.Key,
                MigrationDest.Key,
                line => MigrationLog += line + "\n");

            if (res.ExitCode == 0)
            {
                MigrationLog += "\nMigration completed successfully!";
                _toast.Show("Migration", "Migration finished successfully.");
            }
            else
            {
                MigrationLog += $"\nMigration failed with exit code {res.ExitCode}.";
                _toast.Show("Migration", "Migration failed.", error: true);
            }
        }
        catch (Exception ex)
        {
            MigrationLog += $"\nError during migration: {ex.Message}";
            _toast.Show("Migration", ex.Message, error: true);
        }
        finally
        {
            IsMigrating = false;
        }
    }

    // ── Manifest Pinning ───────────────────────────────────────────

    private void LoadManifestPinning()
    {
        var cfg = _cloudSync.ReadManifestPinningConfig();
        ManifestPinningEnabled = cfg.ManifestPinning;
        AutoCommentEnabled = cfg.AutoComment;
        PinnedAppIdsText = string.Join("\n", cfg.PinnedApps);
    }

    [RelayCommand]
    public void SaveManifestPinning()
    {
        var ids = new List<long>();
        var lines = PinnedAppIdsText.Split(new[] { '\r', '\n', ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var l in lines)
        {
            if (long.TryParse(l.Trim(), out long id) && id > 0)
                ids.Add(id);
        }

        _cloudSync.SaveManifestPinningConfig(new ManifestPinningConfig
        {
            ManifestPinning = ManifestPinningEnabled,
            AutoComment = AutoCommentEnabled,
            PinnedApps = ids.Distinct().ToList()
        });

        _toast.Show("Manifest Pinning", "Manifest pinning configuration saved.");
    }
}
