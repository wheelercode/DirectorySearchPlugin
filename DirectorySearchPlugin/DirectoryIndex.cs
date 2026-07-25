using System.Threading;
using System.IO;

namespace Wheelercode.DirectorySearchPlugin;

public sealed class DirectoryIndex : IDisposable
{
    private readonly ReaderWriterLockSlim indexLock =
        new(LockRecursionPolicy.NoRecursion);

    private readonly Dictionary<string, HashSet<string>> pathsByName;

    public DirectoryIndex(
        Dictionary<string, List<string>> initialPathsByName)
    {
        pathsByName = new Dictionary<string, HashSet<string>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in initialPathsByName)
        {
            pathsByName[entry.Key] = new HashSet<string>(
                entry.Value,
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public List<string> Search(string text)
    {
        var searchText = text.Trim();

        if (searchText.Length == 0)
        {
            return [];
        }

        var results = new List<string>();

        indexLock.EnterReadLock();

        try
        {
            foreach (var entry in pathsByName)
            {
                if (!entry.Key.Contains(
                        searchText,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.AddRange(entry.Value);
            }
        }
        finally
        {
            indexLock.ExitReadLock();
        }

        return results;
    }

    public void Apply(
        IReadOnlyList<DirectoryIndexUpdate> updates)
    {
        if (updates.Count == 0)
        {
            return;
        }

        indexLock.EnterWriteLock();

        try
        {
            foreach (var update in updates)
            {
                if (update.Kind ==
                    DirectoryIndexUpdateKind.Remove)
                {
                    RemoveNoLock(update.Path);
                }
                else
                {
                    UpsertNoLock(update.Path);
                }
            }
        }
        finally
        {
            indexLock.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        indexLock.Dispose();
    }

    private void UpsertNoLock(string path)
    {
        var name = Path.GetFileName(path);

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (!pathsByName.TryGetValue(name, out var paths))
        {
            paths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            pathsByName[name] = paths;
        }

        paths.Add(path);
    }

    private void RemoveNoLock(string path)
    {
        var name = Path.GetFileName(path);

        if (string.IsNullOrWhiteSpace(name) ||
            !pathsByName.TryGetValue(name, out var paths))
        {
            return;
        }

        paths.Remove(path);

        if (paths.Count == 0)
        {
            pathsByName.Remove(name);
        }
    }
}
