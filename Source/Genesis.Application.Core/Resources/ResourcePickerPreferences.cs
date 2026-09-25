using System.Collections.Concurrent;
using System.Text.Json;

namespace Genesis.Application.Core.Resources;

/// <summary>Project-local editor preferences, not resource references or game content.
/// Stable GUID keys survive logical renames and moves. Descriptor-less resources use a name
/// fallback; a name is never promoted into a filesystem path.</summary>
public sealed class ResourcePickerPreferences
{
    public const int RecentLimit = 24;
    public const int FavouriteLimit = 512;
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string _file;
    private readonly object _gate;
    private State _state = new();
    public string LastError { get; private set; } = string.Empty;
    public long SnapshotRevision { get; private set; }

    public ResourcePickerPreferences(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        _file = Path.Combine(Path.GetFullPath(projectRoot), ".genesis", "User", "ResourcePicker.json");
        _gate = Gates.GetOrAdd(_file, _ => new object());
        lock (_gate) { if (TryRead(out State state)) _state = state; }
    }

    /// <summary>Refreshes another picker/browser's committed choices without changing authored data.
    /// A damaged or newer preference file leaves the last known good in-memory snapshot intact.</summary>
    public bool Reload()
    {
        lock (_gate)
        {
            if (!TryRead(out State state)) return false;
            if (!_state.Favourites.SequenceEqual(state.Favourites, StringComparer.OrdinalIgnoreCase)
                || !_state.Recent.SequenceEqual(state.Recent, StringComparer.OrdinalIgnoreCase)) SnapshotRevision++;
            _state = state;
            return true;
        }
    }

    public bool IsFavourite(Guid id, string name) => Contains(_state.FavouriteKeys, id, name);
    public bool IsRecent(Guid id, string name) => Contains(_state.RecentRanks, id, name);
    public int RecentRank(Guid id, string name)
    {
        string key = Key(id, name), fallback = Key(Guid.Empty, name);
        State snapshot = _state;
        int exact = snapshot.RecentRanks.GetValueOrDefault(key, int.MaxValue);
        int legacy = snapshot.RecentRanks.GetValueOrDefault(fallback, int.MaxValue);
        return Math.Min(exact, legacy);
    }

    public bool SetFavourite(Guid id, string name, bool value) => Change(state =>
    {
        Remove(state.Favourites, id, name);
        if (value)
        {
            if (state.Favourites.Count >= FavouriteLimit)
                throw new InvalidOperationException($"The picker supports {FavouriteLimit} favourites per project. Remove one before adding another.");
            state.Favourites.Add(Key(id, name));
        }
    });

    public bool Remember(Guid id, string name) => Change(state =>
    {
        Remove(state.Recent, id, name);
        state.Recent.Insert(0, Key(id, name));
        if (state.Recent.Count > RecentLimit) state.Recent.RemoveRange(RecentLimit, state.Recent.Count - RecentLimit);
    });

    public bool ClearRecent() => Change(state => state.Recent.Clear());

    private bool Change(Action<State> mutate)
    {
        lock (_gate)
        {
            // Re-read before writing so two open pickers cannot overwrite one another's choices.
            if (!TryRead(out State next)) return false;
            string? pending = null;
            try
            {
                mutate(next);
                next.BuildLookups();
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                pending = _file + "." + Guid.NewGuid().ToString("N") + ".pending";
                File.WriteAllText(pending, JsonSerializer.Serialize(next, Json));
                File.Move(pending, _file, overwrite: true);
                _state = next; SnapshotRevision++; LastError = string.Empty;
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LastError = error.Message;
                return false;
            }
            finally
            {
                if (pending is not null)
                    try { File.Delete(pending); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private bool TryRead(out State state)
    {
        state = new State();
        try
        {
            if (!File.Exists(_file)) { LastError = string.Empty; return true; }
            if (new FileInfo(_file).Length > 256 * 1024) throw new InvalidDataException("Picker preferences exceed the supported size.");
            state = JsonSerializer.Deserialize<State>(File.ReadAllText(_file), Json)
                ?? throw new InvalidDataException("Picker preferences are empty.");
            if (state.Version != 1) throw new InvalidDataException("Picker preferences were written by a different format version.");
            state.Favourites = (state.Favourites ?? []).Where(ValidKey).Distinct(StringComparer.OrdinalIgnoreCase).Take(FavouriteLimit).ToList();
            state.Recent = (state.Recent ?? []).Where(ValidKey).Distinct(StringComparer.OrdinalIgnoreCase).Take(RecentLimit).ToList();
            state.BuildLookups();
            LastError = string.Empty;
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            LastError = error.Message;
            return false; // Do not overwrite an unreadable or newer preference file.
        }
    }

    private static bool Contains(HashSet<string> values, Guid id, string name) =>
        values.Contains(Key(id, name)) || values.Contains(Key(Guid.Empty, name));
    private static bool Contains(Dictionary<string, int> values, Guid id, string name) =>
        values.ContainsKey(Key(id, name)) || values.ContainsKey(Key(Guid.Empty, name));
    private static void Remove(List<string> values, Guid id, string name)
    {
        string key = Key(id, name), fallback = Key(Guid.Empty, name);
        values.RemoveAll(value => Same(value, key) || Same(value, fallback));
    }
    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static string Key(Guid id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return id == Guid.Empty ? "name:" + name.Trim() : "id:" + id.ToString("N");
    }
    private static bool ValidKey(string key) => !string.IsNullOrWhiteSpace(key) && key.Length <= 256
        && (key.StartsWith("name:", StringComparison.Ordinal) && key.Length > 5
            || key.StartsWith("id:", StringComparison.Ordinal) && Guid.TryParse(key[3..], out _));

    private sealed class State
    {
        public State() { }
        public int Version { get; set; } = 1;
        public List<string> Favourites { get; set; } = [];
        public List<string> Recent { get; set; } = [];

        // Derived, private-to-the-assembly indexes: not serialized and rebuilt before publishing
        // this snapshot. A large browser scan must not linearly search 512 bookmarks per asset.
        internal readonly HashSet<string> FavouriteKeys = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, int> RecentRanks = new(StringComparer.OrdinalIgnoreCase);
        internal void BuildLookups()
        {
            FavouriteKeys.Clear(); FavouriteKeys.UnionWith(Favourites);
            RecentRanks.Clear();
            for (int i = 0; i < Recent.Count; i++) RecentRanks.TryAdd(Recent[i], i);
        }
    }
}
