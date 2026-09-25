using System;
using System.IO;
using Newtonsoft.Json;

namespace Genesis.Runtime.Modeling
{
    public static class RuntimeModelStore
    {
        private static readonly JsonSerializerSettings Settings = new()
        {
            Formatting = Formatting.Indented,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore,
        };

        public static string ModelsDirectory(string projectPath)
            => Path.Combine(projectPath ?? "", "Models");

        public static string AssetPath(string projectPath, string modelName)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(modelName))
                return "";

            string name = modelName.Trim().Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(name))
                return Path.ChangeExtension(name, ".gmodel");

            if (!name.EndsWith(".gmodel", StringComparison.OrdinalIgnoreCase))
                name += ".gmodel";

            return Path.Combine(ModelsDirectory(projectPath), name);
        }

        public static string MetaPath(string projectPath, string modelName)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(modelName))
                return "";
            string name = modelName.Trim().Replace('/', Path.DirectorySeparatorChar);
            if (name.EndsWith(".gmodel", StringComparison.OrdinalIgnoreCase))
                name = name[..^7];
            return Path.Combine(ModelsDirectory(projectPath), name + ".meta");
        }

        public static bool Exists(string projectPath, string modelName)
            => File.Exists(AssetPath(projectPath, modelName));

        public static GModelAsset CreateEmpty(string name, string message = "Reimport required")
        {
            var asset = new GModelAsset
            {
                Name = name ?? "",
                ImportRequired = true,
                ImportMessage = message ?? "",
            };
            asset.RecalculateBounds();
            return asset;
        }

        public static GModelAsset Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return CreateEmpty(Path.GetFileNameWithoutExtension(path), "Missing .gmodel asset");

            try
            {
                var asset = JsonConvert.DeserializeObject<GModelAsset>(File.ReadAllText(path), Settings)
                    ?? CreateEmpty(Path.GetFileNameWithoutExtension(path), "Invalid .gmodel asset");
                if (string.IsNullOrWhiteSpace(asset.Name))
                    asset.Name = Path.GetFileNameWithoutExtension(path);
                asset.RecalculateBounds();
                GModelPrimitiveFactory.EnsureRigIntegrity(asset);
                return asset;
            }
            catch (Exception ex)
            {
                return CreateEmpty(Path.GetFileNameWithoutExtension(path), "Failed to read .gmodel: " + ex.Message);
            }
        }

        public static GModelAsset LoadByName(string projectPath, string modelName)
            => Load(AssetPath(projectPath, modelName));

        public static void Save(string path, GModelAsset asset)
        {
            if (asset == null || string.IsNullOrWhiteSpace(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            asset.ImportedUtc = asset.ImportedUtc == default ? DateTime.UtcNow : asset.ImportedUtc;
            asset.RecalculateBounds();
            File.WriteAllText(path, JsonConvert.SerializeObject(asset, Settings));
        }

        public static void SaveByName(string projectPath, string modelName, GModelAsset asset)
            => Save(AssetPath(projectPath, modelName), asset);
    }
}
