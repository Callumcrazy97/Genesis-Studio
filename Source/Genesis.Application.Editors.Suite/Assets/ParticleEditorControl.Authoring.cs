using System.Diagnostics;
using System.Text.Json;
using Genesis.Application.Core.Editing.Particles;
using Genesis.Runtime.Particles;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ParticleEditorControl
{
    private sealed record ParticleEditState(ParticleConfig Effect, int Selected, string Preset, string Content);
    private ParticleEditState? _committedParticleState;
    private string _savedParticleContent = string.Empty;
    private bool _restoringParticleState;
    private bool _particleGesture;
    private bool _codeDraftDirty;
    private string _appliedCode = string.Empty;
    private string? _draftError;
    private Label? _draftMessage;
    private ToolStripButton? _applyDraftButton;
    private ToolStripButton? _revertDraftButton;
    private readonly ParticlePreviewClock _previewClock = new();
    private readonly System.Windows.Forms.Timer _previewSeekTimer = new() { Interval = 15 };
    private int _previewSeed = 1337;
    private readonly List<ParticlePreviewKey> _previewKeys = [];
    private readonly List<string> _previewSurfaceKeys = [];
    private readonly List<string> _previewEmitterIds = [];

    private readonly record struct ParticlePreviewKey(string Texture, string Mesh, bool Flipbook,
        int Columns, int Rows, ParticleAlignment Alignment, ParticleBlendMode Blend)
    {
        public static ParticlePreviewKey From(ParticleConfig config) => new(config.TexturePath,
            config.MeshParticleAsset, config.UseFlipbook, config.FlipbookColumns, config.FlipbookRows,
            config.Alignment, config.BlendMode);
    }

    private ParticleEditState CaptureParticleState() => new(_effect.Clone(), _selectedEmitterIndex,
        _activePreset, JsonSerializer.Serialize(_effect, JsonOptions));

    private void InitialiseParticleAuthoring()
    {
        _committedParticleState = CaptureParticleState();
        _savedParticleContent = _committedParticleState.Content;
        _previewSeekTimer.Tick += (_, _) => PumpPreviewSeek();
        RefreshParticleCommands();
    }

    private void CommitParticleEdit(string label = "Edit particle properties")
    {
        if (_restoringParticleState || _committedParticleState is null) return;
        if (_particleGesture) { if (!IsDirty) MarkDirty(); return; }
        ParticleEditState next = CaptureParticleState();
        ParticleEditState before = _committedParticleState;
        _committedParticleState = next;
        if (string.Equals(before.Content, next.Content, StringComparison.Ordinal)) return;
        PushEdit(label, () => RestoreParticleState(next), () => RestoreParticleState(before), maximumEntries: 100);
        SyncParticleDirtyState();
        RefreshParticleCommands();
    }

    private void RestoreParticleState(ParticleEditState state)
    {
        _restoringParticleState = true;
        try
        {
            _codeApplyTimer.Stop(); _previewSeekTimer.Stop();
            _effect = state.Effect.Clone();
            _selectedEmitterIndex = Math.Clamp(state.Selected, 0, _effect.Emitters.Count);
            _config = ParticleEffectEditing.Emitter(_effect, _selectedEmitterIndex);
            _activePreset = state.Preset;
            _committedParticleState = state;
            _particleGesture = false;
            _codeDraftDirty = false;
            InvalidatePreviewTarget();
            RebuildEmitterPreview();
            _viewport.Mode2D = _effect.Preview2D;
            SyncControls(); PushCodeFromConfig();
            ShowParticleDraftMessage(null);
            UpdateStatus();
            InspectorStateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _restoringParticleState = false; }
    }

    public override void Undo()
    {
        FinishParticleGesture();
        if (_codeDraftDirty) { RevertParticleDraft(); return; }
        base.Undo(); SyncParticleDirtyState(); RefreshParticleCommands();
    }

    public override void Redo()
    {
        FinishParticleGesture();
        if (_codeDraftDirty) return;
        base.Redo(); SyncParticleDirtyState(); RefreshParticleCommands();
    }

    private void SyncParticleDirtyState()
    {
        if (!_codeDraftDirty && string.Equals(JsonSerializer.Serialize(_effect, JsonOptions), _savedParticleContent, StringComparison.Ordinal))
            AcceptSave();
        else MarkDirty();
    }

    private void BeginParticleGesture()
    {
        if (_committedParticleState is not null) _particleGesture = true;
    }

    private void FinishParticleGesture()
    {
        if (!_particleGesture) return;
        _particleGesture = false;
        CommitParticleEdit("Edit particle curve / gradient");
        if (!_codeDraftDirty) PushCodeFromConfig();
    }

    private void CancelParticleGesture()
    {
        if (!_particleGesture || _committedParticleState is null) return;
        _particleGesture = false;
        RestoreParticleState(_committedParticleState);
        SyncParticleDirtyState();
    }

    private void ShowParticleDraftMessage(string? error)
    {
        _draftError = error;
        if (_draftMessage is not null)
        {
            _draftMessage.Text = error ?? (_codeDraftDirty ? "Unapplied draft — apply or revert before changing emitter." : "Selected emitter definition • edits use the same runtime configuration.");
            _draftMessage.ForeColor = error is null ? EditorChrome.Muted : EditorChrome.Error;
        }
        _inspectorTabs.Enabled = !_codeDraftDirty;
        if (_curveEditor is not null) _curveEditor.Enabled = !_codeDraftDirty;
        if (_applyDraftButton is not null) _applyDraftButton.Enabled = _codeDraftDirty;
        if (_revertDraftButton is not null) _revertDraftButton.Enabled = _codeDraftDirty;
    }

    private void RevertParticleDraft()
    {
        _codeApplyTimer.Stop();
        PushCodeFromConfig();
        ShowParticleDraftMessage(null);
        SyncParticleDirtyState();
    }

    private bool PrepareParticleOperation()
    {
        FinishParticleGesture();
        if (!_codeDraftDirty || TryApplyParticleDraft()) return true;
        _code.Focus();
        return false;
    }

    private bool TryApplyParticleDraft()
    {
        if (!_codeDraftDirty) return true;
        _codeApplyTimer.Stop();
        try
        {
            string text = _code.CodeText;
            ParticleConfig parsed;
            if (text.TrimStart().StartsWith('{'))
            {
                JsonSerializerOptions options = new(JsonOptions)
                { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
                parsed = JsonSerializer.Deserialize<ParticleConfig>(text, options)
                    ?? throw new FormatException("The emitter definition is empty.");
                if (parsed.Emitters is { Count: > 0 })
                    throw new FormatException("This draft edits one emitter. Use the emitter stack to add or reorder emitters.");
            }
            else parsed = ParticleCodeCodec.Parse(text, _config);
            ValidateParticleDraft(parsed);
            NormaliseConfig(parsed);
            string name = parsed.EmitterName;
            ParticleConfig updated = ParticleEffectEditing.Replace(_effect, _selectedEmitterIndex, parsed);
            ParticleEffectEditing.Rename(updated, _selectedEmitterIndex, name);
            _effect = updated;
            EnsureEffectGradients(_effect);
            _config = ParticleEffectEditing.Emitter(_effect, _selectedEmitterIndex);
            _codeDraftDirty = false; _appliedCode = text;
            _activePreset = "Custom";
            RebuildEmitterPreview();
            SyncControls();
            ShowParticleDraftMessage(null);
            CommitParticleEdit("Apply particle definition");
            InspectorStateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException or OverflowException)
        {
            ShowParticleDraftMessage(error.Message);
            MarkDirty();
            return false;
        }
    }

    private static void ValidateParticleDraft(ParticleConfig config)
    {
        // No NaN/Infinity can reach numeric controls, allocation sizes or simulation loops.
        foreach (System.Reflection.PropertyInfo property in typeof(ParticleConfig).GetProperties())
            if (property.PropertyType == typeof(double) && property.GetValue(config) is double number && (!double.IsFinite(number) || Math.Abs(number) > 1_000_000))
                throw new FormatException(property.Name + " must be a finite number between -1,000,000 and 1,000,000.");
        if (config.StartColor is null || config.EndColor is null)
            throw new FormatException("Start and end colours cannot be null.");
        if (config.Speed < 0 || config.SpeedVariance is < 0 or > 1 || config.LifetimeVariance is < 0 or > .99
            || config.EmitRadius < 0 || config.StartSize < 0 || config.EndSize < 0
            || config.Drag < 0 || config.TurbulenceStrength < 0 || config.Emissive < 0
            || config.BoxSizeX < 0 || config.BoxSizeY < 0 || config.BoxSizeZ < 0
            || config.SizeXScale <= 0 || config.SizeYScale <= 0 || config.FlipbookFps < 0)
            throw new FormatException("Lengths, rates and sizes must be non-negative; scales must be positive; speed/lifetime variance must be in range.");
        if (config.MaxParticles is < 1 or > 100000) throw new FormatException("Maximum particles must be 1–100,000.");
        if (config.EmitRate is < 0 or > 5000) throw new FormatException("Emission rate must be 0–5,000 particles/second.");
        if (config.Lifetime is < .01 or > 3600) throw new FormatException("Lifetime must be 0.01–3,600 seconds.");
        if (config.BurstCount is < 0 or > 100000) throw new FormatException("Burst count must be 0–100,000.");
        if (config.FlipbookColumns is < 1 or > 64 || config.FlipbookRows is < 1 or > 64)
            throw new FormatException("Flipbook columns and rows must be 1–64.");
        if (config.GradientStops is { Count: > 128 }) throw new FormatException("Use at most 128 gradient keys.");
        if (!Enum.IsDefined(config.Shape) || !Enum.IsDefined(config.BlendMode) || !Enum.IsDefined(config.Alignment)
            || !Enum.IsDefined(config.CollisionMode) || !Enum.IsDefined(config.SizeCurve) || !Enum.IsDefined(config.AlphaCurve))
            throw new FormatException("One of the emitter enum values is not recognised.");
        foreach (ParticleBezierCurve? curve in new[] { config.SizeOverLifetime, config.SpeedOverLifetime, config.AlphaOverLifetime, config.VelocityOverLifetime })
            if (curve is not null && (!double.IsFinite(curve.X1) || !double.IsFinite(curve.X2)
                || !double.IsFinite(curve.Y1) || !double.IsFinite(curve.Y2)
                || curve.X1 is < 0 or > 1 || curve.X2 is < 0 or > 1 || curve.X1 > curve.X2
                || Math.Abs(curve.Y1) > 16 || Math.Abs(curve.Y2) > 16))
                throw new FormatException("Curve X coordinates must be ordered within 0–1; Y values must be finite and within -16–16.");
        IEnumerable<ParticleColor?> colours = new ParticleColor?[] { config.StartColor, config.MidColor, config.EndColor }
            .Concat((config.GradientStops ?? []).Select(stop => stop?.Color));
        foreach (ParticleColor? colour in colours)
            if (colour is not null && (!float.IsFinite(colour.R) || !float.IsFinite(colour.G) || !float.IsFinite(colour.B) || !float.IsFinite(colour.A)
                || colour.R is < 0 or > 1 || colour.G is < 0 or > 1 || colour.B is < 0 or > 1 || colour.A is < 0 or > 1))
                throw new FormatException("Gradient colour channels must be between 0 and 1.");
        foreach (ParticleGradientStop? stop in config.GradientStops ?? [])
            if (stop is null || stop.Color is null || !double.IsFinite(stop.Position) || stop.Position is < 0 or > 1)
                throw new FormatException("Gradient stops require a colour and a position between 0 and 1.");
        foreach (string? reference in new[] { config.TexturePath, config.MeshParticleAsset, config.MeshSurfaceAsset })
            if (!string.IsNullOrEmpty(reference) && (reference.Contains('/') || reference.Contains('\\')
                || reference.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || reference.EndsWith(".image", StringComparison.OrdinalIgnoreCase)))
                throw new FormatException("Resource references use only the resource name. Choose the resource in Properties, not a file path or extension.");
    }

    private void CancelPreviewSeek()
    {
        if (!_previewClock.Seeking) return;
        _previewSeekTimer.Stop();
        _previewClock.SetPlaying(false);
        SyncPreviewClock();
    }

    private void ResetParticlePreview(bool play)
    {
        _previewSeekTimer.Stop();
        _previewClock.Reset(play);
        for (int i = 0; i < _previewSimulations.Count; i++)
            _previewSimulations[i].Reset(ParticlePreviewClock.SeedForEmitter(_previewSeed, _previewEmitterIds[i]));
        SyncPreviewClock();
        _lastTime = _clock.Elapsed.TotalSeconds;
        _viewport?.Invalidate(true);
    }

    private void SyncPreviewClock()
    {
        _timelinePlaying = _previewClock.Playing;
        _timelineSeconds = _previewClock.Time;
    }

    private void StepPreviewSimulations(float step)
    {
        foreach (ParticleSimulation simulation in _previewSimulations) simulation.Step(step);
    }

    private void PumpPreviewSeek()
    {
        long start = Stopwatch.GetTimestamp();
        _previewClock.PumpSeek(StepPreviewSimulations, () => Stopwatch.GetElapsedTime(start).TotalMilliseconds < 4);
        SyncPreviewClock();
        if (!_previewClock.Seeking) _previewSeekTimer.Stop();
        _lastTime = _clock.Elapsed.TotalSeconds;
        UpdateTimelineVisual(); UpdateStatus();
        _viewport.Invalidate(true);
    }

    public void StopPreview()
    {
        ResetParticlePreview(false);
        UpdateTimelineVisual(); UpdateStatus();
    }

    public void StepPreviewFrame()
    {
        _previewSeekTimer.Stop();
        _previewClock.StepOne(StepPreviewSimulations);
        SyncPreviewClock();
        _lastTime = _clock.Elapsed.TotalSeconds;
        UpdateTimelineVisual(); UpdateStatus();
        _viewport.Invalidate(true);
    }

    public void SetPreviewSpeed(double speed) => _previewClock.Speed = speed;

    public void SetPreviewSeed(int seed)
    {
        _previewSeed = seed;
        ResetParticlePreview(_timelinePlaying);
        UpdateTimelineVisual(); UpdateStatus();
    }

    public bool PreviewSeeking => _previewClock.Seeking;
    public string? DraftError => _draftError;
}
