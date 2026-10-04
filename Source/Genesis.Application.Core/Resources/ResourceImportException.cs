namespace Genesis.Application.Core.Resources;

/// <summary>
/// Some files of an import could not be brought in. The rest were imported; <see cref="Imported"/>
/// lists them and <see cref="Failed"/> says why each of the others was refused.
/// </summary>
public sealed class ResourceImportException : InvalidOperationException
{
    public ResourceImportException(IReadOnlyList<string> imported, IReadOnlyList<(string Source, Exception Error)> failed)
        : base(Describe(imported, failed), failed.Count > 0 ? failed[0].Error : null)
    {
        Imported = imported;
        Failed = failed;
    }

    public IReadOnlyList<string> Imported { get; }

    public IReadOnlyList<(string Source, Exception Error)> Failed { get; }

    private static string Describe(IReadOnlyList<string> imported, IReadOnlyList<(string Source, Exception Error)> failed)
    {
        int total = imported.Count + failed.Count;
        string reasons = string.Join("; ", failed.Take(5).Select(f => $"{Path.GetFileName(f.Source)}: {f.Error.Message}"));
        string more = failed.Count > 5 ? $" (and {failed.Count - 5} more)" : string.Empty;
        return $"Imported {imported.Count} of {total} file(s). Not imported: {reasons}{more}";
    }
}
