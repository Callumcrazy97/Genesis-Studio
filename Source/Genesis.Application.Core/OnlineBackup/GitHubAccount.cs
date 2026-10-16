using System.Diagnostics;

namespace Genesis.Application.Core.Backup;

/// <summary>
/// The GitHub account Studio backs projects up to: one per Windows user, set in Preferences ›
/// General. The token is stored encrypted for this Windows user (DPAPI), never in a project.
/// </summary>
public sealed class GitHubAccount : IDisposable
{
    private static readonly Lazy<GitHubAccount> SharedAccount = new(() => new GitHubAccount());
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _restored;

    public static GitHubAccount Shared => SharedAccount.Value;

    public GitHubClient Client { get; } = new();

    /// <summary>The signed-in login, or null.</summary>
    public string? Login => Client.CurrentUser?.Login;

    public bool IsSignedIn => Client.IsSignedIn;

    public event EventHandler? Changed;

    /// <summary>Signs in with the stored token, once per session. False when there is none or it no longer works.</summary>
    public async Task<bool> EnsureSignedInAsync(CancellationToken ct = default)
    {
        if (Client.IsSignedIn) return true;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Client.IsSignedIn) return true;
            if (_restored && Client.Token is null) return false;
            _restored = true;
            string? token = CredentialStore.Load();
            if (token is null) return false;
            Client.SetToken(token);
            try
            {
                await Client.SignInAsync(ct).ConfigureAwait(false);
                Changed?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception error) when (error is GitHubApiException or HttpRequestException or TaskCanceledException)
            {
                Logger.Warn("The stored GitHub token was not accepted: " + error.Message);
                Client.SetToken(null);
                return false;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Signs in with a personal access token (it needs the "repo" scope) and stores it.</summary>
    public async Task<string> SignInWithTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Paste a GitHub access token first.");
        Client.SetToken(token);
        try
        {
            GhUser user = await Client.SignInAsync(ct).ConfigureAwait(false);
            CredentialStore.Save(token);
            _restored = true;
            Changed?.Invoke(this, EventArgs.Empty);
            return user.Login;
        }
        catch
        {
            Client.SetToken(null);
            throw;
        }
    }

    /// <summary>
    /// Signs in through Git Credential Manager: it uses the GitHub sign-in already saved for git on
    /// this PC, or opens GitHub's own sign-in in the browser. Needs Git for Windows.
    /// </summary>
    public async Task<string> SignInWithBrowserAsync(CancellationToken ct = default)
    {
        string token = await Task.Run(() => AskGitCredentialManager(ct), ct).ConfigureAwait(false);
        return await SignInWithTokenAsync(token, ct).ConfigureAwait(false);
    }

    public void SignOut()
    {
        CredentialStore.Clear();
        Client.SignOut();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string AskGitCredentialManager(CancellationToken ct)
    {
        ProcessStartInfo start = new("git", "credential fill")
        {
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("Git could not be started.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("Git for Windows is not installed, so there is no browser sign-in. Use an access token instead.");
        }
        using (process)
        {
            process.StandardInput.Write("protocol=https\nhost=github.com\n\n");
            process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync(ct);
            // The browser sign-in can take a while; five minutes is generous.
            if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                throw new TimeoutException("GitHub sign-in timed out.");
            }
            string password = output.Result.Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("password=", StringComparison.Ordinal))?["password=".Length..] ?? string.Empty;
            if (password.Length == 0) throw new InvalidOperationException("GitHub sign-in was cancelled or Git Credential Manager returned no token.");
            return password;
        }
    }

    public void Dispose()
    {
        Client.Dispose();
        _gate.Dispose();
    }
}
