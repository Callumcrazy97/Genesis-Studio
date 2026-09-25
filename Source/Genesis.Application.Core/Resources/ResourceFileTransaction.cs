namespace Genesis.Application.Core.Resources;

internal sealed class ResourceFileTransaction : IDisposable
{
    private readonly Stack<Action> _rollback = new();
    private bool _committed;

    public void Move(string source, string destination)
    {
        EnsureDestinationAvailable(destination);
        string? parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
            _rollback.Push(() => Directory.Move(destination, source));
        }
        else
        {
            File.Move(source, destination);
            _rollback.Push(() => File.Move(destination, source));
        }
    }

    public void Copy(string source, string destination)
    {
        EnsureDestinationAvailable(destination);
        string? parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (Directory.Exists(source))
        {
            _rollback.Push(() => DeleteDirectoryIfExists(destination));
            CopyDirectory(source, destination);
        }
        else
        {
            _rollback.Push(() => DeleteFileIfExists(destination));
            File.Copy(source, destination, overwrite: false);
        }
    }

    public void WriteNewText(string path, string content)
    {
        EnsureDestinationAvailable(path);
        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        _rollback.Push(() => DeleteFileIfExists(path));
        File.WriteAllText(path, content);
    }

    public void WriteText(string path, string content)
    {
        if (!File.Exists(path))
        {
            WriteNewText(path, content);
            return;
        }

        byte[] previous = File.ReadAllBytes(path);
        _rollback.Push(() => File.WriteAllBytes(path, previous));
        File.WriteAllText(path, content);
    }

    public void CreateDirectory(string path)
    {
        EnsureDestinationAvailable(path);
        _rollback.Push(() => DeleteDirectoryIfExists(path));
        Directory.CreateDirectory(path);
    }

    public void Commit()
    {
        _committed = true;
        _rollback.Clear();
    }

    public void Dispose()
    {
        if (_committed)
        {
            return;
        }

        List<Exception>? failures = null;
        while (_rollback.TryPop(out Action? rollback))
        {
            try
            {
                rollback();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                failures ??= [];
                failures.Add(exception);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("A resource transaction could not be fully rolled back.", failures);
        }
    }

    private static void EnsureDestinationAvailable(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException($"The transaction destination '{path}' already exists.");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
