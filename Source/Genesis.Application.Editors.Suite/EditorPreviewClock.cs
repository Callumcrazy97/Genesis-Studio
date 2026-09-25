namespace Genesis.Application.Editors.Suite;

/// <summary>
/// Shared 60 FPS preview clock for 3D editors. Play runs the scene; Pause freezes; Stop rewinds.
/// Room's F5 "run in Player" is a different action and does not use this clock.
/// </summary>
public sealed class EditorPreviewClock
{
    public const float FrameDelta = 1f / 60f;

    public bool Playing { get; private set; }

    public float Time { get; private set; }

    public event EventHandler? Changed;

    public void Play()
    {
        if (Playing)
        {
            return;
        }

        Playing = true;
        RaiseChanged();
    }

    public void Pause()
    {
        if (!Playing)
        {
            return;
        }

        Playing = false;
        RaiseChanged();
    }

    public void Stop()
    {
        bool changed = Playing || Time != 0f;
        Playing = false;
        Time = 0f;
        if (changed)
        {
            RaiseChanged();
        }
    }

    /// <summary>Advances time by one 60 FPS tick when playing. Returns whether a tick happened.</summary>
    public bool TryTickPlaying()
    {
        if (!Playing)
        {
            return false;
        }

        Time += FrameDelta;
        return true;
    }

    /// <summary>Advances time even when paused — used by headless StepPreview.</summary>
    public void Step(float dt, int steps = 1)
    {
        if (dt <= 0f)
        {
            return;
        }

        int count = Math.Max(1, steps);
        Time += dt * count;
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
