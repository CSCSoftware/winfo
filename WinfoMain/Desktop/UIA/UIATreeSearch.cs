namespace WinfoMain.Desktop.UIA;

using System.Drawing;

/// <summary>
/// Search and hit-test operations on UIATreeNode hierarchies.
/// All methods are static — pure functions on the node tree.
/// </summary>
public static class UIATreeSearch
{
    /// <summary>
    /// Find the deepest (smallest area) node whose BoundingRectangle contains the point.
    /// At equal area, deeper node wins (child beats parent).
    /// Returns null if no node contains the point.
    /// </summary>
    public static UIATreeNode? FindByBounds(UIATreeNode node, Point screenPos)
    {
        var r = node.BoundingRectangle;
        if (r.IsEmpty || !r.Contains(screenPos)) return null;

        // Recurse into children — find the deepest (smallest) match
        UIATreeNode? best = node;
        int bestArea = r.Width * r.Height;

        foreach (var child in node.Children)
        {
            var childMatch = FindByBounds(child, screenPos);
            if (childMatch != null)
            {
                var cr = childMatch.BoundingRectangle;
                int childArea = cr.Width * cr.Height;
                if (childArea <= bestArea)
                {
                    best = childMatch;
                    bestArea = childArea;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Find a node by its RuntimeId (int[] exact match).
    /// Returns the first match found (depth-first).
    /// </summary>
    public static UIATreeNode? FindByRuntimeId(UIATreeNode node, int[] targetRid)
    {
        if (node.RuntimeId.Length > 0 && node.RuntimeId.SequenceEqual(targetRid))
            return node;

        foreach (var child in node.Children)
        {
            var found = FindByRuntimeId(child, targetRid);
            if (found != null) return found;
        }

        return null;
    }

    /// <summary>
    /// Search all loaded nodes by text query.
    /// Matches against Name, AutomationId, ClassName, and Value (case-insensitive).
    /// Returns all matching nodes.
    /// </summary>
    public static List<UIATreeNode> SearchByText(UIATreeNode root, string query)
    {
        var matches = new List<UIATreeNode>();
        SearchRecursive(root, query, matches);
        return matches;
    }

    private static void SearchRecursive(UIATreeNode node, string query, List<UIATreeNode> matches)
    {
        bool match = node.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || node.AutomationId.Contains(query, StringComparison.OrdinalIgnoreCase)
            || node.ClassName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (node.Value != null && node.Value.Contains(query, StringComparison.OrdinalIgnoreCase));

        if (match)
            matches.Add(node);

        foreach (var child in node.Children)
            SearchRecursive(child, query, matches);
    }

    /// <summary>
    /// Count all nodes in a tree (including root).
    /// </summary>
    public static int CountNodes(UIATreeNode? root)
    {
        if (root == null) return 0;
        int count = 1;
        foreach (var child in root.Children)
            count += CountNodes(child);
        return count;
    }
}
