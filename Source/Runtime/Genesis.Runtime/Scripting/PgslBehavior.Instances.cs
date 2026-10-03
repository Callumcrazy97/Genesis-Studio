using System;
using System.Collections.Generic;
using System.Globalization;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting
{
    // One instance reaching another: `with (target) { ... }` runs its block as each matching
    // instance, and the Instance* commands read and write another instance's variables.
    public sealed partial class PgslBehavior : IPgslInstance
    {
        private static readonly List<PgslBehavior> LiveInstances = new();
        private bool _activeForOther;

        internal PgslVm Vm => _vm;

        private void RegisterLive()
        {
            lock (LiveInstances)
                if (!LiveInstances.Contains(this)) LiveInstances.Add(this);
        }

        private void UnregisterLive()
        {
            lock (LiveInstances) LiveInstances.Remove(this);
        }

        /// <summary>
        /// Makes this instance the one the running code acts as: its position and other built-in
        /// variables are read from the world first, and commands that act on "this instance" act
        /// on it. When it already is the running instance nothing is re-read, so what the caller
        /// changed this event is kept.
        /// </summary>
        public void SetActiveContext()
        {
            _activeForOther = VMEngine.Bridge.GetContext() != _ctx;
            if (_activeForOther) SyncToContext();
            VMEngine.Bridge.SetContext(_ctx);
        }

        /// <summary>Writes what the block changed back to the world.</summary>
        public void EndActiveContext()
        {
            if (_activeForOther && IsAlive) SyncFromContext();
            _activeForOther = false;
        }

        /// <summary>
        /// The live instances a <c>with</c> target names: an instance id, <c>"all"</c>, or an
        /// Object's name. Only instances of the running game's world are matched.
        /// </summary>
        internal static List<object> FindTargets(string target)
        {
            var found = new List<object>();
            if (string.IsNullOrWhiteSpace(target)) return found;
            string name = target.Trim();
            var world = PgslCommands.ActiveGameContext?.World;
            PgslBehavior[] snapshot;
            lock (LiveInstances)
            {
                LiveInstances.RemoveAll(instance => !instance.IsAlive);
                snapshot = LiveInstances.ToArray();
            }

            bool byId = double.TryParse(name, NumberStyles.Float, CultureInfo.InvariantCulture, out double id);
            bool all = string.Equals(name, "all", StringComparison.OrdinalIgnoreCase);
            foreach (PgslBehavior instance in snapshot)
            {
                if (world != null && instance.World != world) continue;
                bool matches = byId
                    ? instance.Entity.Id == id
                    : all || PgslCommands.InstanceIsObject(instance.Entity.Id, name)
                        || Genesis.Runtime.Scene.ObjectNameMatcher.Matches(instance._scriptName ?? string.Empty, name);
                if (matches) found.Add(instance);
            }
            return found;
        }

        /// <summary>The live PGSL instance with this id in the running game, or null.</summary>
        internal static PgslBehavior FindById(double id)
        {
            List<object> found = double.IsFinite(id) && id >= 1
                ? FindTargets(id.ToString(CultureInfo.InvariantCulture))
                : new List<object>();
            return found.Count > 0 ? (PgslBehavior)found[0] : null;
        }
    }
}
