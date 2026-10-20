using System.IO;
using System.Text.Json;

namespace LuaToolsGui.Services.CloudSync;

/// <summary>
/// Model for an app tracked or found in Cloud Sync.
/// </summary>
public sealed record CloudSyncApp(
    long AppId,
    string Name,
    string AccountId,
    int FileCount,
    long TotalBytes,
    string? ChangeNumber,
    string? RootTokens,
    bool InLocalStorage,
    bool InRemoteStorage
);

/// <summary>
/// Model for Manifest Pinning settings stored in Steam\cloud_redirect\config.json.
/// </summary>
public sealed class ManifestPinningConfig
{
    public bool ManifestPinning { get; set; }
    public bool AutoComment { get; set; }
    public List<long> PinnedApps { get; set; } = [];
}

/// <summary>
/// High-level service coordinating CloudRedirect features:
/// provider config, remote & local synced apps, Steam 760 cleanup, and manifest pinning.
/// </summary>
public sealed class CloudSyncService(
    SteamService steamService,
    SteamAppListCache appListCache,
    CloudSyncCliService cliService,
    CloudSyncOAuthService oauthService)
{
    public SteamService Steam => steamService;
    public CloudSyncCliService Cli => cliService;
    public CloudSyncOAuthService OAuth => oauthService;

    /// <summary>
    /// Gets active Steam accounts (IDs) found in Steam/userdata/.
    /// </summary>
    public List<string> GetSteamAccountIds()
    {
        var accounts = new List<string>();
        string? steamPath = steamService.EffectivePath;
        if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath)) return accounts;

        string userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) return accounts;

        foreach (var dir in Directory.GetDirectories(userdata))
        {
            string name = Path.GetFileName(dir);
            if (long.TryParse(name, out long id) && id > 0 && name != "0")
            {
                accounts.Add(name);
            }
        }
        return accounts;
    }

    /// <summary>
    /// Discovers all apps that have saves in local storage (cloud_redirect/storage) or remote provider.
    /// </summary>
    public async Task<List<CloudSyncApp>> GetAllAppsAsync(string provider, string accountId, CancellationToken ct = default)
    {
        await appListCache.EnsureLoadedAsync();
        var appMap = new Dictionary<long, (int fileCount, long bytes, string? cn, string? rt, bool local, bool remote)>();

        // 1. Check local CloudRedirect storage in Steam directory
        string? steamPath = steamService.EffectivePath;
        if (!string.IsNullOrEmpty(steamPath) && Directory.Exists(steamPath))
        {
            string storageAppDir = Path.Combine(steamPath, "cloud_redirect", "storage", accountId);
            if (Directory.Exists(storageAppDir))
            {
                foreach (var dir in Directory.GetDirectories(storageAppDir))
                {
                    string name = Path.GetFileName(dir);
                    if (long.TryParse(name, out long appId) && appId > 0)
                    {
                        string? cn = null;
                        string? rt = null;
                        string cnPath = Path.Combine(dir, "cn.cloudredirect");
                        if (!File.Exists(cnPath)) cnPath = Path.Combine(dir, "cn.dat");
                        if (File.Exists(cnPath))
                        {
                            try { cn = File.ReadAllText(cnPath).Trim(); } catch { }
                        }
                        string rtPath = Path.Combine(dir, "root_token.dat");
                        if (File.Exists(rtPath))
                        {
                            try { rt = File.ReadAllText(rtPath).Trim().Replace("\n", ", "); } catch { }
                        }

                        int fc = 0;
                        long tb = 0;
                        try
                        {
                            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                            fc = files.Length;
                            foreach (var f in files)
                            {
                                try { tb += new FileInfo(f).Length; } catch { }
                            }
                        }
                        catch { }

                        appMap[appId] = (fc, tb, cn, rt, true, false);
                    }
                }
            }
        }

        // 2. Query remote provider or sync folder
        try
        {
            var remoteApps = await cliService.ListRemoteAppsAsync(provider, accountId, ct);
            foreach (var rem in remoteApps)
            {
                if (appMap.TryGetValue(rem.AppId, out var existing))
                {
                    int totalFiles = Math.Max(existing.fileCount, rem.FileCount);
                    long totalBytes = Math.Max(existing.bytes, rem.TotalBytes);
                    appMap[rem.AppId] = (totalFiles, totalBytes, existing.cn, existing.rt, existing.local, true);
                }
                else
                {
                    appMap[rem.AppId] = (rem.FileCount, rem.TotalBytes, null, null, false, true);
                }
            }
        }
        catch { }

        var list = new List<CloudSyncApp>();
        foreach (var (appId, val) in appMap)
        {
            string name = appListCache.GetName(appId) ?? $"App {appId}";
            list.Add(new CloudSyncApp(
                appId,
                name,
                accountId,
                val.fileCount,
                val.bytes,
                val.cn,
                val.rt,
                val.local,
                val.remote
            ));
        }

        return list.OrderBy(a => a.Name).ToList();
    }

    /// <summary>
    /// Restores / syncs an app's saves from the provider.
    /// </summary>
    public async Task<bool> RestoreAppSavesAsync(string provider, string accountId, long appId, CancellationToken ct = default)
    {
        string? steamPath = steamService.EffectivePath;
        if (string.IsNullOrEmpty(steamPath)) return false;

        if (provider is "folder" or "workshop")
        {
            var cfg = CloudRedirectConfigHelper.ReadConfig();
            string srcRoot = cfg.SyncPath ?? "";
            if (string.IsNullOrEmpty(srcRoot) && provider == "workshop")
                srcRoot = CloudRedirectConfigHelper.GetDefaultWorkshopFolder();

            string appSrc = Path.Combine(srcRoot, accountId, appId.ToString());
            if (!Directory.Exists(appSrc)) return false;

            // Target in Steam userdata
            string targetDir = Path.Combine(steamPath, "userdata", accountId, appId.ToString(), "remote");
            Directory.CreateDirectory(targetDir);

            // Copy files
            foreach (var file in Directory.GetFiles(appSrc, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(appSrc, file);
                // Skip blobs/ or manifest metadata if copying to remote directly
                if (rel.StartsWith("blobs", StringComparison.OrdinalIgnoreCase)) continue;
                string dest = Path.Combine(targetDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
            return true;
        }

        // Remote CLI sync
        string cloudRoot = Path.Combine(steamPath, "cloud_redirect", "storage");
        var res = await cliService.RunCliAsync($"sync-remote-app {provider} {accountId} {appId} \"{cloudRoot}\"", ct: ct);
        return res.ExitCode == 0;
    }

    // ── Steam 760 Cleanup ──────────────────────────────────────────

    public record Steam760File(string RelativePath, string FullPath, long Size);

    /// <summary>
    /// Scans files currently in Steam's 760 (screenshots / legacy SteamTools) cloud directory.
    /// </summary>
    public List<Steam760File> Scan760Files(string accountId)
    {
        var files = new List<Steam760File>();
        string? steamPath = steamService.EffectivePath;
        if (string.IsNullOrEmpty(steamPath)) return files;

        string dir760 = Path.Combine(steamPath, "userdata", accountId, "760", "remote");
        if (!Directory.Exists(dir760)) return files;

        try
        {
            foreach (var f in Directory.GetFiles(dir760, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(f);
                    string rel = Path.GetRelativePath(dir760, f);
                    files.Add(new Steam760File(rel, f, fi.Length));
                }
                catch { }
            }
        }
        catch { }

        return files;
    }

    /// <summary>
    /// Deletes specific files or clears all files in AppID 760.
    /// </summary>
    public int Delete760Files(IEnumerable<Steam760File> filesToDelete)
    {
        int deleted = 0;
        foreach (var file in filesToDelete)
        {
            try
            {
                if (File.Exists(file.FullPath))
                {
                    File.Delete(file.FullPath);
                    deleted++;
                }
            }
            catch { }
        }
        return deleted;
    }

    // ── Manifest Pinning ────────────────────────────────────────────

    private string? GetPinConfigPath()
    {
        string? steamPath = steamService.EffectivePath;
        if (string.IsNullOrEmpty(steamPath)) return null;
        return Path.Combine(steamPath, "cloud_redirect", "config.json");
    }

    public ManifestPinningConfig ReadManifestPinningConfig()
    {
        var config = new ManifestPinningConfig();
        string? path = GetPinConfigPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return config;

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("manifest_pinning", out var mp)) config.ManifestPinning = mp.GetBoolean();
            if (root.TryGetProperty("auto_comment", out var ac)) config.AutoComment = ac.GetBoolean();
            if (root.TryGetProperty("pinned_apps", out var pa) && pa.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in pa.EnumerateArray())
                {
                    if (el.TryGetInt64(out long id)) config.PinnedApps.Add(id);
                }
            }
        }
        catch { }

        return config;
    }

    public void SaveManifestPinningConfig(ManifestPinningConfig config)
    {
        string? path = GetPinConfigPath();
        if (string.IsNullOrEmpty(path)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var obj = new Dictionary<string, object>
        {
            ["manifest_pinning"] = config.ManifestPinning,
            ["auto_comment"] = config.AutoComment,
            ["pinned_apps"] = config.PinnedApps
        };
        string json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}
