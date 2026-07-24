using System.Diagnostics;
using System.IO;
using Wox.Plugin;

namespace Wheelercode.DirectorySearchPlugin;

public sealed class Main : IPlugin
{
    //private static readonly string LogPath =
    //    Path.Combine(Path.GetTempPath(), "DirectorySearchPlugin.log");

    private static readonly string LogPath =
    @"C:\Users\wheel\Documents\code\C#\DirectorySearchPlugin\DirectorySearchPlugin.log";

    private DirectoryIndex? index;
    private volatile bool isIndexing;

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

        if (DirectoryIndexStore.TryLoad(out var pathsByName))
        {
            index = new DirectoryIndex(pathsByName);
            isIndexing = false;

            Log("Loaded persisted directory index.");
            return;
        }

        isIndexing = true;
        Log("No persisted index found; benchmarking index builders.");
        _ = Task.Run(() => BenchmarkIndexInitialization(@"C:\"));
    }

    private void InitializeIndexMft(string root)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            Log("Starting MFT directory enumeration.");

            var pathsByName = MftDirectoryEnumerator.Enumerate(root);

            DirectoryIndexStore.Save(pathsByName);
            index = new DirectoryIndex(pathsByName);
            isIndexing = false;

            Log(
                $"MFT index complete. " +
                $"Unique names: {pathsByName.Count:N0}; " +
                $"Elapsed: {stopwatch.Elapsed}");

            var search = lastSearch;

            if (!string.IsNullOrWhiteSpace(search))
            {
                context?.API.ChangeQuery($"dir:{search}", true);
            }
        }
        catch (Exception ex)
        {
            Log($"MFT index failed: {ex}");
        }
        finally
        {
            isIndexing = false;
        }
    }

    private void InitializeIndexDirectoryScan(string root)
    {
        try
        {
            //var pathsByName = BuildDirectoryScanIndex(root);

            //DirectoryIndexStore.Save(pathsByName);
            //index = new DirectoryIndex(pathsByName);
            //isIndexing = false;

            //Log($"Directory index complete. Unique names: {pathsByName.Count:N0}");

            var search = lastSearch;

            if (!string.IsNullOrWhiteSpace(search))
            {
                this.context?.API.ChangeQuery($"dir:{search}", true);
            }
        }
        catch (Exception ex)
        {
            Log($"Directory index failed: {ex}");
        }
        finally
        {
            isIndexing = false;
        }
    }

    private Dictionary<string, List<string>> BuildDirectoryScanIndex(string root)
    {
        var pathsByName = new Dictionary<string, List<string>>(
            StringComparer.OrdinalIgnoreCase);

        var pending = new Stack<string>();
        pending.Push(root);

        var directoryCount = 0;
        var errorCount = 0;

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            directoryCount++;

            try
            {
                foreach (var path in Directory.EnumerateDirectories(
                    current,
                    "*",
                    SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var attributes = File.GetAttributes(path);

                        if ((attributes & FileAttributes.ReparsePoint) != 0)
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
                        pending.Push(path);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        errorCount++;
                    }
                    catch (IOException)
                    {
                        errorCount++;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                errorCount++;
            }
            catch (IOException)
            {
                errorCount++;
            }

            if (directoryCount % 10_000 == 0)
            {
                Log(
                    $"Directory scan progress: {directoryCount:N0}; " +
                    $"unique names: {pathsByName.Count:N0}; " +
                    $"errors: {errorCount:N0}");
            }
        }

        Log(
            $"Directory scan enumeration complete. " +
            $"Directories: {directoryCount:N0}; " +
            $"unique names: {pathsByName.Count:N0}; " +
            $"errors: {errorCount:N0}");

        return pathsByName;
    }

    private Dictionary<string, List<string>> BuildMftIndex(string root)
    {
        return MftDirectoryEnumerator.Enumerate(root);
    }

    private void BenchmarkIndexInitialization(string root)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            //stopwatch.Restart();

            //Log("Benchmark: starting recursive directory scan.");
            //var directoryScanIndex = BuildDirectoryScanIndex(root);
            //var directoryScanElapsed = stopwatch.Elapsed;

            //Log(
            //    $"Directory scan benchmark: " +
            //    $"Unique names: {directoryScanIndex.Count:N0}; " +
            //    $"Elapsed: {directoryScanElapsed}");

            Log("Benchmark: starting MFT enumeration.");
            stopwatch.Restart();
            var mftIndex = BuildMftIndex(root);
            var mftElapsed = stopwatch.Elapsed;

            Log(
                $"MFT enumeration benchmark: " +
                $"Unique names: {mftIndex.Count:N0}; " +
                $"Elapsed: {mftElapsed}");

            //if (mftElapsed > TimeSpan.Zero)
            //{
            //    Log(
            //        $"MFT speedup: " +
            //        $"{directoryScanElapsed.TotalMilliseconds / mftElapsed.TotalMilliseconds:F2}x");
            //}

            // Use the MFT result as the active index.
            DirectoryIndexStore.Save(mftIndex);
            index = new DirectoryIndex(mftIndex);
            isIndexing = false;

            var search = lastSearch;

            if (!string.IsNullOrWhiteSpace(search))
            {
                context?.API.ChangeQuery($"dir:{search}", true);
            }
        }
        catch (Exception ex)
        {
            Log($"Index benchmark failed: {ex}");
        }
        finally
        {
            isIndexing = false;
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

        if (isIndexing || currentIndex is null)
        {
            return
            [
                new Result
                {
                    Title = "Directory index is still initializing",
                    SubTitle = "Please try again shortly.",
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
        File.AppendAllText(
            LogPath,
            $"{DateTime.Now:O} {message}{Environment.NewLine}");
    }
}