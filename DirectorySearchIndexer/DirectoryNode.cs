namespace Wheelercode.DirectorySearchPlugin;

internal sealed record DirectoryNode(
    ulong FileReference,
    ulong ParentFileReference,
    string Name,
    bool IsReparsePoint);

internal sealed record MftDirectorySnapshot(
    string Root,
    Dictionary<ulong, DirectoryNode> Nodes);

internal static class DirectoryReference
{
    private const ulong MftRecordNumberMask =
        0x0000FFFFFFFFFFFFUL;

    private const ulong RootMftRecordNumber = 5;

    internal static bool IsRoot(ulong fileReference)
    {
        return (fileReference & MftRecordNumberMask)
            == RootMftRecordNumber;
    }
}
