using System.Collections.Generic;
using Genesis.Shared.ECS;

namespace Genesis.Runtime.ECS
{
    // Queues entity creates/destroys requested during system Update.
    // Flushed by SystemScheduler between the Update and Render passes.
    // Systems must never call World.DestroyEntityImmediate directly — always go through
    // World.DestroyEntity(), which routes here.
    internal sealed class DeferredCommandBuffer
    {
        private enum OpKind { Destroy }

        private struct Op
        {
            internal OpKind Kind;
            internal Entity Target;
        }

        private readonly List<Op> _ops = new List<Op>(16);

        internal void EnqueueDestroy(Entity entity)
            => _ops.Add(new Op { Kind = OpKind.Destroy, Target = entity });

        internal void Flush(World world)
        {
            foreach (var op in _ops)
            {
                if (op.Kind == OpKind.Destroy)
                    world.DestroyEntityImmediate(op.Target);
            }
            _ops.Clear();
        }
    }
}
