using Genesis.Application.Core.Editing.Particles;

namespace Genesis.Application.Headless.Suites;

/// <summary>Links the exact production clock into the framework-independent runner.</summary>
internal static class ParticleClockCoreCases
{
    public static void Run(Action<string, Action> check)
    {
        check("Particles.Clock.30HzAnd144HzProduceSame60Steps", () =>
        {
            foreach (int rate in new[] { 30, 60, 144 })
            {
                ParticlePreviewClock clock = new(); int calls = 0;
                for (int i = 0; i < rate; i++) clock.Advance(1d / rate, _ => calls++);
                Assert(calls == 60 && Near(clock.Time, 1), "Display update rate changed simulation time.");
            }
        });
        check("Particles.Clock.AccumulatesFractionalUpdates", () =>
        {
            ParticlePreviewClock clock = new(); int calls = 0;
            for (int i = 0; i < 10; i++) clock.Advance(1d / 600, _ => calls++);
            Assert(calls == 1, "Sub-frame input was discarded.");
        });
        check("Particles.Clock.SpeedDoesNotChangeStepSize", () =>
        {
            ParticlePreviewClock clock = new() { Speed = 2 }; int calls = 0;
            for (int i = 0; i < 60; i++) clock.Advance(1d / 60, step =>
            { Assert(Math.Abs(step - 1f / 60) < 1e-7, "Speed changed the integration step."); calls++; });
            Assert(calls == 120 && Near(clock.Time, 2), "2x preview did not advance two seconds.");
        });
        check("Particles.Clock.QuarterSpeed", () =>
        {
            ParticlePreviewClock clock = new() { Speed = .25 }; int calls = 0;
            for (int i = 0; i < 60; i++) clock.Advance(1d / 60, _ => calls++);
            Assert(calls == 15, "Quarter-speed lost fractional steps.");
        });
        check("Particles.Clock.PauseDoesNotCatchUp", () =>
        {
            ParticlePreviewClock clock = new(); clock.SetPlaying(false); int calls = 0;
            clock.Advance(50, _ => calls++); clock.SetPlaying(true); clock.Advance(1d / 60, _ => calls++);
            Assert(calls == 1 && Near(clock.Time, 1d / 60), "Resume replayed elapsed paused time.");
        });
        check("Particles.Clock.StallWorkIsBounded", () =>
        {
            ParticlePreviewClock clock = new() { Speed = 4 };
            Assert(clock.Advance(500, _ => { }) == 8, "A stalled render caused unbounded catch-up.");
            Assert(clock.Advance(1d / 60, _ => { }) == 4, "Old accumulated lag survived the catch-up bound.");
        });
        check("Particles.Clock.NonFiniteElapsedIsIgnored", () =>
        {
            ParticlePreviewClock clock = new(); int calls = 0;
            foreach (double delta in new[] { double.NaN, double.PositiveInfinity, -1, 0 }) clock.Advance(delta, _ => calls++);
            Assert(calls == 0 && clock.Time == 0, "Invalid deltas entered the integration loop.");
        });
        check("Particles.Clock.InvalidSpeedRejected", () =>
        {
            ParticlePreviewClock clock = new();
            foreach (double speed in new[] { double.NaN, double.PositiveInfinity, -1, 0, 5 })
                Throws<ArgumentOutOfRangeException>(() => clock.Speed = speed);
            Assert(clock.Speed == 1, "Rejected speed still mutated the clock.");
        });
        check("Particles.Clock.ResetRetainsSpeedNotTime", () =>
        {
            ParticlePreviewClock clock = new() { Speed = 2 }; clock.Advance(.05, _ => { }); clock.Reset(false);
            Assert(clock.Time == 0 && !clock.Playing && !clock.Seeking && clock.Speed == 2, "Reset changed preview preferences or retained work.");
        });
        check("Particles.Clock.StepIsExactlyOnePausedFrame", () =>
        {
            ParticlePreviewClock clock = new(); int calls = 0; clock.StepOne(_ => calls++);
            Assert(calls == 1 && !clock.Playing && Near(clock.Time, 1d / 60), "Single-step used speed or remained playing.");
        });
        check("Particles.Clock.SeekStartsWithoutBlockingWork", () =>
        {
            ParticlePreviewClock clock = new(); clock.BeginSeek(10);
            Assert(clock.Seeking && !clock.Playing && clock.Time == 0 && clock.SeekTarget == 10, "Seeking stepped synchronously.");
        });
        check("Particles.Clock.SeekSliceIsAtMostEightSteps", () =>
        {
            ParticlePreviewClock clock = new(); clock.BeginSeek(10); int calls = 0;
            int completed = clock.PumpSeek(_ => calls++);
            Assert(completed == 8 && calls == 8 && clock.Seeking, "Seek consumed a whole clip in one UI slice.");
        });
        check("Particles.Clock.WallBudgetMakesProgressThenYields", () =>
        {
            ParticlePreviewClock clock = new(); clock.BeginSeek(5);
            Assert(clock.PumpSeek(_ => { }, () => false) == 1 && clock.Seeking, "Budget either made no progress or failed to yield.");
        });
        check("Particles.Clock.SeekEndsAtExactTarget", () =>
        {
            ParticlePreviewClock clock = new(); clock.BeginSeek(.5); int calls = 0;
            for (int i = 0; i < 10 && clock.Seeking; i++) clock.PumpSeek(_ => calls++);
            Assert(!clock.Seeking && !clock.Playing && calls == 30 && Near(clock.Time, .5), "Replay overshot or resumed after seek.");
        });
        check("Particles.Clock.LatestSeekReplacesOldTarget", () =>
        {
            ParticlePreviewClock clock = new(); clock.BeginSeek(60); clock.PumpSeek(_ => { }); clock.BeginSeek(.25);
            int calls = 0; while (clock.Seeking) clock.PumpSeek(_ => calls++);
            Assert(calls == 15 && Near(clock.Time, .25), "Old seek work leaked into the newer target.");
        });
        check("Particles.Clock.SeekIsCappedAtSixtySeconds", () =>
        {
            ParticlePreviewClock clock = new(); clock.BeginSeek(3600);
            Assert(clock.SeekTarget == 60, "An authored long effect can queue minutes of replay.");
            clock.BeginSeek(-100); Assert(!clock.Seeking && clock.Time == 0, "Negative seek did not resolve to the start.");
        });
        check("Particles.Clock.SeekRejectsNonFiniteTarget", () =>
        {
            ParticlePreviewClock clock = new(); clock.BeginSeek(.5);
            Throws<ArgumentOutOfRangeException>(() => clock.BeginSeek(double.NaN));
            Assert(clock.Seeking && clock.SeekTarget == .5, "Rejected seek discarded valid existing work.");
        });
        check("Particles.Clock.PlayAndStepCancelPendingReplay", () =>
        {
            ParticlePreviewClock clock = new(); clock.BeginSeek(30); clock.SetPlaying(true);
            Assert(!clock.Seeking && clock.Playing, "Play did not cancel the old target.");
            clock.BeginSeek(30); clock.StepOne(_ => { });
            Assert(!clock.Seeking && !clock.Playing && Near(clock.Time, 1d / 60), "Step did not cancel the old target.");
        });
        check("Particles.Clock.RenderCannotRaceSeekIntegration", () =>
        {
            ParticlePreviewClock clock = new(); clock.BeginSeek(1); int calls = 0;
            clock.Advance(.1, _ => calls++);
            Assert(calls == 0 && clock.Time == 0, "Render transport advanced a pending seek.");
        });
        check("Particles.Clock.EmitterSeedsFollowIdentityNotOrder", () =>
        {
            int first = ParticlePreviewClock.SeedForEmitter(1337, "flame");
            Assert(first == ParticlePreviewClock.SeedForEmitter(1337, "flame"), "Seed depends on process-random string hashing.");
            Assert(first != ParticlePreviewClock.SeedForEmitter(1338, "flame")
                && first != ParticlePreviewClock.SeedForEmitter(1337, "smoke"), "Seed ignored resource-local identity or seed preference.");
        });
    }
    private static bool Near(double first, double second) => Math.Abs(first - second) < 1e-8;
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
