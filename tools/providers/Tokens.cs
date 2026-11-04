using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OneMail.Tools.Providers;

/// <summary>Reads KEY=VALUE files (the secrets env and the token file). Values never leave this process except into HTTP requests.</summary>
static class EnvFile
{
    public static Dictionary<string, string> Read(string? path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (path is null || !File.Exists(path))
        {
            return map;
        }

        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (line.Length == 0 || line[0] == '#' || eq <= 0)
            {
                continue;
            }

            map[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"');
        }

        return map;
    }

    /// <summary>Sets one key, keeping every other line, and leaves the file readable by its owner only.</summary>
    public static void Set(string path, string key, string value)
    {
        List<string> lines = File.Exists(path) ? [.. File.ReadAllLines(path)] : [];
        int at = lines.FindIndex(l => l.StartsWith(key + "=", StringComparison.Ordinal));
        string entry = key + "=" + value;
        if (at >= 0)
        {
            lines[at] = entry;
        }
        else
        {
            lines.Add(entry);
        }

        if (!File.Exists(path))
        {
            File.WriteAllText(path, "", new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        }

        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }
}

/// <summary>OAuth 2.0 token minting and refreshing for the two providers. Google: installed-app loopback flow with PKCE (RFC 7636, RFC 8252).
/// Microsoft: device authorization grant (RFC 8628) on the v2.0 endpoints.</summary>
static class Tokens
{
    public const string GoogleScope = "https://mail.google.com/";
    // IMAP, POP and SMTP all sit on the outlook.office.com resource; offline_access yields the refresh token.
    public const string MicrosoftScope = "https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/POP.AccessAsUser.All https://outlook.office.com/SMTP.Send offline_access";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    static string MicrosoftBase(Dictionary<string, string> env) =>
        "https://login.microsoftonline.com/" + (env.GetValueOrDefault("MS_TENANT") is { Length: > 0 } t ? t : "consumers") + "/oauth2/v2.0/";

    static string Need(Dictionary<string, string> env, string key) =>
        env.TryGetValue(key, out string? v) && v.Length > 0 ? v : throw new InvalidOperationException("missing " + key + " in the secrets env");

    static async Task<JsonElement> PostForm(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using HttpResponseMessage r = await Http.PostAsync(url, new FormUrlEncodedContent(form), ct);
        string body = await r.Content.ReadAsStringAsync(ct);
        JsonElement j = JsonDocument.Parse(body).RootElement.Clone();
        if (!r.IsSuccessStatusCode && !(j.TryGetProperty("error", out JsonElement e) && e.GetString() is "authorization_pending" or "slow_down"))
        {
            // Error bodies carry codes and descriptions, never the credentials we sent.
            throw new InvalidOperationException("token endpoint " + (int)r.StatusCode + ": " + Mask(body));
        }

        return j;
    }

    static string Mask(string s) => s.Length > 400 ? s[..400] + "…" : s;

    static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Prints the consent URL, waits for the loopback redirect and stores the refresh token under <c>GOOGLE_REFRESH_TOKEN</c>.</summary>
    public static async Task MintGoogle(Dictionary<string, string> env, string tokenFile, CancellationToken ct)
    {
        string clientId = Need(env, "GOOGLE_CLIENT_ID");
        string verifier = B64Url(RandomNumberGenerator.GetBytes(48));
        string challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = B64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = new HttpListener();
        int port = FreePort();
        string redirect = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(redirect);
        listener.Start();

        string url = "https://accounts.google.com/o/oauth2/v2/auth?response_type=code"
            + "&client_id=" + Uri.EscapeDataString(clientId)
            + "&redirect_uri=" + Uri.EscapeDataString(redirect)
            + "&scope=" + Uri.EscapeDataString(GoogleScope)
            + "&code_challenge=" + challenge + "&code_challenge_method=S256"
            + "&state=" + state + "&access_type=offline&prompt=consent"
            + (env.GetValueOrDefault("GMAIL_ADDRESS") is { Length: > 0 } hint ? "&login_hint=" + Uri.EscapeDataString(hint) : "");
        Console.WriteLine("Open this URL in a browser signed in to the test mailbox and approve (the redirect lands on this machine, 127.0.0.1:" + port + "):");
        Console.WriteLine(url);

        HttpListenerContext ctx = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(10), ct);
        string? code = ctx.Request.QueryString["code"];
        string? err = ctx.Request.QueryString["error"];
        bool stateOk = ctx.Request.QueryString["state"] == state;
        byte[] page = Encoding.UTF8.GetBytes(code is not null && stateOk ? "Done. You can close this tab." : "Authorization failed.");
        ctx.Response.OutputStream.Write(page);
        ctx.Response.Close();
        if (code is null || !stateOk)
        {
            throw new InvalidOperationException("authorization failed" + (err is null ? "" : ": " + err));
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = clientId,
            ["redirect_uri"] = redirect, ["code_verifier"] = verifier,
        };
        // Google's Desktop clients still have a client secret that the token endpoint expects.
        if (env.GetValueOrDefault("GOOGLE_CLIENT_SECRET") is { Length: > 0 } secret)
        {
            form["client_secret"] = secret;
        }

        JsonElement j = await PostForm("https://oauth2.googleapis.com/token", form, ct);
        string refresh = j.TryGetProperty("refresh_token", out JsonElement rt) ? rt.GetString()! : throw new InvalidOperationException("no refresh_token returned (revoke the app's earlier grant and retry)");
        EnvFile.Set(tokenFile, "GOOGLE_REFRESH_TOKEN", refresh);
        Console.WriteLine("stored GOOGLE_REFRESH_TOKEN");
    }

    /// <summary>Starts the device flow: prints the code and verification URL, polls, and stores <c>MS_REFRESH_TOKEN</c>.</summary>
    public static async Task MintMicrosoft(Dictionary<string, string> env, string tokenFile, CancellationToken ct)
    {
        string clientId = Need(env, "MS_CLIENT_ID");
        JsonElement dc = await PostForm(MicrosoftBase(env) + "devicecode", new() { ["client_id"] = clientId, ["scope"] = MicrosoftScope }, ct);
        Console.WriteLine("Go to " + dc.GetProperty("verification_uri").GetString() + " and enter the code " + dc.GetProperty("user_code").GetString());
        int interval = dc.TryGetProperty("interval", out JsonElement iv) ? iv.GetInt32() : 5;
        DateTime until = DateTime.UtcNow.AddSeconds(dc.TryGetProperty("expires_in", out JsonElement ex) ? ex.GetInt32() : 900);
        string deviceCode = dc.GetProperty("device_code").GetString()!;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            JsonElement j = await PostForm(MicrosoftBase(env) + "token", new()
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["client_id"] = clientId, ["device_code"] = deviceCode,
            }, ct);
            if (j.TryGetProperty("error", out JsonElement e))
            {
                string code = e.GetString()!;
                if (code == "slow_down")
                {
                    interval += 5;
                }
                else if (code != "authorization_pending")
                {
                    throw new InvalidOperationException("device flow failed: " + code);
                }

                continue;
            }

            EnvFile.Set(tokenFile, "MS_REFRESH_TOKEN", j.GetProperty("refresh_token").GetString()!);
            Console.WriteLine("stored MS_REFRESH_TOKEN");
            return;
        }

        throw new TimeoutException("device code expired");
    }

    /// <summary>Exchanges the stored refresh token for an access token. Microsoft rotates refresh tokens, so a new one is stored when it comes back.</summary>
    public static async Task<string> Access(string provider, Dictionary<string, string> env, string tokenFile, CancellationToken ct)
    {
        Dictionary<string, string> tokens = EnvFile.Read(tokenFile);
        if (provider == "gmail")
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["client_id"] = Need(env, "GOOGLE_CLIENT_ID"), ["refresh_token"] = Need(tokens, "GOOGLE_REFRESH_TOKEN"),
            };
            if (env.GetValueOrDefault("GOOGLE_CLIENT_SECRET") is { Length: > 0 } s)
            {
                form["client_secret"] = s;
            }

            return (await PostForm("https://oauth2.googleapis.com/token", form, ct)).GetProperty("access_token").GetString()!;
        }

        JsonElement j = await PostForm(MicrosoftBase(env) + "token", new()
        {
            ["grant_type"] = "refresh_token", ["client_id"] = Need(env, "MS_CLIENT_ID"), ["refresh_token"] = Need(tokens, "MS_REFRESH_TOKEN"), ["scope"] = MicrosoftScope,
        }, ct);
        if (j.TryGetProperty("refresh_token", out JsonElement nr) && nr.GetString() is { Length: > 0 } rotated)
        {
            EnvFile.Set(tokenFile, "MS_REFRESH_TOKEN", rotated);
        }

        return j.GetProperty("access_token").GetString()!;
    }

    static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
