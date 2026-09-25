using System.Collections.Concurrent;

namespace Genesis.Application.Core.Diagnostics;

public sealed class StudioLog
{
    private readonly ConcurrentQueue<StudioLogEntry> _entries = new();
    private readonly string? _logFile;
    private readonly object _fileGate = new();

    public StudioLog(string? logFile = null)
    {
        _logFile = logFile;
    }

    public event EventHandler<StudioLogEntry>? EntryAdded;

    public IReadOnlyList<StudioLogEntry> Entries => _entries.ToArray();

    public void Information(string source, string message) =>
        Write(StudioLogLevel.Information, source, message);

    public void Warning(string source, string message) =>
        Write(StudioLogLevel.Warning, source, message);

    public void Error(string source, string message, Exception? exception = null) =>
        Write(
            StudioLogLevel.Error,
            source,
            exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    public void Write(StudioLogLevel level, string source, string message)
    {
        StudioLogEntry entry = new(DateTime.UtcNow, level, source, message);
        _entries.Enqueue(entry);
        while (_entries.Count > 2_000)
        {
            _entries.TryDequeue(out _);
        }

        if (!string.IsNullOrWhiteSpace(_logFile))
        {
            try
            {
                lock (_fileGate)
                {
                    string? directory = Path.GetDirectoryName(_logFile);
                    if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                    File.AppendAllText(_logFile, entry.ToString() + Environment.NewLine);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceError("Unable to write Studio log: " + error.Message);
            }
        }

        try { EntryAdded?.Invoke(this, entry); }
        catch (Exception error) { System.Diagnostics.Trace.TraceError("Studio log display: " + error); }
    }
}

public sealed record StudioLogEntry(
    DateTime TimestampUtc,
    StudioLogLevel Level,
    string Source,
    string Message)
{
    public override string ToString() =>
        $"[{TimestampUtc:O}] [{Level}] [{Source}] {Message}";
}

public enum StudioLogLevel
{
    Information,
    Warning,
    Error,
}
