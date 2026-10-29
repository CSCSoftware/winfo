namespace WinfoMain.MCP;

/// <summary>
/// Encodes and decodes Element IDs for MCP tools.
/// Three types:
///   eid:u:42.1234.5678       — UIA element (RuntimeId as dot-separated)
///   eid:o:A1B2:3:17           — OCR pseudo-element (hwnd hex : region index : text index)
///   eid:r:A1B2:0              — Layout region (hwnd hex : region index)
/// </summary>
public static class ElementId
{
    private const string Prefix = "eid:";
    private const string UiaPrefix = "eid:u:";
    private const string OcrPrefix = "eid:o:";
    private const string RegionPrefix = "eid:r:";

    // --- Encoding ---

    /// <summary>Create an element ID for a UIA node (from its RuntimeId).</summary>
    public static string FromUia(int[] runtimeId)
        => UiaPrefix + string.Join(".", runtimeId);

    /// <summary>Create an element ID for an OCR pseudo-element.</summary>
    public static string FromOcr(nint hwnd, int regionIndex, int textIndex)
        => $"{OcrPrefix}{hwnd:X}:{regionIndex}:{textIndex}";

    /// <summary>Create an element ID for a layout region.</summary>
    public static string FromRegion(nint hwnd, int regionIndex)
        => $"{RegionPrefix}{hwnd:X}:{regionIndex}";

    // --- Type checking ---

    public static bool IsValid(string eid) => eid.StartsWith(Prefix);
    public static bool IsUia(string eid) => eid.StartsWith(UiaPrefix);
    public static bool IsOcr(string eid) => eid.StartsWith(OcrPrefix);
    public static bool IsRegion(string eid) => eid.StartsWith(RegionPrefix);

    // --- Decoding ---

    /// <summary>
    /// Parse a UIA element ID into its RuntimeId components.
    /// Returns null if the format is invalid.
    /// </summary>
    public static int[]? ParseUia(string eid)
    {
        if (!IsUia(eid)) return null;
        var parts = eid[UiaPrefix.Length..].Split('.');
        var result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out result[i]))
                return null;
        }
        return result;
    }

    /// <summary>
    /// Parse an OCR element ID into (hwnd, regionIndex, textIndex).
    /// Returns null if the format is invalid.
    /// </summary>
    public static (nint Hwnd, int RegionIndex, int TextIndex)? ParseOcr(string eid)
    {
        if (!IsOcr(eid)) return null;
        var parts = eid[OcrPrefix.Length..].Split(':');
        if (parts.Length != 3) return null;
        if (!TryParseHex(parts[0], out var hwnd)) return null;
        if (!int.TryParse(parts[1], out var regionIndex)) return null;
        if (!int.TryParse(parts[2], out var textIndex)) return null;
        return (hwnd, regionIndex, textIndex);
    }

    /// <summary>
    /// Parse a region element ID into (hwnd, regionIndex).
    /// Returns null if the format is invalid.
    /// </summary>
    public static (nint Hwnd, int RegionIndex)? ParseRegion(string eid)
    {
        if (!IsRegion(eid)) return null;
        var parts = eid[RegionPrefix.Length..].Split(':');
        if (parts.Length != 2) return null;
        if (!TryParseHex(parts[0], out var hwnd)) return null;
        if (!int.TryParse(parts[1], out var regionIndex)) return null;
        return (hwnd, regionIndex);
    }

    private static bool TryParseHex(string hex, out nint value)
    {
        value = 0;
        if (!long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var parsed))
            return false;
        value = (nint)parsed;
        return true;
    }
}
