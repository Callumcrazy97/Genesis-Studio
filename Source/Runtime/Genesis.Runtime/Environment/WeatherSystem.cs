using System;
using System.Numerics;
using Genesis.Shared.Simulation;
namespace Genesis.Runtime.Climate;

public enum WeatherEpisode : byte { Clear, Overcast, Rain, WindStorm, ColdSnap, Heatwave }
public readonly record struct EpisodeParameters(float Rain, float Wind, float TemperatureOffsetC, float CloudCover, float WindAngle);
public sealed record WeatherSystemState(int Version, uint RandomState, WeatherEpisode Current, WeatherEpisode Target,
    double RemainingSeconds, double TransitionElapsed, double TransitionDuration, EpisodeParameters From, EpisodeParameters To,
    double MinimumEpisodeSeconds, double MaximumEpisodeSeconds, double DefaultTransitionSeconds);

/// <summary>
/// Deterministic, frame-rate-independent episode scheduling. It consumes all elapsed simulation time,
/// including multiple episode boundaries, and saves the PRNG plus the exact transition endpoints.
/// Namespace follows Genesis's existing Climate service; adding a Runtime.Environment namespace would
/// shadow System.Environment in existing runtime source.
/// </summary>
public sealed class WeatherSystem
{
    private readonly DeterministicRandom _random;
    private double _remaining, _transitionElapsed, _transitionDuration;
    private EpisodeParameters _from, _to;
    public WeatherEpisode Current { get; private set; } = WeatherEpisode.Clear;
    public WeatherEpisode Target { get; private set; } = WeatherEpisode.Clear;
    public float TransitionProgress => _transitionDuration <= 0 ? 1 : (float)Math.Clamp(_transitionElapsed / _transitionDuration, 0, 1);
    public EpisodeParameters Parameters { get; private set; }
    public float RainIntensity => Parameters.Rain;
    public float WindSpeed => Parameters.Wind;
    public float TemperatureOffsetC => Parameters.TemperatureOffsetC;
    public Vector3 WindDirection => new(MathF.Cos(Parameters.WindAngle), 0, MathF.Sin(Parameters.WindAngle));
    public double EpisodeDurationRemaining => _remaining;
    public double MinimumEpisodeSeconds { get; set; } = 300;
    public double MaximumEpisodeSeconds { get; set; } = 900;
    public double DefaultTransitionSeconds { get; set; } = 20;
    public WeatherSystem(int seed)
    {
        _random = new(seed ^ 0x47454E); _from = _to = Profile(WeatherEpisode.Clear, 0);
        Parameters = _from; _remaining = 300 + _random.NextDouble() * 600;
    }
    public void ForceEpisode(WeatherEpisode episode, double durationSeconds = 600, double transitionSeconds = 20)
    {
        ValidateEpisode(episode);
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0 || !double.IsFinite(transitionSeconds) || transitionSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        _from = Parameters; _to = Profile(episode, (float)(_random.NextDouble() * Math.Tau));
        Target = episode; _remaining = durationSeconds; _transitionElapsed = 0;
        _transitionDuration = Math.Min(durationSeconds, transitionSeconds); Evaluate();
    }
    public void Update(float dt) => Advance(dt);
    public void Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > 31_536_000)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Advance in finite increments up to one simulated year.");
        ValidateConfiguration();
        while (seconds > 0)
        {
            double step = Math.Min(seconds, _remaining);
            _transitionElapsed = Math.Min(_transitionDuration, _transitionElapsed + step);
            _remaining -= step; seconds -= step; Evaluate();
            if (_remaining <= 1e-9)
            {
                var next = SelectNextEpisode(Target);
                double duration = MinimumEpisodeSeconds + _random.NextDouble() * (MaximumEpisodeSeconds - MinimumEpisodeSeconds);
                ForceEpisode(next, duration, DefaultTransitionSeconds);
            }
        }
    }
    private void Evaluate()
    {
        float t = TransitionProgress; t = t * t * (3 - 2 * t);
        float angle = MathF.IEEERemainder(_to.WindAngle - _from.WindAngle, MathF.Tau);
        Parameters = new(Lerp(_from.Rain,_to.Rain,t), Lerp(_from.Wind,_to.Wind,t),
            Lerp(_from.TemperatureOffsetC,_to.TemperatureOffsetC,t), Lerp(_from.CloudCover,_to.CloudCover,t), _from.WindAngle + angle * t);
        if (TransitionProgress >= 1) Current = Target;
    }
    private WeatherEpisode SelectNextEpisode(WeatherEpisode previous)
    {
        int roll = _random.Next(0,100);
        // All six episodes are reachable; the original sketch never selected cold snaps or heatwaves.
        return previous switch
        {
            WeatherEpisode.Clear => roll < 50 ? WeatherEpisode.Overcast : roll < 72 ? WeatherEpisode.WindStorm : roll < 86 ? WeatherEpisode.ColdSnap : WeatherEpisode.Heatwave,
            WeatherEpisode.Overcast => roll < 55 ? WeatherEpisode.Rain : roll < 80 ? WeatherEpisode.Clear : WeatherEpisode.ColdSnap,
            WeatherEpisode.Rain => roll < 80 ? WeatherEpisode.Overcast : WeatherEpisode.WindStorm,
            _ => roll < 70 ? WeatherEpisode.Clear : WeatherEpisode.Overcast,
        };
    }
    private static EpisodeParameters Profile(WeatherEpisode episode, float angle) => episode switch
    {
        WeatherEpisode.Clear => new(0,.05f,0,.08f,angle),
        WeatherEpisode.Overcast => new(0,.25f,-2,.9f,angle),
        WeatherEpisode.Rain => new(1,.5f,-4,.98f,angle),
        WeatherEpisode.WindStorm => new(.1f,.9f,-2.5f,.7f,angle),
        WeatherEpisode.ColdSnap => new(0,.3f,-15,.45f,angle),
        WeatherEpisode.Heatwave => new(0,.02f,12,.04f,angle),
        _ => throw new ArgumentOutOfRangeException(nameof(episode)),
    };
    public WeatherState ToClimateState(float baseTemperatureC = 18)
    {
        if (!float.IsFinite(baseTemperatureC)) throw new ArgumentOutOfRangeException(nameof(baseTemperatureC));
        var kind = Target switch { WeatherEpisode.Rain => WeatherKind.Rain, WeatherEpisode.Overcast => WeatherKind.Overcast,
            WeatherEpisode.WindStorm => WeatherKind.Wind, WeatherEpisode.ColdSnap => WeatherKind.ColdSnap,
            WeatherEpisode.Heatwave => WeatherKind.Heatwave, _ => WeatherKind.Clear };
        return new(kind, Parameters.CloudCover, Parameters.Rain, 0, 0, WindDirection, Parameters.Wind * 12,
            baseTemperatureC + Parameters.TemperatureOffsetC, .003f + Parameters.CloudCover * .025f, 0);
    }
    public WeatherSystemState CaptureState() => new(1, _random.State, Current, Target, _remaining, _transitionElapsed,
        _transitionDuration, _from, _to, MinimumEpisodeSeconds, MaximumEpisodeSeconds, DefaultTransitionSeconds);
    public void RestoreState(WeatherSystemState state)
    {
        ArgumentNullException.ThrowIfNull(state); ValidateEpisode(state.Current); ValidateEpisode(state.Target);
        if (state.Version != 1 || !double.IsFinite(state.RemainingSeconds) || state.RemainingSeconds <= 0 ||
            !double.IsFinite(state.TransitionElapsed) || state.TransitionElapsed < 0 ||
            !double.IsFinite(state.TransitionDuration) || state.TransitionDuration < state.TransitionElapsed ||
            !Valid(state.From) || !Valid(state.To) || !ValidConfiguration(state.MinimumEpisodeSeconds,state.MaximumEpisodeSeconds,state.DefaultTransitionSeconds))
            throw new ArgumentException("Invalid weather save state.", nameof(state));
        _random.State=state.RandomState; Current=state.Current; Target=state.Target; _remaining=state.RemainingSeconds;
        _transitionElapsed=state.TransitionElapsed; _transitionDuration=state.TransitionDuration; _from=state.From; _to=state.To;
        MinimumEpisodeSeconds=state.MinimumEpisodeSeconds; MaximumEpisodeSeconds=state.MaximumEpisodeSeconds; DefaultTransitionSeconds=state.DefaultTransitionSeconds;
        Evaluate();
    }
    private static bool Valid(EpisodeParameters p) => float.IsFinite(p.Rain) && p.Rain >= 0 && p.Rain <= 1 &&
        float.IsFinite(p.Wind) && p.Wind >= 0 && p.Wind <= 1 && float.IsFinite(p.TemperatureOffsetC) && MathF.Abs(p.TemperatureOffsetC)<=200 &&
        float.IsFinite(p.CloudCover) && p.CloudCover>=0 && p.CloudCover<=1 && float.IsFinite(p.WindAngle);
    private static void ValidateEpisode(WeatherEpisode episode) { if ((byte)episode > (byte)WeatherEpisode.Heatwave) throw new ArgumentOutOfRangeException(nameof(episode)); }
    private static bool ValidConfiguration(double min,double max,double blend) => double.IsFinite(min) && min >= 1 &&
        double.IsFinite(max) && max >= min && max <= 31_536_000 && double.IsFinite(blend) && blend >= 0;
    private void ValidateConfiguration() { if(!ValidConfiguration(MinimumEpisodeSeconds,MaximumEpisodeSeconds,DefaultTransitionSeconds)) throw new InvalidOperationException("Invalid weather timing configuration."); }
    private static float Lerp(float a,float b,float t) => a+(b-a)*t;
}
