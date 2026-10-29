namespace WinfoMain.Desktop;

using System.Diagnostics;

/// <summary>
/// Lightweight self-monitoring: CPU% and memory of the current process.
/// Samples every 2 seconds using Process.TotalProcessorTime delta.
/// Singleton — call Start()/Stop(). Read CpuPercent and MemoryMB.
/// </summary>
public sealed class PerformanceMonitor : IDisposable
{
    public static PerformanceMonitor Instance { get; } = new();

    private Timer? _timer;
    private TimeSpan _lastCpuTime;
    private DateTime _lastSampleTime;
    private readonly int _processorCount;

    /// <summary>CPU usage of this process in percent (0-100). Updated every 2s.</summary>
    public double CpuPercent { get; private set; }

    /// <summary>Working set (RAM) in MB. Updated every 2s.</summary>
    public double MemoryMB { get; private set; }

    /// <summary>Fires after each sample with (cpuPercent, memoryMB).</summary>
    public event Action<double, double>? Sampled;

    private PerformanceMonitor()
    {
        _processorCount = Environment.ProcessorCount;
    }

    public void Start()
    {
        if (_timer != null) return;

        var proc = Process.GetCurrentProcess();
        _lastCpuTime = proc.TotalProcessorTime;
        _lastSampleTime = DateTime.UtcNow;

        // Initial values
        MemoryMB = proc.WorkingSet64 / (1024.0 * 1024.0);

        _timer = new Timer(Sample, null, 2000, 2000);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void Sample(object? state)
    {
        try
        {
            var proc = Process.GetCurrentProcess();
            var now = DateTime.UtcNow;
            var cpuTime = proc.TotalProcessorTime;

            var elapsed = (now - _lastSampleTime).TotalMilliseconds;
            if (elapsed > 0)
            {
                var cpuDelta = (cpuTime - _lastCpuTime).TotalMilliseconds;
                CpuPercent = cpuDelta / (elapsed * _processorCount) * 100.0;
            }

            MemoryMB = proc.WorkingSet64 / (1024.0 * 1024.0);
            _lastCpuTime = cpuTime;
            _lastSampleTime = now;

            Sampled?.Invoke(CpuPercent, MemoryMB);
        }
        catch
        {
            // ignore — process might be exiting
        }
    }

    public void Dispose() => Stop();
}
