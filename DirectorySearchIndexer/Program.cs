using System.Security.Principal;

using var identity = WindowsIdentity.GetCurrent();
var principal = new WindowsPrincipal(identity);

Console.WriteLine($"Identity: {identity.Name}");
Console.WriteLine(
    $"Administrator: " +
    $"{principal.IsInRole(WindowsBuiltInRole.Administrator)}");

Console.WriteLine("Press Enter to exit.");
Console.ReadLine();