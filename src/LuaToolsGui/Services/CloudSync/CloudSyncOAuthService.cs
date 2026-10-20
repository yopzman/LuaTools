using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LuaToolsGui.Services.CloudSync;

/// <summary>
/// Handles OAuth2 authorization code flow for Google Drive and OneDrive.
/// Opens the user's browser, listens for the callback on localhost, exchanges
/// the auth code for tokens, and saves them encrypted with DPAPI for the DLL.
/// </summary>
public sealed class CloudSyncOAuthService : IDisposable
{
    private const string GDriveClientId = "1072944905499-vm2v2i5dvn0a0d2o4ca36i1vge8cvbn0.apps.googleusercontent.com";
    private const string GDriveClientSecret = "v6V3fKV_zWU7iw1DrpO1rknX";
    private const string GDriveScope = "https://www.googleapis.com/auth/drive.file";
    private const string GDriveAuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string GDriveTokenUrl = "https://oauth2.googleapis.com/token";

    private const string OneDriveClientId = "b15665d9-eda6-4092-8539-0eec376afd59";
    private const string OneDriveClientSecret = "qtyfaBBYA403=unZUP40~_#";
    private const string OneDriveScope = "Files.ReadWrite offline_access";
    private const string OneDriveAuthUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
    private const string OneDriveTokenUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
    private const int OneDrivePort = 53682;

    private readonly HttpClient _http = AppHttp.Create(TimeSpan.FromSeconds(30));
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private string? _oauthState;
    private string? _codeVerifier;

    public void Cancel()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
    }

    public async Task<bool> AuthorizeAsync(
        string provider,
        string tokenPath,
        Action<string>? log = null,
        CancellationToken cancel = default)
    {
        Cancel();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        _listener = new HttpListener();

        int port = provider == "onedrive" ? OneDrivePort : 0;
        string redirectUri;

        try
        {
            if (port > 0)
            {
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Start();
                redirectUri = $"http://localhost:{port}";
            }
            else
            {
                // Dynamic port for Google Drive
                using var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                tcp.Start();
                port = ((IPEndPoint)tcp.LocalEndpoint).Port;
                tcp.Stop();

                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                redirectUri = $"http://127.0.0.1:{port}";
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Failed to bind listener on port {port}: {ex.Message}");
            return false;
        }

        try
        {
            _oauthState = GenerateRandomString(32);
            _codeVerifier = GenerateRandomString(64);
            string codeChallenge = ComputeCodeChallenge(_codeVerifier);

            string authUrl = provider == "onedrive"
                ? $"{OneDriveAuthUrl}?client_id={Uri.EscapeDataString(OneDriveClientId)}" +
                  $"&response_type=code&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                  $"&scope={Uri.EscapeDataString(OneDriveScope)}" +
                  $"&state={_oauthState}&code_challenge={codeChallenge}&code_challenge_method=S256"
                : $"{GDriveAuthUrl}?client_id={Uri.EscapeDataString(GDriveClientId)}" +
                  $"&response_type=code&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                  $"&scope={Uri.EscapeDataString(GDriveScope)}" +
                  $"&state={_oauthState}&access_type=offline&prompt=consent" +
                  $"&code_challenge={codeChallenge}&code_challenge_method=S256";

            log?.Invoke("Opening browser for authorization...");
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

            log?.Invoke("Waiting for authorization callback...");
            var context = await _listener.GetContextAsync().WaitAsync(_cts.Token);
            var req = context.Request;
            var res = context.Response;

            string? code = req.QueryString["code"];
            string? state = req.QueryString["state"];
            string? error = req.QueryString["error"];

            string html = "<html><body style='font-family:sans-serif;text-align:center;padding:40px;'>" +
                          (string.IsNullOrEmpty(error) && !string.IsNullOrEmpty(code)
                              ? "<h2>Authorization Successful!</h2><p>You can close this tab and return to LuaTools.</p>"
                              : $"<h2>Authorization Failed</h2><p>{WebUtility.HtmlEncode(error ?? "Unknown error")}</p>") +
                          "</body></html>";
            byte[] buffer = Encoding.UTF8.GetBytes(html);
            res.ContentType = "text/html";
            res.ContentLength64 = buffer.Length;
            await res.OutputStream.WriteAsync(buffer, 0, buffer.Length, _cts.Token);
            res.OutputStream.Close();

            if (!string.IsNullOrEmpty(error))
            {
                log?.Invoke($"OAuth error: {error}");
                return false;
            }

            if (state != _oauthState)
            {
                log?.Invoke("OAuth state mismatch (potential CSRF).");
                return false;
            }

            if (string.IsNullOrEmpty(code))
            {
                log?.Invoke("No authorization code received.");
                return false;
            }

            log?.Invoke("Exchanging authorization code for tokens...");
            return await ExchangeCodeAsync(provider, code, redirectUri, tokenPath, log, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            log?.Invoke("Authorization was cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Authorization failed: {ex.Message}");
            return false;
        }
        finally
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            _listener = null;
        }
    }

    private async Task<bool> ExchangeCodeAsync(
        string provider,
        string code,
        string redirectUri,
        string tokenPath,
        Action<string>? log,
        CancellationToken ct)
    {
        string tokenUrl = provider == "onedrive" ? OneDriveTokenUrl : GDriveTokenUrl;
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = provider == "onedrive" ? OneDriveClientId : GDriveClientId,
            ["client_secret"] = provider == "onedrive" ? OneDriveClientSecret : GDriveClientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = _codeVerifier ?? ""
        };

        using var content = new FormUrlEncodedContent(parameters);
        using var response = await _http.PostAsync(tokenUrl, content, ct);
        string responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            log?.Invoke($"Token exchange failed ({response.StatusCode}): {responseBody}");
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (!root.TryGetProperty("refresh_token", out var rt) || string.IsNullOrEmpty(rt.GetString()))
            {
                // In some cases (if user re-authorizes without prompt=consent) refresh_token might be omitted
                // but for initial auth we expect it
            }

            CloudTokenFile.WriteJson(tokenPath, responseBody);
            log?.Invoke($"Tokens successfully saved to {tokenPath}");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Failed to parse/save tokens: {ex.Message}");
            return false;
        }
    }

    private static string GenerateRandomString(int length)
    {
        byte[] bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").Replace("=", "")[..length];
    }

    private static string ComputeCodeChallenge(string verifier)
    {
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash).Replace("+", "-").Replace("/", "_").Replace("=", "");
    }

    public void Dispose() => Cancel();
}
