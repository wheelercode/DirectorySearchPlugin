using System.Buffers.Binary;
using System.Text;

namespace Wheelercode.DirectorySearchPlugin;

internal static class LiveDirectoryIndexSelfTest
{
    internal static void Run()
    {
        const ulong rootReference = 0x0001000000000005;
        const ulong projectsReference = 0x0001000000000100;
        const ulong alphaReference = 0x0001000000000101;
        const ulong betaReference = 0x0001000000000102;

        var nodes = new Dictionary<ulong, DirectoryNode>
        {
            [rootReference] = new(
                rootReference,
                rootReference,
                string.Empty,
                false),
            [projectsReference] = new(
                projectsReference,
                rootReference,
                "Projects",
                false),
            [alphaReference] = new(
                alphaReference,
                projectsReference,
                "Alpha",
                false),
        };

        using var index = new LiveDirectoryIndex(
            new MftDirectorySnapshot(@"C:\", nodes));

        AssertSinglePath(
            index.Search("Alpha", 10),
            @"C:\Projects\Alpha",
            "Initial MFT snapshot");

        index.Apply(
        [
            new DirectoryMutation(
                DirectoryMutationKind.Remove,
                projectsReference,
                rootReference,
                "Projects",
                false),
            new DirectoryMutation(
                DirectoryMutationKind.Upsert,
                projectsReference,
                rootReference,
                "Code",
                false),
        ]);

        AssertSinglePath(
            index.Search("Alpha", 10),
            @"C:\Code\Alpha",
            "Parent rename");

        index.Apply(
        [
            new DirectoryMutation(
                DirectoryMutationKind.Upsert,
                betaReference,
                projectsReference,
                "Beta",
                false),
        ]);

        AssertSinglePath(
            index.Search("Beta", 10),
            @"C:\Code\Beta",
            "Directory create");

        index.Apply(
        [
            new DirectoryMutation(
                DirectoryMutationKind.Remove,
                alphaReference,
                projectsReference,
                "Alpha",
                false),
        ]);

        Assert(
            index.Search("Alpha", 10).Count == 0,
            "Directory delete did not remove Alpha.");

        TestUsnRecordParsing(
            betaReference,
            projectsReference);

        Console.WriteLine(
            "Live directory index self-test passed.");
    }

    private static void TestUsnRecordParsing(
        ulong fileReference,
        ulong parentFileReference)
    {
        const int recordOffset = 8;
        const int recordLength = 72;
        const uint fileCreateReason = 0x00000100;
        const uint renameOldNameReason = 0x00001000;
        const uint renameNewNameReason = 0x00002000;
        const uint directoryAttribute = 0x00000010;

        var output = new byte[recordOffset + recordLength];
        var nameBytes = Encoding.Unicode.GetBytes("Gamma");

        BinaryPrimitives.WriteInt32LittleEndian(
            output.AsSpan(recordOffset, 4),
            recordLength);

        BinaryPrimitives.WriteUInt16LittleEndian(
            output.AsSpan(recordOffset + 4, 2),
            2);

        BinaryPrimitives.WriteUInt64LittleEndian(
            output.AsSpan(recordOffset + 8, 8),
            fileReference);

        BinaryPrimitives.WriteUInt64LittleEndian(
            output.AsSpan(recordOffset + 16, 8),
            parentFileReference);

        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(recordOffset + 40, 4),
            fileCreateReason |
            renameOldNameReason |
            renameNewNameReason);

        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(recordOffset + 52, 4),
            directoryAttribute);

        BinaryPrimitives.WriteUInt16LittleEndian(
            output.AsSpan(recordOffset + 56, 2),
            (ushort)nameBytes.Length);

        BinaryPrimitives.WriteUInt16LittleEndian(
            output.AsSpan(recordOffset + 58, 2),
            60);

        nameBytes.CopyTo(output, recordOffset + 60);

        var mutations = new List<DirectoryMutation>();

        var recordsRead = UsnJournalReader.ParseRecords(
            output,
            output.Length,
            mutations);

        Assert(
            recordsRead == 1,
            "The synthetic USN record was not read.");

        Assert(
            mutations.Count == 1,
            "The synthetic directory create did not produce a mutation.");

        var mutation = mutations[0];

        Assert(
            mutation.Kind == DirectoryMutationKind.Upsert &&
            mutation.FileReference == fileReference &&
            mutation.ParentFileReference == parentFileReference &&
            mutation.Name == "Gamma",
            "A combined create/rename USN record must preserve " +
            "the directory's final name.");
    }

    private static void AssertSinglePath(
        IReadOnlyList<string> paths,
        string expected,
        string scenario)
    {
        Assert(
            paths.Count == 1 &&
            paths[0].Equals(
                expected,
                StringComparison.OrdinalIgnoreCase),
            $"{scenario} returned an unexpected path.");
    }

    private static void Assert(
        bool condition,
        string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
