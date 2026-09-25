using System;
using Genesis.Runtime;
using Genesis.Runtime.Core;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Project
{
    /// <summary>Ticks <see cref="ScriptHostSystem"/> inside the scene update loop.</summary>
    public sealed class ScriptHostSubsystem : ISceneSubsystem
    {
        private readonly ScriptHostSystem _host;
        private readonly Func<bool> _allowUpdate;

        public ScriptHostSubsystem(ScriptHostSystem host, Func<bool> allowUpdate = null)
        {
            _host = host;
            _allowUpdate = allowUpdate;
        }

        public void FixedUpdate(RuntimeScene scene, float fixedDelta) { }

        public void Update(RuntimeScene scene, GameTime time)
        {
            if (_allowUpdate != null && !_allowUpdate()) return;
            _host?.Update(time.Delta);
            scene.World?.FlushDeferred();
        }

        public void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, IRenderController renderer) { }

        public void Dispose() => _host?.Shutdown();
    }
}
