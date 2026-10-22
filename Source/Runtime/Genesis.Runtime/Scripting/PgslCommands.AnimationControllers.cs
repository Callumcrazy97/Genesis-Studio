#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Assets;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

public static partial class PgslCommands
{
    // Animation Controller resources as read, by file, while the file is unchanged.
    private static readonly ConcurrentDictionary<string, (DateTime Written, AnimationControllerAsset Asset)> AnimationControllers =
        new(StringComparer.OrdinalIgnoreCase);

    [PgslCommand("AnimationControllerUse", "AnimationControllerUse(controller) -> bool",
        "Give this instance's model the states, blends and transitions of an Animation Controller resource (made in Studio's Animation Controller editor); set its parameters with AnimationStateSetFloat, AnimationStateSetBool and AnimationStateSetTrigger. False when there is no such controller (AnimationControllerError says why)", "Animation")]
    public static bool AnimationControllerUse(string controller)
    {
        PgslContext? ctx = GetContext();
        if (ctx == null) return false;
        _animationControllerError = string.Empty;
        string? path = string.IsNullOrWhiteSpace(ProjectPath) || string.IsNullOrWhiteSpace(controller)
            ? null : ResourceCatalog.Resolve(ProjectPath, controller.Trim(), ResourceType.AnimationController);
        if (path == null || !File.Exists(path))
        {
            _animationControllerError = $"No Animation Controller '{controller}' in the project.";
            return false;
        }
        try
        {
            DateTime written = File.GetLastWriteTimeUtc(path);
            if (!AnimationControllers.TryGetValue(path, out var cached) || cached.Written != written)
                AnimationControllers[path] = cached = (written, AnimationControllerAsset.Load(path));
            AnimationController? graph = Graph();
            if (graph == null) return false;
            cached.Asset.ApplyTo(graph);
            ctx.ModelAnimationActive = true;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Text.Json.JsonException or ArgumentException or FormatException)
        {
            _animationControllerError = $"Animation Controller '{controller}' could not be used: {error.Message}";
            ActiveGameContext?.Log(_animationControllerError);
            return false;
        }
    }

    [ThreadStatic] private static string? _animationControllerError;

    [PgslCommand("AnimationControllerError", "AnimationControllerError() -> string",
        "Why the last AnimationControllerUse failed; empty after one that worked", "Animation")]
    public static string AnimationControllerError() => _animationControllerError ?? string.Empty;

    [PgslCommand("AnimationStateCurrent", "AnimationStateCurrent() -> string",
        "The state this instance's animation is in (of its base layer); empty when it has none", "Animation")]
    public static string AnimationStateCurrent() =>
        GetContext()?.AnimationController is { } graph && graph.Layers.TryGetValue(0, out AnimationLayer? layer)
            ? layer.StateMachine.CurrentState : string.Empty;
}
