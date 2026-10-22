#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Genesis.Runtime.Modeling;

/// <summary>
/// An Animation Controller resource (".animator.json"): a model's states (a clip each, or a 1D blend
/// of clips by a parameter), the transitions between them (on a condition, after a blend), the
/// parameters scripts set, and where Studio draws each state. One controller serves every Object
/// that uses it (AnimationControllerUse); each instance has its own copy of the state machine.
/// </summary>
public sealed class AnimationControllerAsset
{
    public int Version { get; set; } = 1;
    public string DefaultState { get; set; } = string.Empty;
    public List<AnimationControllerParameter> Parameters { get; set; } = [];
    public List<AnimationControllerState> States { get; set; } = [];
    public List<AnimationControllerTransition> Transitions { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AnimationControllerAsset Load(string path)
    {
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Animation controller is too large.");
        var asset = JsonSerializer.Deserialize<AnimationControllerAsset>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Empty animation controller.");
        asset.Normalize();
        return asset;
    }

    public void Save(string path)
    {
        Normalize();
        string temporary = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Drops what cannot be used (unnamed states, transitions to states that are not there) and picks a default.</summary>
    public void Normalize()
    {
        Parameters ??= [];
        States ??= [];
        Transitions ??= [];
        Parameters.RemoveAll(parameter => parameter == null || string.IsNullOrWhiteSpace(parameter.Name));
        States.RemoveAll(state => state == null || string.IsNullOrWhiteSpace(state.Name));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        States.RemoveAll(state => !names.Add(state.Name.Trim()));
        Transitions.RemoveAll(transition => transition == null || !names.Contains(transition.To ?? string.Empty)
            || (transition.From != "*" && !names.Contains(transition.From ?? string.Empty)));
        if (States.Count > 0 && !names.Contains(DefaultState ?? string.Empty)) DefaultState = States[0].Name;
    }

    /// <summary>
    /// Builds this controller into an instance's graph: its base layer becomes the controller's
    /// state machine (from the default state), and each parameter not set yet takes its default.
    /// </summary>
    public void ApplyTo(AnimationController graph)
    {
        Normalize();
        foreach (AnimationControllerParameter parameter in Parameters)
        {
            if (graph.Parameters.ContainsKey(parameter.Name)) continue;
            graph.Parameters[parameter.Name] = parameter.Kind is "bool" or "trigger" ? parameter.Default != 0 : parameter.Default;
        }
        var layer = new AnimationLayer();
        AnimationStateMachine machine = layer.StateMachine;
        foreach (AnimationControllerState state in States)
        {
            AnimationBlendTree node;
            if (!string.IsNullOrWhiteSpace(state.BlendParameter) && state.Blend is { Count: > 0 })
            {
                var tree = new BlendTree1D(state.BlendParameter) { Synced = state.Synced };
                foreach (AnimationControllerBlendPoint point in state.Blend)
                    if (!string.IsNullOrWhiteSpace(point.Clip)) tree.AddClip(point.Clip, (float)point.Threshold);
                node = tree;
            }
            else
            {
                node = new ClipNode(state.Clip ?? string.Empty, state.Loop);
            }
            machine.AddState(state.Name, node);
        }
        foreach (AnimationControllerTransition transition in Transitions)
            machine.AddTransition(transition.From, transition.To, string.IsNullOrWhiteSpace(transition.Condition) ? "true" : transition.Condition,
                (float)Math.Max(0, double.IsFinite(transition.BlendSeconds) ? transition.BlendSeconds : 0));
        if (machine.Contains(DefaultState)) machine.Play(DefaultState);
        graph.Layers[0] = layer;
        graph.ActiveLayer = 0;
    }
}

public sealed class AnimationControllerParameter
{
    public string Name { get; set; } = "speed";
    /// <summary>"float", "bool" or "trigger".</summary>
    public string Kind { get; set; } = "float";
    public double Default { get; set; }
}

public sealed class AnimationControllerState
{
    public string Name { get; set; } = "Idle";
    /// <summary>The clip it plays, when it is not a blend.</summary>
    public string? Clip { get; set; } = "Idle";
    public bool Loop { get; set; } = true;
    /// <summary>A 1D blend: the parameter that chooses between the clips below.</summary>
    public string? BlendParameter { get; set; }
    public List<AnimationControllerBlendPoint>? Blend { get; set; }
    /// <summary>Keep a blend's clips in step (a walk and a run put their feet down together).</summary>
    public bool Synced { get; set; }
    /// <summary>Where Studio draws the state.</summary>
    public float X { get; set; }
    public float Y { get; set; }
}

public sealed class AnimationControllerBlendPoint
{
    public string Clip { get; set; } = string.Empty;
    public double Threshold { get; set; }
}

public sealed class AnimationControllerTransition
{
    /// <summary>The state it leaves, or "*" for any state.</summary>
    public string From { get; set; } = "*";
    public string To { get; set; } = string.Empty;
    /// <summary>As AnimationStateAddTransition reads it: a bool parameter, a comparison ("speed &gt; 0.5"), AnimationFinished, or true.</summary>
    public string Condition { get; set; } = "true";
    public double BlendSeconds { get; set; } = 0.15;
}
