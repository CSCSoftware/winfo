namespace WinfoMain.Common;

/// <summary>
/// Central log for the library.
///
/// Background: System.Diagnostics.Debug.WriteLine is marked [Conditional("DEBUG")]
/// and is removed entirely in Release builds. A published MCP server would then
/// have no diagnostic output at all -- errors vanish without a trace.
/// WinfoLog.Write is kept in Release and can be forwarded to the host via a
/// sink (MCP server: stderr).
/// </summary>
public static class WinfoLog
{
    /// <summary>
    /// Target for log messages. The host sets this e.g. to Console.Error.WriteLine.
    /// Null = debug output only.
    /// </summary>
    public static Action<string>? Sink { get; set; }

    /// <summary>Writes to the debug output and to the sink, if one is set.</summary>
    public static void Write(string message)
    {
        System.Diagnostics.Debug.WriteLine(message);

        var sink = Sink;
        if (sink == null) return;

        // Logging must never interfere with the actual logic.
        try { sink(message); }
        catch { }
    }
}
