using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
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
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;

    private sealed record DirectoryNode(
        long FileReference,
        long ParentFileReference,
        string Name);

    public static Dictionary<string, List<string>> Enumerate(string root)
    {
        var drive = Path.GetPathRoot(root);

        if (string.IsNullOrWhiteSpace(drive))
        {
            throw new ArgumentException("Invalid root path.", nameof(root));
        }

        var volumePath = $@"\\.\{drive.TrimEnd('\\')}";

        using var volume = CreateFile(
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
        var resolvedPaths = new Dictionary<long, string>();
        var pathsByName = new Dictionary<string, List<string>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var node in nodes.Values)
        {
            var path = ResolvePath(
                node.FileReference,
                nodes,
                resolvedPaths,
                root,
                new HashSet<long>());

            if (string.IsNullOrWhiteSpace(path) ||
                string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Path.GetFileName(path);

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!pathsByName.TryGetValue(name, out var paths))
            {
                paths = [];
                pathsByName[name] = paths;
            }

            paths.Add(path);
        }

        return pathsByName;
    }

    private static Dictionary<long, DirectoryNode> ReadDirectoryRecords(
        SafeFileHandle volume)
    {
        var nodes = new Dictionary<long, DirectoryNode>();
        var input = new byte[24];
        var output = new byte[1024 * 1024];
        long startFileReference = 0;

        while (true)
        {
            Array.Clear(input);
            BinaryPrimitives.WriteInt64LittleEndian(
                input.AsSpan(0, 8),
                startFileReference);

            BinaryPrimitives.WriteInt64LittleEndian(
                input.AsSpan(8, 8),
                0);

            BinaryPrimitives.WriteInt64LittleEndian(
                input.AsSpan(16, 8),
                long.MaxValue);

            var success = DeviceIoControl(
                volume,
                FsctlEnumUsnData,
                input,
                input.Length,
                output,
                output.Length,
                out var bytesReturned,
                IntPtr.Zero);

            var error = Marshal.GetLastWin32Error();

            if (!success &&
                error != ErrorHandleEof)
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
                BinaryPrimitives.ReadInt64LittleEndian(
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

                var majorVersion =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        output.AsSpan(offset + 4, 2));

                if (majorVersion == 2)
                {
                    var fileReference =
                        BinaryPrimitives.ReadInt64LittleEndian(
                            output.AsSpan(offset + 8, 8));

                    var parentReference =
                        BinaryPrimitives.ReadInt64LittleEndian(
                            output.AsSpan(offset + 16, 8));

                    var attributes =
                        BinaryPrimitives.ReadUInt32LittleEndian(
                            output.AsSpan(offset + 52, 4));

                    var fileNameLength =
                        BinaryPrimitives.ReadUInt16LittleEndian(
                            output.AsSpan(offset + 56, 2));

                    var fileNameOffset =
                        BinaryPrimitives.ReadUInt16LittleEndian(
                            output.AsSpan(offset + 58, 2));

                    var isDirectory =
                        (attributes & FileAttributeDirectory) != 0;

                    var isReparsePoint =
                        (attributes & FileAttributeReparsePoint) != 0;

                    if (isDirectory &&
                        !isReparsePoint &&
                        fileNameOffset + fileNameLength <= recordLength)
                    {
                        var name = Encoding.Unicode.GetString(
                            output,
                            offset + fileNameOffset,
                            fileNameLength);

                        nodes[fileReference] = new DirectoryNode(
                            fileReference,
                            parentReference,
                            name);
                    }
                }

                offset += recordLength;
            }

            if (!success ||
                nextStartFileReference == startFileReference)
            {
                break;
            }

            startFileReference = nextStartFileReference;
        }

        return nodes;
    }

    private static string? ResolvePath(
        long fileReference,
        Dictionary<long, DirectoryNode> nodes,
        Dictionary<long, string> cache,
        string root,
        HashSet<long> resolving)
    {
        if (cache.TryGetValue(fileReference, out var cached))
        {
            return cached;
        }

        if (!nodes.TryGetValue(fileReference, out var node))
        {
            return null;
        }

        if (!resolving.Add(fileReference))
        {
            return null;
        }

        if (node.ParentFileReference == node.FileReference)
        {
            cache[fileReference] = root;
            return root;
        }

        var parentPath = ResolvePath(
            node.ParentFileReference,
            nodes,
            cache,
            root,
            resolving);

        resolving.Remove(fileReference);

        if (parentPath is null)
        {
            return null;
        }

        var path = Path.Combine(parentPath, node.Name);
        cache[fileReference] = path;
        return path;
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
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