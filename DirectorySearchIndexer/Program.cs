using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wheelercode.DirectorySearchPlugin;

if (args.Any(
        argument => argument.Equals(
            "--self-test",
            StringComparison.OrdinalIgnoreCase)))
{
    LiveDirectoryIndexSelfTest.Run();
    return;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(
    options =>
    {
        options.ServiceName =
            DirectoryIndexWorker.ServiceName;
    });

builder.Services.AddHostedService<DirectoryIndexWorker>();

using var host = builder.Build();
await host.RunAsync();
