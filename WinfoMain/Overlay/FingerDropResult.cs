namespace WinfoMain.Overlay;

using System.Drawing;

/// <summary>
/// Result of a finger drop — the screen position where the user released the finger.
/// Consumers (MCP Server, custom apps) use this to query what's at the position.
/// </summary>
public record FingerDropResult(Point ScreenPosition);
