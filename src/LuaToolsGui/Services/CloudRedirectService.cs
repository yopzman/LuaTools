using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// Downloads + launches CloudRedirect.exe. The user-facing CloudRedirect GUI manager (qibo30). The
/// release is fetched from the latest GitHub release (via <see cref="GithubProxy"/>, so it works in blocked
/// regions) and cached under %AppData%\LuaToolsGui\cloudredirect. The Mode page's "Manage" button (shown when
/// CloudRedirect is enabled) calls <see cref="LaunchAsync"/>, which opens its window visibly and returns immediately.
/// </summary>
public class CloudRedirectService(GithubProxy gh)
{
    private static readonly string ToolDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui", "cloudredirect");
    private static string ExePath => Path.Combine(ToolDir, "CloudRedirect.exe");
    private static string TagPath => Path.Combine(ToolDir, "tag.txt");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Ensure CloudRedirect.exe (and accompanying tools) is on disk. Returns its
    /// path, or null if it couldn't be obtained.</summary>
    public async Task<string?> EnsureToolAsync(IProgress<double?>? progress, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        bool have = File.Exists(ExePath);
        try
        {
            string url = $"https://api.github.com/repos/{AppConfig.CloudRedirectRepo}/releases/latest";
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) return have ? ExePath : null;

            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            if (release is null) return have ? ExePath : null;

            // If we already have the exe and the installed tag matches the latest release, we're up to date.
            if (have && File.Exists(TagPath) && string.Equals(File.ReadAllText(TagPath).Trim(), release.TagName, StringComparison.OrdinalIgnoreCase))
            {
                return ExePath;
            }

            var zipAsset = release.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !a.Name.Contains("Debug", StringComparison.OrdinalIgnoreCase));
            var exeAsset = release.Assets.FirstOrDefault(a => a.Name.Equals("CloudRedirect.exe", StringComparison.OrdinalIgnoreCase));

            if (zipAsset is null && exeAsset is null) return have ? ExePath : null;

            Directory.CreateDirectory(ToolDir);

            if (zipAsset is not null)
            {
                string zipPath = Path.Combine(ToolDir, "cloudredirect.zip");
                await gh.DownloadAsync(zipAsset.DownloadUrl, zipPath, progress, ct);

                if (AssetHash.Matches(zipPath, zipAsset.Digest))
                {
                    ZipFile.ExtractToDirectory(zipPath, ToolDir, overwriteFiles: true);
                }
                else
                {
                    try { File.Delete(zipPath); } catch { }
                    return have ? ExePath : null;
                }
                try { File.Delete(zipPath); } catch { }
            }
            else if (exeAsset is not null)
            {
                await gh.DownloadAsync(exeAsset.DownloadUrl, ExePath, progress, ct);
            }

            if (File.Exists(ExePath))
            {
                if (!string.IsNullOrEmpty(release.TagName))
                {
                    try { File.WriteAllText(TagPath, release.TagName); } catch { }
                }
                return ExePath;
            }

            return have ? ExePath : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return have ? ExePath : null; }
        finally { _gate.Release(); }
    }

    /// <summary>Download (if needed), and launch the CloudRedirect GUI. Fire-and-forget. Its window opens and
    /// we don't wait for it. Returns false if the tool couldn't be downloaded or started.</summary>
    public async Task<bool> LaunchAsync(IProgress<double?>? progress, CancellationToken ct = default)
    {
        string? exe = await EnsureToolAsync(progress, ct);
        if (exe is null) return false;

        try
        {
            // GUI app: show its window (UseShellExecute) and don't block our app on it.
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = ToolDir,
            });
            return true;
        }
        catch { return false; }
    }
}
