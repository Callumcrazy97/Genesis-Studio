using Genesis.Application.Core.Editing;
using Genesis.Application.Core.Resources;
using Genesis.Application.Studio.Docking;

namespace Genesis.Application.Studio.Forms;

public sealed partial class StudioShellForm
{
    private readonly DocumentSaveCoordinator _documentSaves = new();

    internal IReadOnlyList<DocumentSaveTarget> CaptureSaveTargets()
    {
        List<DocumentSaveTarget> targets = [];
        // Prefer the authoring window to a read-mostly viewer of the SAME image session.
        foreach (ImageEditorWindow window in _imageEditorWindows.Values.ToArray())
        {
            if (window.IsDisposed) continue;
            targets.Add(new(Path.GetFullPath(window.Resource.FullPath), window.Resource.Name, window.Session,
                () => window.IsDirty, window.Save, () => !window.IsDisposed));
        }
        foreach (ModelComposerWindow window in _modelComposerWindows.Values.ToArray())
        {
            if (window.IsDisposed) continue;
            targets.Add(new(Path.GetFullPath(window.Resource.FullPath), window.Resource.Name, window.Editor,
                () => window.IsDirty, window.Save, () => !window.IsDisposed));
        }
        foreach (IStudioDocument document in _dockPanel.Contents.OfType<IStudioDocument>().ToArray())
        {
            object session = document is ImageViewerDocument image ? image.Session
                : document is SuiteEditorDocument suite ? suite.Surface : document;
            targets.Add(new(Path.GetFullPath(document.Resource.FullPath), document.Resource.Name, session,
                () => document.IsDirty, document.Save,
                () => document is not Control { IsDisposed: true }));
        }
        return targets;
    }

    internal DocumentSaveResult SaveOpenDocuments()
    {
        try { return _documentSaves.Save(CaptureSaveTargets()); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Target discovery itself can fail (closed/malformed/external editor). In particular,
            // never allow that exception to escape the WinForms autosave timer.
            return new([], null, error);
        }
    }

    private void SaveAll() => TrySaveProject("Save Project");

    private bool TrySaveProject(string operation)
    {
        DocumentSaveResult result = SaveOpenDocuments();
        if (!ReportSaveResult(result, operation)) return false;
        ProjectModelCooker.Result models = ProjectModelCooker.CookProject(_project.RootPath);
        if (!models.Success)
        {
            foreach (ProjectModelCooker.Failure failure in models.Failures)
                _services.Log.Error("Model Cook", $"{ResourceNames.Name(_project.RootPath, failure.ResourcePath)}: {failure.Message}");
            ShowToolWindow(_console);
            SetStatus($"{operation} stopped — {models.Failures.Count} model(s) could not be cooked.");
            return false;
        }
        SetStatus(result.SavedNames.Count == 0
            ? (models.CookedCount == 0 ? "Everything is already saved." : $"Cooked {models.CookedCount} model(s).")
            : $"Saved {result.SavedNames.Count} resource(s) across all project windows · cooked {models.CookedCount} model(s).");
        return true;
    }

    private bool ReportSaveResult(DocumentSaveResult result, string operation)
    {
        if (result.Success) return true;
        string name = result.FailedName ?? "project";
        _services.Log.Error("Save", $"{operation} stopped at '{name}': {result.Error!.Message}", result.Error);
        // No modal retry loop during autosave. Successful earlier writes are not rolled back.
        SetStatus($"{operation} stopped at '{name}'. {result.SavedNames.Count} resource(s) saved; remaining edits retained. See Console.");
        if (!string.Equals(operation, "Autosave", StringComparison.Ordinal)) ShowToolWindow(_console);
        return false;
    }
}
