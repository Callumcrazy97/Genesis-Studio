using System.Collections.Generic;
using Genesis.Shared.ECS;

namespace Genesis.Runtime.ECS
{
    // Drives fixed and variable loops:
    //   RunFixedPhase — pre-physics (90–99) and post-physics (100–109) IFixedEcsSystem passes
    //   RunVariablePhase — Update systems → DeferredFlush → Render systems
    public sealed class SystemScheduler
    {
        private readonly List<IFixedEcsSystem> _prePhysicsSystems  = new List<IFixedEcsSystem>(4);
        private readonly List<IFixedEcsSystem> _postPhysicsSystems = new List<IFixedEcsSystem>(4);
        private readonly List<IEcsSystem>      _updateSystems      = new List<IEcsSystem>(8);
        private readonly List<IEcsSystem>      _renderSystems      = new List<IEcsSystem>(8);
        private readonly World _world;

        public SystemScheduler(World world) { _world = world; }

        public void AddSystem(IEcsSystem system)
        {
            var list = system.Priority >= SystemPhase.RenderThreshold ? _renderSystems : _updateSystems;
            list.Add(system);
            list.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        public void AddFixedSystem(IFixedEcsSystem system)
        {
            var list = system.FixedPriority <= SystemPhase.PrePhysicsMax
                ? _prePhysicsSystems
                : _postPhysicsSystems;
            list.Add(system);
            list.Sort((a, b) => a.FixedPriority.CompareTo(b.FixedPriority));
        }

        public void RemoveSystem(object system)
        {
            if (system == null) return;
            _prePhysicsSystems.RemoveAll(item => ReferenceEquals(item, system));
            _postPhysicsSystems.RemoveAll(item => ReferenceEquals(item, system));
            _updateSystems.RemoveAll(item => ReferenceEquals(item, system));
            _renderSystems.RemoveAll(item => ReferenceEquals(item, system));
        }

        public void RunFixedPhase(float fixedDelta, FixedPhase phase)
        {
            var list = phase == FixedPhase.PrePhysics ? _prePhysicsSystems : _postPhysicsSystems;
            foreach (var s in list)
                s.FixedUpdate(_world, fixedDelta);
        }

        public void RunVariablePhase(float dt)
        {
            foreach (var s in _updateSystems)
                s.Update(_world, dt);

            _world.Deferred.Flush(_world);

            foreach (var s in _renderSystems)
                s.Update(_world, dt);
        }

        public void Step(float dt) => RunVariablePhase(dt);
    }
}
