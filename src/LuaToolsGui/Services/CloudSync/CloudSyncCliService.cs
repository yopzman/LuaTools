using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace LuaToolsGui.Services.CloudSync;

/// <summary>
/// Service wrapping cloud_redirect_cli.exe and companion native tools.
/// Automatically locates the CLI in %AppData%\LuaToolsGui\cloudredirect\
/// or extracts/copies necessary binaries for Steam Workshop support.
/// </summary>
public sealed class CloudSyncCliService(CloudRedirectService cloudRedirectService)
{
    private static readonly string ToolDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui", "cloudredirect");

    private static string CliPath => Path.Combine(ToolDir, "cloud_redirect_cli.exe");

    public record CliResult(int ExitCode, string Output, string Error);

    public record AuthStatusResult(bool Authenticated, string? TokenPath, string? Error);

    public record RemoteAppSummary(long AppId, int FileCount, long TotalBytes);

    /// <summary>
    /// Ensure the CloudRedirect tools (CLI, DLL, workshop tool) are downloaded and present on disk.
    /// </summary>
    public async Task<bool> EnsureToolsAsync(IProgress<double?>? progress = null, CancellationToken ct = default)
    {
        string? exe = await cloudRedirectService.EnsureToolAsync(progress, ct);
        return exe != null && File.Exists(CliPath);
    }

    /// <summary>
    /// Run an arbitrary command through cloud_redirect_cli.exe.
    /// </summary>
    public async Task<CliResult> RunCliAsync(string arguments, Action<string>? onLine = null, CancellationToken ct = default)
    {
        if (!File.Exists(CliPath))
        {
            bool ok = await EnsureToolsAsync(ct: ct);
            if (!ok || !File.Exists(CliPath))
                return new CliResult(-1, "", "cloud_redirect_cli.exe is not installed or available.");
        }

        try
        {
            var psi = new ProcessStartInfo(CliPath, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = ToolDir
            };

            using var proc = new Process { StartInfo = psi };
            var output = new System.Text.StringBuilder();
            var error = new System.Text.StringBuilder();

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    output.AppendLine(e.Data);
                    onLine?.Invoke(e.Data);
                }
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    error.AppendLine(e.Data);
                    onLine?.Invoke(e.Data);
                }
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await proc.WaitForExitAsync(ct);
            return new CliResult(proc.ExitCode, output.ToString().Trim(), error.ToString().Trim());
        }
        catch (OperationCanceledException)
        {
            return new CliResult(-2, "", "Operation cancelled.");
        }
        catch (Exception ex)
        {
            return new CliResult(-1, "", ex.Message);
        }
    }

    /// <summary>
    /// Check authentication status for a provider (gdrive, onedrive, r2, s3, folder).
    /// </summary>
    public async Task<AuthStatusResult> CheckAuthStatusAsync(string provider, CancellationToken ct = default)
    {
        if (provider == "folder")
        {
            var cfg = CloudRedirectConfigHelper.ReadConfig();
            string syncPath = cfg.SyncPath ?? "";
            if (string.IsNullOrWhiteSpace(syncPath))
                return new AuthStatusResult(false, null, "No sync folder selected.");
            if (!Directory.Exists(syncPath))
            {
                try { Directory.CreateDirectory(syncPath); }
                catch (Exception ex) { return new AuthStatusResult(false, syncPath, $"Cannot create folder: {ex.Message}"); }
            }
            return new AuthStatusResult(true, syncPath, null);
        }

        if (provider == "workshop")
        {
            var cfg = CloudRedirectConfigHelper.ReadConfig();
            string tool = EnsureWorkshopTools() ?? "";
            string syncPath = cfg.SyncPath ?? CloudRedirectConfigHelper.GetDefaultWorkshopFolder();
            if (string.IsNullOrEmpty(tool) || !File.Exists(tool))
                return new AuthStatusResult(false, null, "Workshop tool not installed.");
            if (!Directory.Exists(syncPath))
            {
                try { Directory.CreateDirectory(syncPath); }
                catch (Exception ex) { return new AuthStatusResult(false, syncPath, $"Cannot create folder: {ex.Message}"); }
            }
            return new AuthStatusResult(true, syncPath, null);
        }

        var res = await RunCliAsync($"auth-status {provider}", ct: ct);
        if (res.ExitCode != 0)
        {
            return new AuthStatusResult(false, null, string.IsNullOrEmpty(res.Error) ? res.Output : res.Error);
        }

        try
        {
            using var doc = JsonDocument.Parse(res.Output);
            var root = doc.RootElement;
            bool authed = root.TryGetProperty("authenticated", out var a) && a.GetBoolean();
            string? tp = root.TryGetProperty("token_path", out var t) ? t.GetString() : null;
            string? err = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            return new AuthStatusResult(authed, tp, err);
        }
        catch
        {
            bool authed = res.Output.Contains("authenticated\":true", StringComparison.OrdinalIgnoreCase);
            return new AuthStatusResult(authed, null, authed ? null : res.Output);
        }
    }

    /// <summary>
    /// List apps in cloud for a given provider and Steam account ID.
    /// </summary>
    public async Task<List<RemoteAppSummary>> ListRemoteAppsAsync(string provider, string accountId, CancellationToken ct = default)
    {
        var apps = new List<RemoteAppSummary>();

        if (provider is "folder" or "workshop")
        {
            var cfg = CloudRedirectConfigHelper.ReadConfig();
            string basePath = cfg.SyncPath ?? "";
            if (string.IsNullOrEmpty(basePath) && provider == "workshop")
                basePath = CloudRedirectConfigHelper.GetDefaultWorkshopFolder();

            string accountDir = Path.Combine(basePath, accountId);
            if (!Directory.Exists(accountDir)) return apps;

            foreach (var dir in Directory.GetDirectories(accountDir))
            {
                string name = Path.GetFileName(dir);
                if (long.TryParse(name, out long appId) && appId > 0)
                {
                    try
                    {
                        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                        long totalBytes = 0;
                        foreach (var f in files)
                        {
                            try { totalBytes += new FileInfo(f).Length; } catch { }
                        }
                        apps.Add(new RemoteAppSummary(appId, files.Length, totalBytes));
                    }
                    catch { }
                }
            }
            return apps;
        }

        var res = await RunCliAsync($"list-remote-apps {provider} {accountId}", ct: ct);
        if (res.ExitCode == 0 && !string.IsNullOrWhiteSpace(res.Output))
        {
            try
            {
                using var doc = JsonDocument.Parse(res.Output);
                var root = doc.RootElement;
                if (root.TryGetProperty("apps", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        long appId = 0;
                        int files = 0;
                        long bytes = 0;

                        if (item.TryGetProperty("app_id", out var idProp))
                        {
                            if (idProp.ValueKind == JsonValueKind.Number) appId = idProp.GetInt64();
                            else if (idProp.ValueKind == JsonValueKind.String && long.TryParse(idProp.GetString(), out var parsed)) appId = parsed;
                        }
                        if (item.TryGetProperty("file_count", out var fc)) files = fc.GetInt32();
                        if (item.TryGetProperty("size", out var s)) bytes = s.GetInt64();

                        if (appId > 0) apps.Add(new RemoteAppSummary(appId, files, bytes));
                    }
                }
            }
            catch { }
        }

        return apps;
    }

    /// <summary>
    /// Delete all cloud save data for a specific app.
    /// </summary>
    public async Task<bool> DeleteRemoteAppAsync(string provider, string accountId, long appId, CancellationToken ct = default)
    {
        if (provider is "folder" or "workshop")
        {
            var cfg = CloudRedirectConfigHelper.ReadConfig();
            string basePath = cfg.SyncPath ?? "";
            if (string.IsNullOrEmpty(basePath) && provider == "workshop")
                basePath = CloudRedirectConfigHelper.GetDefaultWorkshopFolder();

            string appDir = Path.Combine(basePath, accountId, appId.ToString());
            if (Directory.Exists(appDir))
            {
                try
                {
                    Directory.Delete(appDir, true);
                    return true;
                }
                catch { return false; }
            }
            return true;
        }

        var res = await RunCliAsync($"delete-remote-app {provider} {accountId} {appId}", ct: ct);
        return res.ExitCode == 0;
    }

    /// <summary>
    /// Migrate all cloud save data from source provider to destination provider.
    /// </summary>
    public async Task<CliResult> MigrateAsync(string srcProvider, string dstProvider, Action<string>? onLog = null, CancellationToken ct = default)
    {
        return await RunCliAsync($"migrate {srcProvider} {dstProvider}", onLog, ct);
    }

    /// <summary>
    /// Prepares workshop_sync_tool.exe and steam_api64.dll inside %AppData%\CloudRedirect\tools.
    /// Returns the absolute path to workshop_sync_tool.exe or null.
    /// </summary>
    public string? EnsureWorkshopTools()
    {
        try
        {
            string toolsDir = Path.Combine(CloudRedirectConfigHelper.ConfigDir, "tools");
            Directory.CreateDirectory(toolsDir);

            string targetExe = Path.Combine(toolsDir, "workshop_sync_tool.exe");
            string targetDll = Path.Combine(toolsDir, "steam_api64.dll");

            string sourceExe = Path.Combine(ToolDir, "workshop_sync_tool.exe");
            string sourceDll = Path.Combine(ToolDir, "steam_api64.dll");

            if (File.Exists(sourceExe) && (!File.Exists(targetExe) || File.GetLastWriteTimeUtc(sourceExe) > File.GetLastWriteTimeUtc(targetExe)))
            {
                File.Copy(sourceExe, targetExe, overwrite: true);
            }

            if (File.Exists(sourceDll) && (!File.Exists(targetDll) || File.GetLastWriteTimeUtc(sourceDll) > File.GetLastWriteTimeUtc(targetDll)))
            {
                File.Copy(sourceDll, targetDll, overwrite: true);
            }

            return File.Exists(targetExe) ? targetExe : null;
        }
        catch
        {
            return null;
        }
    }
}
