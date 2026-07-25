using System.Diagnostics;
using Wheelercode.DirectorySearchPlugin;

if (args.Any(
        argument => argument.Equals(
            "--self-test",
            StringComparison.OrdinalIgnoreCase)))
{
    LiveDirectoryIndexSelfTest.Run();
    return;
}

var root = args.Length > 0
    ? args[0]
    : @"C:\";

using var shutdown = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

var journalReader = new UsnJournalReader();

try
{
    while (!shutdown.IsCancellationRequested)
    {
        var stopwatch = Stopwatch.StartNew();

        Console.WriteLine(
            "Capturing USN checkpoint before MFT enumeration...");

        var checkpoint =
            journalReader.CaptureCheckpoint(root);

        Console.WriteLine(
            $"USN checkpoint: " +
            $"journal={checkpoint.JournalId:X16}; " +
            $"next={checkpoint.NextUsn:N0}");

        Console.WriteLine(
            "Starting MFT directory enumeration...");

        var snapshot =
            MftDirectoryEnumerator.EnumerateSnapshot(root);

        var index = new LiveDirectoryIndex(snapshot);

        Console.WriteLine(
            $"In-memory index built. " +
            $"Directories: {index.DirectoryCount:N0}; " +
            $"unique names: {index.UniqueNameCount:N0}; " +
            $"elapsed: {stopwatch.Elapsed}");

        Console.WriteLine(
            "Replaying journal changes recorded during MFT " +
            "enumeration...");

        checkpoint = journalReader.ReplayAvailable(
            root,
            checkpoint,
            index,
            ReportJournalBatch);

        var pathsByName = index.CreatePathSnapshot();
        var fallbackUniqueNameCount = pathsByName.Count;

        DirectoryIndexStore.Save(pathsByName);
        pathsByName.Clear();

        Console.WriteLine(
            $"Fallback snapshot saved. " +
            $"Unique names: {fallbackUniqueNameCount:N0}; " +
            $"elapsed: {stopwatch.Elapsed}");

        Console.WriteLine(
            $"Live index ready on pipe " +
            $"[{DirectorySearchProtocol.PipeName}].");

        Console.WriteLine(
            "Monitoring the USN journal. Press Ctrl+C to stop.");

        using var cycleCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                shutdown.Token);

        var pipeServer =
            new DirectorySearchPipeServer(index);

        var serverTask = pipeServer.RunAsync(
            cycleCancellation.Token);

        var journalTask = journalReader.FollowAsync(
            root,
            checkpoint,
            index,
            ReportJournalBatch,
            cycleCancellation.Token);

        try
        {
            var completedTask = await Task.WhenAny(
                serverTask,
                journalTask);

            await completedTask;

            if (completedTask == serverTask &&
                !cycleCancellation.IsCancellationRequested)
            {
                throw new IOException(
                    "The directory-search pipe server stopped.");
            }
        }
        catch (UsnJournalResetException exception)
        {
            Console.WriteLine(
                $"USN journal changed: {exception.Message}");

            Console.WriteLine(
                "Rebuilding the in-memory index from the MFT.");
        }
        finally
        {
            cycleCancellation.Cancel();

            await IgnoreCancellationAsync(serverTask);
            await IgnoreCancellationAsync(journalTask);
        }
    }
}
catch (OperationCanceledException)
    when (shutdown.IsCancellationRequested)
{
}
catch (Exception exception)
{
    Console.WriteLine(
        $"Directory indexer failed: {exception}");
}

static void ReportJournalBatch(JournalReadBatch batch)
{
    if (batch.Mutations.Total == 0)
    {
        return;
    }

    Console.WriteLine(
        $"USN update: " +
        $"records={batch.RecordsRead:N0}; " +
        $"upserted={batch.Mutations.Upserted:N0}; " +
        $"removed={batch.Mutations.Removed:N0}; " +
        $"next={batch.Checkpoint.NextUsn:N0}");
}

static async Task IgnoreCancellationAsync(Task task)
{
    try
    {
        await task;
    }
    catch (OperationCanceledException)
    {
    }
    catch (UsnJournalResetException)
    {
    }
}
