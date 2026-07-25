using Wheelercode.DirectorySearchPlugin;
using System.Diagnostics;

var stopwatch = Stopwatch.StartNew();

try
{
    Console.WriteLine("Starting MFT directory enumeration...");

    var pathsByName =
    MftDirectoryEnumerator.Enumerate(@"C:\");

    DirectoryIndexStore.Save(pathsByName);

    Console.WriteLine("Directory index saved.");

    Console.WriteLine(
            $"MFT enumeration complete. " +
        $"Unique directory names: {pathsByName.Count:N0}; " +
        $"Elapsed: {stopwatch.Elapsed}");
}
catch (Exception ex)
{
    Console.WriteLine($"MFT enumeration failed: {ex}");
}

Console.WriteLine("Press Enter to exit.");
Console.ReadLine();