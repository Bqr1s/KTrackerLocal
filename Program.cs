using KTracker.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;

namespace KTracker;

internal static class Program
{
    [STAThread]
    private static async Task Main(string[] args)
    {
        if (args.Any(a => a.Equals("--mcp", StringComparison.OrdinalIgnoreCase)))
        {
            await RunMcpServerAsync();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(ParseOpenTaskId(args)));
    }

    private static string? ParseOpenTaskId(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--task=", StringComparison.OrdinalIgnoreCase))
            {
                var id = arg["--task=".Length..];
                return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
            }

            if (!arg.Equals("--task", StringComparison.OrdinalIgnoreCase)
                && !arg.Equals("-t", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                return args[i + 1].Trim();
            }
        }

        return null;
    }

    private static async Task RunMcpServerAsync()
    {
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();
        var app = builder.Build();
        await app.RunAsync();
    }
}
