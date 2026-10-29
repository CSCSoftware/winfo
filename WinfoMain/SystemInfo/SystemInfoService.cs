namespace WinfoMain.SystemInfo;

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

/// <summary>
/// Service for system knowledge: installed apps, running processes, system info.
/// Singleton with cached installed apps (5 min TTL).
/// </summary>
public class SystemInfoService
{
    public static SystemInfoService Instance { get; } = new();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private List<InstalledApp>? _cachedApps;
    private DateTime _cacheTime = DateTime.MinValue;
    private readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(5);

    private SystemInfoService() { }

    /// <summary>
    /// Get all installed applications from the Windows Registry.
    /// Results are cached for 5 minutes.
    /// </summary>
    public List<InstalledApp> GetInstalledApps(string? filter = null)
    {
        if (_cachedApps == null || (DateTime.UtcNow - _cacheTime) > _cacheTtl)
        {
            _cachedApps = ScanRegistry();
            _cacheTime = DateTime.UtcNow;
        }

        if (string.IsNullOrEmpty(filter))
            return _cachedApps;

        return _cachedApps
            .Where(a => a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Find the executable path for an app by name.
    /// Searches InstallPath from Registry, then falls back to PATH environment.
    /// </summary>
    public string? FindExecutable(string appName)
    {
        var apps = GetInstalledApps(appName);

        foreach (var app in apps)
        {
            if (string.IsNullOrEmpty(app.InstallPath))
                continue;

            // Look for .exe files in the install path
            if (Directory.Exists(app.InstallPath))
            {
                try
                {
                    var exes = Directory.GetFiles(app.InstallPath, "*.exe", SearchOption.TopDirectoryOnly);
                    if (exes.Length > 0)
                    {
                        // Prefer exe that matches the app name
                        var bestMatch = exes.FirstOrDefault(e =>
                            Path.GetFileNameWithoutExtension(e)
                                .Contains(appName, StringComparison.OrdinalIgnoreCase));
                        return bestMatch ?? exes[0];
                    }
                }
                catch { }
            }

            // InstallPath might be a direct exe path
            if (app.InstallPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && File.Exists(app.InstallPath))
                return app.InstallPath;
        }

        // Fallback: search PATH
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? [];
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, appName + ".exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Get system information: OS, CPU, RAM, hostname.
    /// </summary>
    public string GetSystemInfo()
    {
        var os = RuntimeInformation.OSDescription;
        var arch = RuntimeInformation.OSArchitecture;
        var hostname = Environment.MachineName;
        var user = Environment.UserName;
        var cpuCount = Environment.ProcessorCount;

        // Get total physical RAM via GlobalMemoryStatusEx
        var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        // Output in GB: nobody reads "130436 MB" as 130 GB -- RAM is stated in GB.
        var totalRamBytes = GlobalMemoryStatusEx(ref memStatus)
            ? (long)memStatus.ullTotalPhys
            : GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; // fallback
        var totalRamGb = totalRamBytes / (1024.0 * 1024 * 1024);

        string cpu = "Unknown";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            cpu = key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "Unknown";
        }
        catch { }

        return $"System: {os} ({arch})\nCPU: {cpu} ({cpuCount} cores)\nRAM: {totalRamGb:N1} GB total\nHost: {hostname}\\{user}";
    }

    /// <summary>
    /// Get running processes grouped by name with memory usage.
    /// </summary>
    public List<(string Name, int Count, long MemoryMb)> GetRunningProcesses()
    {
        Process[] allProcs = [];
        try
        {
            allProcs = Process.GetProcesses();
            return allProcs
                .GroupBy(p =>
                {
                    try { return p.ProcessName; }
                    catch { return "unknown"; }
                })
                .Select(g =>
                {
                    long totalMemory = 0;
                    int count = 0;
                    foreach (var p in g)
                    {
                        count++;
                        try { totalMemory += p.WorkingSet64; }
                        catch { }
                    }
                    return (Name: g.Key, Count: count, MemoryMb: totalMemory / (1024 * 1024));
                })
                .OrderByDescending(x => x.MemoryMb)
                .ToList();
        }
        catch
        {
            return new List<(string, int, long)>();
        }
        finally
        {
            foreach (var p in allProcs) p.Dispose();
        }
    }

    private static List<InstalledApp> ScanRegistry()
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        string[] registryKeys =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        ];

        // HKLM
        foreach (var path in registryKeys)
            ScanRegistryKey(Registry.LocalMachine, path, apps);

        // HKCU
        ScanRegistryKey(Registry.CurrentUser, registryKeys[0], apps);

        return apps.Values.OrderBy(a => a.Name).ToList();
    }

    private static void ScanRegistryKey(RegistryKey root, string path, Dictionary<string, InstalledApp> apps)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key == null) return;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                try
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey == null) continue;

                    var name = subKey.GetValue("DisplayName")?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // Skip duplicates (same app registered in multiple locations)
                    if (apps.ContainsKey(name)) continue;

                    apps[name] = new InstalledApp(
                        Name: name,
                        Version: subKey.GetValue("DisplayVersion")?.ToString(),
                        Publisher: subKey.GetValue("Publisher")?.ToString(),
                        InstallPath: subKey.GetValue("InstallLocation")?.ToString(),
                        InstallDate: subKey.GetValue("InstallDate")?.ToString()
                    );
                }
                catch { }
            }
        }
        catch { }
    }
}
