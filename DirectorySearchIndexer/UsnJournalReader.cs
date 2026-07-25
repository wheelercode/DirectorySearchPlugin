using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wheelercode.DirectorySearchPlugin;

internal readonly record struct UsnJournalCheckpoint(
    ulong JournalId,
    long NextUsn);

internal readonly record struct JournalReadBatch(
    UsnJournalCheckpoint Checkpoint,
    int RecordsRead,
    DirectoryMutationSummary Mutations,
    IReadOnlyList<DirectoryIndexUpdateDraft> Updates);

internal sealed class UsnJournalResetException : Exception
{
    internal UsnJournalResetException(string message)
        : base(message)
    {
    }

    internal UsnJournalResetException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class UsnJournalReader
{
    private const uint FsctlReadUsnJournal = 0x000900BB;
    private const uint FsctlQueryUsnJournal = 0x000900F4;

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;

    private const int ErrorJournalDeleteInProgress = 1178;
    private const int ErrorJournalNotActive = 1179;
    private const int ErrorJournalEntryDeleted = 1181;

    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;

    private const uint UsnReasonFileCreate = 0x00000100;
    private const uint UsnReasonFileDelete = 0x00000200;
    private const uint UsnReasonRenameOldName = 0x00001000;
    private const uint UsnReasonRenameNewName = 0x00002000;
    private const uint UsnReasonBasicInfoChange = 0x00008000;
    private const uint UsnReasonReparsePointChange = 0x00100000;

    private const uint RelevantReasons =
        UsnReasonFileCreate |
        UsnReasonFileDelete |
        UsnReasonRenameOldName |
        UsnReasonRenameNewName |
        UsnReasonBasicInfoChange |
        UsnReasonReparsePointChange;

    private static readonly TimeSpan IdlePollInterval =
        TimeSpan.FromMilliseconds(250);

    internal UsnJournalCheckpoint CaptureCheckpoint(string root)
    {
        using var volume = OpenVolume(root);
        var journal = QueryJournal(volume);

        return new UsnJournalCheckpoint(
            journal.JournalId,
            journal.NextUsn);
    }

    internal UsnJournalCheckpoint ReplayAvailable(
        string root,
        UsnJournalCheckpoint checkpoint,
        LiveDirectoryIndex index,
        Action<JournalReadBatch>? batchObserved = null)
    {
        using var volume = OpenVolume(root);
        ValidateCheckpoint(volume, checkpoint);

        while (true)
        {
            var batch = ReadOnce(volume, checkpoint, index);
            batchObserved?.Invoke(batch);

            var advanced =
                batch.Checkpoint.NextUsn != checkpoint.NextUsn;

            checkpoint = batch.Checkpoint;

            if (!advanced && batch.RecordsRead == 0)
            {
                return checkpoint;
            }
        }
    }

    internal async Task FollowAsync(
        string root,
        UsnJournalCheckpoint checkpoint,
        LiveDirectoryIndex index,
        Action<JournalReadBatch>? batchObserved,
        CancellationToken cancellationToken)
    {
        using var volume = OpenVolume(root);
        ValidateCheckpoint(volume, checkpoint);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = ReadOnce(volume, checkpoint, index);
            batchObserved?.Invoke(batch);

            var advanced =
                batch.Checkpoint.NextUsn != checkpoint.NextUsn;

            checkpoint = batch.Checkpoint;

            if (!advanced && batch.RecordsRead == 0)
            {
                await Task.Delay(
                    IdlePollInterval,
                    cancellationToken);
            }
        }
    }

    private static JournalReadBatch ReadOnce(
        SafeFileHandle volume,
        UsnJournalCheckpoint checkpoint,
        LiveDirectoryIndex index)
    {
        // READ_USN_JOURNAL_DATA_V0:
        //
        // Offset 0  : StartUsn,          8 bytes, signed
        // Offset 8  : ReasonMask,        4 bytes
        // Offset 12 : ReturnOnlyOnClose, 4 bytes
        // Offset 16 : Timeout,           8 bytes
        // Offset 24 : BytesToWaitFor,    8 bytes
        // Offset 32 : UsnJournalID,      8 bytes
        var input = new byte[40];

        BinaryPrimitives.WriteInt64LittleEndian(
            input.AsSpan(0, 8),
            checkpoint.NextUsn);

        BinaryPrimitives.WriteUInt32LittleEndian(
            input.AsSpan(8, 4),
            RelevantReasons);

        BinaryPrimitives.WriteUInt64LittleEndian(
            input.AsSpan(32, 8),
            checkpoint.JournalId);

        var output = new byte[1024 * 1024];

        var success = DeviceIoControl(
            volume,
            FsctlReadUsnJournal,
            input,
            input.Length,
            output,
            output.Length,
            out var bytesReturned,
            IntPtr.Zero);

        if (!success)
        {
            ThrowJournalReadError(Marshal.GetLastWin32Error());
        }

        if (bytesReturned < 8)
        {
            throw new InvalidDataException(
                "FSCTL_READ_USN_JOURNAL returned no next USN.");
        }

        var nextUsn = BinaryPrimitives.ReadInt64LittleEndian(
            output.AsSpan(0, 8));

        if (nextUsn < checkpoint.NextUsn)
        {
            throw new UsnJournalResetException(
                "The USN journal moved backwards and the index " +
                "must be rebuilt.");
        }

        var mutations = new List<DirectoryMutation>();
        var recordsRead = ParseRecords(
            output,
            bytesReturned,
            mutations);

        var mutationResult = index.Apply(mutations);

        return new JournalReadBatch(
            new UsnJournalCheckpoint(
                checkpoint.JournalId,
                nextUsn),
            recordsRead,
            mutationResult.Summary,
            mutationResult.Updates);
    }

    internal static int ParseRecords(
        byte[] output,
        int bytesReturned,
        ICollection<DirectoryMutation> mutations)
    {
        var offset = 8;
        var recordsRead = 0;

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

            recordsRead++;

            var majorVersion =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    output.AsSpan(offset + 4, 2));

            if (majorVersion != 2)
            {
                offset += recordLength;
                continue;
            }

            var fileAttributes =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    output.AsSpan(offset + 52, 4));

            if ((fileAttributes & FileAttributeDirectory) == 0)
            {
                offset += recordLength;
                continue;
            }

            var fileReference =
                BinaryPrimitives.ReadUInt64LittleEndian(
                    output.AsSpan(offset + 8, 8));

            var parentFileReference =
                BinaryPrimitives.ReadUInt64LittleEndian(
                    output.AsSpan(offset + 16, 8));

            var reason =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    output.AsSpan(offset + 40, 4));

            var fileNameLength =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    output.AsSpan(offset + 56, 2));

            var fileNameOffset =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    output.AsSpan(offset + 58, 2));

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

            if ((reason & UsnReasonFileDelete) != 0)
            {
                mutations.Add(
                    new DirectoryMutation(
                        DirectoryMutationKind.Remove,
                        fileReference,
                        parentFileReference,
                        name,
                        false));

                offset += recordLength;
                continue;
            }

            var isUpsert =
                (reason & UsnReasonFileCreate) != 0 ||
                (reason & UsnReasonRenameNewName) != 0 ||
                (reason & UsnReasonBasicInfoChange) != 0 ||
                (reason & UsnReasonReparsePointChange) != 0;

            if (isUpsert)
            {
                var isReparsePoint =
                    (fileAttributes & FileAttributeReparsePoint) != 0;

                mutations.Add(
                    new DirectoryMutation(
                        DirectoryMutationKind.Upsert,
                        fileReference,
                        parentFileReference,
                        name,
                        isReparsePoint));

                offset += recordLength;
                continue;
            }

            if ((reason & UsnReasonRenameOldName) != 0)
            {
                mutations.Add(
                    new DirectoryMutation(
                        DirectoryMutationKind.Remove,
                        fileReference,
                        parentFileReference,
                        name,
                        false));
            }

            offset += recordLength;
        }

        return recordsRead;
    }

    private static void ValidateCheckpoint(
        SafeFileHandle volume,
        UsnJournalCheckpoint checkpoint)
    {
        var current = QueryJournal(volume);

        if (current.JournalId != checkpoint.JournalId)
        {
            throw new UsnJournalResetException(
                "The USN journal identifier changed and the index " +
                "must be rebuilt.");
        }

        if (checkpoint.NextUsn < current.FirstUsn)
        {
            throw new UsnJournalResetException(
                "The saved USN is no longer present in the journal " +
                "and the index must be rebuilt.");
        }

        if (checkpoint.NextUsn > current.NextUsn)
        {
            throw new UsnJournalResetException(
                "The saved USN is ahead of the current journal and " +
                "the index must be rebuilt.");
        }
    }

    private static JournalInformation QueryJournal(
        SafeFileHandle volume)
    {
        // USN_JOURNAL_DATA_V1 begins with:
        // JournalId, FirstUsn, NextUsn, LowestValidUsn, MaxUsn,
        // MaximumSize and AllocationDelta.
        var output = new byte[80];

        var success = DeviceIoControl(
            volume,
            FsctlQueryUsnJournal,
            IntPtr.Zero,
            0,
            output,
            output.Length,
            out var bytesReturned,
            IntPtr.Zero);

        if (!success)
        {
            ThrowJournalReadError(Marshal.GetLastWin32Error());
        }

        if (bytesReturned < 56)
        {
            throw new InvalidDataException(
                "FSCTL_QUERY_USN_JOURNAL returned an incomplete " +
                "journal data structure.");
        }

        return new JournalInformation(
            BinaryPrimitives.ReadUInt64LittleEndian(
                output.AsSpan(0, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(
                output.AsSpan(8, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(
                output.AsSpan(16, 8)));
    }

    private static SafeFileHandle OpenVolume(string root)
    {
        var normalizedRoot = Path.GetPathRoot(root);

        if (string.IsNullOrWhiteSpace(normalizedRoot))
        {
            throw new ArgumentException(
                "Invalid root path.",
                nameof(root));
        }

        var volumePath = $@"\\.\{normalizedRoot.TrimEnd('\\')}";

        var volume = CreateFileW(
            volumePath,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (volume.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            volume.Dispose();

            throw new Win32Exception(
                error,
                $"Unable to open volume {volumePath}.");
        }

        return volume;
    }

    private static void ThrowJournalReadError(int error)
    {
        var exception = new Win32Exception(error);

        if (error == ErrorJournalDeleteInProgress ||
            error == ErrorJournalNotActive ||
            error == ErrorJournalEntryDeleted)
        {
            throw new UsnJournalResetException(
                "The USN journal is unavailable or no longer " +
                "contains the requested checkpoint.",
                exception);
        }

        throw exception;
    }

    private readonly record struct JournalInformation(
        ulong JournalId,
        long FirstUsn,
        long NextUsn);

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
        EntryPoint = "DeviceIoControl",
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

    [DllImport(
        "kernel32.dll",
        EntryPoint = "DeviceIoControl",
        SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inputBuffer,
        int inputBufferSize,
        byte[] outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);
}
