using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LuaToolsGui.Services.CloudSync;

/// <summary>
/// Handles DPAPI protected token and credential files used by CloudRedirect.
/// Compatible with the C++ DLL (dpapi_util.h) and CloudRedirect companion app:
///  - Write: JSON -> DPAPI encrypt (CurrentUser) -> binary file (atomic write)
///  - Read: binary file -> DPAPI decrypt; if first byte is '{' treats as legacy plaintext JSON and re-encrypts.
/// </summary>
public static class CloudTokenFile
{
    public static string? ReadJson(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var raw = File.ReadAllBytes(path);
            if (raw.Length == 0) return null;

            if (raw[0] == (byte)'{')
            {
                var json = Encoding.UTF8.GetString(raw);
                try { WriteJson(path, json); } catch { }
                return json;
            }

            var plain = ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    public static void WriteJson(string path, string json)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var plain = Encoding.UTF8.GetBytes(json);
        var blob = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

        string temp = path + ".tmp." + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, blob);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// Model for CloudRedirect's config.json.
/// </summary>
public sealed class CloudRedirectConfigData
{
    public string Provider { get; set; } = "folder";
    public string? TokenPath { get; set; }
    public string? SyncPath { get; set; }
    public Dictionary<string, string> TokenPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int UploadInflightMb { get; set; } = 24;
    public int WorkshopAppId { get; set; } = 480;
    public int WorkshopVisibility { get; set; } = 2; // 0=Public, 1=FriendsOnly, 2=Private/Unlisted
    public string? WorkshopToolPath { get; set; }
}

/// <summary>
/// R2 Credentials model stored in r2_credentials.json (DPAPI encrypted).
/// </summary>
public sealed class R2Credentials
{
    public string AccountId { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string? KeyPrefix { get; set; }
    public string? Endpoint { get; set; }
}

/// <summary>
/// S3 Credentials model stored in s3_credentials.json (DPAPI encrypted).
/// </summary>
public sealed class S3Credentials
{
    public string Endpoint { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string Region { get; set; } = "";
    public string? KeyPrefix { get; set; }
    public bool SignPayload { get; set; }
    public bool InsecureHttp { get; set; }
    public bool InsecureTls { get; set; }
    public string? CaCertPath { get; set; }
}

/// <summary>
/// Helper for reading and writing %AppData%\CloudRedirect\config.json,
/// credential files, and steam\cloud_redirect\config.json (manifest pinning).
/// </summary>
public static class CloudRedirectConfigHelper
{
    private static readonly object FileLock = new();

    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CloudRedirect");

    public static string ConfigFilePath => Path.Combine(ConfigDir, "config.json");

    public static string GetR2CredentialPath() => Path.Combine(ConfigDir, "r2_credentials.json");

    public static string GetS3CredentialPath() => Path.Combine(ConfigDir, "s3_credentials.json");

    public static string GetDefaultTokenPath(string provider) =>
        Path.Combine(ConfigDir, $"tokens_{provider}.json");

    public static string GetDefaultSyncFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CloudRedirect_Saves");

    public static string GetDefaultWorkshopFolder() =>
        Path.Combine(ConfigDir, "workshop_mirror");

    public static CloudRedirectConfigData ReadConfig()
    {
        lock (FileLock)
        {
            var data = new CloudRedirectConfigData();
            if (!File.Exists(ConfigFilePath)) return data;

            try
            {
                var json = File.ReadAllText(ConfigFilePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("provider", out var p))
                    data.Provider = p.GetString() ?? "folder";

                if (root.TryGetProperty("token_path", out var tp))
                    data.TokenPath = tp.GetString();

                if (root.TryGetProperty("sync_path", out var sp))
                    data.SyncPath = sp.GetString();

                if (root.TryGetProperty("upload_inflight_mb", out var uim) && uim.TryGetInt32(out var mb))
                    data.UploadInflightMb = mb;

                if (root.TryGetProperty("workshop_appid", out var wa) && wa.TryGetInt32(out var appid))
                    data.WorkshopAppId = appid;

                if (root.TryGetProperty("workshop_visibility", out var wv) && wv.TryGetInt32(out var vis))
                    data.WorkshopVisibility = vis;

                if (root.TryGetProperty("workshop_tool_path", out var wtp))
                    data.WorkshopToolPath = wtp.GetString();

                if (root.TryGetProperty("token_paths", out var tps) && tps.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in tps.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                            data.TokenPaths[prop.Name] = prop.Value.GetString() ?? "";
                    }
                }
            }
            catch { }

            return data;
        }
    }

    public static void SaveConfig(CloudRedirectConfigData config)
    {
        lock (FileLock)
        {
            Directory.CreateDirectory(ConfigDir);

            // Read existing properties to preserve unowned keys
            JsonElement existing = default;
            if (File.Exists(ConfigFilePath))
            {
                try
                {
                    var oldJson = File.ReadAllText(ConfigFilePath);
                    using var oldDoc = JsonDocument.Parse(oldJson);
                    existing = oldDoc.RootElement.Clone();
                }
                catch { }
            }

            var skipKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "provider", "token_path", "sync_path", "token_paths",
                "upload_inflight_mb", "workshop_appid", "workshop_visibility", "workshop_tool_path"
            };

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();

                writer.WriteString("provider", config.Provider);

                if (config.Provider is "folder" or "workshop")
                {
                    if (!string.IsNullOrEmpty(config.SyncPath))
                        writer.WriteString("sync_path", config.SyncPath);
                }
                else
                {
                    if (!string.IsNullOrEmpty(config.TokenPath))
                        writer.WriteString("token_path", config.TokenPath);
                }

                if (config.Provider == "workshop")
                {
                    writer.WriteNumber("workshop_appid", config.WorkshopAppId);
                    writer.WriteNumber("workshop_visibility", config.WorkshopVisibility);
                    if (!string.IsNullOrEmpty(config.WorkshopToolPath))
                        writer.WriteString("workshop_tool_path", config.WorkshopToolPath);
                }

                writer.WriteNumber("upload_inflight_mb", config.UploadInflightMb);

                writer.WritePropertyName("token_paths");
                writer.WriteStartObject();
                foreach (var (k, v) in config.TokenPaths)
                {
                    writer.WriteString(k, v);
                }
                writer.WriteEndObject();

                // Preserve other properties
                if (existing.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in existing.EnumerateObject())
                    {
                        if (skipKeys.Contains(prop.Name)) continue;
                        prop.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            string outJson = Encoding.UTF8.GetString(ms.ToArray());
            string temp = ConfigFilePath + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, outJson);
            File.Move(temp, ConfigFilePath, overwrite: true);
        }
    }

    public static R2Credentials LoadR2Credentials()
    {
        var creds = new R2Credentials();
        string path = GetR2CredentialPath();
        string? json = CloudTokenFile.ReadJson(path);
        if (string.IsNullOrEmpty(json)) return creds;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("account_id", out var a)) creds.AccountId = a.GetString() ?? "";
            if (root.TryGetProperty("access_key_id", out var b)) creds.AccessKeyId = b.GetString() ?? "";
            if (root.TryGetProperty("secret_access_key", out var c)) creds.SecretAccessKey = c.GetString() ?? "";
            if (root.TryGetProperty("bucket", out var d)) creds.Bucket = d.GetString() ?? "";
            if (root.TryGetProperty("key_prefix", out var kp)) creds.KeyPrefix = kp.GetString();
            if (root.TryGetProperty("endpoint", out var ep)) creds.Endpoint = ep.GetString();
        }
        catch { }

        return creds;
    }

    public static void SaveR2Credentials(R2Credentials creds)
    {
        var obj = new Dictionary<string, string>
        {
            ["account_id"] = creds.AccountId,
            ["access_key_id"] = creds.AccessKeyId,
            ["secret_access_key"] = creds.SecretAccessKey,
            ["bucket"] = creds.Bucket
        };
        if (!string.IsNullOrEmpty(creds.KeyPrefix)) obj["key_prefix"] = creds.KeyPrefix;
        if (!string.IsNullOrEmpty(creds.Endpoint)) obj["endpoint"] = creds.Endpoint;

        string json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        CloudTokenFile.WriteJson(GetR2CredentialPath(), json);
    }

    public static S3Credentials LoadS3Credentials()
    {
        var creds = new S3Credentials();
        string path = GetS3CredentialPath();
        string? json = CloudTokenFile.ReadJson(path);
        if (string.IsNullOrEmpty(json)) return creds;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("endpoint", out var ep)) creds.Endpoint = ep.GetString() ?? "";
            if (root.TryGetProperty("access_key_id", out var a)) creds.AccessKeyId = a.GetString() ?? "";
            if (root.TryGetProperty("secret_access_key", out var b)) creds.SecretAccessKey = b.GetString() ?? "";
            if (root.TryGetProperty("bucket", out var c)) creds.Bucket = c.GetString() ?? "";
            if (root.TryGetProperty("region", out var r)) creds.Region = r.GetString() ?? "";
            if (root.TryGetProperty("key_prefix", out var kp)) creds.KeyPrefix = kp.GetString();
            if (root.TryGetProperty("sign_payload", out var sp)) creds.SignPayload = sp.GetBoolean();
            if (root.TryGetProperty("insecure_http", out var ih)) creds.InsecureHttp = ih.GetBoolean();
            if (root.TryGetProperty("insecure_tls", out var it)) creds.InsecureTls = it.GetBoolean();
            if (root.TryGetProperty("ca_cert", out var cc)) creds.CaCertPath = cc.GetString();
        }
        catch { }

        return creds;
    }

    public static void SaveS3Credentials(S3Credentials creds)
    {
        var obj = new Dictionary<string, object>
        {
            ["endpoint"] = creds.Endpoint,
            ["access_key_id"] = creds.AccessKeyId,
            ["secret_access_key"] = creds.SecretAccessKey,
            ["bucket"] = creds.Bucket,
            ["region"] = creds.Region,
            ["sign_payload"] = creds.SignPayload,
            ["insecure_http"] = creds.InsecureHttp,
            ["insecure_tls"] = creds.InsecureTls
        };
        if (!string.IsNullOrEmpty(creds.KeyPrefix)) obj["key_prefix"] = creds.KeyPrefix;
        if (!string.IsNullOrEmpty(creds.CaCertPath)) obj["ca_cert"] = creds.CaCertPath;

        string json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        CloudTokenFile.WriteJson(GetS3CredentialPath(), json);
    }
}
