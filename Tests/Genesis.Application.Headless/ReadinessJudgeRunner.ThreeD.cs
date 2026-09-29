using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Application.Headless.Suites;
using Genesis.Runtime.Scene;
using Genesis.World.Terrain;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless;

internal enum JudgeScope { TwoD, ThreeD, All }
internal sealed record JudgeRequirement(string Name, string State);

internal static partial class ReadinessJudgeRunner
{
    internal static readonly string[] ThreeDRequirements =
    [
        "Editor.ResourceInspector", "Runtime.PGSL.ThreeD", "Runtime.Pgsl.CommandAutoTest",
        "Acceptance.CodeAssistance.AssetDefinitionsKeepEditingSpaceAndSave",
        "Acceptance.CodeAssistance.PersistentPositionAndTypedArguments",
        "Acceptance.CodeAssistance.DeclarationsCompleteFieldsAndTypedValues",
        "Acceptance.CodeAssistance.PgslCommandsAndLocalFunctionHints",
        "Acceptance.CodeAssistance.TerrainRecipeArgumentsAndGeneratedEdits",
        "Editor.Model.Intake.EmptySaveReopenAndImportControls",
        "Editor.Model.PoseWorkflow.RigPoseGenerateSaveReopen",
        "Editor.Model.Image.PreviewEditableGeometryPreservesRigAndUndo",
        "Editor.Model.Image.EmptyImageRejectsCreationAndDenseGeometryKeepsValidIndices",
        "Editor.Model.Image.OpacityCutoffControlsGeometryAndPersists",
        "Editor.Model.Image.Layout.1100.Scale1", "Editor.Model.Image.Layout.780.Scale1",
        "Editor.Model.Image.Layout.1100.Scale2", "Editor.Model.Image.Layout.1100.Scale2.GameSteps",
        "Runtime.Model.Image.SavedObjectAndLiveImage.DX11", "Runtime.Model.Image.SavedObjectAndLiveImage.DX12",
        "Runtime.Model.Image.SavedObjectAndLiveImage.Vulkan", "Runtime.Model.Image.SavedObjectAndLiveImage.OpenGL",
        "Runtime.Model.Image.SavedObjectAndLiveImage.Software",
        "Editor.Model.TexturePainting.SharedImageHistorySaveReopenAndCancelledStroke",
        "Editor.Model.TexturePainting.PerFaceMaterialsAndTiledUvs",
        "Editor.Model.TexturePainting.Layout.1440.Scale1", "Editor.Model.TexturePainting.Layout.1000.Scale1",
        "Editor.Model.TexturePainting.Layout.1440.Scale2",
        "Runtime.Model.TexturePainting.SavedObject.DX11", "Runtime.Model.TexturePainting.SavedObject.DX12",
        "Runtime.Model.TexturePainting.SavedObject.Vulkan", "Runtime.Model.TexturePainting.SavedObject.OpenGL",
        "Runtime.Model.TexturePainting.SavedObject.Software",
        "Render.OpenGL.Compatibility.Glsl45CatalogAndCache", "Render.OpenGL.Compatibility.Glsl45NativeProgramsAndCompute",
        "Editor.Model.Profile.Concave.XY.Forward", "Editor.Model.Profile.Concave.XY.Reverse",
        "Editor.Model.Profile.Concave.XZ.Forward", "Editor.Model.Profile.Concave.XZ.Reverse",
        "Editor.Model.Profile.Concave.YZ.Forward", "Editor.Model.Profile.Concave.YZ.Reverse",
        "Editor.Model.Profile.InvalidClosureIsAtomicAndUndoRestoresDrawing",
        "Editor.Model.Profile.CollinearCornersRetainValidTopology", "Editor.Model.Profile.PointerDrawAndVisibleTriangleCost",
        "Editor.Model.Tube.ThreeDGeometryTaperSeamsRigUndoAndSave",
        "Editor.Model.Tube.PointerLayout.1440.Scale1", "Editor.Model.Tube.PointerLayout.1000.Scale1", "Editor.Model.Tube.PointerLayout.1440.Scale2",
        "Editor.Model.PushPull.SignedNumericFacesUndoAndReopen", "Editor.Model.PushPull.PointerLayout.1440.Scale1",
        "Editor.Model.PushPull.PointerLayout.1000.Scale1", "Editor.Model.PushPull.PointerLayout.1440.Scale2",
        "Editor.Room.CameraList.AllAuthoredPinnedAndLiveViewsPreserveGameCamera",
        "Editor.Room.CameraList.TwoDInsetTracksAuthoredAndViewportEdits",
        "Editor.Room.CameraList.Layout.1440.Scale1", "Editor.Room.CameraList.Layout.1000.Scale1", "Editor.Room.CameraList.Layout.1440.Scale2",
        "Editor.Room.PhysicsOverlay.AuthoredCollidersContactsRayAndIsolation",
        "Editor.Room.PhysicsOverlay.Layout.1440.Scale1", "Editor.Room.PhysicsOverlay.Layout.1000.Scale1", "Editor.Room.PhysicsOverlay.Layout.1440.Scale2",
        "Editor.Physics.Model.GeneratedObjectAndEveryShapeUseSavedBoundsAndMaterial",
        "Runtime.Physics.Model.SavedScriptsBodyEditsAndDisabledComponent",
        "Editor.Physics.Model.Layout.1440.Scale1", "Editor.Physics.Model.Layout.1000.Scale1", "Editor.Physics.Model.Layout.1440.Scale2",
        "Acceptance.Physics.Model.Export.Package", "Acceptance.Physics.Model.Export.DX11", "Acceptance.Physics.Model.Export.DX12",
        "Acceptance.Physics.Model.Export.Vulkan", "Acceptance.Physics.Model.Export.OpenGL", "Acceptance.Physics.Model.Export.Software",
        "Runtime.Physics.Damping.SavedObjects.TwoD", "Runtime.Physics.Damping.SavedObjects.ThreeD",
        "Runtime.Physics.Damping.SandboxMatchesSavedBodyDecay",
        "Editor.Room.Workspace.TerrainActivationAndLiveInstances",
        "Editor.Room.Camera.ThreeDDeadZoneMatchesRuntime",
        "Editor.Terrain.Entities.OwnedPartsAndLegacyCompatibility",
        "Runtime.Model.FrameCacheRefreshesChangedAssets",
        "Runtime.PGSL.AnimationBlendTree", "Runtime.Animation.TransitionsAndRootMotion",
        "Runtime.PGSL.TransformsAndRaycast", "Runtime.PGSL.ThirdPersonCamera", "Runtime.PGSL.NavMesh",
        "Runtime.Navigation.ObstaclesAndPersistence", "Runtime.Components.CameraAndNavigationAuthoring",
        "Editor.Pathing.ThreeD.GeneratedModelObjectRunsSavedRouteAndLiveEdits",
        "Acceptance.Pathing.ThreeD.Export.Package", "Acceptance.Pathing.ThreeD.Export.DX11",
        "Acceptance.Pathing.ThreeD.Export.DX12", "Acceptance.Pathing.ThreeD.Export.Vulkan",
        "Acceptance.Pathing.ThreeD.Export.OpenGL", "Acceptance.Pathing.ThreeD.Export.Software",
        "Editor.Water.ExplicitPhysicsModes", "Walkthrough.Terrain.GrassForestPond",
        "Runtime.Terrain.Parts.SavedScriptAndConditionDriveGameplay",
        "Runtime.Terrain.Parts.PlacementAndDisabledComponents",
        "Runtime.Terrain.Parts.PrivateModelRefreshAndDependencies",
        "Runtime.Terrain.Parts.RoomPreviewShowsOwnedGeometry",
        "Runtime.Terrain.Parts.PreviewIsolationAndLivePlacement",
        "Runtime.Terrain.Parts.AuthoredSpatialAudioUsesNativeMixer",
        "Runtime.Terrain.Parts.PhysicsEnterExitScriptsUseRealContacts",
        "Runtime.Terrain.Parts.MeshColliderUsesGeometryPivotAndScale",
        "Runtime.Terrain.Parts.SavedImageMaterialsAnimateAndBindModels",
        "Runtime.Terrain.Parts.TextureModesAndLiveImageMatchAuthoringViews",
        "Runtime.Terrain.Parts.SavedScriptControlsActualParticleEmission",
        "Runtime.Terrain.Loading.CreateAndRoomStartSeeSavedGround",
        "Runtime.Terrain.Materials.SavedPaintPbrAndLiveImageMatchAuthoring",
        "Runtime.Terrain.Materials.TiledImagesKeepDetailBeyondSurfaceBake",
        "Render.Software.Materials.LinearSamplingSmoothsBakedTerrain",
        "Runtime.Model.Colliders.SavedRoundShapesUseScaledDimensionsAndPivot",
        "Runtime.Model.Colliders.SavedHullAndMeshUseRealMirroredRampGeometry",
        "Runtime.Model.Transform.SavedYawMatchesPhysicsWithoutRoll",
        "Runtime.Object.Rendering.SavedControllersStayInvisibleAndProceduralDrawsRemain",
        "Runtime.Physics.Character.GroundProbeHandlesSlopePenetrationAndOffset",
        "Runtime.Physics.Character.MotorDescendsAndJumpsOncePerInput",
        "Runtime.Physics.Character.SavedTerrainWaterSwimmingAndLanding",
        "Runtime.Physics.Character.BufferedTapWaitsForSurface",
        "Render.DX12.UploadPages.PreserveTextureCopiesAcrossFrameReuse",
        "Editor.Shader.Terrain.ComponentsGeometryAndShaderIsolation.SilkNetDx11",
        "Editor.Shader.Terrain.ComponentsGeometryAndShaderIsolation.Direct3D12",
        "Editor.Shader.Terrain.ComponentsGeometryAndShaderIsolation.Vulkan",
        "Editor.Shader.Terrain.ComponentsGeometryAndShaderIsolation.OpenGL",
        "Editor.Shader.Terrain.ComponentsGeometryAndShaderIsolation.Software",
        "Render.Readback.AlphaAndPresentedPixels.DX11", "Render.Readback.AlphaAndPresentedPixels.DX12",
        "Render.Readback.AlphaAndPresentedPixels.Vulkan", "Render.Readback.AlphaAndPresentedPixels.OpenGL",
        "Render.Readback.AlphaAndPresentedPixels.Software",
        "Render.Fog.NoisePreservesForeground.DX11", "Render.Fog.NoisePreservesForeground.DX12",
        "Render.Fog.NoisePreservesForeground.Vulkan", "Render.Fog.NoisePreservesForeground.OpenGL",
        "Render.Fog.NoisePreservesForeground.Software",
        "Render.Fog.WaterMistRespectsFootprint.DX11", "Render.Fog.WaterMistRespectsFootprint.DX12",
        "Render.Fog.WaterMistRespectsFootprint.Vulkan", "Render.Fog.WaterMistRespectsFootprint.OpenGL",
        "Acceptance.VerdantHollow.Export.Package",
        "Acceptance.VerdantHollow.Export.DX11", "Acceptance.VerdantHollow.Export.DX12",
        "Acceptance.VerdantHollow.Export.Vulkan", "Acceptance.VerdantHollow.Export.OpenGL",
        "Acceptance.VerdantHollow.Export.Software",
        "Acceptance.ExportEvidence.MeadowAndVerdantKeepIndependentReports",
        "Export.Publication.FolderWaitsForStagingHandle", "Export.Publication.FolderWaitsForPreviousHandle",
        "Export.Publication.ArchiveWaitsForPreviousHandle", "Export.Publication.FolderCancellationRestoresPreviousRelease",
        "Export.Publication.ArchiveCancellationPreservesPreviousRelease",
        "Export.Publication.LockedFolderFailureIsBoundedAndRestoresRelease", "Export.Publication.NoReplacePreservesFolderAndArchive",
    ];

    internal static JudgeRequirement[] Requirements(IEnumerable<string> names, IReadOnlyList<TestCaseResult> tests) =>
        names.Select(name => new JudgeRequirement(name,
            tests.Any(test => test.Name == name && !test.Passed) ? "Failed"
                : tests.Any(test => test.Name == name && test.Passed) ? "Passed" : "Unverified")).ToArray();

    internal static JudgeCaptureManifest MergeCaptures(string fingerprint, JudgeCaptureManifest? previous, IReadOnlyList<JudgeCapture> current) =>
        new(fingerprint, (previous?.ProductFingerprint == fingerprint ? previous.Captures : [])
            .Concat(current).GroupBy(item => (item.Surface, item.Variant)).Select(group => group.Last()).ToArray());

    internal static bool Complete(IReadOnlyList<JudgeDecision> decisions, IReadOnlyList<JudgeRequirement> requirements) =>
        decisions.Count == Surfaces.Length && Surfaces.All(surface => decisions.Count(item => item.Surface == surface.Name) == 1)
        && decisions.All(item => item.Functionality == "Passed"
            && item.Usability == "Passed" && item.Aesthetics == "Passed")
        && ThreeDRequirements.All(name => requirements.Count(item => item.Name == name && item.State == "Passed") == 1)
        && requirements.All(item => item.State == "Passed");

    internal static string[] ThreeDVariants(string surface)
    {
        string[] baseline = ["normal", "narrow", "scale125", "scale150", "scale200"];
        IEnumerable<string> additional = surface switch
        {
            "Room" => baseline.Select(variant => "3d-" + variant).Concat(["3d-camera-list", "3d-camera-list-narrow", "3d-camera-list-scale200",
                "3d-physics-overlay", "3d-physics-overlay-narrow", "3d-physics-overlay-scale200"]),
            "Pathing" => baseline.Select(variant => "3d-" + variant),
            "Model" => ["viewer", "viewer-narrow", "viewer-scale200", "edit-lower-scale200", "texture-scale200",
                "rig-dialog", "rig-dialog-scale200", "pose-dialog", "pose-dialog-scale200", "animate-dialog",
                "animate-dialog-scale200", "use-in-game", "use-in-game-scale200",
                "from-image-dialog", "from-image-dialog-narrow", "from-image-dialog-scale200", "from-image-lower-dialog-scale200",
                "texture-paint-dialog", "texture-paint-dialog-narrow", "texture-paint-dialog-scale200",
                "tube", "tube-narrow", "tube-scale200", "push-pull", "push-pull-narrow", "push-pull-scale200"],
            "Terrain" => ["sculpt", "sculpt-scale200", "paint", "paint-scale200", "objects", "objects-scale200",
                "paths-edit-dialog", "paths-edit-dialog-scale200", "foliage-generate-dialog", "foliage-generate-dialog-scale200",
                "water-properties-gameplay-dialog", "water-properties-gameplay-dialog-scale200", "part-condition-dialog",
                "part-condition-dialog-scale200", "part-script-dialog", "part-script-dialog-scale200", "material-dialog",
                "material-dialog-scale200", "use-in-game", "use-in-game-scale200"],
            "Shader" or "Particle" or "Physics" or "Script" or "Object" => ["code", "code-narrow", "code-scale200"],
            _ => [],
        };
        if (surface == "Pathing") additional = additional.Concat(["code", "code-narrow", "code-scale200",
            "3d-use-in-game", "3d-use-in-game-narrow", "3d-use-in-game-scale200", "3d-game-steps-scale200"]);
        if (surface == "Physics") additional = additional.Concat(["3d-model", "3d-model-narrow", "3d-model-scale200",
            "3d-use-in-game", "3d-use-in-game-narrow", "3d-use-in-game-scale200", "3d-game-steps-scale200"]);
        return baseline.Concat(additional).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static (ResourceItem? Room, string Object) CreateThreeDInspectionResources(JudgeSurface surface,
        ResourceService resources, ProjectSession project, string workspace)
    {
        string model = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Model),
            ResourceKind.Model, "Judge " + surface.Name + " animated model");
        using (ModelViewerControl importer = new(model, project.RootPath))
            importer.ImportExternalModel(AnimatedGlbFixture.Write(Path.Combine(workspace, surface.Name + " 3D source")));
        string obj = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.GameObject),
            ResourceKind.GameObject, "Judge " + surface.Name + " model Object");
        File.WriteAllText(obj, new JObject { ["dimension"] = "ThreeD", ["components"] = new JArray(new JObject
        { ["type"] = "ModelRendererComponent", ["props"] = new JObject { ["ModelAsset"] = ResourceNames.Name(project.RootPath, model) } }) }.ToString());
        ResourceNames.Invalidate(project.RootPath);
        string reference = ResourceNames.Name(project.RootPath, obj);
        if (surface.Name != "Room") return (null, reference);

        string terrain = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Terrain),
            ResourceKind.Terrain, "Judge 3D ground");
        Dictionary<float, float> heights = new();
        using (TerrainEditorControl editor = new(terrain, project.RootPath))
        {
            editor.ApplyGeneration(new TerrainGenParams { Preset = TerrainPreset.Flatlands,
                ResolutionX = 33, ResolutionZ = 33, CellSize = .5f, MinHeight = -2, MaxHeight = 4, Seed = 9102 });
            editor.Save();
            foreach (float x in new[] { -3f, 0f, 3f }) heights[x] = editor.Terrain.SampleHeight(x, 0) + .25f;
        }
        string file = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Room), ResourceKind.Room, "Judge 3D Room");
        RoomAsset room = RoomAsset.Create("3D model and painted ground", RoomDimension.ThreeD);
        room.Nodes.Add(new RoomNode { Name = "Authored ground", Kind = RoomNodeKind.Terrain, LayerId = room.Layers[0].Id,
            Terrain = new RoomTerrainData { Asset = ResourceNames.Name(project.RootPath, terrain), UvScale = 1 } });
        foreach (float x in new[] { -3f, 0f, 3f })
            room.Nodes.Add(new RoomNode { Name = "Animated model", Kind = RoomNodeKind.GameObject, LayerId = room.Layers[0].Id,
                GameObject = new RoomGameObjectData { Prefab = reference }, Transform = new RoomTransform { X = x, Y = heights[x] } });
        RoomAssetLoader.Save(room, file);
        return (Flatten(resources.BuildTree()).Single(item => item.FullPath == file), reference);
    }
}
