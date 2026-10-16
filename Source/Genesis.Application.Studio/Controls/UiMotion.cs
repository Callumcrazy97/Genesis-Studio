using System.Diagnostics;
using System.Windows.Forms;

namespace Genesis.Application.Studio.Controls;

/// <summary>
/// Small eased animations for Studio's own controls: hover fades, card reveals, tree highlights.
/// </summary>
/// <remarks>
/// One UI-thread clock drives every running animation and stops when none is left, so an idle
/// Studio has no timer ticking. Animations change only how a control paints (a colour, an alpha,
/// a highlight), never its bounds, so layout and the layout tests see the same positions with or
/// without motion. With Windows' animation effects turned off, or <c>GENESIS_REDUCED_MOTION=1</c>
/// (set by the headless tests so captures are never taken mid-fade), every animation jumps to its
/// end at once.
/// </remarks>
internal static class UiMotion
{
    private sealed class Tween
    {
        public required Control Owner;
        public required string Key;
        public required long Started;
        public required double Milliseconds;
        public required Action<float> Step;
        public required Func<float, float> Ease;
        public Action? Done;
        public double Delay;
    }

    private static readonly List<Tween> Running = [];
    private static System.Windows.Forms.Timer? _clock;

    public static bool Enabled
    {
        get
        {
            if (Environment.GetEnvironmentVariable("GENESIS_REDUCED_MOTION") == "1") return false;
            try { return SystemInformation.UIEffectsEnabled; }
            catch (InvalidOperationException) { return true; }
        }
    }

    /// <summary>
    /// Runs <paramref name="step"/> with eased progress from 0 to 1. A new animation with the same
    /// owner and key replaces the running one, so a hover in and out never stacks.
    /// </summary>
    public static void Animate(Control owner, string key, double milliseconds, Action<float> step,
        Action? done = null, Func<float, float>? ease = null, double delay = 0)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Cancel(owner, key);
        if (!Enabled || owner.IsDisposed || milliseconds <= 0)
        {
            step(1f);
            done?.Invoke();
            return;
        }
        Running.Add(new Tween
        {
            Owner = owner,
            Key = key,
            Started = Stopwatch.GetTimestamp(),
            Milliseconds = milliseconds,
            Step = step,
            Ease = ease ?? EaseOutCubic,
            Done = done,
            Delay = delay,
        });
        step(0f);
        _clock ??= CreateClock();
        if (!_clock.Enabled) _clock.Start();
    }

    public static void Cancel(Control owner, string key) =>
        Running.RemoveAll(tween => ReferenceEquals(tween.Owner, owner) && tween.Key == key);

    public static float EaseOutCubic(float t) => 1f - MathF.Pow(1f - t, 3f);

    public static float EaseInOut(float t) => t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f;

    /// <summary>Overshoots a little and settles, for things that pop in.</summary>
    public static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f, c3 = c1 + 1f;
        return 1f + c3 * MathF.Pow(t - 1f, 3f) + c1 * MathF.Pow(t - 1f, 2f);
    }

    public static float Lerp(float from, float to, float t) => from + (to - from) * t;

    public static System.Drawing.Color Mix(System.Drawing.Color from, System.Drawing.Color to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return System.Drawing.Color.FromArgb(
            (int)MathF.Round(Lerp(from.A, to.A, t)),
            (int)MathF.Round(Lerp(from.R, to.R, t)),
            (int)MathF.Round(Lerp(from.G, to.G, t)),
            (int)MathF.Round(Lerp(from.B, to.B, t)));
    }

    private static System.Windows.Forms.Timer CreateClock()
    {
        System.Windows.Forms.Timer clock = new() { Interval = 15 };
        clock.Tick += (_, _) => Advance();
        return clock;
    }

    private static void Advance()
    {
        long now = Stopwatch.GetTimestamp();
        foreach (Tween tween in Running.ToArray())
        {
            if (tween.Owner.IsDisposed)
            {
                Running.Remove(tween);
                continue;
            }
            double elapsed = Stopwatch.GetElapsedTime(tween.Started, now).TotalMilliseconds - tween.Delay;
            if (elapsed < 0) continue;
            float t = (float)Math.Clamp(elapsed / tween.Milliseconds, 0, 1);
            try
            {
                tween.Step(tween.Ease(t));
            }
            catch (ObjectDisposedException)
            {
                Running.Remove(tween);
                continue;
            }
            if (t >= 1f)
            {
                Running.Remove(tween);
                tween.Done?.Invoke();
            }
        }
        if (Running.Count == 0) _clock?.Stop();
    }
}
