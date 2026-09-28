using System.Numerics;
using Genesis.Runtime.Particles;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ParticleEditorControl
{
    private readonly record struct PreviewBirth(Vector3 Position, Vector3 Velocity, int Count, float Inheritance);
    private readonly Dictionary<string, List<PreviewBirth>> _pendingPreviewBirths =
        new(StringComparer.OrdinalIgnoreCase);
    private Action<ParticleSimulation.ParticleEvent>? _primaryPreviewEventSink;
    private Random _previewEventRandom = new(1337);
    private int _droppedPreviewBirths;

    private bool IsLinkedPreviewTarget(int index) => _effect.EventLinks?.Any(link =>
        link.Trigger != ParticleEventTrigger.None
        && string.Equals(link.TargetEmitterId, _previewEmitterIds[index], StringComparison.OrdinalIgnoreCase)) == true;

    private void BindSoftwarePreviewEvents(ParticleSimulation simulation, string emitterId)
    {
        Action<ParticleSimulation.ParticleEvent> sink = occurrence => QueuePreviewBirths(emitterId, occurrence);
        simulation.Occurred += sink;
        if (ReferenceEquals(simulation, _simulation)) _primaryPreviewEventSink = sink;
    }

    private void QueuePreviewBirths(string sourceId, ParticleSimulation.ParticleEvent occurrence)
    {
        if (_effect.EventLinks is not { Count: > 0 }) return;
        foreach (ParticleEventLink link in _effect.EventLinks)
        {
            double probability = Math.Clamp(link.Probability, 0, 1);
            if (!string.Equals(link.SourceEmitterId, sourceId, StringComparison.OrdinalIgnoreCase)
                || (link.Trigger & occurrence.Trigger) == 0 || probability <= 0
                || _previewEventRandom.NextDouble() >= probability) continue;
            if (!_pendingPreviewBirths.TryGetValue(link.TargetEmitterId, out List<PreviewBirth>? pending))
                _pendingPreviewBirths[link.TargetEmitterId] = pending = [];
            int count = Math.Clamp(link.Count, 1, 32);
            if (pending.Count >= 1024)
            {
                _droppedPreviewBirths += count;
                continue;
            }
            pending.Add(new PreviewBirth(occurrence.Position, occurrence.Velocity, count,
                (float)Math.Clamp(link.InheritVelocity, 0, 4)));
        }
    }

    private void DrainPreviewBirths(int index)
    {
        if (!_pendingPreviewBirths.TryGetValue(_previewEmitterIds[index], out List<PreviewBirth>? pending)
            || pending.Count == 0) return;
        PreviewBirth[] births = pending.ToArray();
        pending.Clear();
        ParticleSimulation target = _previewSimulations[index];
        foreach (PreviewBirth birth in births)
            _droppedPreviewBirths += birth.Count - target.BurstAt(
                birth.Count, birth.Position, birth.Velocity, birth.Inheritance);
    }
}
