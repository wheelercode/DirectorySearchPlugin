using System.IO;

namespace Wheelercode.DirectorySearchPlugin;

public sealed class DirectoryWatcher : IDisposable
{
    private readonly FileSystemWatcher watcher;

    public event Action<string>? DirectoryChanged;

    public DirectoryWatcher(string root)
    {
        watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.DirectoryName,
            InternalBufferSize = 64 * 1024,
            Filter = "*",
            EnableRaisingEvents = true,
        };

        watcher.Created += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnError;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        DirectoryChanged?.Invoke(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        DirectoryChanged?.Invoke(e.OldFullPath);
        DirectoryChanged?.Invoke(e.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow means events may have been lost.
        // We will handle this by rebuilding the index later.
        DirectoryChanged?.Invoke(string.Empty);
    }

    public void Dispose()
    {
        watcher.Dispose();
    }
}