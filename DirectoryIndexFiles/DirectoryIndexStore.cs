using System.Text;
using System.Text.Json;
using System.IO;

namespace Wheelercode.DirectorySearchPlugin;

public enum DirectoryIndexUpdateKind
{
    Upsert,
    Remove,
}

public sealed record DirectoryIndexUpdateDraft(
    DirectoryIndexUpdateKind Kind,
    string Path);

public sealed record DirectoryIndexUpdate(
    long Sequence,
    DirectoryIndexUpdateKind Kind,
    string Path);

public sealed record DirectoryIndexManifest(
    string Generation,
    string SnapshotFileName,
    string UpdatesFileName,
    long BaseUsn);

public sealed record DirectoryIndexState(
    string Generation,
    Dictionary<string, List<string>> PathsByName,
    long LastSequence);

public static class DirectoryIndexStore
{
    private const string ManifestFileName = "current-index.json";

    private static readonly UTF8Encoding Utf8WithoutBom =
        new(false);

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData),
        "Wheelercode",
        "DirectorySearchPlugin");

    public static string ManifestPath =>
        Path.Combine(DataDirectory, ManifestFileName);

    public static DirectoryIndexManifest PublishSnapshot(
        Dictionary<string, List<string>> pathsByName,
        long baseUsn)
    {
        Directory.CreateDirectory(DataDirectory);

        var generation = Guid.NewGuid().ToString("N");
        var snapshotFileName =
            $"directory-index-{generation}.jsonl";
        var updatesFileName =
            $"directory-updates-{generation}.jsonl";

        var snapshotPath = Path.Combine(
            DataDirectory,
            snapshotFileName);

        var updatesPath = Path.Combine(
            DataDirectory,
            updatesFileName);

        WriteSnapshot(snapshotPath, pathsByName);
        WriteEmptyFile(updatesPath);

        var manifest = new DirectoryIndexManifest(
            generation,
            snapshotFileName,
            updatesFileName,
            baseUsn);

        WriteManifest(manifest);
        DeleteOlderGenerations(manifest);
        return manifest;
    }

    public static DirectoryIndexUpdateWriter CreateUpdateWriter(
        DirectoryIndexManifest manifest)
    {
        var updatesPath = Path.Combine(
            DataDirectory,
            manifest.UpdatesFileName);

        return new DirectoryIndexUpdateWriter(
            updatesPath,
            JsonOptions);
    }

    public static bool TryLoad(out DirectoryIndexState state)
    {
        state = new DirectoryIndexState(
            string.Empty,
            new Dictionary<string, List<string>>(
                StringComparer.OrdinalIgnoreCase),
            0);

        try
        {
            var manifest = ReadManifest();

            if (manifest is null)
            {
                return false;
            }

            var pathsByName = ReadSnapshot(manifest);
            var updates = ReadUpdates(manifest, 0);

            foreach (var update in updates)
            {
                ApplyUpdate(pathsByName, update);
            }

            state = new DirectoryIndexState(
                manifest.Generation,
                pathsByName,
                updates.Count == 0
                    ? 0
                    : updates[^1].Sequence);

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryReadUpdates(
        string generation,
        long afterSequence,
        out List<DirectoryIndexUpdate> updates)
    {
        updates = [];

        try
        {
            var manifest = ReadManifest();

            if (manifest is null ||
                !manifest.Generation.Equals(
                    generation,
                    StringComparison.Ordinal))
            {
                return false;
            }

            updates = ReadUpdates(
                manifest,
                afterSequence);

            return true;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static void WriteSnapshot(
        string snapshotPath,
        Dictionary<string, List<string>> pathsByName)
    {
        var temporaryPath = snapshotPath + ".tmp";

        using (var writer = new StreamWriter(
                   temporaryPath,
                   false,
                   Utf8WithoutBom))
        {
            foreach (var entry in pathsByName)
            {
                foreach (var path in entry.Value)
                {
                    writer.WriteLine(
                        JsonSerializer.Serialize(
                            path,
                            JsonOptions));
                }
            }
        }

        File.Move(temporaryPath, snapshotPath, true);
    }

    private static void WriteEmptyFile(string path)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            string.Empty,
            Utf8WithoutBom);

        File.Move(temporaryPath, path, true);
    }

    private static void WriteManifest(
        DirectoryIndexManifest manifest)
    {
        var temporaryPath = ManifestPath + ".tmp";
        var json = JsonSerializer.Serialize(
            manifest,
            JsonOptions);

        File.WriteAllText(
            temporaryPath,
            json,
            Utf8WithoutBom);

        File.Move(temporaryPath, ManifestPath, true);
    }

    private static void DeleteOlderGenerations(
        DirectoryIndexManifest current)
    {
        foreach (var path in Directory.EnumerateFiles(
                     DataDirectory,
                     "directory-*.jsonl",
                     SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);

            if (fileName.Equals(
                    current.SnapshotFileName,
                    StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals(
                    current.UpdatesFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A plugin may still be finishing a read of the old
                // generation. It can be cleaned up at the next publish.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep indexing even if an obsolete file cannot be removed.
            }
        }
    }

    private static DirectoryIndexManifest? ReadManifest()
    {
        if (!File.Exists(ManifestPath))
        {
            return null;
        }

        using var stream = new FileStream(
            ManifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        return JsonSerializer.Deserialize<DirectoryIndexManifest>(
            stream,
            JsonOptions);
    }

    private static Dictionary<string, List<string>> ReadSnapshot(
        DirectoryIndexManifest manifest)
    {
        var pathsByName = new Dictionary<string, List<string>>(
            StringComparer.OrdinalIgnoreCase);

        var snapshotPath = Path.Combine(
            DataDirectory,
            manifest.SnapshotFileName);

        using var stream = new FileStream(
            snapshotPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            true);

        while (reader.ReadLine() is { } line)
        {
            var path = JsonSerializer.Deserialize<string>(
                line,
                JsonOptions);

            if (!string.IsNullOrWhiteSpace(path))
            {
                AddPath(pathsByName, path);
            }
        }

        return pathsByName;
    }

    private static List<DirectoryIndexUpdate> ReadUpdates(
        DirectoryIndexManifest manifest,
        long afterSequence)
    {
        var updates = new List<DirectoryIndexUpdate>();
        var updatesPath = Path.Combine(
            DataDirectory,
            manifest.UpdatesFileName);

        using var stream = new FileStream(
            updatesPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            true);

        while (reader.ReadLine() is { } line)
        {
            DirectoryIndexUpdate? update;

            try
            {
                update =
                    JsonSerializer.Deserialize<DirectoryIndexUpdate>(
                        line,
                        JsonOptions);
            }
            catch (JsonException)
            {
                // The service may still be flushing the final line.
                continue;
            }

            if (update is not null &&
                update.Sequence > afterSequence)
            {
                updates.Add(update);
            }
        }

        updates.Sort(
            static (left, right) =>
                left.Sequence.CompareTo(right.Sequence));

        return updates;
    }

    private static void ApplyUpdate(
        Dictionary<string, List<string>> pathsByName,
        DirectoryIndexUpdate update)
    {
        if (update.Kind == DirectoryIndexUpdateKind.Upsert)
        {
            AddPath(pathsByName, update.Path);
            return;
        }

        var name = Path.GetFileName(update.Path);

        if (string.IsNullOrWhiteSpace(name) ||
            !pathsByName.TryGetValue(name, out var paths))
        {
            return;
        }

        paths.RemoveAll(
            path => path.Equals(
                update.Path,
                StringComparison.OrdinalIgnoreCase));

        if (paths.Count == 0)
        {
            pathsByName.Remove(name);
        }
    }

    private static void AddPath(
        Dictionary<string, List<string>> pathsByName,
        string path)
    {
        var name = Path.GetFileName(path);

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (!pathsByName.TryGetValue(name, out var paths))
        {
            paths = [];
            pathsByName[name] = paths;
        }

        if (!paths.Any(
                existing => existing.Equals(
                    path,
                    StringComparison.OrdinalIgnoreCase)))
        {
            paths.Add(path);
        }
    }
}

public sealed class DirectoryIndexUpdateWriter : IDisposable
{
    private readonly JsonSerializerOptions jsonOptions;
    private readonly FileStream stream;
    private readonly StreamWriter writer;
    private long sequence;

    internal DirectoryIndexUpdateWriter(
        string updatesPath,
        JsonSerializerOptions jsonOptions)
    {
        this.jsonOptions = jsonOptions;

        stream = new FileStream(
            updatesPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);

        writer = new StreamWriter(
            stream,
            new UTF8Encoding(false),
            4_096,
            true);
    }

    public long Append(
        IReadOnlyList<DirectoryIndexUpdateDraft> updates)
    {
        foreach (var update in updates)
        {
            var persistedUpdate = new DirectoryIndexUpdate(
                ++sequence,
                update.Kind,
                update.Path);

            writer.WriteLine(
                JsonSerializer.Serialize(
                    persistedUpdate,
                    jsonOptions));
        }

        if (updates.Count > 0)
        {
            writer.Flush();
            stream.Flush(true);
        }

        return sequence;
    }

    public void Dispose()
    {
        writer.Dispose();
        stream.Dispose();
    }
}
