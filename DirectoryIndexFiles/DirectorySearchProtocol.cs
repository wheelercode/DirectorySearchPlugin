namespace Wheelercode.DirectorySearchPlugin;

public static class DirectorySearchProtocol
{
    public const string PipeName =
        "Wheelercode.DirectorySearchPlugin.Index";

    public const int DefaultMaximumResults = 5_000;
}

public sealed record DirectorySearchRequest(
    string Query,
    int MaximumResults);

public sealed record DirectorySearchResponse(
    IReadOnlyList<string> Paths,
    string? Error = null);
