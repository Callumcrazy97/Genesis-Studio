using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Genesis.Shared.Commands
{
    // ════════════════════════════════════════════════════════════════════════════
    //   EngineCommandPipeline
    //   The single instrumentation chokepoint for every Engine.* command.
    //   All gameplay funnels through here so profiling, debugging, throttling
    //   and auditing can be added in one place (Architecture Principle #1).
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One frame's worth of aggregate per-command statistics, fed to the in-game
    /// profiler (Phase 2.3) and the editor's perf HUD. Cheap to read; the pipeline
    /// accumulates into it with Interlocked operations on the hot path.
    /// </summary>
    public sealed class EngineCommandStat
    {
        public string Command { get; }
        public string Category { get; }
        public long CallCount;
        public double TotalMicroseconds;   // cumulative CPU time across all calls
        public long MaxMicroseconds;
        public DateTime LastCalled;

        internal EngineCommandStat(string command, string category)
        {
            Command = command;
            Category = category;
        }

        public double AverageMicroseconds => CallCount == 0 ? 0 : TotalMicroseconds / CallCount;

        internal void Record(long microseconds)
        {
            System.Threading.Interlocked.Increment(ref CallCount);
            // Accumulate doubles with a compare-exchange loop (good enough for stats).
            double newTotal;
            double snapshot;
            do
            {
                snapshot = TotalMicroseconds;
                newTotal = snapshot + microseconds;
            } while (System.Threading.Interlocked.CompareExchange(ref TotalMicroseconds, newTotal, snapshot) != snapshot);

            long prevMax, candidate;
            do
            {
                prevMax = MaxMicroseconds;
                candidate = Math.Max(prevMax, microseconds);
            } while (System.Threading.Interlocked.CompareExchange(ref MaxMicroseconds, candidate, prevMax) != prevMax);

            LastCalled = DateTime.UtcNow;
        }
    }

    /// <summary>Context handed to <see cref="EngineCommandPipeline.OnBeforeInvoke"/> / OnAfterInvoke.</summary>
    public readonly struct CommandInvocation
    {
        public readonly string Command;        // bare name, e.g. "DrawSprite"
        public readonly string Category;       // e.g. "Drawing 2D"
        public readonly object[] Args;         // arguments about to be / just passed
        public readonly object Result;         // set in OnAfter only
        public readonly double ElapsedMicroseconds; // set in OnAfter only

        public CommandInvocation(string command, string category, object[] args, object result, double elapsedUs)
        {
            Command = command; Category = category; Args = args; Result = result; ElapsedMicroseconds = elapsedUs;
        }
    }

    /// <summary>
    /// Centralised dispatch + instrumentation for every <c>[EngineCommand]</c> on
    /// <see cref="Engine"/>. Built once at startup by reflecting over the host
    /// type; each command is bound to a compiled <see cref="Func{Object[], Object}"/>
    /// delegate (mirrors <c>PGSLEngineBridge.BuildNativeCallTable</c>) so runtime
    /// invocation avoids reflection cost. Every call is wrapped with
    /// OnBefore/OnAfter hooks and accumulates into <see cref="EngineCommandStat"/>
    /// — this is where the in-game profiler (Phase 2.3) and the throttling /
    /// auditing layer hook in.
    /// </summary>
    public static class EngineCommandPipeline
    {
        private sealed class Bound
        {
            public string Name;
            public string Category;
            public Func<object[], object> Invoke;
            public bool IsVoid;
            public EngineCommandStat Stat;
        }

        private static readonly object _buildLock = new object();
        private static Dictionary<string, Bound> _byName;        // case-insensitive
        private static List<EngineCommandStat> _stats;
        private static volatile bool _built;

        /// <summary>
        /// Raised just before a command runs. Set <c>e.Cancel</c> to short-circuit
        /// (returns null/zero). Use for throttling or command gating.
        /// </summary>
        public static event EventHandler<CommandInvokingEventArgs> OnBeforeInvoke;

        /// <summary>Raised after a command runs, with elapsed time and result. Use for logging/auditing.</summary>
        public static event Action<CommandInvocation> OnAfterInvoke;

        public sealed class CommandInvokingEventArgs : EventArgs
        {
            public string Command { get; set; }
            public object[] Args { get; set; }
            public bool Cancel { get; set; }
        }

        /// <summary>True after <see cref="Build"/> has run.</summary>
        public static bool IsBuilt => _built;

        /// <summary>Per-command cumulative statistics (empty until <see cref="Build"/>).</summary>
        public static IReadOnlyList<EngineCommandStat> Stats
        {
            get { EnsureBuilt(); return _stats; }
        }

        /// <summary>
        /// Reflect over the <see cref="Engine"/> host (and any additional host types)
        /// and build the compiled-delegate dispatch table. Safe to call repeatedly;
        /// rebuilds only if not yet built. Pass additional hosts to merge commands
        /// from other static facades (used by the PGSL bridge in Phase 1).
        /// </summary>
        public static void Build(params Type[] additionalHosts)
        {
            if (_built) return;
            lock (_buildLock)
            {
                if (_built) return;
                var byName = new Dictionary<string, Bound>(StringComparer.OrdinalIgnoreCase);
                var stats = new List<EngineCommandStat>();

                var hosts = new List<Type> { typeof(Engine) };
                if (additionalHosts != null) hosts.AddRange(additionalHosts);

                foreach (Type host in hosts)
                {
                    BuildHost(host, byName, stats);
                }

                _byName = byName;
                _stats = stats;
                _built = true;
            }
        }

        private static void BuildHost(Type host, Dictionary<string, Bound> byName, List<EngineCommandStat> stats)
        {
            foreach (MethodInfo m in host.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var attr = m.GetCustomAttribute<EngineCommandAttribute>();
                if (attr == null) continue;

                string name = ExtractName(attr.Signature, m.Name);
                if (byName.ContainsKey(name)) continue; // first host wins (Engine canonical)

                var stat = new EngineCommandStat(name, attr.Category);
                stats.Add(stat);

                var bound = new Bound
                {
                    Name = name,
                    Category = attr.Category,
                    Invoke = CompileDelegate(m),
                    IsVoid = m.ReturnType == typeof(void),
                    Stat = stat,
                };
                byName[name] = bound;
            }
        }

        /// <summary>Extract the bare command name from the signature, falling back to the C# method name.</summary>
        private static string ExtractName(string signature, string methodName)
        {
            if (string.IsNullOrEmpty(signature)) return methodName;
            int dot = signature.IndexOf('.');
            int paren = signature.IndexOf('(');
            if (dot >= 0 && paren > dot)
                return signature.Substring(dot + 1, paren - dot - 1).Trim();
            if (paren > 0)
                return signature.Substring(0, paren).Trim();
            return signature.Trim();
        }

        /// <summary>Compile a static method into a boxed-args → boxed-result delegate (no per-call reflection).</summary>
        private static Func<object[], object> CompileDelegate(MethodInfo method)
        {
            var argsParam = Expression.Parameter(typeof(object[]), "args");
            var paramExprs = new List<Expression>();
            ParameterInfo[] ps = method.GetParameters();

            for (int i = 0; i < ps.Length; i++)
            {
                var index = Expression.ArrayIndex(argsParam, Expression.Constant(i));
                paramExprs.Add(Expression.Convert(index, ps[i].ParameterType));
            }

            Expression call = Expression.Call(null, method, paramExprs);
            if (method.ReturnType == typeof(void))
            {
                var block = Expression.Block(call, Expression.Constant(null, typeof(object)));
                return Expression.Lambda<Func<object[], object>>(block, argsParam).Compile();
            }
            return Expression.Lambda<Func<object[], object>>(
                Expression.Convert(call, typeof(object)), argsParam).Compile();
        }

        private static void EnsureBuilt()
        {
            if (!_built) Build();
        }

        // ── Dispatch ──────────────────────────────────────────────────────────────

        /// <summary>True if a command with this name is registered.</summary>
        public static bool Contains(string name)
        {
            EnsureBuilt();
            return name != null && _byName.ContainsKey(name);
        }

        /// <summary>
        /// Invoke a command by bare name with boxed args, funnelling through the
        /// OnBefore/OnAfter hooks and the stats accumulator. This is the
        /// replacement for the DevTerminal's per-call <c>MethodInfo.Invoke</c>
        /// reflection path.
        /// </summary>
        public static object Invoke(string name, params object[] args)
        {
            EnsureBuilt();
            if (string.IsNullOrEmpty(name) || !_byName.TryGetValue(name, out Bound b))
                throw new InvalidOperationException($"Unknown Engine command: {name}");

            // OnBefore hook (throttle / gate).
            if (OnBeforeInvoke != null)
            {
                var ea = new CommandInvokingEventArgs { Command = name, Args = args };
                OnBeforeInvoke?.Invoke(null, ea);
                if (ea.Cancel) return null;
            }

            var sw = Stopwatch.IsHighResolution ? Stopwatch.StartNew() : null;
            object result = b.Invoke(args);
            long elapsedUs = 0;
            if (sw != null)
            {
                sw.Stop();
                elapsedUs = (long)(sw.Elapsed.TotalMilliseconds * 1000.0);
            }
            b.Stat.Record(elapsedUs);

            OnAfterInvoke?.Invoke(new CommandInvocation(name, b.Category, args, result, elapsedUs));
            return result;
        }

        /// <summary>Reset all accumulated statistics (e.g. between profiler frames).</summary>
        public static void ResetStats()
        {
            EnsureBuilt();
            lock (_buildLock)
            {
                foreach (var s in _stats)
                {
                    s.CallCount = 0;
                    s.TotalMicroseconds = 0;
                    s.MaxMicroseconds = 0;
                }
            }
        }

        /// <summary>Enumerate the registered command names (for autocomplete / catalog UI).</summary>
        public static IEnumerable<string> RegisteredNames()
        {
            EnsureBuilt();
            return _byName.Keys.OrderBy(n => n).ToList();
        }
    }
}
