using System.Threading;

namespace Genesis.Shared.Assets
{
    /// <summary>
    /// Process-wide counters for file-system work done by asset resolution on the frame path.
    /// A warmed static scene should report zero per frame; a steady non-zero delta means some
    /// resolver is re-checking or re-reading the disk every frame.
    /// </summary>
    public static class AssetIoCounters
    {
        private static long _fileChecks;
        private static long _fileReads;

        /// <summary>Existence, timestamp, length or attribute queries.</summary>
        public static void Check(int count = 1) => Interlocked.Add(ref _fileChecks, count);

        /// <summary>Whole-file content reads, including descriptor/manifest parses.</summary>
        public static void Read(int count = 1) => Interlocked.Add(ref _fileReads, count);

        public static AssetIoSnapshot Capture() => new(
            Interlocked.Read(ref _fileChecks),
            Interlocked.Read(ref _fileReads));
    }

    public readonly record struct AssetIoSnapshot(long FileChecks, long FileReads)
    {
        public AssetIoSnapshot Since(AssetIoSnapshot earlier) => new(
            FileChecks - earlier.FileChecks,
            FileReads - earlier.FileReads);
    }
}
