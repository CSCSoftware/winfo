namespace WinfoMain.MCPServer.Services;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinfoMain.Desktop;
using WinfoMain.Overlay;

/// <summary>
/// Starts the DesktopModel on a dedicated STA thread with a message pump.
/// Required because SetWinEventHook (WINEVENT_OUTOFCONTEXT) needs:
///   1. STA apartment state
///   2. A running message pump (Application.Run)
///
/// The model starts in sleep mode (near-zero CPU). On first tool call,
/// PipelineHelper.Wake() wakes it. After 5 minutes of inactivity
/// it goes back to sleep automatically.
/// </summary>
public sealed class DesktopModelHostedService : IHostedService, IDisposable
{
    private readonly ILogger<DesktopModelHostedService> _logger;
    private readonly DesktopModel _model = new();
    private readonly FingerOverlay _finger = new();
    private Thread? _staThread;
    private TaskCompletionSource<bool>? _startTcs;

    /// <summary>Access to the DesktopModel for Wake/Sleep from tools.</summary>
    public DesktopModel Model => _model;

    /// <summary>Singleton instance — set on construction, used by PipelineHelper.</summary>
    public static DesktopModelHostedService? Instance { get; private set; }

    public DesktopModelHostedService(ILogger<DesktopModelHostedService> logger)
    {
        _logger = logger;
        Instance = this;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _startTcs = new TaskCompletionSource<bool>();

        _staThread = new Thread(() =>
        {
            try
            {
                _model.KeepLastCapture = false; // MCP Server doesn't need debug bitmaps
                _model.AutoSleepTimeout = TimeSpan.FromMinutes(5);
                _model.Start();
                _model.Sleep(); // Start in sleep mode — wake on first tool call
                _finger.Initialize();
                _logger.LogInformation("DesktopModel started (sleeping, auto-sleep 5min) + FingerOverlay on STA thread");
                _startTcs.TrySetResult(true);

                // Message pump — required for SetWinEventHook callbacks
                System.Windows.Forms.Application.Run();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DesktopModel STA thread failed");
                _startTcs.TrySetException(ex);
            }
        });

        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.IsBackground = true;
        _staThread.Name = "DesktopModel-STA";
        _staThread.Start();

        return _startTcs.Task;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _model.Stop();
        _finger.Dispose();
        System.Windows.Forms.Application.ExitThread();
        _logger.LogInformation("DesktopModel stopped");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _model.Stop();
        _finger.Dispose();
        Instance = null;
    }
}
