namespace WinfoMain.Desktop.UIA;

using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Automation;

/// <summary>
/// Converts AutomationElements to UIATreeNode DTOs.
/// All methods are static — the conversion logic is independent of walk strategy.
/// </summary>
public static class UIATreeBuilder
{
    /// <summary>
    /// Convert a cached AutomationElement to a UIATreeNode snapshot.
    /// Extracts all properties from CacheRequest, detects supported patterns.
    /// Does NOT load children or check HasChildren — caller decides walk strategy.
    /// </summary>
    public static UIATreeNode ConvertToNode(AutomationElement element, UIATreeView treeView)
    {
        var node = new UIATreeNode { LiveElement = element };

        try
        {
            node.Name = element.Cached.Name ?? "";
            node.AutomationId = element.Cached.AutomationId ?? "";
            node.ClassName = element.Cached.ClassName ?? "";
            node.FrameworkId = element.Cached.FrameworkId ?? "";
            node.LocalizedControlType = element.Cached.LocalizedControlType ?? "";
            node.ProcessId = element.Cached.ProcessId;
            node.NativeWindowHandle = new nint(element.Cached.NativeWindowHandle);

            var ct = element.Cached.ControlType;
            node.ControlType = ct?.ProgrammaticName?.Replace("ControlType.", "") ?? "";

            node.IsEnabled = element.Cached.IsEnabled;
            node.IsOffscreen = element.Cached.IsOffscreen;
            node.IsKeyboardFocusable = element.Cached.IsKeyboardFocusable;
            node.HasKeyboardFocus = element.Cached.HasKeyboardFocus;
            node.IsContentElement = element.Cached.IsContentElement;
            node.IsControlElement = element.Cached.IsControlElement;

            var rect = element.Cached.BoundingRectangle;
            if (rect != System.Windows.Rect.Empty)
                node.BoundingRectangle = new Rectangle(
                    (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height);

            // Additional
            node.AcceleratorKey = SafeCachedString(element, AutomationElement.AcceleratorKeyProperty);
            node.AccessKey = SafeCachedString(element, AutomationElement.AccessKeyProperty);
            node.HelpText = SafeCachedString(element, AutomationElement.HelpTextProperty);
            node.ItemType = SafeCachedString(element, AutomationElement.ItemTypeProperty);
            node.ItemStatus = SafeCachedString(element, AutomationElement.ItemStatusProperty);

            // RuntimeId
            try { node.RuntimeId = element.GetRuntimeId(); }
            catch { node.RuntimeId = []; }

            // Supported patterns
            DetectPatterns(element, node);
        }
        catch (ElementNotAvailableException) { node.Name = "(unavailable)"; }
        catch (InvalidOperationException) { node.Name = "(unavailable)"; }
        catch (COMException) { node.Name = "(unavailable)"; }

        return node;
    }

    /// <summary>
    /// Check if an element has children using the appropriate TreeWalker.
    /// Uses GetFirstChild — one RPC call. Returns true if at least one child exists.
    /// </summary>
    internal static bool CheckHasChildren(AutomationElement element, UIATreeView treeView)
    {
        try
        {
            var walker = GetTreeWalker(treeView);
            return walker.GetFirstChild(element) != null;
        }
        catch { return false; }
    }

    internal static string? SafeCachedString(AutomationElement element, AutomationProperty prop)
    {
        try
        {
            var val = element.GetCachedPropertyValue(prop);
            return val is string s && !string.IsNullOrEmpty(s) ? s : null;
        }
        catch { return null; }
    }

    internal static void DetectPatterns(AutomationElement element, UIATreeNode node)
    {
        ReadOnlySpan<(AutomationPattern Pattern, string Name)> patterns =
        [
            (ValuePattern.Pattern, "Value"),
            (InvokePattern.Pattern, "Invoke"),
            (TogglePattern.Pattern, "Toggle"),
            (SelectionPattern.Pattern, "Selection"),
            (ExpandCollapsePattern.Pattern, "ExpandCollapse"),
            (ScrollPattern.Pattern, "Scroll"),
            (GridPattern.Pattern, "Grid"),
            (TextPattern.Pattern, "Text"),
            (RangeValuePattern.Pattern, "RangeValue"),
            (WindowPattern.Pattern, "Window"),
            (TransformPattern.Pattern, "Transform"),
            (DockPattern.Pattern, "Dock"),
            (TablePattern.Pattern, "Table"),
        ];

        foreach (var (pattern, name) in patterns)
        {
            try
            {
                if (element.TryGetCachedPattern(pattern, out _))
                    node.SupportedPatterns.Add(name);
            }
            catch { /* not cached or element stale */ }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────

    internal static TreeWalker GetTreeWalker(UIATreeView view) => view switch
    {
        UIATreeView.ContentView => TreeWalker.ContentViewWalker,
        UIATreeView.RawView => TreeWalker.RawViewWalker,
        _ => TreeWalker.ControlViewWalker,
    };

    internal static Condition GetViewCondition(UIATreeView view) => view switch
    {
        UIATreeView.ContentView => Automation.ContentViewCondition,
        UIATreeView.RawView => Automation.RawViewCondition,
        _ => Automation.ControlViewCondition,
    };
}
