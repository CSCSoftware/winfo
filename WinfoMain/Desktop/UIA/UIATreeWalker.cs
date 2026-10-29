namespace WinfoMain.Desktop.UIA;

using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using WinfoMain.Common;

/// <summary>
/// Walks the UI Automation tree of a window and builds a UIATreeNode hierarchy.
/// Instantiated per operation — not a singleton.
/// Standalone version: CacheRequest is created inline (no UIAEngine dependency).
///
/// Usage:
///   var walker = new UIATreeWalker();
///   var root = await walker.LoadRootAsync(window);
///   var children = await walker.LoadChildrenAsync(node);
///   await walker.LoadDetailsAsync(node);
/// </summary>
public class UIATreeWalker
{
    /// <summary>Which tree view to use (Control, Content, Raw).</summary>
    public UIATreeView TreeView { get; set; } = UIATreeView.ControlView;

    /// <summary>Load children on demand (true) or full tree upfront (false).</summary>
    public bool LazyLoading { get; set; }

    /// <summary>Timeout per child-loading operation in ms.</summary>
    public int TimeoutMs { get; set; } = 5000;

    private TreeWalker Walker => UIATreeBuilder.GetTreeWalker(TreeView);

    // ──────────────────────────────────────────────────────────────
    //  Public API
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Load the root element of a window.
    /// Returns a single UIATreeNode with HasChildren=true (children not yet loaded).
    /// </summary>
    public async Task<UIATreeNode?> LoadRootAsync(WindowInfo window)
    {
        return await Task.Run(() =>
        {
            try
            {
                var cr = CreateCacheRequest(TreeView);
                using (cr.Activate())
                {
                    var root = AutomationElement.FromHandle(window.Hwnd);
                    if (root == null) return null;

                    var node = UIATreeBuilder.ConvertToNode(root, TreeView);
                    node.HasChildren = true; // root always expandable
                    return node;
                }
            }
            catch (Exception ex) { WinfoLog.Write($"[WinfoMain] LoadRootAsync failed: {ex}"); return null; }
        });
    }

    /// <summary>
    /// Load direct children of a node using FindAll (single RPC call).
    /// Falls back to TreeWalker if FindAll fails.
    /// Sets node.ChildrenLoaded = true after completion.
    /// </summary>
    public async Task<List<UIATreeNode>> LoadChildrenAsync(UIATreeNode parent)
    {
        var children = new List<UIATreeNode>();

        using var cts = new CancellationTokenSource(TimeoutMs);

        try
        {
            await Task.Run(() =>
            {
                var live = parent.LiveElement;
                if (live == null) return;

                var cr = CreateCacheRequest(TreeView);

                try
                {
                    // FindAll: single RPC call for all direct children
                    using (cr.Activate())
                    {
                        var found = live.FindAll(TreeScope.Children, Condition.TrueCondition);
                        foreach (AutomationElement child in found)
                        {
                            cts.Token.ThrowIfCancellationRequested();
                            try
                            {
                                var node = UIATreeBuilder.ConvertToNode(child, TreeView);
                                node.HasChildren = UIATreeBuilder.CheckHasChildren(child, TreeView);
                                children.Add(node);
                            }
                            catch (ElementNotAvailableException) { }
                            catch (InvalidOperationException) { }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // FindAll failed — fallback to TreeWalker
                    children.Clear();
                    try
                    {
                        using (cr.Activate())
                        {
                            var child = Walker.GetFirstChild(live);
                            while (child != null)
                            {
                                cts.Token.ThrowIfCancellationRequested();
                                try
                                {
                                    var node = UIATreeBuilder.ConvertToNode(child, TreeView);
                                    node.HasChildren = UIATreeBuilder.CheckHasChildren(child, TreeView);
                                    children.Add(node);
                                }
                                catch (Exception ex) { WinfoLog.Write($"[WinfoMain] TreeWalker child conversion failed: {ex}"); }

                                try { child = Walker.GetNextSibling(child); }
                                catch { break; }
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { WinfoLog.Write($"[WinfoMain] TreeWalker fallback failed: {ex}"); }
                }
            }, cts.Token);
        }
        catch (OperationCanceledException) { /* timeout — return what we have */ }

        parent.Children = children;
        parent.ChildrenLoaded = true;
        return children;
    }

    /// <summary>
    /// Load the full tree recursively starting from a node.
    /// Calls LoadChildrenAsync at each level until all HasChildren nodes are expanded.
    /// Respects TimeoutMs per level. Max depth 15.
    /// </summary>
    public async Task LoadFullTreeAsync(UIATreeNode node, int depth = 0)
    {
        if (depth > 15) return;
        if (!node.HasChildren || node.ChildrenLoaded) return;

        await LoadChildrenAsync(node);

        foreach (var child in node.Children)
        {
            if (child.HasChildren && !child.ChildrenLoaded)
                await LoadFullTreeAsync(child, depth + 1);
        }
    }

    /// <summary>
    /// Load detailed properties for a selected node.
    /// Fetches Value, Text via live element (not cached).
    /// </summary>
    public async Task LoadDetailsAsync(UIATreeNode node)
    {
        await Task.Run(() =>
        {
            var live = node.LiveElement;
            if (live == null) return;

            try
            {
                // Refresh bounds from live element
                var rect = live.Current.BoundingRectangle;
                if (rect != System.Windows.Rect.Empty)
                    node.BoundingRectangle = new Rectangle(
                        (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height);

                // Value
                if (live.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
                    node.Value = ((ValuePattern)vp).Current.Value;

                // Text
                if (live.TryGetCurrentPattern(TextPattern.Pattern, out var tp))
                {
                    var text = ((TextPattern)tp).DocumentRange.GetText(2000);
                    if (!string.IsNullOrEmpty(text))
                        node.Text = text;
                }
            }
            catch (ElementNotAvailableException) { node.LiveElement = null; }
            catch (COMException) { node.LiveElement = null; }
            catch (InvalidOperationException) { /* property not available */ }
        });
    }

    // ──────────────────────────────────────────────────────────────
    //  CacheRequest (inline — no UIAEngine dependency)
    // ──────────────────────────────────────────────────────────────

    /// <summary>Cached CacheRequest instances keyed by UIATreeView — built once, reused every call.</summary>
    private static readonly Dictionary<UIATreeView, CacheRequest> _cacheRequestCache = new();
    private static readonly object _cacheRequestLock = new();

    /// <summary>
    /// Return a cached CacheRequest for the given tree view.
    /// Built on first call, reused on all subsequent calls (no allocation overhead).
    /// </summary>
    public static CacheRequest CreateCacheRequest(UIATreeView treeView)
    {
        lock (_cacheRequestLock)
        {
            if (_cacheRequestCache.TryGetValue(treeView, out var cached))
                return cached;

            var cr = new CacheRequest();

            // Core identity
            cr.Add(AutomationElement.NameProperty);
            cr.Add(AutomationElement.AutomationIdProperty);
            cr.Add(AutomationElement.ControlTypeProperty);
            cr.Add(AutomationElement.ClassNameProperty);
            cr.Add(AutomationElement.FrameworkIdProperty);
            cr.Add(AutomationElement.RuntimeIdProperty);
            cr.Add(AutomationElement.ProcessIdProperty);
            cr.Add(AutomationElement.NativeWindowHandleProperty);
            cr.Add(AutomationElement.LocalizedControlTypeProperty);

            // State
            cr.Add(AutomationElement.IsEnabledProperty);
            cr.Add(AutomationElement.IsOffscreenProperty);
            cr.Add(AutomationElement.IsKeyboardFocusableProperty);
            cr.Add(AutomationElement.HasKeyboardFocusProperty);
            cr.Add(AutomationElement.IsContentElementProperty);
            cr.Add(AutomationElement.IsControlElementProperty);

            // Geometry
            cr.Add(AutomationElement.BoundingRectangleProperty);

            // Additional
            cr.Add(AutomationElement.AcceleratorKeyProperty);
            cr.Add(AutomationElement.AccessKeyProperty);
            cr.Add(AutomationElement.HelpTextProperty);
            cr.Add(AutomationElement.ItemTypeProperty);
            cr.Add(AutomationElement.ItemStatusProperty);

            // Patterns (detect which are supported)
            cr.Add(ValuePattern.Pattern);
            cr.Add(InvokePattern.Pattern);
            cr.Add(TogglePattern.Pattern);
            cr.Add(SelectionPattern.Pattern);
            cr.Add(ExpandCollapsePattern.Pattern);
            cr.Add(ScrollPattern.Pattern);
            cr.Add(GridPattern.Pattern);
            cr.Add(TextPattern.Pattern);
            cr.Add(RangeValuePattern.Pattern);
            cr.Add(WindowPattern.Pattern);
            cr.Add(TransformPattern.Pattern);
            cr.Add(DockPattern.Pattern);
            cr.Add(TablePattern.Pattern);

            cr.TreeScope = TreeScope.Element;
            cr.TreeFilter = UIATreeBuilder.GetViewCondition(treeView);

            _cacheRequestCache[treeView] = cr;
            return cr;
        }
    }
}
