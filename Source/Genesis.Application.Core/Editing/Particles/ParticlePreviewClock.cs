namespace Genesis.Application.Core.Editing.Particles;

/// <summary>
/// UI-independent fixed-step transport. Seeking is pumped in bounded slices by the editor UI
/// timer; no worker thread races the renderer or touches controls. Speed/seed are preview-only.
/// </summary>
public sealed class ParticlePreviewClock
{
    public const double StepSeconds = 1d / 60d;
    public const double MaximumSeekSeconds = 60d;
    public const int MaximumStepsPerPump = 8;
    private double _accumulator;
    private int _frame;
    private int? _seekFrame;
    private double _speed = 1d;

    public bool Playing { get; private set; } = true;
    public bool Seeking => _seekFrame.HasValue;
    public double Time => _frame * StepSeconds;
    public double SeekTarget => (_seekFrame ?? _frame) * StepSeconds;
    public double Speed
    {
        get => _speed;
        set
        {
            if (!double.IsFinite(value) || value is < .1 or > 4) throw new ArgumentOutOfRangeException(nameof(value));
            _speed = value;
        }
    }

    public void Reset(bool play)
    {
        _accumulator = 0; _frame = 0; _seekFrame = null; Playing = play;
    }

    public void SetPlaying(bool play)
    {
        _seekFrame = null; _accumulator = 0; Playing = play;
    }

    public void BeginSeek(double seconds)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        Reset(false);
        int frame = (int)Math.Round(Math.Clamp(seconds, 0, MaximumSeekSeconds) / StepSeconds);
        _seekFrame = frame > 0 ? frame : null;
    }

    /// <param name="continueWork">Optional wall-time budget predicate, checked between steps.</param>
    public int PumpSeek(Action<float> step, Func<bool>? continueWork = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        int completed = 0;
        while (_seekFrame is int target && _frame < target && completed < MaximumStepsPerPump
            && (completed == 0 || continueWork is null || continueWork()))
        {
            step((float)StepSeconds); _frame++; completed++;
        }
        if (_seekFrame.HasValue && _frame >= _seekFrame.Value) _seekFrame = null;
        return completed;
    }

    public int Advance(double elapsedSeconds, Action<float> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (!Playing || Seeking || !double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return 0;
        _accumulator = Math.Min(_accumulator + Math.Min(elapsedSeconds, .1) * Speed,
            MaximumStepsPerPump * StepSeconds);
        int completed = 0;
        while (_accumulator + 1e-10 >= StepSeconds && completed < MaximumStepsPerPump)
        {
            step((float)StepSeconds); _accumulator -= StepSeconds; _frame++; completed++;
        }
        return completed;
    }

    public void StepOne(Action<float> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        SetPlaying(false);
        step((float)StepSeconds); _frame++;
    }

    /// <summary>Stable per-emitter salt: reordering a stack does not change an emitter's noise.</summary>
    public static int SeedForEmitter(int seed, string id)
    {
        uint hash = unchecked((uint)seed) ^ 2166136261u;
        foreach (char value in id ?? string.Empty) hash = unchecked((hash ^ value) * 16777619u);
        return unchecked((int)hash);
    }
}
