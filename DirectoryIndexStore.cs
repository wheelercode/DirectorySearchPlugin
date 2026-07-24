using System.IO;
using System.Text;

namespace Wheelercode.DirectorySearchPlugin;

public static class DirectoryIndexStore
{
    private static readonly string IndexPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "Wheelercode",
        "DirectorySearchPlugin",
        "directory-index.tsv");

    public static bool TryLoad(
        out Dictionary<string, List<string>> pathsByName)
    {
        pathsByName = new Dictionary<string, List<string>>(
            StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(IndexPath))
        {
            return false;
        }

        foreach (var line in File.ReadLines(IndexPath, Encoding.UTF8))
        {
            var separator = line.IndexOf('\t');

            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator];
            var path = line[(separator + 1)..];

            if (!pathsByName.TryGetValue(name, out var paths))
            {
                paths = [];
                pathsByName[name] = paths;
            }

            paths.Add(path);
        }

        return true;
    }

    public static void Save(
        Dictionary<string, List<string>> pathsByName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IndexPath)!);

        var temporaryPath = IndexPath + ".tmp";

        using (var writer = new StreamWriter(
            temporaryPath,
            false,
            Encoding.UTF8))
        {
            foreach (var entry in pathsByName)
            {
                foreach (var path in entry.Value)
                {
                    writer.Write(entry.Key);
                    writer.Write('\t');
                    writer.WriteLine(path);
                }
            }
        }

        File.Move(temporaryPath, IndexPath, true);
    }
}