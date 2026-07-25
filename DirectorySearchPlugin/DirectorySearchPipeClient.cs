using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Wheelercode.DirectorySearchPlugin;

internal static class DirectorySearchPipeClient
{
    private const int ConnectionTimeoutMilliseconds = 40;

    private static readonly TimeSpan RequestTimeout =
        TimeSpan.FromMilliseconds(250);

    internal static bool TrySearch(
        string text,
        out IReadOnlyList<string> paths)
    {
        using var cancellation =
            new CancellationTokenSource(RequestTimeout);

        try
        {
            paths = SearchAsync(
                    text,
                    cancellation.Token)
                .GetAwaiter()
                .GetResult();

            return true;
        }
        catch (Exception)
        {
            paths = [];
            return false;
        }
    }

    private static async Task<IReadOnlyList<string>> SearchAsync(
        string text,
        CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            DirectorySearchProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await pipe.ConnectAsync(
            ConnectionTimeoutMilliseconds,
            cancellationToken);

        using var reader = new StreamReader(
            pipe,
            Encoding.UTF8,
            false,
            4_096,
            true);

        using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(false),
            4_096,
            true)
        {
            AutoFlush = true,
        };

        var request = new DirectorySearchRequest(
            text,
            DirectorySearchProtocol.DefaultMaximumResults);

        var json = JsonSerializer.Serialize(request);

        await writer.WriteLineAsync(
            json.AsMemory(),
            cancellationToken);

        var responseLine = await reader.ReadLineAsync(
            cancellationToken);

        if (responseLine is null)
        {
            throw new IOException(
                "The directory index helper closed the pipe.");
        }

        var response =
            JsonSerializer.Deserialize<DirectorySearchResponse>(
                responseLine);

        if (response is null)
        {
            throw new JsonException(
                "The directory index helper returned no response.");
        }

        if (response.Error is not null)
        {
            throw new IOException(response.Error);
        }

        return response.Paths;
    }
}
