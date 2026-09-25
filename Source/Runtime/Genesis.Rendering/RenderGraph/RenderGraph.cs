using System;
using System.Collections.Generic;
using System.Linq;

namespace Genesis.Rendering.RenderGraph
{
    public readonly record struct RenderResourceHandle(int Id)
    {
        public bool IsValid => Id >= 0;
    }

    public enum RenderResourceUsage
    {
        Undefined,
        ColorAttachment,
        DepthAttachment,
        ShaderRead,
        ShaderWrite,
        TransferSource,
        TransferDestination,
        Present,
    }

    public enum RenderAccess
    {
        Read,
        Write,
        ReadWrite,
    }

    public readonly record struct RenderResourceUse(
        RenderResourceHandle Resource,
        RenderResourceUsage Usage,
        RenderAccess Access);

    public readonly record struct RenderBarrier(
        RenderResourceHandle Resource,
        string ResourceName,
        RenderResourceUsage Before,
        RenderResourceUsage After,
        int SourcePassIndex,
        int DestinationPassIndex,
        bool HasWriteHazard);

    public sealed record RenderPassPlan(
        int Index,
        string Name,
        IReadOnlyList<RenderResourceUse> Uses,
        IReadOnlyList<int> Dependencies);

    public sealed class CompiledRenderGraph
    {
        internal CompiledRenderGraph(
            IReadOnlyList<RenderPassPlan> passes,
            IReadOnlyList<RenderBarrier> barriers,
            IReadOnlyDictionary<RenderResourceHandle, string> resources)
        {
            Passes = passes;
            Barriers = barriers;
            Resources = resources;
        }

        public IReadOnlyList<RenderPassPlan> Passes { get; }
        public IReadOnlyList<RenderBarrier> Barriers { get; }
        public IReadOnlyDictionary<RenderResourceHandle, string> Resources { get; }
    }

    /// <summary>
    /// Backend-neutral render-graph planner ported from AetherForge. Passes declare resource access;
    /// the graph derives ordering and usage/write-hazard barriers for a backend executor to consume.
    /// </summary>
    public sealed class RenderGraphBuilder
    {
        private readonly List<ResourceDefinition> _resources = new();
        private readonly List<PassDefinition> _passes = new();

        public RenderResourceHandle CreateResource(
            string name,
            RenderResourceUsage initialUsage = RenderResourceUsage.Undefined)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            RenderResourceHandle handle = new(_resources.Count);
            _resources.Add(new ResourceDefinition(handle, name, initialUsage));
            return handle;
        }

        public RenderGraphBuilder AddPass(string name, Action<RenderPassBuilder> configure)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(configure);
            RenderPassBuilder builder = new(_resources.Count);
            configure(builder);
            _passes.Add(new PassDefinition(_passes.Count, name, builder.Uses.ToArray()));
            return this;
        }

        public CompiledRenderGraph Compile()
        {
            HashSet<int>[] dependencies = new HashSet<int>[_passes.Count];
            for (int i = 0; i < dependencies.Length; i++) dependencies[i] = new HashSet<int>();

            int?[] lastWriter = new int?[_resources.Count];
            List<int>[] readersSinceWrite = new List<int>[_resources.Count];
            for (int i = 0; i < readersSinceWrite.Length; i++) readersSinceWrite[i] = new List<int>();

            for (int passIndex = 0; passIndex < _passes.Count; passIndex++)
            {
                foreach (RenderResourceUse use in _passes[passIndex].Uses)
                {
                    ValidateHandle(use.Resource);
                    int resourceId = use.Resource.Id;
                    if (use.Access is RenderAccess.Read or RenderAccess.ReadWrite)
                    {
                        if (lastWriter[resourceId] is int writer && writer != passIndex)
                            dependencies[passIndex].Add(writer);
                        readersSinceWrite[resourceId].Add(passIndex);
                    }

                    if (use.Access is RenderAccess.Write or RenderAccess.ReadWrite)
                    {
                        if (lastWriter[resourceId] is int writer && writer != passIndex)
                            dependencies[passIndex].Add(writer);
                        foreach (int reader in readersSinceWrite[resourceId])
                        {
                            if (reader != passIndex) dependencies[passIndex].Add(reader);
                        }

                        readersSinceWrite[resourceId].Clear();
                        lastWriter[resourceId] = passIndex;
                    }
                }
            }

            List<int> order = StableTopologicalSort(dependencies);
            int[] oldToNew = new int[_passes.Count];
            for (int newIndex = 0; newIndex < order.Count; newIndex++)
                oldToNew[order[newIndex]] = newIndex;

            List<RenderPassPlan> plans = new(_passes.Count);
            foreach (int oldIndex in order)
            {
                PassDefinition pass = _passes[oldIndex];
                int[] remappedDependencies = dependencies[oldIndex]
                    .Select(index => oldToNew[index])
                    .Order()
                    .ToArray();
                plans.Add(new RenderPassPlan(plans.Count, pass.Name, pass.Uses, remappedDependencies));
            }

            IReadOnlyList<RenderBarrier> barriers = DeriveBarriers(order, oldToNew);
            Dictionary<RenderResourceHandle, string> resourceNames = _resources.ToDictionary(
                static resource => resource.Handle,
                static resource => resource.Name);
            return new CompiledRenderGraph(plans, barriers, resourceNames);
        }

        private IReadOnlyList<RenderBarrier> DeriveBarriers(
            IReadOnlyList<int> order,
            IReadOnlyList<int> oldToNew)
        {
            RenderResourceUsage[] usage = _resources
                .Select(static resource => resource.InitialUsage)
                .ToArray();
            int[] previousPass = Enumerable.Repeat(-1, _resources.Count).ToArray();
            RenderAccess[] previousAccess = Enumerable.Repeat(RenderAccess.Read, _resources.Count).ToArray();
            List<RenderBarrier> barriers = new();

            foreach (int oldPassIndex in order)
            {
                int newPassIndex = oldToNew[oldPassIndex];
                foreach (RenderResourceUse use in _passes[oldPassIndex].Uses)
                {
                    int id = use.Resource.Id;
                    bool writeHazard = previousPass[id] >= 0 &&
                        (previousAccess[id] is RenderAccess.Write or RenderAccess.ReadWrite ||
                         use.Access is RenderAccess.Write or RenderAccess.ReadWrite);

                    if (usage[id] != use.Usage || writeHazard)
                    {
                        barriers.Add(new RenderBarrier(
                            use.Resource,
                            _resources[id].Name,
                            usage[id],
                            use.Usage,
                            previousPass[id] < 0 ? -1 : oldToNew[previousPass[id]],
                            newPassIndex,
                            writeHazard));
                    }

                    usage[id] = use.Usage;
                    previousPass[id] = oldPassIndex;
                    previousAccess[id] = use.Access;
                }
            }

            return barriers;
        }

        private static List<int> StableTopologicalSort(IReadOnlyList<HashSet<int>> dependencies)
        {
            HashSet<int>[] remaining = dependencies
                .Select(static set => new HashSet<int>(set))
                .ToArray();
            List<int> output = new(remaining.Length);
            bool[] emitted = new bool[remaining.Length];

            while (output.Count < remaining.Length)
            {
                int found = -1;
                for (int i = 0; i < remaining.Length; i++)
                {
                    if (!emitted[i] && remaining[i].All(dependency => emitted[dependency]))
                    {
                        found = i;
                        break;
                    }
                }

                if (found < 0)
                    throw new InvalidOperationException("Render graph contains a dependency cycle.");

                emitted[found] = true;
                output.Add(found);
            }

            return output;
        }

        private void ValidateHandle(RenderResourceHandle handle)
        {
            if (!handle.IsValid || handle.Id >= _resources.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(handle),
                    "Render resource handle does not belong to this graph.");
            }
        }

        private sealed record ResourceDefinition(
            RenderResourceHandle Handle,
            string Name,
            RenderResourceUsage InitialUsage);

        private sealed record PassDefinition(
            int InsertionIndex,
            string Name,
            IReadOnlyList<RenderResourceUse> Uses);
    }

    public sealed class RenderPassBuilder
    {
        private readonly int _resourceCount;
        private readonly List<RenderResourceUse> _uses = new();

        internal RenderPassBuilder(int resourceCount) => _resourceCount = resourceCount;
        internal IReadOnlyList<RenderResourceUse> Uses => _uses;

        public RenderPassBuilder Read(RenderResourceHandle resource, RenderResourceUsage usage) =>
            Use(resource, usage, RenderAccess.Read);

        public RenderPassBuilder Write(RenderResourceHandle resource, RenderResourceUsage usage) =>
            Use(resource, usage, RenderAccess.Write);

        public RenderPassBuilder ReadWrite(RenderResourceHandle resource, RenderResourceUsage usage) =>
            Use(resource, usage, RenderAccess.ReadWrite);

        private RenderPassBuilder Use(
            RenderResourceHandle resource,
            RenderResourceUsage usage,
            RenderAccess access)
        {
            if (!resource.IsValid || resource.Id >= _resourceCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(resource),
                    "Render resource handle does not belong to this graph.");
            }

            if (_uses.Any(existing => existing.Resource == resource))
            {
                throw new InvalidOperationException(
                    $"Pass already declares resource {resource.Id}; use ReadWrite instead of duplicate declarations.");
            }

            _uses.Add(new RenderResourceUse(resource, usage, access));
            return this;
        }
    }
}
