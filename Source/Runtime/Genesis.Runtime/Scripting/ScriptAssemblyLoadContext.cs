using System;
using System.Reflection;
using System.Runtime.Loader;

namespace Genesis.Runtime.Scripting
{
    // ════════════════════════════════════════════════════════════════════════════
    //   ScriptAssemblyLoadContext
    //   A collectible AssemblyLoadContext that owns a single compiled GameScripts
    //   assembly. Because it is collectible and isAssemblyCollectable=true, the
    //   whole context (assembly + JITted code + behaviour Type objects) can be
    //   unloaded when scripts are recompiled — enabling true hot-reload without
    //   restarting the editor or the player, and without the GameScripts.dll
    //   file-lock (B5). Scripts resolve their engine dependencies (Genesis.*)
    //   through the default ALC via the resolving fallback below.
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Collectible load context for a single compilation of project scripts.
    /// Create one per compile, load the assembly, hand it to
    /// <see cref="ScriptHostSystem.LoadAssembly(Assembly)"/>, and when recompiling
    /// call <see cref="Unload"/> on the previous context.
    /// </summary>
    public sealed class ScriptAssemblyLoadContext : AssemblyLoadContext, IDisposable
    {
        private readonly AssemblyLoadContext _main = AssemblyLoadContext.GetLoadContext(typeof(EntityBehavior).Assembly)
                                                     ?? AssemblyLoadContext.Default;

        /// <summary>Creates a collectible, unresolvable-by-name context.</summary>
        public ScriptAssemblyLoadContext() : base(isCollectible: true) { }

        /// <summary>
        /// Load the compiled script assembly from an in-memory PE image. The bytes
        /// are never written to a locked file — fixing the B5 file-lock — and the
        /// returned assembly is owned by this collectible context.
        /// </summary>
        public Assembly LoadFromBytes(byte[] assemblyBytes, byte[] symbolBytes = null)
            => symbolBytes == null ? LoadFromStream(new System.IO.MemoryStream(assemblyBytes))
                                   : LoadFromStream(new System.IO.MemoryStream(assemblyBytes),
                                                     new System.IO.MemoryStream(symbolBytes));

        /// <summary>
        /// Resolution fallback: a project script references Genesis.* engine types
        /// that live in the default context. Defer to the main context so the
        /// script's <c>using Genesis.Runtime.Scripting;</c> binds to the *same*
        /// <see cref="EntityBehavior"/> the host uses (critical — otherwise
        /// <c>is</c>/<c>as</c> casts across contexts fail).
        /// </summary>
        protected override Assembly Load(AssemblyName assemblyName)
        {
            // Never pull another script assembly in here; scripts are one-per-context.
            if (assemblyName.Name == "GameScripts") return null;

            // Engine + BCL assemblies resolve from the main context.
            try
            {
                Assembly found = _main.LoadFromAssemblyName(assemblyName);
                if (found != null) return found;
            }
            catch { /* fall through */ }
            return null;
        }

        public void Dispose()
        {
            try { Unload(); } catch { /* best effort */ }
        }
    }
}
