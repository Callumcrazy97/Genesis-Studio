using System;

namespace Genesis.Rendering.Abstractions
{
    /// <summary>
    /// A device that can wait for the GPU with a time limit, for work that can be skipped (a
    /// screenshot) rather than worth freezing the game for. Optional: a device without it is
    /// waited on with <see cref="IGpuDevice.WaitIdle"/>.
    /// </summary>
    public interface IGpuBoundedWait
    {
        /// <summary>
        /// Submits what is recorded (ending the frame) and waits until the GPU has finished all of
        /// it, or until <paramref name="timeout"/> has passed. False in the second case, with the
        /// device still consistent: nothing it hands back to the CPU is reused before the GPU is
        /// done with it. <paramref name="purpose"/> names the wait in the log.
        /// </summary>
        bool TryWaitIdle(TimeSpan timeout, string purpose);
    }
}
