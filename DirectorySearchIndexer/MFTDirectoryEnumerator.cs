using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wheelercode.DirectorySearchPlugin;

public static class MftDirectoryEnumerator
{
    private const uint FsctlEnumUsnData = 0x000900B3;

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;

    private const int ErrorHandleEof = 38;

    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;

    public static Dictionary<string, List<string>> Enumerate(string root)
    {
        var snapshot = EnumerateSnapshot(root);
        using var index = new LiveDirectoryIndex(snapshot);

        return index.CreatePathSnapshot();
    }

    internal static MftDirectorySnapshot EnumerateSnapshot(string root)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);

        Console.WriteLine(
            $"MFT process identity: {identity.Name}; " +
            $"Administrator: " +
            $"{principal.IsInRole(WindowsBuiltInRole.Administrator)}");

        var normalizedRoot = Path.GetPathRoot(root);

        if (string.IsNullOrWhiteSpace(normalizedRoot))
        {
            throw new ArgumentException(
                "Invalid root path.",
                nameof(root));
        }

        var volumePath = $@"\\.\{normalizedRoot.TrimEnd('\\')}";

        using var volume = CreateFileW(
            volumePath,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (volume.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Unable to open volume {volumePath}.");
        }

        var nodes = ReadDirectoryRecords(volume);
        var missingParentCount = CountMissingParents(nodes);

        Console.WriteLine(
            $"Missing parent references: {missingParentCount:N0}");

        Console.WriteLine(
            $"Directory nodes collected: {nodes.Count:N0}");

        return new MftDirectorySnapshot(normalizedRoot, nodes);
    }

    private static int CountMissingParents(
        IReadOnlyDictionary<ulong, DirectoryNode> nodes)
    {
        var missingParentCount = 0;

        foreach (var node in nodes.Values)
        {
            if (DirectoryReference.IsRoot(node.FileReference))
            {
                continue;
            }

            if (!DirectoryReference.IsRoot(node.ParentFileReference) &&
                !nodes.ContainsKey(node.ParentFileReference))
            {
                missingParentCount++;

                if (missingParentCount <= 20)
                {
                    Console.WriteLine(
                        $"Missing parent: " +
                        $"child={node.FileReference:X16}; " +
                        $"parent={node.ParentFileReference:X16}; " +
                        $"name=[{node.Name}]");
                }
            }
        }

        var rootNode = nodes.Values.FirstOrDefault(
            node => DirectoryReference.IsRoot(node.FileReference));

        if (rootNode is not null)
        {
            Console.WriteLine(
                $"Root found: " +
                $"FRN={rootNode.FileReference:X16}; " +
                $"Parent={rootNode.ParentFileReference:X16}; " +
                $"Name=[{rootNode.Name}]");
        }
        else
        {
            Console.WriteLine(
                "Root record was not returned; parent references to " +
                "MFT record 5 will be treated as root.");
        }

        return missingParentCount;
    }

    private static Dictionary<ulong, DirectoryNode> ReadDirectoryRecords(
        SafeFileHandle volume)
    {
        var nodes = new Dictionary<ulong, DirectoryNode>();

        // MFT_ENUM_DATA_V1:
        //
        // Offset 0   : StartFileReferenceNumber, 8 bytes, unsigned
        // Offset 8   : LowUsn,                    8 bytes, signed
        // Offset 16  : HighUsn,                   8 bytes, signed
        // Offset 24  : MinMajorVersion,          2 bytes
        // Offset 26  : MaxMajorVersion,          2 bytes
        var input = new byte[32];

        // The first eight output bytes contain the next starting
        // file-reference number. Records begin at offset eight.
        var output = new byte[1024 * 1024];

        ulong startFileReference = 0;

        var totalRecords = 0;
        var version2Records = 0;
        var directoryRecords = 0;

        while (true)
        {
            Array.Clear(input);

            BinaryPrimitives.WriteUInt64LittleEndian(
                input.AsSpan(0, 8),
                startFileReference);

            BinaryPrimitives.WriteInt64LittleEndian(
                input.AsSpan(8, 8),
                0);

            BinaryPrimitives.WriteInt64LittleEndian(
                input.AsSpan(16, 8),
                long.MaxValue);

            BinaryPrimitives.WriteUInt16LittleEndian(
                input.AsSpan(24, 2),
                2);

            BinaryPrimitives.WriteUInt16LittleEndian(
                input.AsSpan(26, 2),
                2);

            var success = DeviceIoControl(
                volume,
                FsctlEnumUsnData,
                input,
                input.Length,
                output,
                output.Length,
                out var bytesReturned,
                IntPtr.Zero);

            var error = success
                ? 0
                : Marshal.GetLastWin32Error();

            if (!success && error != ErrorHandleEof)
            {
                throw new Win32Exception(
                    error,
                    "FSCTL_ENUM_USN_DATA failed.");
            }

            if (bytesReturned < 8)
            {
                break;
            }

            var nextStartFileReference =
                BinaryPrimitives.ReadUInt64LittleEndian(
                    output.AsSpan(0, 8));

            var offset = 8;

            while (offset + 60 <= bytesReturned)
            {
                var recordLength =
                    BinaryPrimitives.ReadInt32LittleEndian(
                        output.AsSpan(offset, 4));

                if (recordLength < 60 ||
                    offset + recordLength > bytesReturned)
                {
                    break;
                }

                totalRecords++;

                var majorVersion =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        output.AsSpan(offset + 4, 2));

                if (majorVersion != 2)
                {
                    offset += recordLength;
                    continue;
                }

                version2Records++;

                var fileReference =
                    BinaryPrimitives.ReadUInt64LittleEndian(
                        output.AsSpan(offset + 8, 8));

                var parentFileReference =
                    BinaryPrimitives.ReadUInt64LittleEndian(
                        output.AsSpan(offset + 16, 8));

                var fileAttributes =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        output.AsSpan(offset + 52, 4));

                var fileNameLength =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        output.AsSpan(offset + 56, 2));

                var fileNameOffset =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        output.AsSpan(offset + 58, 2));

                var isDirectory =
                    (fileAttributes & FileAttributeDirectory) != 0;

                if (!isDirectory)
                {
                    offset += recordLength;
                    continue;
                }

                directoryRecords++;

                var fileNameEnd =
                    (int)fileNameOffset + fileNameLength;

                if (fileNameEnd > recordLength)
                {
                    offset += recordLength;
                    continue;
                }

                var name = Encoding.Unicode.GetString(
                    output,
                    offset + fileNameOffset,
                    fileNameLength);

                var isReparsePoint =
                    (fileAttributes & FileAttributeReparsePoint) != 0;

                // Keep the complete 64-bit FRN, including its sequence
                // number. The journal emits the same identifier.
                nodes[fileReference] = new DirectoryNode(
                    fileReference,
                    parentFileReference,
                    name,
                    isReparsePoint);

                offset += recordLength;
            }

            if (!success ||
                nextStartFileReference == startFileReference)
            {
                break;
            }

            startFileReference = nextStartFileReference;
        }

        Console.WriteLine(
            $"MFT records: " +
            $"total={totalRecords:N0}; " +
            $"version2={version2Records:N0}; " +
            $"directories={directoryRecords:N0}; " +
            $"nodes={nodes.Count:N0}");

        return nodes;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[] inputBuffer,
        int inputBufferSize,
        byte[] outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);
}
