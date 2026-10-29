using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinfoMain.Common;
using WinfoMain.MCPServer.Services;

// Route the library log to stderr: System.Diagnostics.Debug.WriteLine is removed
// entirely in Release builds, so without a sink, errors from the library would be
// invisible in the published server. stdout belongs to MCP JSON-RPC and must
// not be written to.
WinfoLog.Sink = message => Console.Error.WriteLine(message);

var builder = Host.CreateApplicationBuilder(args);

// Logging: stdout is MCP JSON-RPC — all logs go to stderr
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// Live Desktop Model (STA thread with message pump)
builder.Services.AddHostedService<DesktopModelHostedService>();

// MCP Server: stdio transport, auto-discover tools via reflection
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "winfo",
            Version = "2.0.0",
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
