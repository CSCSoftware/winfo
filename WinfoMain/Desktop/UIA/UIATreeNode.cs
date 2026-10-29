namespace WinfoMain.Desktop.UIA;

using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Automation;

/// <summary>
/// Snapshot of a UI Automation element for tree exploration.
/// All properties are extracted immediately from CacheRequest.
/// LiveElement is kept for lazy child loading (may go stale).
/// </summary>
public class UIATreeNode
{
    // --- Live reference (for lazy child loading and pattern access) ---
    internal AutomationElement? LiveElement { get; set; }

    /// <summary>True if the underlying COM element is still available.</summary>
    public bool HasLiveElement => LiveElement != null;

    /// <summary>
    /// Try to get a UIA pattern from the live element.
    /// Returns null if element is stale or pattern not supported.
    /// </summary>
    public T? TryGetPattern<T>(AutomationPattern pattern) where T : BasePattern
    {
        if (LiveElement == null) return null;
        try
        {
            if (LiveElement.TryGetCurrentPattern(pattern, out var p))
                return (T)p;
        }
        catch { }
        return null;
    }

    // --- Identity ---
    public string Name { get; set; } = "";
    public string AutomationId { get; set; } = "";
    public string ControlType { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string FrameworkId { get; set; } = "";
    public string LocalizedControlType { get; set; } = "";
    public int[] RuntimeId { get; set; } = [];
    public int ProcessId { get; set; }
    public nint NativeWindowHandle { get; set; }

    // --- State ---
    public bool IsEnabled { get; set; }
    public bool IsOffscreen { get; set; }
    public bool IsKeyboardFocusable { get; set; }
    public bool HasKeyboardFocus { get; set; }
    public bool IsContentElement { get; set; }
    public bool IsControlElement { get; set; }

    // --- Geometry ---
    public Rectangle BoundingRectangle { get; set; }

    // --- Content (populated on demand for selected node) ---
    public string? Value { get; set; }
    public string? Text { get; set; }

    // --- Additional ---
    public string? AcceleratorKey { get; set; }
    public string? AccessKey { get; set; }
    public string? HelpText { get; set; }
    public string? ItemType { get; set; }
    public string? ItemStatus { get; set; }

    // --- Supported Patterns ---
    public List<string> SupportedPatterns { get; set; } = [];

    // --- Tree Structure ---
    public List<UIATreeNode> Children { get; set; } = [];
    public bool ChildrenLoaded { get; set; }
    public bool HasChildren { get; set; }

    // --- Visible Text Extraction ─────────────────────────────────

    // ControlTypes where Name = visible text (not a container label)
    private static readonly HashSet<string> NameIsTextTypes =
    [
        "Text", "Button", "MenuItem", "CheckBox", "RadioButton",
        "Hyperlink", "ListItem", "TreeItem", "TabItem", "HeaderItem",
        "ToolTip", "StatusBar", "DataItem", "SplitButton",
    ];

    // Strip trailing shortcut hints: "(Ctrl+X)", "(Strg+Umschalt+F5)", etc.
    private static readonly Regex ShortcutSuffix = new(
        @"\s*\((Ctrl|Strg|Alt|Shift|Umschalt|Cmd|Win|Meta)(\s*\+\s*\w+)+\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Extract the visible text from this node using priority rules:
    ///   1. TextPattern result (Text property) — for Document, Text, Edit
    ///   2. ValuePattern result (Value property) — for Edit, ComboBox, Document
    ///   3. Name (cleaned of shortcut suffixes) — for label-type controls
    /// Returns null for containers (Window, Pane, Group, etc.) that have no visible text.
    /// </summary>
    public string? GetVisibleText()
    {
        // Step 1: TextPattern (richest — actual rendered content)
        if (ControlType is "Document" or "Text" or "Edit")
        {
            if (!string.IsNullOrWhiteSpace(Text)) return Text;
        }

        // Step 2: ValuePattern (user-entered/selected content)
        if (ControlType is "Edit" or "ComboBox" or "Document")
        {
            if (!string.IsNullOrWhiteSpace(Value)) return Value;
        }

        // Step 3: Name — only for types where Name = visible text
        if (NameIsTextTypes.Contains(ControlType))
        {
            if (!string.IsNullOrWhiteSpace(Name))
                return CleanName(Name);
        }

        return null;
    }

    /// <summary>
    /// Remove trailing shortcut hints from a Name string.
    /// "Channels (Ctrl+Alt+A)" → "Channels"
    /// "Save" → "Save" (unchanged)
    /// </summary>
    public static string CleanName(string name)
    {
        var cleaned = ShortcutSuffix.Replace(name, "");
        return string.IsNullOrWhiteSpace(cleaned) ? name : cleaned;
    }

    // --- Display ---

    /// <summary>
    /// Compact display text for TreeView node.
    /// Format: [ControlType] "Name" (AutomationId)
    /// </summary>
    public string DisplayText
    {
        get
        {
            var parts = new List<string>(3) { $"[{ControlType}]" };
            if (!string.IsNullOrEmpty(Name))
                parts.Add($"\"{Name}\"");
            if (!string.IsNullOrEmpty(AutomationId) && AutomationId != Name)
                parts.Add($"({AutomationId})");
            if (parts.Count == 1 && !string.IsNullOrEmpty(ClassName))
                parts.Add(ClassName);
            return string.Join(" ", parts);
        }
    }

    /// <summary>
    /// All properties as (Name, Display, Raw) tuples for the detail ListView.
    /// Display = formatted for single-line ListView, Raw = full untruncated value.
    /// </summary>
    public List<(string Name, string Display, string Raw)> GetPropertyList(Rectangle windowBounds = default)
    {
        var props = new List<(string, string, string)>();

        void Add(string name, string value)
            => props.Add((name, value, value));

        // Identity
        Add("ControlType", ControlType);
        Add("LocalizedControlType", LocalizedControlType);
        Add("Name", Name);
        Add("AutomationId", AutomationId);
        Add("ClassName", ClassName);
        Add("FrameworkId", FrameworkId);
        Add("RuntimeId", RuntimeId.Length > 0 ? string.Join(".", RuntimeId) : "");
        Add("ProcessId", ProcessId.ToString());
        Add("NativeWindowHandle", NativeWindowHandle != 0 ? $"0x{NativeWindowHandle:X}" : "");

        // State
        Add("IsEnabled", IsEnabled.ToString());
        Add("IsOffscreen", IsOffscreen.ToString());
        Add("IsKeyboardFocusable", IsKeyboardFocusable.ToString());
        Add("HasKeyboardFocus", HasKeyboardFocus.ToString());
        Add("IsContentElement", IsContentElement.ToString());
        Add("IsControlElement", IsControlElement.ToString());

        // Geometry
        var r = BoundingRectangle;
        Add("BoundingRectangle", r.IsEmpty ? "(empty)" : $"{r.X}, {r.Y}  {r.Width} x {r.Height}");
        if (!r.IsEmpty && r.Width > 0 && r.Height > 0)
        {
            Add("Squareness", Squareness(r.Width, r.Height).ToString("F2"));
            if (!windowBounds.IsEmpty && windowBounds.Width > 0 && windowBounds.Height > 0)
            {
                double areaRatio = (double)(r.Width * r.Height) / (windowBounds.Width * windowBounds.Height);
                Add("AreaRatio", Math.Min(areaRatio, 1.0).ToString("F3"));
            }
        }

        // Content — Display is formatted (truncated), Raw is full text
        if (Value != null) props.Add(("Value", FormatContentForList(Value), Value));
        if (Text != null) props.Add(("Text", FormatContentForList(Text), Text));

        // Additional
        if (!string.IsNullOrEmpty(AcceleratorKey)) Add("AcceleratorKey", AcceleratorKey);
        if (!string.IsNullOrEmpty(AccessKey)) Add("AccessKey", AccessKey);
        if (!string.IsNullOrEmpty(HelpText)) Add("HelpText", HelpText);
        if (!string.IsNullOrEmpty(ItemType)) Add("ItemType", ItemType);
        if (!string.IsNullOrEmpty(ItemStatus)) Add("ItemStatus", ItemStatus);

        // Patterns
        if (SupportedPatterns.Count > 0)
            Add("SupportedPatterns", string.Join(", ", SupportedPatterns));

        return props;
    }

    private static double Squareness(double w, double h, double k = 1.0)
    {
        double r = Math.Abs(Math.Log(w / h));
        return Math.Exp(-k * r);
    }

    private static string FormatContentForList(string text)
    {
        int lineCount = text.Count(c => c == '\n') + 1;
        if (lineCount > 1)
        {
            // Show first line + hint about remaining lines (full text in preview panel)
            var firstLine = text.AsSpan();
            int nl = text.IndexOf('\n');
            if (nl >= 0) firstLine = text.AsSpan(0, nl).TrimEnd('\r');
            var preview = firstLine.Length > 120 ? $"{firstLine[..120]}..." : firstLine.ToString();
            return $"{preview}  ({lineCount} lines — see below)";
        }
        return text.Length > 200 ? text[..200] + "..." : text;
    }
}
