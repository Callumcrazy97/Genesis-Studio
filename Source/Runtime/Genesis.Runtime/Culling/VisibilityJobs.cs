using System;
using System.Numerics;
using Genesis.Rendering.D3dMath;
using Genesis.Shared.Threading;
using Genesis.Streaming;

namespace Genesis.Runtime.Culling
{
    /// <summary>
    /// Read-only parallel visibility tests. Results merge on the main thread into the submit list.
    /// Does not touch D3D; jobs use StreamingJobQueue or EngineJobs under SystemScheduler order.
    /// </summary>
    public static class VisibilityJobs
    {
        public static int LastJobBatchCount { get; private set; }

        /// <summary>
        /// Frustum-test the first <paramref name="count"/> spheres. When
        /// <paramref name="parallel"/> is true, uses the engine's workers in batches of 2 048
        /// (<see cref="EngineJobs.For"/>); fewer spheres are tested on this thread.
        /// </summary>
        public static void TestSpheres(
            in Frustum frustum,
            Vector3[] centers,
            float[] radii,
            byte[] visibleOut,
            int count,
            bool parallel = false)
        {
            if (centers == null || radii == null || visibleOut == null)
                throw new ArgumentNullException();
            if (count < 0
                || count > centers.Length
                || count > radii.Length
                || count > visibleOut.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            LastJobBatchCount = count;
            if (count == 0)
                return;

            Frustum local = frustum;
            if (!parallel || count < 2)
            {
                for (int i = 0; i < count; i++)
                    visibleOut[i] = local.ContainsSphere(centers[i], radii[i]) ? (byte)1 : (byte)0;
                return;
            }

            // In batches on the engine's workers: a task per sphere cost more than the test itself.
            EngineJobs.For(count, 2048, (start, end) =>
            {
                for (int i = start; i < end; i++)
                    visibleOut[i] = local.ContainsSphere(centers[i], radii[i]) ? (byte)1 : (byte)0;
            });
        }

        public static void TestSpheresParallel(
            in Frustum frustum,
            Vector3[] centers,
            float[] radii,
            byte[] visibleOut)
        {
            if (centers == null || radii == null || visibleOut == null)
                throw new ArgumentNullException();
            if (centers.Length != radii.Length || centers.Length != visibleOut.Length)
                throw new ArgumentException("Span lengths must match.");

            TestSpheres(frustum, centers, radii, visibleOut, centers.Length, parallel: true);
        }

        /// <summary>
        /// Queue frustum tests on the streaming worker pool; posts results via main-thread callback.
        /// </summary>
        public static bool TryEnqueue(
            StreamingJobQueue jobs,
            Frustum frustum,
            Vector3[] centers,
            float[] radii,
            byte[] visibleOut,
            Action onComplete)
        {
            if (jobs == null || centers == null || radii == null || visibleOut == null)
                return false;

            Frustum local = frustum;
            Vector3[] c = centers;
            float[] r = radii;
            byte[] v = visibleOut;
            return jobs.TryRunBackground(() =>
            {
                LastJobBatchCount = c.Length;
                for (int i = 0; i < c.Length; i++)
                    v[i] = local.ContainsSphere(c[i], r[i]) ? (byte)1 : (byte)0;
                if (onComplete != null)
                    jobs.PostMainThread(onComplete);
            });
        }
    }
}
