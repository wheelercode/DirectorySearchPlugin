using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wheelercode.DirectorySearchPlugin;

internal sealed class DirectoryIndexWorker : BackgroundService
{
    internal const string ServiceName =
        "WheelercodeDirectorySearch";

    private static readonly TimeSpan RestartDelay =
        TimeSpan.FromSeconds(5);

    private const long UpdateCompactionThreshold = 10_000;

    private readonly ILogger<DirectoryIndexWorker> logger;

    internal DirectoryIndexWorker(
        ILogger<DirectoryIndexWorker> logger)
    {
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await BuildAndFollowAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (UsnJournalResetException exception)
            {
                logger.LogWarning(
                    exception,
                    "The USN journal changed. Rebuilding the index.");
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Directory indexing failed. Retrying in {Delay}.",
                    RestartDelay);

                await Task.Delay(
                    RestartDelay,
                    stoppingToken);
            }
        }
    }

    private async Task BuildAndFollowAsync(
        CancellationToken cancellationToken)
    {
        const string root = @"C:\";

        var stopwatch = Stopwatch.StartNew();
        var journalReader = new UsnJournalReader();

        logger.LogInformation(
            "Capturing the USN checkpoint.");

        var checkpoint =
            journalReader.CaptureCheckpoint(root);

        logger.LogInformation(
            "Starting MFT directory enumeration.");

        var snapshot =
            MftDirectoryEnumerator.EnumerateSnapshot(root);

        using var index = new LiveDirectoryIndex(snapshot);

        checkpoint = journalReader.ReplayAvailable(
            root,
            checkpoint,
            index);

        var firstGeneration = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            var pathsByName = index.CreatePathSnapshot();

            var manifest = DirectoryIndexStore.PublishSnapshot(
                pathsByName,
                checkpoint.NextUsn);

            logger.LogInformation(
                "Published index generation {Generation}. " +
                "Directories: {Directories}; unique names: " +
                "{UniqueNames}; elapsed: {Elapsed}; reason: {Reason}.",
                manifest.Generation,
                index.DirectoryCount,
                index.UniqueNameCount,
                stopwatch.Elapsed,
                firstGeneration
                    ? "startup"
                    : "update-log compaction");

            firstGeneration = false;

            using var updateWriter =
                DirectoryIndexStore.CreateUpdateWriter(manifest);

            var latestCheckpoint = checkpoint;

            try
            {
                await journalReader.FollowAsync(
                    root,
                    checkpoint,
                    index,
                    batch =>
                    {
                        latestCheckpoint = batch.Checkpoint;

                        var sequence =
                            updateWriter.Append(batch.Updates);

                        if (batch.Mutations.Total > 0)
                        {
                            logger.LogInformation(
                                "USN update: records={Records}; " +
                                "upserted={Upserted}; removed={Removed}; " +
                                "published={Published}; " +
                                "sequence={Sequence}; next={NextUsn}.",
                                batch.RecordsRead,
                                batch.Mutations.Upserted,
                                batch.Mutations.Removed,
                                batch.Updates.Count,
                                sequence,
                                batch.Checkpoint.NextUsn);
                        }

                        if (sequence >= UpdateCompactionThreshold)
                        {
                            throw new IndexCompactionRequestedException();
                        }
                    },
                    cancellationToken);
            }
            catch (IndexCompactionRequestedException)
            {
                checkpoint = latestCheckpoint;

                logger.LogInformation(
                    "The update log reached {Threshold} records. " +
                    "Publishing a compacted base generation.",
                    UpdateCompactionThreshold);
            }
        }
    }

    private sealed class IndexCompactionRequestedException :
        Exception
    {
    }
}
