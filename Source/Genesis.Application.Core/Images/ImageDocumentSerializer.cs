using System.Text.Json;
using System.Text.Json.Serialization;

namespace Genesis.Application.Core.Images;

public sealed record ImageDocumentLoadResult(
    ImageDocument Document,
    int SourceSchemaVersion,
    bool WasMigrated);

public static class ImageDocumentSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    public static string Serialize(ImageDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ImageDocumentNormalizer.Normalize(document);
        ImageDocumentPathNormalizer.Normalize(document);
        ImageDocumentValidator.Validate(document);
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public static ImageDocumentLoadResult Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using JsonDocument source = JsonDocument.Parse(json);
        return Deserialize(source);
    }

    public static ImageDocumentLoadResult LoadAtomic(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        using FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using JsonDocument source = JsonDocument.Parse(stream);
        return Deserialize(source);
    }

    public static void SaveAtomic(string path, ImageDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        ImageDocumentNormalizer.Normalize(document);
        ImageDocumentPathNormalizer.Normalize(document);
        ImageDocumentValidator.Validate(document);

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The sprite document must have a parent directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (FileStream stream = new(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.WriteThrough))
            {
                using Utf8JsonWriter writer = new(
                    stream,
                    new JsonWriterOptions { Indented = true });
                JsonSerializer.Serialize(writer, document, JsonOptions);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static ImageDocumentLoadResult Deserialize(JsonDocument source)
    {
        int sourceVersion = ReadSchemaVersion(source.RootElement);
        if (sourceVersion == 1)
        {
            ImageDocument migrated = ImageDocumentMigrator.MigrateFromV1(source.RootElement);
            ImageDocumentNormalizer.Normalize(migrated);
            ImageDocumentPathNormalizer.Normalize(migrated);
            ImageDocumentValidator.Validate(migrated);
            return new ImageDocumentLoadResult(migrated, sourceVersion, WasMigrated: true);
        }

        if (sourceVersion != ImageDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Sprite schema version {sourceVersion} is not supported. Expected version " +
                $"{ImageDocument.CurrentSchemaVersion}.");
        }

        ImageDocument document = JsonSerializer.Deserialize<ImageDocument>(
                               source.RootElement.GetRawText(),
                               JsonOptions)
                           ?? throw new InvalidDataException("The sprite document is empty.");

        ImageDocumentNormalizer.Normalize(document);
        ImageDocumentPathNormalizer.Normalize(document);
        ImageDocumentValidator.Validate(document);
        return new ImageDocumentLoadResult(document, sourceVersion, WasMigrated: false);
    }

    private static int ReadSchemaVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The sprite document root must be a JSON object.");
        }

        if (TryGetSchemaProperty(root, "schemaVersion", out JsonElement version) &&
            version.TryGetInt32(out int value))
        {
            return value;
        }

        throw new InvalidDataException("Sprite documents must declare schemaVersion 2.");
    }

    private static bool TryGetSchemaProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.TryGetProperty(name, out value))
            return true;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
public static class ImageDocumentValidator
{
    public static void Validate(ImageDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != ImageDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Expected sprite schema version {ImageDocument.CurrentSchemaVersion}, " +
                $"but found {document.SchemaVersion}.");
        }

        if (document.Canvas.Width <= 0 || document.Canvas.Height <= 0)
        {
            throw new InvalidDataException("Sprite canvas dimensions must be positive.");
        }

        ValidateRelativePath(document.Import.Source, "import source");
        var slices = document.NineSlice;
        if (slices.Left < 0 || slices.Right < 0 || slices.Top < 0 || slices.Bottom < 0 ||
            (long)slices.Left+slices.Right >= document.Canvas.Width || (long)slices.Top+slices.Bottom >= document.Canvas.Height ||
            !Enum.IsDefined(slices.HorizontalEdges) || !Enum.IsDefined(slices.VerticalEdges) || !Enum.IsDefined(slices.Centre))
            throw new InvalidDataException("Nine-slice guides must leave a non-empty centre and use supported resize modes.");
        if (document.Palette.Count > 256 || document.Palette.Any(c => new[]{c.Red,c.Green,c.Blue,c.Alpha}.Any(v => !double.IsFinite(v) || v < 0 || v > 1)))
            throw new InvalidDataException("A sprite palette supports up to 256 RGBA colours with components from 0 to 1.");
        ValidateUniqueIds(document.PixelRigs.Select(rig => rig.Id), "pixel rig");
        foreach (var rig in document.PixelRigs)
        {
            if (rig.Width <= 0 || rig.Height <= 0 || (long)rig.Width*rig.Height > 4_194_304 ||
                (rig.BindPixels.Length != 0 && rig.BindPixels.LongLength != (long)rig.Width*rig.Height*4) || rig.Bones.Count > 128 || rig.Joints.Count > 128)
                throw new InvalidDataException($"Pixel rig '{rig.Name}' has invalid canvas, binding pixels or bone count.");
            var jointIds = ValidateUniqueIds(rig.Joints.Select(joint => joint.Id), "pixel rig joint");
            foreach (var joint in rig.Joints)
                if (!double.IsFinite(joint.Centre.X) || !double.IsFinite(joint.Centre.Y) || !double.IsFinite(joint.Radius) || joint.Radius <= 0)
                    throw new InvalidDataException($"Pixel rig joint '{joint.Name}' has invalid geometry.");
            var ids = ValidateUniqueIds(rig.Bones.Select(bone => bone.Id), "pixel rig bone");
            foreach (var bone in rig.Bones)
            {
                if (!double.IsFinite(bone.Start.X) || !double.IsFinite(bone.Start.Y) || !double.IsFinite(bone.End.X) || !double.IsFinite(bone.End.Y))
                    throw new InvalidDataException($"Pixel rig bone '{bone.Name}' has non-finite coordinates.");
                if (bone.ParentId != null) RequireReference(ids,bone.ParentId,"pixel rig parent");
                if (bone.StartJointId != null) RequireReference(jointIds,bone.StartJointId,"pixel rig start joint");
                if (bone.CentreJointId != null) RequireReference(jointIds,bone.CentreJointId,"pixel rig centre joint");
                if (bone.EndJointId != null) RequireReference(jointIds,bone.EndJointId,"pixel rig end joint");
                var visited = new HashSet<string>(); var current = bone;
                while (current != null) { if (!visited.Add(current.Id)) throw new InvalidDataException("Pixel rig hierarchy contains a cycle."); current = rig.Bones.FirstOrDefault(b => b.Id == current.ParentId); }
            }
            var poseIds = ValidateUniqueIds(rig.Poses.Select(pose => pose.Id), "pixel rig pose");
            foreach (var pose in rig.Poses)
            {
                var poseJointIds = ValidateUniqueIds(pose.Joints.Select(joint => joint.Id), "pose joint");
                foreach (var joint in pose.Joints)
                {
                    RequireReference(jointIds,joint.Id,"pose joint");
                    if (!double.IsFinite(joint.Centre.X) || !double.IsFinite(joint.Centre.Y) || !double.IsFinite(joint.Radius) || joint.Radius <= 0)
                        throw new InvalidDataException($"Pose '{pose.Name}' has invalid joint geometry.");
                }
                ValidateUniqueIds(pose.Bones.Select(bone => bone.Id), "pose bone");
                foreach (var bone in pose.Bones)
                {
                    RequireReference(ids,bone.Id,"pose bone");
                    if (bone.StartJointId != null) RequireReference(poseJointIds.Count > 0 ? poseJointIds : jointIds,bone.StartJointId,"pose start joint");
                    if (bone.CentreJointId != null) RequireReference(poseJointIds.Count > 0 ? poseJointIds : jointIds,bone.CentreJointId,"pose centre joint");
                    if (bone.EndJointId != null) RequireReference(poseJointIds.Count > 0 ? poseJointIds : jointIds,bone.EndJointId,"pose end joint");
                    if (!double.IsFinite(bone.Start.X) || !double.IsFinite(bone.Start.Y) || !double.IsFinite(bone.End.X) || !double.IsFinite(bone.End.Y))
                        throw new InvalidDataException($"Pose '{pose.Name}' has non-finite bone coordinates.");
                }
            }
            ValidateUniqueIds(rig.Animations.Select(animation => animation.Id), "pose animation");
            foreach (var animation in rig.Animations)
            {
                if (animation.FramesPerSecond is < 1 or > 120 || animation.Keys.Any(key => key.Frame is < 1 or > 600) || animation.Keys.Select(k => k.Frame).Distinct().Count() != animation.Keys.Count)
                    throw new InvalidDataException($"Pose animation '{animation.Name}' needs distinct frame numbers from 1 to 600 and a frame rate from 1 to 120.");
                foreach (var key in animation.Keys) RequireReference(poseIds,key.PoseId,"animation pose");
            }
        }
        HashSet<string> frameIds = ValidateUniqueIds(document.Frames.Select(frame => frame.Id), "frame");
        HashSet<string> layerIds = ValidateUniqueIds(document.Layers.Select(layer => layer.Id), "layer");
        HashSet<string> attachmentIds = ValidateUniqueIds(
            document.Attachments.Select(attachment => attachment.Id),
            "attachment");
        HashSet<string> materialChannelIds = ValidateUniqueIds(
            document.MaterialChannels.Select(channel => channel.Id),
            "material channel");
        HashSet<string> meshIds = ValidateUniqueIds(
            document.DeformMeshes.Select(mesh => mesh.Id),
            "deform mesh");
        HashSet<string> boneIds = document.Armature is null
            ? []
            : ValidateUniqueIds(document.Armature.Bones.Select(bone => bone.Id), "bone");

        foreach (ImageFrame frame in document.Frames)
        {
            if (frame.DurationMilliseconds <= 0)
            {
                throw new InvalidDataException($"Frame '{frame.Id}' must have a positive duration.");
            }

            ValidateRelativePath(frame.Source, $"frame '{frame.Id}' source");
        }

        foreach (ImageAnimationTag tag in document.Tags)
        {
            RequireReference(frameIds, tag.StartFrameId, "tag start frame");
            RequireReference(frameIds, tag.EndFrameId, "tag end frame");
        }

        foreach (ImageAnimationEvent animationEvent in document.Events)
        {
            RequireReference(frameIds, animationEvent.FrameId, "event frame");
        }

        foreach (ImageAttachment attachment in document.Attachments)
        {
            if (attachment.BoneId is not null)
            {
                RequireReference(boneIds, attachment.BoneId, "attachment bone");
            }
        }

        foreach (ImageCollisionShape shape in document.CollisionShapes)
        {
            if (shape.FrameId is not null)
            {
                RequireReference(frameIds, shape.FrameId, "collision shape frame");
            }
        }

        foreach (ImageLayer layer in document.Layers)
        {
            if (layer.ParentId is not null)
            {
                RequireReference(layerIds, layer.ParentId, "parent layer");
            }

            foreach (ImageCel cel in layer.Cels)
            {
                RequireReference(frameIds, cel.FrameId, "cel frame");
                ValidateRelativePath(cel.Source, "cel source");
            }
        }

        foreach (ImageMaterialChannelDefinition channel in document.MaterialChannels)
        {
            if (channel.LayerId is not null)
            {
                RequireReference(layerIds, channel.LayerId, "material channel layer");
            }

            ValidateRelativePath(channel.Source, "material channel source");
        }

        if (document.Armature is not null)
        {
            foreach (ImageBone bone in document.Armature.Bones)
            {
                if (bone.ParentId is not null)
                {
                    RequireReference(boneIds, bone.ParentId, "parent bone");
                }
            }
        }

        foreach (ImageDeformMesh mesh in document.DeformMeshes)
        {
            if (mesh.LayerId is not null)
            {
                RequireReference(layerIds, mesh.LayerId, "mesh layer");
            }

            if (mesh.FrameId is not null)
            {
                RequireReference(frameIds, mesh.FrameId, "mesh frame");
            }

            if (mesh.Indices.Count % 3 != 0 ||
                mesh.Indices.Any(index => index < 0 || index >= mesh.Vertices.Count))
            {
                throw new InvalidDataException($"Mesh '{mesh.Id}' has invalid triangle indices.");
            }

            foreach (ImageMeshVertex vertex in mesh.Vertices)
            {
                double weightTotal = 0;
                foreach (ImageBoneWeight weight in vertex.Weights)
                {
                    RequireReference(boneIds, weight.BoneId, "mesh weight bone");
                    if (!double.IsFinite(weight.Weight) || weight.Weight < 0 || weight.Weight > 1)
                    {
                        throw new InvalidDataException("Mesh bone weights must be between zero and one.");
                    }

                    weightTotal += weight.Weight;
                }

                if (weightTotal > 1.000001)
                {
                    throw new InvalidDataException("Mesh bone weights cannot total more than one.");
                }
            }
        }

        foreach (ImageConstraint constraint in document.Constraints)
        {
            foreach (string boneId in constraint.BoneIds)
            {
                RequireReference(boneIds, boneId, "constraint bone");
            }

            if (!boneIds.Contains(constraint.TargetId) &&
                !attachmentIds.Contains(constraint.TargetId))
            {
                throw new InvalidDataException(
                    $"The constraint target reference '{constraint.TargetId}' does not exist.");
            }
        }

        foreach (ImageAnimationTrack track in document.Tracks)
        {
            HashSet<string> targets = track.TargetKind switch
            {
                ImageTrackTargetKind.Bone => boneIds,
                ImageTrackTargetKind.Attachment => attachmentIds,
                ImageTrackTargetKind.Mesh => meshIds,
                ImageTrackTargetKind.Layer => layerIds,
                ImageTrackTargetKind.MaterialChannel => materialChannelIds,
                _ => throw new InvalidDataException($"Unknown track target kind '{track.TargetKind}'."),
            };
            RequireReference(targets, track.TargetId, "animation track target");
            int previousTime = -1;
            foreach (ImageTrackKeyframe keyframe in track.Keyframes)
            {
                if (keyframe.TimeMilliseconds < previousTime)
                {
                    throw new InvalidDataException(
                        $"Track '{track.Id}' keyframes must be ordered by time.");
                }

                previousTime = keyframe.TimeMilliseconds;
            }
        }
    }

    private static HashSet<string> ValidateUniqueIds(IEnumerable<string> ids, string kind)
    {
        HashSet<string> result = new(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            if (string.IsNullOrWhiteSpace(id) || !result.Add(id))
            {
                throw new InvalidDataException($"Every {kind} must have a unique, non-empty ID.");
            }
        }

        return result;
    }

    private static void RequireReference(HashSet<string> ids, string id, string description)
    {
        if (string.IsNullOrWhiteSpace(id) || !ids.Contains(id))
        {
            throw new InvalidDataException($"The {description} reference '{id}' does not exist.");
        }
    }

    private static void ValidateRelativePath(string? path, string description)
    {
        if (path is null)
        {
            return;
        }

        string normalized = path.Replace('\\', '/');
        if (Path.IsPathRooted(path) ||
            normalized.Split('/').Any(segment => segment == ".."))
        {
            throw new InvalidDataException($"The {description} must be a resource-relative path.");
        }
    }
}

internal static class ImageDocumentNormalizer
{
    public static void Normalize(ImageDocument document)
    {
        document.Canvas ??= new ImageCanvas();
        document.Import ??= new ImageImportSettings();
        document.Usage ??= new ImageUsageProfile();
        document.Origin ??= new ImageOrigin();
        document.Attachments ??= [];
        document.Frames ??= [];
        document.Tags ??= [];
        document.Events ??= [];
        document.CollisionShapes ??= [];
        document.Layers ??= [];
        document.MaterialChannels ??= [];
        document.DeformMeshes ??= [];
        document.Constraints ??= [];
        document.Tracks ??= [];
        document.PixelRigs ??= [];
        document.Palette ??= [];
        document.NineSlice ??= new();
        document.PixelRigs.RemoveAll(rig => rig is null);
        foreach (var rig in document.PixelRigs)
        {
            rig.BindPixels ??= []; rig.Joints ??= []; rig.Bones ??= []; rig.Poses ??= []; rig.Animations ??= [];
            foreach (var joint in rig.Joints) joint.Centre ??= new();
            foreach (var bone in rig.Bones) { bone.Start ??= new(); bone.End ??= new(); }
            foreach (var pose in rig.Poses) { pose.Bones ??= []; pose.Joints ??= []; foreach (var joint in pose.Joints) joint.Centre ??= new(); foreach (var bone in pose.Bones) { bone.Start ??= new(); bone.End ??= new(); } }
            foreach (var animation in rig.Animations) { animation.Keys ??= []; animation.GeneratedFrameIds ??= []; }
        }
        document.Usage.Sprite ??= new ImageRenderingSettings();
        document.Usage.Tileset ??= new ImageTilesetSettings();
        document.Usage.Background ??= new ImageBackgroundSettings();
        if (string.IsNullOrWhiteSpace(document.TextureGroup))
            document.TextureGroup = Genesis.Shared.Assets.TextureGroupDefaults.DefaultName;
        else
            document.TextureGroup = document.TextureGroup.Trim();

        foreach (ImageAttachment attachment in document.Attachments)
        {
            attachment.Properties ??= [];
        }

        foreach (ImageAnimationEvent animationEvent in document.Events)
        {
            animationEvent.Parameters ??= [];
        }

        foreach (ImageCollisionShape shape in document.CollisionShapes)
        {
            shape.Points ??= [];
        }

        foreach (ImageLayer layer in document.Layers)
        {
            layer.Cels ??= [];
        }

        if (document.Armature is not null)
        {
            document.Armature.Bones ??= [];
        }

        foreach (ImageDeformMesh mesh in document.DeformMeshes)
        {
            mesh.Vertices ??= [];
            mesh.Indices ??= [];
            foreach (ImageMeshVertex vertex in mesh.Vertices)
            {
                vertex.Weights ??= [];
            }
        }

        foreach (ImageConstraint constraint in document.Constraints)
        {
            constraint.BoneIds ??= [];
        }

        foreach (ImageAnimationTrack track in document.Tracks)
        {
            track.Keyframes ??= [];
            foreach (ImageTrackKeyframe keyframe in track.Keyframes)
            {
                keyframe.Deform ??= [];
            }
        }
    }
}
