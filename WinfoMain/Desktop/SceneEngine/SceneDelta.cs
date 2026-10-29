namespace WinfoMain.Desktop.SceneEngine;

using System.IO.Hashing;
using System.Text;

/// <summary>
/// Delta computation between two LayoutResults.
/// Matches regions by (Type, Name) tuple, detects Added/Removed/Changed/Unchanged.
/// </summary>
public class SceneDelta
{
    public List<LayoutRegionResult> Added { get; set; } = [];
    public List<LayoutRegionResult> Removed { get; set; } = [];
    public List<(LayoutRegionResult Old, LayoutRegionResult New)> Changed { get; set; } = [];
    public List<LayoutRegionResult> Unchanged { get; set; } = [];

    /// <summary>
    /// Compute the delta between an old and new LayoutResult.
    /// </summary>
    public static SceneDelta Compute(LayoutResult? oldLayout, LayoutResult newLayout)
    {
        var delta = new SceneDelta();

        if (oldLayout == null)
        {
            delta.Added.AddRange(newLayout.Regions);
            return delta;
        }

        var oldByKey = new Dictionary<(string, string?), LayoutRegionResult>();
        foreach (var region in oldLayout.Regions)
        {
            var key = (region.Type, region.Name);
            oldByKey.TryAdd(key, region);
        }

        var matchedOldKeys = new HashSet<(string, string?)>();

        foreach (var newRegion in newLayout.Regions)
        {
            var key = (newRegion.Type, newRegion.Name);

            if (oldByKey.TryGetValue(key, out var oldRegion))
            {
                matchedOldKeys.Add(key);

                if (ComputeRegionHash(oldRegion) == ComputeRegionHash(newRegion))
                    delta.Unchanged.Add(newRegion);
                else
                    delta.Changed.Add((oldRegion, newRegion));
            }
            else
            {
                delta.Added.Add(newRegion);
            }
        }

        foreach (var region in oldLayout.Regions)
        {
            var key = (region.Type, region.Name);
            if (!matchedOldKeys.Contains(key))
                delta.Removed.Add(region);
        }

        return delta;
    }

    /// <summary>
    /// Render delta as compact LLM text.
    /// </summary>
    public string Render()
    {
        var sb = new StringBuilder();

        int totalRegions = Added.Count + Removed.Count + Changed.Count + Unchanged.Count;
        int changedCount = Added.Count + Removed.Count + Changed.Count;

        sb.AppendLine($"Delta: {changedCount} changed, {Unchanged.Count} unchanged (of {totalRegions} regions)");

        if (Added.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Added:");
            foreach (var region in Added)
                sb.AppendLine($"  + [{region.Type}] {region.Name ?? ""}");
        }

        if (Removed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Removed:");
            foreach (var region in Removed)
                sb.AppendLine($"  - [{region.Type}] {region.Name ?? ""}");
        }

        if (Changed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Changed:");
            foreach (var (_, newRegion) in Changed)
                sb.AppendLine($"  ~ {SceneRenderer.RenderRegionDetail(newRegion)}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>True if nothing changed between the two layouts.</summary>
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;

    /// <summary>
    /// Compute a hash for a region based on its texts (for change detection).
    /// </summary>
    private static ulong ComputeRegionHash(LayoutRegionResult region)
    {
        var hasher = new XxHash64();
        hasher.Append(Encoding.UTF8.GetBytes(region.Type));
        hasher.Append(BitConverter.GetBytes(region.Bounds.X));
        hasher.Append(BitConverter.GetBytes(region.Bounds.Y));
        hasher.Append(BitConverter.GetBytes(region.Bounds.Width));
        hasher.Append(BitConverter.GetBytes(region.Bounds.Height));

        foreach (var text in region.Texts)
        {
            hasher.Append(Encoding.UTF8.GetBytes(text.Text));
            hasher.Append(BitConverter.GetBytes(text.X));
            hasher.Append(BitConverter.GetBytes(text.Y));
        }

        return hasher.GetCurrentHashAsUInt64();
    }
}
