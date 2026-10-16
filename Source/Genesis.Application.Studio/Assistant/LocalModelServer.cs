using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Genesis.Application.Studio.Assistant;

/// <summary>Where the assistant's engine and model are, and how it runs them; kept per Windows user.</summary>
public sealed class AssistantSettings
{
    public string ServerPath { get; set; } = string.Empty;
    public string ModelPath { get; set; } = string.Empty;
    public int ContextLength { get; set; } = 8192;
    public bool UseGpu { get; set; } = true;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Genesis Studio", "assistant.json");

    public static AssistantSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AssistantSettings>(File.ReadAllText(FilePath), Json) ?? new() : new();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>The llama.cpp server: the one chosen, else the one Nova installed, else one beside Genesis.</summary>
    public string FindServer()
    {
        if (File.Exists(ServerPath)) return ServerPath;
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (string root in new[] { Path.Combine(local, "NovaStudio", "runtime"), Path.Combine(AppContext.BaseDirectory, "Tools", "llama") })
        {
            if (!Directory.Exists(root)) continue;
            string? found = Directory.EnumerateFiles(root, "llama-server.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (found is not null) return found;
        }
        return string.Empty;
    }

    /// <summary>GGUF models on this PC: Nova's models, Documents\Genesis Models and the chosen model's folder.</summary>
    public IReadOnlyList<string> FindModels()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        HashSet<string> models = new(StringComparer.OrdinalIgnoreCase);
        foreach (string root in new[] { Path.Combine(local, "NovaStudio", "models"), Path.Combine(documents, "Genesis Models"), Path.GetDirectoryName(ModelPath) ?? string.Empty })
        {
            if (root.Length == 0 || !Directory.Exists(root)) continue;
            foreach (string file in Directory.EnumerateFiles(root, "*.gguf", SearchOption.AllDirectories))
                if (!Path.GetFileName(file).Contains("mmproj", StringComparison.OrdinalIgnoreCase)) models.Add(file);
        }
        if (File.Exists(ModelPath)) models.Add(ModelPath);
        return [.. models.OrderBy(Path.GetFileName)];
    }
}

/// <summary>
/// A llama.cpp server running a GGUF model for the assistant: a hidden child process on loopback,
/// reachable only with a key made for this run (the way Nova runs it). Studio stops it on exit.
/// </summary>
public sealed partial class LocalModelServer : IDisposable
{
    private static readonly Lazy<LocalModelServer> SharedServer = new(() => new LocalModelServer());
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly StringBuilder _log = new();
    private Process? _process;
    private int _port;
    private string _key = string.Empty;

    public static LocalModelServer Shared => SharedServer.Value;

    public string ModelName { get; private set; } = string.Empty;

    public bool IsRunning => _process is { HasExited: false } && _port != 0;

    public event EventHandler? StateChanged;

    private LocalModelServer()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
    }

    /// <summary>Starts the server with the settings' model, waiting until it answers (loading a model takes a while).</summary>
    public async Task StartAsync(AssistantSettings settings, IProgress<string>? progress, CancellationToken ct = default)
    {
        Stop();
        string server = settings.FindServer();
        if (server.Length == 0)
            throw new InvalidOperationException("No llama.cpp server (llama-server.exe) was found. Install one through Nova, or choose it under Assistant ⚙ › Locate llama-server.");
        if (!File.Exists(settings.ModelPath))
            throw new InvalidOperationException("Choose a GGUF model first.");
        _port = FreePort();
        _key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        string[] args =
        [
            "--model", settings.ModelPath, "--alias", Path.GetFileNameWithoutExtension(settings.ModelPath),
            "--host", "127.0.0.1", "--port", _port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--api-key", _key,
            "--ctx-size", Math.Clamp(settings.ContextLength, 2048, 131072).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--threads", Math.Max(2, Environment.ProcessorCount / 2).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--n-gpu-layers", settings.UseGpu ? "999" : "0", "--parallel", "1", "--jinja", "--no-webui",
        ];
        ProcessStartInfo start = new(server)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(server)!,
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        lock (_log) _log.Clear();
        Process process = Process.Start(start) ?? throw new InvalidOperationException("llama-server could not be started.");
        process.OutputDataReceived += (_, e) => Remember(e.Data);
        process.ErrorDataReceived += (_, e) => Remember(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        ModelName = Path.GetFileNameWithoutExtension(settings.ModelPath);
        progress?.Report($"Loading {ModelName}…");

        Stopwatch waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromMinutes(3))
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException("llama-server stopped while loading the model:\n" + LogTail());
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, $"http://127.0.0.1:{_port}/health");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
                using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    StateChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }
            catch (HttpRequestException) { }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        Stop();
        throw new TimeoutException("The model did not finish loading within three minutes.");
    }

    public void Stop()
    {
        Process? process = _process;
        _process = null;
        _port = 0;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        process.Dispose();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// One chat turn. With <paramref name="jsonSchema"/>, the server constrains the reply to that
    /// JSON shape (llama.cpp turns the schema into a grammar), so a small model cannot wander off it.
    /// </summary>
    public async Task<string> ChatAsync(IReadOnlyList<(string Role, string Content)> messages, JsonObject? jsonSchema = null,
        int maxTokens = 1536, CancellationToken ct = default)
    {
        if (!IsRunning) throw new InvalidOperationException("Start the assistant's model first.");
        JsonObject body = new()
        {
            ["model"] = ModelName,
            ["temperature"] = 0.2,
            ["max_tokens"] = maxTokens,
            // Reasoning models (Qwen3 and others) would spend the answer's tokens thinking; Genesis asks for the answer.
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            ["messages"] = new JsonArray([.. messages.Select(message => (JsonNode)new JsonObject { ["role"] = message.Role, ["content"] = message.Content })]),
        };
        if (jsonSchema is not null)
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "answer", ["schema"] = jsonSchema, ["strict"] = true },
            };
        using HttpRequestMessage request = new(HttpMethod.Post, $"http://127.0.0.1:{_port}/v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"The model server answered {(int)response.StatusCode}: {text}");
        string content = JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? string.Empty;
        // Reasoning models (Qwen3 and others) may think out loud first; only the answer is kept.
        content = ThinkBlock().Replace(content, string.Empty);
        int open = content.IndexOf("<think>", StringComparison.Ordinal);
        if (open >= 0) content = content[..open];
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("The model gave no answer (it may have run out of room). Try again, or a shorter request.");
        return content.Trim();
    }

    private void Remember(string? line)
    {
        if (line is null) return;
        lock (_log)
        {
            _log.AppendLine(line);
            if (_log.Length > 8000) _log.Remove(0, _log.Length - 6000);
        }
    }

    private string LogTail()
    {
        lock (_log) return _log.Length > 2000 ? _log.ToString(_log.Length - 2000, 2000) : _log.ToString();
    }

    private static int FreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [GeneratedRegex("<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
