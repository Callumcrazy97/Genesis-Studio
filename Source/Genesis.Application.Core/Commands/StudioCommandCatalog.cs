namespace Genesis.Application.Core.Commands;

/// <summary>One stable command identity, independent of menus, WinForms and editor controls.</summary>
public sealed class StudioCommand<TContext>
{
    public StudioCommand(string id, string title, string category, string description,
        Action<TContext> execute, Func<TContext, CommandAvailability>? availability = null,
        IEnumerable<CommandShortcut>? shortcuts = null, string keywords = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentNullException.ThrowIfNull(execute);
        Id = id; Title = title; Category = category; Description = description;
        Execute = execute; Availability = availability ?? (_ => CommandAvailability.Available);
        Shortcuts = Array.AsReadOnly((shortcuts ?? []).ToArray());
        SearchText = string.Join(' ', title, category, description, keywords, id,
            string.Join(' ', Shortcuts.Select(shortcut => shortcut.DisplayText)));
    }

    public string Id { get; }
    public string Title { get; }
    public string Category { get; }
    public string Description { get; }
    public IReadOnlyList<CommandShortcut> Shortcuts { get; }
    internal string SearchText { get; }
    internal Action<TContext> Execute { get; }
    internal Func<TContext, CommandAvailability> Availability { get; }
}

/// <summary>Platform key data is opaque to the catalog; the host supplies its display label.</summary>
public sealed record CommandShortcut(int KeyData, string DisplayText);
public readonly record struct CommandAvailability(bool Enabled, string Reason)
{
    public static CommandAvailability Available => new(true, string.Empty);
    public static CommandAvailability Unavailable(string reason) => new(false, reason);
}
public sealed record CommandSearchResult<TContext>(StudioCommand<TContext> Command, CommandAvailability State);
public enum CommandExecutionStatus { Executed, Unavailable, Unknown, Failed }
public sealed record CommandExecutionResult(CommandExecutionStatus Status, string Message, Exception? Error = null)
{
    public bool Succeeded => Status == CommandExecutionStatus.Executed;
}

/// <summary>
/// UI-thread-owned command registry. All invocation surfaces share execution, validation and search.
/// Registration is atomic, shortcuts are unique, and availability is checked again at execution.
/// Search never invokes a command or scans project assets. Recent commands are bounded and in-memory.
/// </summary>
public sealed class StudioCommandCatalog<TContext>
{
    private readonly Dictionary<string, StudioCommand<TContext>> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _shortcuts = [];
    private readonly HashSet<string> _executing = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _recent = [];
    private const int RecentLimit = 12;

    public IReadOnlyCollection<StudioCommand<TContext>> Commands => _commands.Values;
    public IReadOnlyList<string> RecentIds => _recent.AsReadOnly();

    public void Register(StudioCommand<TContext> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_commands.ContainsKey(command.Id))
            throw new ArgumentException($"A command named '{command.Id}' is already registered.", nameof(command));
        HashSet<int> keys = [];
        foreach (CommandShortcut shortcut in command.Shortcuts)
        {
            if (shortcut.KeyData == 0 || !keys.Add(shortcut.KeyData) || _shortcuts.ContainsKey(shortcut.KeyData))
                throw new ArgumentException($"Shortcut '{shortcut.DisplayText}' is already assigned or invalid.", nameof(command));
        }
        _commands.Add(command.Id, command);
        foreach (CommandShortcut shortcut in command.Shortcuts) _shortcuts.Add(shortcut.KeyData, command.Id);
    }

    public StudioCommand<TContext>? Find(string id) => _commands.GetValueOrDefault(id);
    public string? FindShortcut(int keyData) => _shortcuts.GetValueOrDefault(keyData);

    public CommandAvailability GetAvailability(string id, TContext context)
    {
        if (!_commands.TryGetValue(id, out StudioCommand<TContext>? command))
            return CommandAvailability.Unavailable("This command is no longer registered.");
        if (_executing.Contains(id)) return CommandAvailability.Unavailable("This command is already running.");
        try { return command.Availability(context); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A stale editor/control must not take down the palette or command-state timer.
            return CommandAvailability.Unavailable("Cannot check this command: " + error.Message);
        }
    }

    public CommandExecutionResult TryExecute(string id, TContext context)
    {
        if (!_commands.TryGetValue(id, out StudioCommand<TContext>? command))
            return new(CommandExecutionStatus.Unknown, "This command is no longer registered.");
        try
        {
            CommandAvailability state = GetAvailability(id, context);
            if (!state.Enabled) return new(CommandExecutionStatus.Unavailable, state.Reason);
            _executing.Add(id);
            try { command.Execute(context); }
            finally { _executing.Remove(id); }
            _recent.RemoveAll(recent => string.Equals(recent, id, StringComparison.OrdinalIgnoreCase));
            _recent.Insert(0, id);
            if (_recent.Count > RecentLimit) _recent.RemoveRange(RecentLimit, _recent.Count - RecentLimit);
            return new(CommandExecutionStatus.Executed, command.Title);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new(CommandExecutionStatus.Failed, command.Title + ": " + error.Message, error);
        }
    }

    public IReadOnlyList<CommandSearchResult<TContext>> Search(string? query, TContext context, int limit = 200)
    {
        if (limit <= 0) return [];
        string text = (query ?? string.Empty).Trim();
        string[] tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return _commands.Values
            .Where(command => tokens.All(token => command.SearchText.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(command => Score(command, text, tokens))
            .ThenBy(command => command.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(command => command.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(command => command.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select(command => new CommandSearchResult<TContext>(command, GetAvailability(command.Id, context)))
            .ToArray();
    }

    private int Score(StudioCommand<TContext> command, string text, string[] tokens)
    {
        if (text.Length == 0)
        {
            int recent = _recent.FindIndex(id => string.Equals(id, command.Id, StringComparison.OrdinalIgnoreCase));
            return recent < 0 ? 0 : RecentLimit - recent;
        }
        if (string.Equals(command.Title, text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(command.Id, text, StringComparison.OrdinalIgnoreCase)) return 1000;
        int score = command.Title.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 200 : 0;
        return score + tokens.Count(token => command.Title.Contains(token, StringComparison.OrdinalIgnoreCase)) * 10;
    }
}
