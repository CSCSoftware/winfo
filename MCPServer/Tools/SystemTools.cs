namespace WinfoMain.MCPServer.Tools;

using System.ComponentModel;
using ModelContextProtocol.Server;
using WinfoMain.SystemInfo;

[McpServerToolType]
public static class SystemTools
{
    [McpServerTool(Name = "read_registry")]
    [Description("Read a Windows Registry key. Shows all values and subkeys, or a specific value. Path format: HKCU\\Software\\..., HKLM\\Software\\..., HKCR\\..., HKU\\..., HKCC\\...")]
    public static string ReadRegistry(
        [Description("Registry path, e.g. HKCU\\Software\\Microsoft\\Windows\\CurrentVersion")] string path,
        [Description("Optional: specific value name to read. Without this, lists all values and subkeys.")] string? value_name = null)
    {
        return RegistryService.ReadKey(path, value_name);
    }

    [McpServerTool(Name = "search_registry")]
    [Description("Search the Windows Registry recursively. Searches key names, value names, and string value data (case-insensitive). Use to find settings, device configs, app preferences etc.")]
    public static string SearchRegistry(
        [Description("Registry root: HKCU, HKLM, HKCR, HKU, HKCC")] string root,
        [Description("Starting subpath, e.g. Software\\Microsoft. Empty string to search from root (slow!).")] string path,
        [Description("Search query (case-insensitive substring match in key names, value names, and string data)")] string query,
        [Description("Maximum recursion depth (default: 5). Increase for deeper searches.")] int max_depth = 5,
        [Description("Maximum number of results (default: 20)")] int max_results = 20)
    {
        return RegistryService.Search(root, path, query, max_depth, max_results);
    }

    [McpServerTool(Name = "list_installed_apps")]
    [Description("List installed applications from Windows Registry. Results are cached for 5 minutes.")]
    public static string ListInstalledApps(
        [Description("Optional: filter by name (case-insensitive substring match)")] string? filter = null)
    {
        var apps = SystemInfoService.Instance.GetInstalledApps(filter);

        if (apps.Count == 0)
            return string.IsNullOrEmpty(filter)
                ? "No installed applications found."
                : $"No installed applications matching \"{filter}\".";

        var sb = new System.Text.StringBuilder();
        var displayApps = string.IsNullOrEmpty(filter) && apps.Count > 50
            ? apps.Take(50).ToList()
            : apps;

        sb.AppendLine($"Installed apps: {apps.Count}{(displayApps.Count < apps.Count ? $" (showing first {displayApps.Count})" : "")}");

        foreach (var app in displayApps)
        {
            sb.Append($"  {app.Name}");
            if (!string.IsNullOrEmpty(app.Version))
                sb.Append($" v{app.Version}");
            if (!string.IsNullOrEmpty(app.Publisher))
                sb.Append($" ({app.Publisher})");
            if (!string.IsNullOrEmpty(app.InstallPath))
                sb.Append($" @ {app.InstallPath}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "find_app")]
    [Description("Find an installed application and its executable path.")]
    public static string FindApp(
        [Description("Application name to search for")] string name)
    {
        var sb = new System.Text.StringBuilder();

        var exe = SystemInfoService.Instance.FindExecutable(name);
        if (exe != null)
            sb.AppendLine($"Executable: {exe}");
        else
            sb.AppendLine($"No executable found for \"{name}\".");

        var apps = SystemInfoService.Instance.GetInstalledApps(name);
        if (apps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Matching installed apps ({Math.Min(apps.Count, 5)} of {apps.Count}):");
            foreach (var app in apps.Take(5))
            {
                sb.Append($"  {app.Name}");
                if (!string.IsNullOrEmpty(app.Version))
                    sb.Append($" v{app.Version}");
                if (!string.IsNullOrEmpty(app.Publisher))
                    sb.Append($" ({app.Publisher})");
                if (!string.IsNullOrEmpty(app.InstallPath))
                    sb.Append($" @ {app.InstallPath}");
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "system_info")]
    [Description("Get system information: OS, CPU, RAM, hostname.")]
    public static string SystemInfo()
    {
        return SystemInfoService.Instance.GetSystemInfo();
    }

    [McpServerTool(Name = "list_processes")]
    [Description("List running processes grouped by name, sorted by memory usage (highest first).")]
    public static string ListProcesses(
        [Description("Maximum number of process groups to show (default: 30)")] int limit = 30)
    {
        var groups = SystemInfoService.Instance.GetRunningProcesses();

        if (groups.Count == 0)
            return "No running processes found.";

        var sb = new System.Text.StringBuilder();
        var display = groups.Take(limit).ToList();
        var totalMemory = groups.Sum(g => g.MemoryMb);
        var totalProcesses = groups.Sum(g => g.Count);

        sb.AppendLine($"Running processes: {totalProcesses} ({groups.Count} groups, {totalMemory:N0} MB total)");

        foreach (var (name, count, memoryMb) in display)
        {
            sb.AppendLine($"  {name} ({count}x) — {memoryMb} MB");
        }

        if (display.Count < groups.Count)
            sb.AppendLine($"  ... and {groups.Count - display.Count} more groups");

        return sb.ToString().TrimEnd();
    }
}
