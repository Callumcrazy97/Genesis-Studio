using Genesis.Runtime.Modeling;

namespace Genesis.Application.Core.Resources;

/// <summary>
/// Produces the runtime-native model beside every Studio model descriptor. Import, F5 and export
/// all use this boundary so an authored FBX/OBJ/DAE/BLEND source can never leak into the Player.
/// </summary>
public static class ProjectModelCooker
{
    public sealed record Failure(string ResourcePath, string Message);

    public sealed record Result(int ModelCount, int CookedCount, IReadOnlyList<Failure> Failures)
    {
        public bool Success => Failures.Count == 0;
    }

    public static Result CookProject(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string assets = Path.Combine(Path.GetFullPath(projectRoot), "Assets");
        if (!Directory.Exists(assets)) return new Result(0, 0, []);

        int modelCount = 0;
        int cookedCount = 0;
        List<Failure> failures = [];
        foreach (string resourcePath in Directory.EnumerateFiles(
                     assets,
                     "*.model.json",
                     SearchOption.AllDirectories))
        {
            modelCount++;
            try
            {
                string canonicalPath = StudioModelResourceLoader.CanonicalPath(resourcePath);
                long before = File.Exists(canonicalPath)
                    ? File.GetLastWriteTimeUtc(canonicalPath).Ticks
                    : 0L;
                GModelAsset model = StudioModelResourceLoader.Load(resourcePath);
                if (model.ImportRequired || model.Meshes.Count == 0)
                {
                    failures.Add(new Failure(
                        resourcePath,
                        string.IsNullOrWhiteSpace(model.ImportMessage)
                            ? "The model contains no runtime-renderable geometry."
                            : model.ImportMessage));
                    continue;
                }

                if (!File.Exists(canonicalPath))
                    StudioModelResourceLoader.SaveCanonical(resourcePath, model);
                long after = File.Exists(canonicalPath)
                    ? File.GetLastWriteTimeUtc(canonicalPath).Ticks
                    : 0L;
                if (after != before) cookedCount++;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or NotSupportedException
                or System.Text.Json.JsonException)
            {
                failures.Add(new Failure(resourcePath, exception.Message));
            }
        }

        return new Result(modelCount, cookedCount, failures);
    }

    public static void CookOne(string resourcePath)
    {
        GModelAsset model = StudioModelResourceLoader.Load(resourcePath);
        if (model.ImportRequired || model.Meshes.Count == 0)
        {
            throw new InvalidDataException(
                string.IsNullOrWhiteSpace(model.ImportMessage)
                    ? "The imported model contains no runtime-renderable geometry."
                    : model.ImportMessage);
        }

        string canonicalPath = StudioModelResourceLoader.CanonicalPath(resourcePath);
        if (!File.Exists(canonicalPath))
            StudioModelResourceLoader.SaveCanonical(resourcePath, model);
    }
}
