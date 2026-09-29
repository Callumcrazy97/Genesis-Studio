using System.IO.Compression;

namespace Genesis.Application.Core.Projects;

public static partial class GameExportService
{
    private static void Publish(string staging, string output, GameExportFormat format, bool replaceExisting,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
        if (format == GameExportFormat.Zip)
        {
            if (File.Exists(output) && !replaceExisting) throw new IOException("The export archive already exists.");
            string archive = output + ".new-" + Guid.NewGuid().ToString("N");
            try
            {
                ZipFile.CreateFromDirectory(staging, archive, CompressionLevel.Optimal, includeBaseDirectory: false);
                RetryPublication(() => File.Move(archive, output, overwrite: replaceExisting),
                    "publish the archive at " + output, progress, cancellationToken);
            }
            finally
            {
                // Cleanup must not obscure a publishing or cancellation failure.
                try { if (File.Exists(archive)) File.Delete(archive); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            TryDeleteDirectory(staging);
            return;
        }

        string previous = output + ".previous-" + Guid.NewGuid().ToString("N");
        bool movedPrevious = false;
        try
        {
            if (Directory.Exists(output))
            {
                if (!replaceExisting) throw new IOException("The export folder already exists.");
                RetryPublication(() => Directory.Move(output, previous),
                    "replace the release at " + output, progress, cancellationToken);
                movedPrevious = true;
            }
            RetryPublication(() => Directory.Move(staging, output),
                "publish the release at " + output, progress, cancellationToken);
        }
        catch (Exception publishError)
        {
            if (movedPrevious && !Directory.Exists(output))
            {
                try
                {
                    // Cancellation must still restore the previous working release.
                    RetryPublication(() => Directory.Move(previous, output),
                        "restore the release at " + output, null, CancellationToken.None);
                }
                catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                {
                    throw new IOException("Export failed. The previous release is preserved at " + previous
                        + ", but could not be restored to " + output + ". " + restoreError.Message,
                        new AggregateException(publishError, restoreError));
                }
            }
            throw;
        }
        if (movedPrevious) TryDeleteDirectory(previous);
    }

    private static void RetryPublication(Action operation, string description, IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        // A scanner or a just-closed Player can briefly hold a handle without FILE_SHARE_DELETE.
        // Retry only Windows sharing/access errors, for at most 1.8 seconds, without changing ACLs.
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { operation(); return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                && (error.HResult & 0xffff) is 5 or 32 or 33)
            {
                if (attempt == 9) throw new IOException("Could not " + description + ". " + error.Message, error);
                if (attempt == 0) progress?.Report("Waiting for Windows to " + description + "…");
                if (cancellationToken.WaitHandle.WaitOne(200)) cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }
}
