using System.Collections.Generic;

namespace Wheelercode.DirectorySearchPlugin;

public sealed class DirectoryIndex
{
    private readonly Dictionary<string, List<string>> pathsByName;

    public DirectoryIndex(Dictionary<string, List<string>> pathsByName)
    {
        this.pathsByName = pathsByName;
    }

    public IEnumerable<string> Search(string text)
    {
        var searchText = text.Trim();

        if (searchText.Length == 0)
        {
            yield break;
        }

        foreach (var entry in pathsByName)
        {
            if (!entry.Key.Contains(
                    searchText,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var path in entry.Value)
            {
                yield return path;
            }
        }
    }
}