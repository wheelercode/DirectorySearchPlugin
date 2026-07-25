using System.Diagnostics;
using System.IO;
using Wox.Plugin;

namespace Wheelercode.DirectorySearchPlugin;

public sealed class Main : IPlugin, IDisposable
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "Wheelercode",
        "DirectorySearchPlugin",
        "DirectorySearchPlugin.log");

    private readonly object refreshLock = new();
    private volatile DirectoryIndex? index;
    private volatile bool isIndexing;
    private FileSystemWatcher? indexWatcher;
    private Timer? refreshTimer;
    private string? generation;
    private long lastSequence;
    private int refreshQueued;

    public static string PluginID =>
        "B8B9C5B7A3A44F1A9D4E5C7D8E9F0012";

    public string Name => "Directory Search";

    public string Description =>
        "Search directory names and open them in File Explorer.";

    private PluginInitContext? context;
    private string lastSearch = string.Empty;

    public void Init(PluginInitContext context)
    {
        this.context = context;
        isIndexing = true;

        EnsureIndexWatcher();
        RefreshIndexFromFiles();

        refreshTimer = new Timer(
            _ => RefreshIndexFromFiles(),
            null,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2));
    }

    private void RefreshIndexFromFiles()
    {
        lock (refreshLock)
        {
            try
            {
                EnsureIndexWatcher();

                var currentIndex = index;
                var currentGeneration = generation;

                if (currentIndex is null ||
                    string.IsNullOrWhiteSpace(currentGeneration))
                {
                    TryLoadFullIndex();
                    return;
                }

                if (!DirectoryIndexStore.TryReadUpdates(
                        currentGeneration,
                        lastSequence,
                        out var updates))
                {
                    TryLoadFullIndex();
                    return;
                }

                if (updates.Count == 0)
                {
                    return;
                }

                currentIndex.Apply(updates);
                lastSequence = updates[^1].Sequence;
                isIndexing = false;

                Log(
                    $"Applied {updates.Count:N0} directory updates; " +
                    $"sequence={lastSequence:N0}.");

                RefreshCurrentQuery();
            }
            catch (Exception exception)
            {
                Log($"Directory index refresh failed: {exception}");
            }
        }
    }

    private void TryLoadFullIndex()
    {
        if (!DirectoryIndexStore.TryLoad(out var state))
        {
            isIndexing = true;
            return;
        }

        var newIndex = new DirectoryIndex(state.PathsByName);

        index = newIndex;
        generation = state.Generation;
        lastSequence = state.LastSequence;
        isIndexing = false;

        Log(
            $"Loaded directory index generation " +
            $"{generation}; unique names: " +
            $"{state.PathsByName.Count:N0}; sequence=" +
            $"{lastSequence:N0}.");

        RefreshCurrentQuery();
    }

    private void EnsureIndexWatcher()
    {
        if (indexWatcher is not null ||
            !Directory.Exists(
                DirectoryIndexStore.DataDirectory))
        {
            return;
        }

        indexWatcher = new FileSystemWatcher(
            DirectoryIndexStore.DataDirectory)
        {
            Filter = "*",
            NotifyFilter =
                NotifyFilters.FileName |
                NotifyFilters.LastWrite |
                NotifyFilters.Size,
            EnableRaisingEvents = true,
        };

        indexWatcher.Changed += IndexFilesChanged;
        indexWatcher.Created += IndexFilesChanged;
        indexWatcher.Deleted += IndexFilesChanged;
        indexWatcher.Renamed += IndexFilesRenamed;
        indexWatcher.Error += IndexWatcherError;
    }

    private void IndexFilesChanged(
        object sender,
        FileSystemEventArgs eventArgs)
    {
        QueueIndexRefresh();
    }

    private void IndexFilesRenamed(
        object sender,
        RenamedEventArgs eventArgs)
    {
        QueueIndexRefresh();
    }

    private void IndexWatcherError(
        object sender,
        ErrorEventArgs eventArgs)
    {
        QueueIndexRefresh();
    }

    private void QueueIndexRefresh()
    {
        if (Interlocked.Exchange(ref refreshQueued, 1) != 0)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(
            _ =>
            {
                try
                {
                    RefreshIndexFromFiles();
                }
                finally
                {
                    Volatile.Write(ref refreshQueued, 0);
                }
            });
    }

    private void RefreshCurrentQuery()
    {
        var search = lastSearch;

        if (!string.IsNullOrWhiteSpace(search))
        {
            this.context?.API.ChangeQuery(
                $@"\\{search}",
                true);
        }
    }

    public List<Result> Query(Query query)
    {
        var searchText = query.Search?.Trim();
        lastSearch = searchText ?? string.Empty;

        if (string.IsNullOrWhiteSpace(searchText))
        {
            return [];
        }

        var currentIndex = index;

        if (currentIndex is null)
        {
            return
            [
                new Result
                {
                    Title = isIndexing
                        ? "Directory index is still initializing"
                        : "Directory index could not be initialized",
                    SubTitle = isIndexing
                        ? "Please try again shortly."
                        : "See DirectorySearchPlugin.log for details.",
                    Score = 10_000,
                },
            ];
        }

        var results = new List<Result>();

        var rankedMatches = currentIndex
            .Search(searchText)
            .Select(path => new
            {
                Path = path,
                Name = Path.GetFileName(path),
            })
            .Select(match => new
            {
                match.Path,
                match.Name,
                Score = ScoreMatch(
                    match.Name,
                    match.Path,
                    searchText),
            })
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Name.Length)
            .ThenBy(match => match.Path.Length)
            .Take(50);

        foreach (var match in rankedMatches)
        {
            results.Add(
                new Result
                {
                    Title = match.Name,
                    SubTitle = match.Path,
                    Score = match.Score,
                    Action = _ =>
                    {
                        Process.Start(
                            new ProcessStartInfo
                            {
                                FileName = "explorer.exe",
                                Arguments = $"\"{match.Path}\"",
                                UseShellExecute = true,
                            });

                        return true;
                    },
                });
        }

        return results;
    }

    private static int ScoreMatch(
        string name,
        string path,
        string query)
    {
        var comparison = StringComparison.OrdinalIgnoreCase;
        var score = 0;

        if (name.Equals(query, comparison))
        {
            score = 10_000;
        }
        else if (name.StartsWith(query, comparison))
        {
            score = 8_000;
        }
        else
        {
            var position = name.IndexOf(query, comparison);

            if (position < 0)
            {
                return 0;
            }

            var startsAtWordBoundary =
                position == 0 ||
                !char.IsLetterOrDigit(name[position - 1]);

            score = startsAtWordBoundary
                ? 6_000
                : 3_000;

            score -= position * 10;
        }

        if (path.StartsWith(
                @"C:\Windows\",
                comparison))
        {
            score -= 1_000;
        }
        else if (path.StartsWith(
                     @"C:\Program Files\",
                     comparison))
        {
            score -= 500;
        }

        return score;
    }

    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(LogPath)!);

            File.AppendAllText(
                LogPath,
                $"{DateTime.Now:O} {message}" +
                $"{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break PowerToys Run.
        }
    }

    public void Dispose()
    {
        refreshTimer?.Dispose();
        indexWatcher?.Dispose();
        index?.Dispose();
    }
}
