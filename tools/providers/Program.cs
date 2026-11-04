using OneMail.Tools.Providers;

// providers token  <gmail|outlook>          mint a refresh token (consent URL / device code printed)
// providers smoke  <gmail|outlook> [--protocols imap,smtp,pop] [--trace] [--out file.md]
//
// Environment (from the secrets env file, ONEMAIL_PROVIDERS_ENV, default: the secrets directory's providers.env):
//   GMAIL_ADDRESS, GOOGLE_CLIENT_ID, [GOOGLE_CLIENT_SECRET]      MS_ADDRESS, MS_CLIENT_ID, [MS_TENANT=consumers]
// Refresh tokens live in ONEMAIL_PROVIDERS_TOKENS (default: tokens.env beside the env file), mode 600.
// Without the credentials `smoke` exits 77 (skipped) so a CI run is a no-op.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: providers token|smoke gmail|outlook [--protocols imap,smtp,pop] [--trace] [--out file]");
    return 2;
}

string envPath = Environment.GetEnvironmentVariable("ONEMAIL_PROVIDERS_ENV") ?? "";
Dictionary<string, string> env = EnvFile.Read(string.IsNullOrEmpty(envPath) ? null : envPath);
foreach (string k in new[] { "GMAIL_ADDRESS", "GOOGLE_CLIENT_ID", "GOOGLE_CLIENT_SECRET", "MS_ADDRESS", "MS_CLIENT_ID", "MS_TENANT" })
{
    if (Environment.GetEnvironmentVariable(k) is { Length: > 0 } v)
    {
        env[k] = v; // the process environment wins over the file
    }
}

string tokenFile = Environment.GetEnvironmentVariable("ONEMAIL_PROVIDERS_TOKENS")
    ?? (envPath.Length > 0 ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(envPath))!, "tokens.env") : "tokens.env");
string provider = args[1].ToLowerInvariant();
bool gmail = provider == "gmail";
if (!gmail && provider != "outlook")
{
    Console.Error.WriteLine("provider must be gmail or outlook");
    return 2;
}

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
try
{
    if (args[0] == "token")
    {
        await (gmail ? Tokens.MintGoogle(env, tokenFile, cts.Token) : Tokens.MintMicrosoft(env, tokenFile, cts.Token));
        return 0;
    }

    string addrKey = gmail ? "GMAIL_ADDRESS" : "MS_ADDRESS";
    string clientKey = gmail ? "GOOGLE_CLIENT_ID" : "MS_CLIENT_ID";
    string refreshKey = gmail ? "GOOGLE_REFRESH_TOKEN" : "MS_REFRESH_TOKEN";
    if (!env.TryGetValue(addrKey, out string? address) || !env.ContainsKey(clientKey) || !EnvFile.Read(tokenFile).ContainsKey(refreshKey))
    {
        Console.WriteLine($"skipped: {addrKey}, {clientKey} or {refreshKey} not set");
        return 77;
    }

    string protocols = "imap,smtp,pop";
    string? outFile = null;
    bool trace = false;
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--protocols") { protocols = args[++i]; }
        else if (args[i] == "--out") { outFile = args[++i]; }
        else if (args[i] == "--trace") { trace = true; }
    }

    var smoke = new Smoke(gmail ? Smoke.Gmail : Smoke.Outlook, address, () => Tokens.Access(provider, env, tokenFile, cts.Token), trace);
    string[] want = protocols.Split(',');
    await smoke.RunAsync(want.Contains("imap"), want.Contains("smtp"), want.Contains("pop"));

    if (outFile is not null)
    {
        var lines = new List<string> { "| Provider | Protocol | Feature | Result | Detail |", "|---|---|---|---|---|" };
        lines.AddRange(smoke.Rows.Select(r => $"| {r.Provider} | {r.Protocol} | {r.Feature} | {r.Status} | {r.Detail.Replace("|", "\\|")} |"));
        File.WriteAllLines(outFile, lines);
    }

    int failed = smoke.Rows.Count(r => r.Status == "FAIL");
    Console.WriteLine($"{smoke.Rows.Count} rows, {smoke.Rows.Count(r => r.Status == "pass")} pass, {smoke.Rows.Count(r => r.Status == "not-offered")} not-offered, {failed} FAIL");
    return failed == 0 ? 0 : 1;
}
catch (Exception e)
{
    Console.Error.WriteLine(e.GetType().Name + ": " + e.Message);
    return 1;
}
