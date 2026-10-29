namespace WinfoMain.SystemInfo;

using System.Text;
using Microsoft.Win32;

/// <summary>
/// General-purpose Windows Registry reader and searcher.
/// Read specific keys/values or search recursively with depth/result limits.
/// </summary>
public static class RegistryService
{
    private static readonly Dictionary<string, RegistryKey> RootKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HKCU"] = Registry.CurrentUser,
        ["HKEY_CURRENT_USER"] = Registry.CurrentUser,
        ["HKLM"] = Registry.LocalMachine,
        ["HKEY_LOCAL_MACHINE"] = Registry.LocalMachine,
        ["HKCR"] = Registry.ClassesRoot,
        ["HKEY_CLASSES_ROOT"] = Registry.ClassesRoot,
        ["HKU"] = Registry.Users,
        ["HKEY_USERS"] = Registry.Users,
        ["HKCC"] = Registry.CurrentConfig,
        ["HKEY_CURRENT_CONFIG"] = Registry.CurrentConfig,
    };

    /// <summary>
    /// Read a registry key: list its values and subkeys.
    /// If valueName is specified, return only that value.
    /// </summary>
    public static string ReadKey(string path, string? valueName = null)
    {
        var (rootKey, subPath) = ParsePath(path);
        if (rootKey == null)
            return $"Unknown registry root in path: {path}\nUse: HKCU\\..., HKLM\\..., HKCR\\..., HKU\\..., HKCC\\...";

        try
        {
            using var key = rootKey.OpenSubKey(subPath);
            if (key == null)
                return $"Registry key not found: {path}";

            if (valueName != null)
            {
                var val = key.GetValue(valueName);
                if (val == null)
                    return $"Value \"{valueName}\" not found in {path}";

                var kind = key.GetValueKind(valueName);
                return $"{valueName} ({kind}): {FormatValue(val, kind)}";
            }

            // List all values + subkeys
            var sb = new StringBuilder();
            sb.AppendLine($"Key: {path}");

            var valueNames = key.GetValueNames();
            if (valueNames.Length > 0)
            {
                sb.AppendLine($"\nValues ({valueNames.Length}):");
                foreach (var name in valueNames)
                {
                    try
                    {
                        var val = key.GetValue(name);
                        var kind = key.GetValueKind(name);
                        var displayName = string.IsNullOrEmpty(name) ? "(Default)" : name;
                        sb.AppendLine($"  {displayName} ({kind}): {FormatValue(val, kind)}");
                    }
                    catch { }
                }
            }

            var subKeyNames = key.GetSubKeyNames();
            if (subKeyNames.Length > 0)
            {
                var display = subKeyNames.Length > 50 ? subKeyNames.Take(50).ToArray() : subKeyNames;
                sb.AppendLine($"\nSubKeys ({subKeyNames.Length}):");
                foreach (var name in display)
                    sb.AppendLine($"  {name}\\");
                if (display.Length < subKeyNames.Length)
                    sb.AppendLine($"  ... and {subKeyNames.Length - display.Length} more");
            }

            return sb.ToString().TrimEnd();
        }
        catch (System.Security.SecurityException)
        {
            return $"Access denied: {path} (requires elevated permissions)";
        }
        catch (Exception ex)
        {
            return $"Error reading {path}: {ex.Message}";
        }
    }

    /// <summary>
    /// Search the registry recursively for a query string.
    /// Searches in key names, value names, and string value data.
    /// </summary>
    public static string Search(string rootName, string subPath, string query,
        int maxDepth = 5, int maxResults = 20)
    {
        if (!RootKeys.TryGetValue(rootName, out var rootKey))
            return $"Unknown registry root: {rootName}\nUse: HKCU, HKLM, HKCR, HKU, HKCC";

        var results = new List<string>();
        var rootDisplay = rootName.ToUpper();

        try
        {
            using var startKey = string.IsNullOrEmpty(subPath)
                ? rootKey.OpenSubKey("")
                : rootKey.OpenSubKey(subPath);

            if (startKey == null)
                return $"Registry key not found: {rootDisplay}\\{subPath}";

            var basePath = string.IsNullOrEmpty(subPath) ? rootDisplay : $"{rootDisplay}\\{subPath}";
            SearchRecursive(startKey, basePath, query, 0, maxDepth, maxResults, results);
        }
        catch (System.Security.SecurityException)
        {
            return $"Access denied: {rootDisplay}\\{subPath}";
        }

        if (results.Count == 0)
            return $"No matches for \"{query}\" in {rootDisplay}\\{subPath} (depth {maxDepth})";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} match(es) for \"{query}\":");
        foreach (var r in results)
            sb.AppendLine($"  {r}");

        if (results.Count >= maxResults)
            sb.AppendLine($"  ... (limit {maxResults} reached, increase max_results for more)");

        return sb.ToString().TrimEnd();
    }

    private static void SearchRecursive(RegistryKey key, string currentPath, string query,
        int depth, int maxDepth, int maxResults, List<string> results)
    {
        if (depth > maxDepth || results.Count >= maxResults)
            return;

        // Search value names and data
        try
        {
            foreach (var valName in key.GetValueNames())
            {
                if (results.Count >= maxResults) return;

                bool nameMatch = valName.Contains(query, StringComparison.OrdinalIgnoreCase);

                try
                {
                    var val = key.GetValue(valName);
                    var kind = key.GetValueKind(valName);
                    bool dataMatch = false;

                    if (val is string strVal)
                        dataMatch = strVal.Contains(query, StringComparison.OrdinalIgnoreCase);
                    else if (val is string[] strArr)
                        dataMatch = strArr.Any(s => s.Contains(query, StringComparison.OrdinalIgnoreCase));

                    if (nameMatch || dataMatch)
                    {
                        var displayName = string.IsNullOrEmpty(valName) ? "(Default)" : valName;
                        results.Add($"{currentPath} :: {displayName} ({kind}) = {FormatValue(val, kind, 100)}");
                    }
                }
                catch { }
            }
        }
        catch { }

        // Search subkey names and recurse
        try
        {
            foreach (var subName in key.GetSubKeyNames())
            {
                if (results.Count >= maxResults) return;

                var subPath = $"{currentPath}\\{subName}";

                if (subName.Contains(query, StringComparison.OrdinalIgnoreCase))
                    results.Add($"{subPath}\\  (key name match)");

                try
                {
                    using var subKey = key.OpenSubKey(subName);
                    if (subKey != null)
                        SearchRecursive(subKey, subPath, query, depth + 1, maxDepth, maxResults, results);
                }
                catch { } // Access denied for individual subkeys — skip silently
            }
        }
        catch { }
    }

    private static (RegistryKey? root, string subPath) ParsePath(string path)
    {
        // Normalize separators
        path = path.Replace('/', '\\');

        var sepIdx = path.IndexOf('\\');
        if (sepIdx < 0)
        {
            // Just a root key, no subpath
            return RootKeys.TryGetValue(path, out var r) ? (r, "") : (null, "");
        }

        var rootPart = path[..sepIdx];
        var subPart = path[(sepIdx + 1)..].TrimEnd('\\');

        return RootKeys.TryGetValue(rootPart, out var root) ? (root, subPart) : (null, "");
    }

    private static string FormatValue(object? val, RegistryValueKind kind, int maxLen = 200)
    {
        if (val == null) return "(null)";

        var str = kind switch
        {
            RegistryValueKind.Binary when val is byte[] bytes =>
                bytes.Length <= 32
                    ? BitConverter.ToString(bytes)
                    : $"{BitConverter.ToString(bytes[..32])}... ({bytes.Length} bytes)",
            RegistryValueKind.MultiString when val is string[] arr =>
                string.Join(" | ", arr),
            RegistryValueKind.DWord or RegistryValueKind.QWord =>
                val.ToString() ?? "",
            _ => val.ToString() ?? ""
        };

        return str.Length > maxLen ? str[..maxLen] + "..." : str;
    }
}
