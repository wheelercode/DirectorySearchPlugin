using System.Threading;

namespace Wheelercode.DirectorySearchPlugin;

internal enum DirectoryMutationKind
{
    Upsert,
    Remove,
}

internal sealed record DirectoryMutation(
    DirectoryMutationKind Kind,
    ulong FileReference,
    ulong ParentFileReference,
    string Name,
    bool IsReparsePoint);

internal readonly record struct DirectoryMutationSummary(
    int Upserted,
    int Removed)
{
    internal int Total => Upserted + Removed;
}

internal readonly record struct DirectoryMutationResult(
    DirectoryMutationSummary Summary,
    IReadOnlyList<DirectoryIndexUpdateDraft> Updates);

internal sealed class LiveDirectoryIndex : IDisposable
{
    private readonly ReaderWriterLockSlim indexLock =
        new(LockRecursionPolicy.NoRecursion);

    private readonly Dictionary<ulong, DirectoryNode> nodesByReference;

    private readonly Dictionary<string, HashSet<ulong>> referencesByName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<ulong, HashSet<ulong>> childrenByParent =
        [];

    private readonly string root;

    internal LiveDirectoryIndex(MftDirectorySnapshot snapshot)
    {
        root = snapshot.Root;
        nodesByReference = snapshot.Nodes;

        foreach (var node in nodesByReference.Values)
        {
            AddNameReferenceNoLock(node);
            AddChildReferenceNoLock(node);
        }
    }

    internal int DirectoryCount
    {
        get
        {
            indexLock.EnterReadLock();

            try
            {
                return nodesByReference.Count;
            }
            finally
            {
                indexLock.ExitReadLock();
            }
        }
    }

    internal int UniqueNameCount
    {
        get
        {
            indexLock.EnterReadLock();

            try
            {
                return referencesByName.Count;
            }
            finally
            {
                indexLock.ExitReadLock();
            }
        }
    }

    internal DirectoryMutationResult Apply(
        IReadOnlyList<DirectoryMutation> mutations)
    {
        if (mutations.Count == 0)
        {
            return new DirectoryMutationResult(
                default,
                []);
        }

        var upserted = 0;
        var removed = 0;
        var updates = new List<DirectoryIndexUpdateDraft>();

        indexLock.EnterWriteLock();

        try
        {
            var affectedReferences = mutations
                .Select(mutation => mutation.FileReference)
                .Distinct()
                .ToArray();

            var oldPaths = CaptureSubtreePathsNoLock(
                affectedReferences);

            foreach (var mutation in mutations)
            {
                if (mutation.Kind == DirectoryMutationKind.Remove)
                {
                    if (RemoveNoLock(mutation.FileReference))
                    {
                        removed++;
                    }

                    continue;
                }

                UpsertNoLock(
                    new DirectoryNode(
                        mutation.FileReference,
                        mutation.ParentFileReference,
                        mutation.Name,
                        mutation.IsReparsePoint));

                upserted++;
            }

            var newPaths = CaptureSubtreePathsNoLock(
                affectedReferences);

            foreach (var path in oldPaths.Except(
                         newPaths,
                         StringComparer.Ordinal))
            {
                updates.Add(
                    new DirectoryIndexUpdateDraft(
                        DirectoryIndexUpdateKind.Remove,
                        path));
            }

            foreach (var path in newPaths.Except(
                         oldPaths,
                         StringComparer.Ordinal))
            {
                updates.Add(
                    new DirectoryIndexUpdateDraft(
                        DirectoryIndexUpdateKind.Upsert,
                        path));
            }
        }
        finally
        {
            indexLock.ExitWriteLock();
        }

        return new DirectoryMutationResult(
            new DirectoryMutationSummary(upserted, removed),
            updates);
    }

    internal List<string> Search(
        string text,
        int maximumResults)
    {
        var searchText = text.Trim();

        if (searchText.Length == 0 || maximumResults <= 0)
        {
            return [];
        }

        var results = new List<string>();
        var pathCache = new Dictionary<ulong, string?>();

        indexLock.EnterReadLock();

        try
        {
            foreach (var entry in referencesByName)
            {
                if (!entry.Key.Contains(
                        searchText,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var fileReference in entry.Value)
                {
                    var path = ResolvePathNoLock(
                        fileReference,
                        pathCache,
                        new HashSet<ulong>());

                    if (path is null)
                    {
                        continue;
                    }

                    results.Add(path);

                    if (results.Count >= maximumResults)
                    {
                        return results;
                    }
                }
            }
        }
        finally
        {
            indexLock.ExitReadLock();
        }

        return results;
    }

    internal Dictionary<string, List<string>> CreatePathSnapshot()
    {
        var pathsByName = new Dictionary<string, List<string>>(
            StringComparer.OrdinalIgnoreCase);

        var pathCache = new Dictionary<ulong, string?>();

        indexLock.EnterReadLock();

        try
        {
            foreach (var entry in referencesByName)
            {
                foreach (var fileReference in entry.Value)
                {
                    var path = ResolvePathNoLock(
                        fileReference,
                        pathCache,
                        new HashSet<ulong>());

                    if (path is null)
                    {
                        continue;
                    }

                    if (!pathsByName.TryGetValue(
                            entry.Key,
                            out var paths))
                    {
                        paths = [];
                        pathsByName[entry.Key] = paths;
                    }

                    paths.Add(path);
                }
            }
        }
        finally
        {
            indexLock.ExitReadLock();
        }

        return pathsByName;
    }

    public void Dispose()
    {
        indexLock.Dispose();
    }

    private void UpsertNoLock(DirectoryNode node)
    {
        if (nodesByReference.TryGetValue(
                node.FileReference,
                out var existing))
        {
            RemoveNameReferenceNoLock(existing);
            RemoveChildReferenceNoLock(existing);
        }

        nodesByReference[node.FileReference] = node;
        AddNameReferenceNoLock(node);
        AddChildReferenceNoLock(node);
    }

    private bool RemoveNoLock(ulong fileReference)
    {
        if (!nodesByReference.Remove(
                fileReference,
                out var existing))
        {
            return false;
        }

        RemoveNameReferenceNoLock(existing);
        RemoveChildReferenceNoLock(existing);
        return true;
    }

    private HashSet<string> CaptureSubtreePathsNoLock(
        IEnumerable<ulong> roots)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<ulong>();
        var pending = new Stack<ulong>(roots);
        var pathCache = new Dictionary<ulong, string?>();

        while (pending.Count > 0)
        {
            var fileReference = pending.Pop();

            if (!visited.Add(fileReference))
            {
                continue;
            }

            if (nodesByReference.TryGetValue(
                    fileReference,
                    out var node) &&
                IsSearchableNoLock(node))
            {
                var path = ResolvePathNoLock(
                    fileReference,
                    pathCache,
                    new HashSet<ulong>());

                if (path is not null)
                {
                    paths.Add(path);
                }
            }

            if (!childrenByParent.TryGetValue(
                    fileReference,
                    out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                pending.Push(child);
            }
        }

        return paths;
    }

    private void AddNameReferenceNoLock(DirectoryNode node)
    {
        if (DirectoryReference.IsRoot(node.FileReference) ||
            node.IsReparsePoint ||
            string.IsNullOrWhiteSpace(node.Name))
        {
            return;
        }

        if (!referencesByName.TryGetValue(
                node.Name,
                out var references))
        {
            references = [];
            referencesByName[node.Name] = references;
        }

        references.Add(node.FileReference);
    }

    private void RemoveNameReferenceNoLock(DirectoryNode node)
    {
        if (!referencesByName.TryGetValue(
                node.Name,
                out var references))
        {
            return;
        }

        references.Remove(node.FileReference);

        if (references.Count == 0)
        {
            referencesByName.Remove(node.Name);
        }
    }

    private void AddChildReferenceNoLock(DirectoryNode node)
    {
        if (DirectoryReference.IsRoot(node.FileReference))
        {
            return;
        }

        if (!childrenByParent.TryGetValue(
                node.ParentFileReference,
                out var children))
        {
            children = [];
            childrenByParent[node.ParentFileReference] = children;
        }

        children.Add(node.FileReference);
    }

    private void RemoveChildReferenceNoLock(DirectoryNode node)
    {
        if (!childrenByParent.TryGetValue(
                node.ParentFileReference,
                out var children))
        {
            return;
        }

        children.Remove(node.FileReference);

        if (children.Count == 0)
        {
            childrenByParent.Remove(node.ParentFileReference);
        }
    }

    private bool IsSearchableNoLock(DirectoryNode node)
    {
        return !DirectoryReference.IsRoot(node.FileReference) &&
            !node.IsReparsePoint &&
            !string.IsNullOrWhiteSpace(node.Name) &&
            referencesByName.TryGetValue(
                node.Name,
                out var references) &&
            references.Contains(node.FileReference);
    }

    private string? ResolvePathNoLock(
        ulong fileReference,
        IDictionary<ulong, string?> cache,
        ISet<ulong> resolving)
    {
        if (cache.TryGetValue(fileReference, out var cached))
        {
            return cached;
        }

        if (DirectoryReference.IsRoot(fileReference))
        {
            cache[fileReference] = root;
            return root;
        }

        if (!nodesByReference.TryGetValue(
                fileReference,
                out var node))
        {
            cache[fileReference] = null;
            return null;
        }

        if (!resolving.Add(fileReference))
        {
            cache[fileReference] = null;
            return null;
        }

        var parentPath = ResolvePathNoLock(
            node.ParentFileReference,
            cache,
            resolving);

        resolving.Remove(fileReference);

        if (parentPath is null)
        {
            cache[fileReference] = null;
            return null;
        }

        var path = Path.Combine(parentPath, node.Name);

        cache[fileReference] = path;
        return path;
    }
}
