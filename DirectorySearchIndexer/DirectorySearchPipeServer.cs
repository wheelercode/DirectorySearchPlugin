using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Wheelercode.DirectorySearchPlugin;

internal sealed class DirectorySearchPipeServer
{
    private readonly LiveDirectoryIndex index;

    internal DirectorySearchPipeServer(LiveDirectoryIndex index)
    {
        this.index = index;
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            NamedPipeServerStream? pipe = new(
                DirectorySearchProtocol.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);

                var connectedPipe = pipe;
                pipe = null;

                _ = HandleClientAsync(
                    connectedPipe,
                    cancellationToken);
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        using (pipe)
        using (var reader = new StreamReader(
                   pipe,
                   Encoding.UTF8,
                   false,
                   4_096,
                   true))
        using (var writer = new StreamWriter(
                   pipe,
                   new UTF8Encoding(false),
                   4_096,
                   true)
               {
                   AutoFlush = true,
               })
        {
            try
            {
                var line = await reader.ReadLineAsync(
                    cancellationToken);

                if (line is null)
                {
                    return;
                }

                var request =
                    JsonSerializer.Deserialize<DirectorySearchRequest>(
                        line);

                if (request is null ||
                    string.IsNullOrWhiteSpace(request.Query))
                {
                    await WriteResponseAsync(
                        writer,
                        new DirectorySearchResponse(
                            [],
                            "The search request was invalid."),
                        cancellationToken);

                    return;
                }

                var maximumResults = Math.Clamp(
                    request.MaximumResults,
                    1,
                    DirectorySearchProtocol.DefaultMaximumResults);

                var paths = index.Search(
                    request.Query,
                    maximumResults);

                await WriteResponseAsync(
                    writer,
                    new DirectorySearchResponse(paths),
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
                // The PowerToys query was superseded and the client
                // disconnected before reading the response.
            }
            catch (JsonException)
            {
                // A malformed request is isolated to this connection.
            }
            catch (Exception exception)
            {
                Console.WriteLine(
                    $"Directory-search pipe request failed: " +
                    $"{exception.Message}");
            }
        }
    }

    private static async Task WriteResponseAsync(
        StreamWriter writer,
        DirectorySearchResponse response,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(response);

        await writer.WriteLineAsync(
            json.AsMemory(),
            cancellationToken);
    }
}
