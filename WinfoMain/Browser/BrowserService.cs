namespace WinfoMain.Browser;

using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Automation;

public class BrowserService
{
    public static BrowserService Instance { get; } = new();
    private BrowserService() { }

    private static readonly Dictionary<string, BrowserProfile> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"]  = new("chrome",  "Address and search bar", null, "Chrome_WidgetWin_1"),
        ["msedge"]  = new("msedge",  "Address and search bar", null, "Chrome_WidgetWin_1"),
        ["firefox"] = new("firefox", "", "urlbar-input", "MozillaWindowClass"),
    };

    // --- P/Invoke for mouse click fallback ---

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, nint dwExtraInfo);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP   = 0x0004;

    // --- Public methods ---

    public string OpenUrl(string url, string? browser = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "Error: URL is empty.";

        if (!url.Contains("://"))
            url = "https://" + url;

        try
        {
            if (browser == null)
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return $"Opened {url} in default browser.";
            }

            var profile = FindProfile(browser);
            if (profile == null)
                return $"Error: Unknown browser '{browser}'. Supported: chrome, msedge, firefox.";

            Process.Start(new ProcessStartInfo
            {
                FileName = profile.ProcessName,
                Arguments = url,
                UseShellExecute = true
            });
            return $"Opened {url} in {profile.ProcessName}.";
        }
        catch (Exception ex)
        {
            return $"Error opening URL: {ex.Message}";
        }
    }

    public string GetCurrentUrl(string? browser)
    {
        var (profile, hwnd, error) = FindRunningBrowser(browser);
        if (error != null) return error;

        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var bar = FindAddressBar(root, profile!);
            if (bar == null)
                return $"Error: Could not find address bar in {profile!.ProcessName}.";

            var value = GetValueSafe(bar);
            if (string.IsNullOrEmpty(value))
                return $"Error: Address bar is empty or unreadable in {profile!.ProcessName}.";

            return value;
        }
        catch (Exception ex)
        {
            return $"Error reading URL: {ex.Message}";
        }
    }

    public string ListTabs(string? browser)
    {
        var (profile, hwnd, error) = FindRunningBrowser(browser);
        if (error != null) return error;

        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var tabs = FindTabs(root);

            if (tabs.Count == 0)
                return $"No tabs found in {profile!.ProcessName}. The browser may use a non-standard tab structure.";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{profile!.ProcessName}] {tabs.Count} tab(s):");
            for (int i = 0; i < tabs.Count; i++)
            {
                var (_, name, isSelected) = tabs[i];
                var marker = isSelected ? " *" : "";
                sb.AppendLine($"  {i + 1}. {Truncate(name, 80)}{marker}");
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Error listing tabs: {ex.Message}";
        }
    }

    public string SwitchTab(string titleOrIndex, string? browser)
    {
        var (profile, hwnd, error) = FindRunningBrowser(browser);
        if (error != null) return error;

        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var tabs = FindTabs(root);

            if (tabs.Count == 0)
                return $"No tabs found in {profile!.ProcessName}.";

            // Try as 1-based index first
            AutomationElement? target = null;
            string targetName = "";

            if (int.TryParse(titleOrIndex, out int index))
            {
                if (index < 1 || index > tabs.Count)
                    return $"Error: Tab index {index} out of range (1-{tabs.Count}).";
                (target, targetName, _) = tabs[index - 1];
            }
            else
            {
                // Search by title substring (case-insensitive)
                var match = tabs.FirstOrDefault(t =>
                    t.name.Contains(titleOrIndex, StringComparison.OrdinalIgnoreCase));
                if (match.element == null)
                    return $"Error: No tab found matching '{titleOrIndex}'.";
                (target, targetName, _) = match;
            }

            // Try SelectionItemPattern
            try
            {
                if (target.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selPattern))
                {
                    ((SelectionItemPattern)selPattern).Select();
                    return $"Switched to tab: {Truncate(targetName, 80)}";
                }
            }
            catch { }

            // Try InvokePattern
            if (InvokeSafe(target))
                return $"Switched to tab: {Truncate(targetName, 80)}";

            // Mouse click fallback
            var bounds = GetBoundsSafe(target);
            if (bounds != Rectangle.Empty)
            {
                int cx = bounds.X + bounds.Width / 2;
                int cy = bounds.Y + bounds.Height / 2;
                SetCursorPos(cx, cy);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                return $"Clicked tab: {Truncate(targetName, 80)} (mouse fallback)";
            }

            return $"Error: Could not activate tab '{Truncate(targetName, 80)}' — no pattern or bounds available.";
        }
        catch (Exception ex)
        {
            return $"Error switching tab: {ex.Message}";
        }
    }

    // --- Private helpers ---

    private static AutomationElement? FindAddressBar(AutomationElement root, BrowserProfile profile)
    {
        // Try by AutomationId first (Firefox)
        if (!string.IsNullOrEmpty(profile.AddressBarAutomationId))
        {
            var byId = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, profile.AddressBarAutomationId));
            if (byId != null) return byId;
        }

        // Try by Name (Chrome/Edge)
        if (!string.IsNullOrEmpty(profile.AddressBarName))
        {
            var byName = root.FindFirst(TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.NameProperty, profile.AddressBarName),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
            if (byName != null) return byName;
        }

        // Generic fallback: first Edit with "address" in name
        var allEdits = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        foreach (AutomationElement edit in allEdits)
        {
            try
            {
                var name = edit.Current.Name ?? "";
                if (name.Contains("address", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("url", StringComparison.OrdinalIgnoreCase))
                    return edit;
            }
            catch { }
        }

        return null;
    }

    private static List<(AutomationElement element, string name, bool isSelected)> FindTabs(AutomationElement root)
    {
        var result = new List<(AutomationElement, string, bool)>();

        // Find tab items
        var tabItems = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));

        foreach (AutomationElement tab in tabItems)
        {
            try
            {
                var name = tab.Current.Name ?? "(untitled)";
                bool isSelected = false;

                try
                {
                    if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern))
                        isSelected = ((SelectionItemPattern)pattern).Current.IsSelected;
                }
                catch { }

                result.Add((tab, name, isSelected));
            }
            catch { }
        }

        return result;
    }

    private static (BrowserProfile? profile, nint hwnd, string? error) FindRunningBrowser(string? browser)
    {
        if (browser != null)
        {
            var profile = FindProfile(browser);
            if (profile == null)
                return (null, 0, $"Error: Unknown browser '{browser}'. Supported: chrome, msedge, firefox.");

            var procs = Process.GetProcessesByName(profile.ProcessName);
            if (procs.Length == 0)
                return (null, 0, $"Error: {profile.ProcessName} is not running.");

            // Find the main window (largest, with title)
            nint bestHwnd = 0;
            string bestTitle = "";
            foreach (var p in procs)
            {
                if (p.MainWindowHandle != 0 && !string.IsNullOrEmpty(p.MainWindowTitle))
                {
                    if (bestHwnd == 0 || p.MainWindowTitle.Length > bestTitle.Length)
                    {
                        bestHwnd = p.MainWindowHandle;
                        bestTitle = p.MainWindowTitle;
                    }
                }
            }

            if (bestHwnd == 0)
                return (null, 0, $"Error: {profile.ProcessName} is running but has no visible window.");

            return (profile, bestHwnd, null);
        }

        // Auto-detect: try each browser in order
        foreach (var kvp in Profiles)
        {
            var (p, hwnd, err) = FindRunningBrowser(kvp.Key);
            if (err == null)
                return (p, hwnd, null);
        }

        return (null, 0, "Error: No supported browser is running (chrome, msedge, firefox).");
    }

    private static BrowserProfile? FindProfile(string browser)
    {
        return Profiles.TryGetValue(browser, out var profile) ? profile : null;
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        return s[..(max - 3)] + "...";
    }

    // --- UIA helper methods (inlined from old AutomationExtensions) ---

    private static string? GetValueSafe(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
                return ((ValuePattern)pattern).Current.Value;
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        catch (COMException) { }
        return null;
    }

    private static bool InvokeSafe(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
            {
                ((InvokePattern)pattern).Invoke();
                return true;
            }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        catch (COMException) { }
        return false;
    }

    private static Rectangle GetBoundsSafe(AutomationElement element)
    {
        try
        {
            var rect = element.Current.BoundingRectangle;
            if (rect == System.Windows.Rect.Empty) return Rectangle.Empty;
            return new Rectangle((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height);
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        catch (COMException) { }
        return Rectangle.Empty;
    }
}
