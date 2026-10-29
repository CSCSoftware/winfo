namespace WinfoMain.MCPServer.Tools;

using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using ModelContextProtocol.Server;
using WinfoMain.Common;
using WinfoMain.Desktop.SceneEngine;
using WinfoMain.Desktop.UIA;
using WinfoMain.MCP;
using WinfoMain.Overlay;

/// <summary>
/// Action tools for clicking, typing, scrolling, and clipboard.
/// Uses keybd_event / mouse_event (legacy APIs) — more reliable than SendInput in .NET 8.
/// </summary>
[McpServerToolType]
public static class ActionTools
{
    [McpServerTool(Name = "click_element")]
    [Description("Click a UI element. For UIA elements, tries InvokePattern first, falls back to mouse click. For OCR/region elements, clicks at the center position.")]
    public static string ClickElement(
        [Description("Element ID to click (eid:u:..., eid:o:..., or eid:r:...)")] string eid,
        [Description("Click type: 'left' (default), 'right', 'double'")] string button = "left")
    {
        PipelineHelper.Wake();
        // Try UIA InvokePattern first for UIA elements (left click only)
        if (ElementId.IsUia(eid) && button == "left")
        {
            var uiaResult = ElementResolver.ResolveUia(eid);
            if (uiaResult.Success)
            {
                var invoke = uiaResult.Value!.TryGetPattern<InvokePattern>(InvokePattern.Pattern);
                if (invoke != null)
                {
                    try
                    {
                        invoke.Invoke();
                        return $"Invoked {ElementSerializer.FormatNode(uiaResult.Value)}";
                    }
                    catch { /* fall through to mouse click */ }
                }
            }
        }

        // Resolve to screen point and click via mouse_event
        var pointResult = ElementResolver.ResolveToScreenPoint(eid);
        if (!pointResult.Success)
            return pointResult.Error!;

        var pt = pointResult.Value;
        FingerOverlay.Instance?.ClickAt(pt.X, pt.Y);
        Thread.Sleep(150); // let user see the finger before click
        ClickAt(pt.X, pt.Y, button);

        return $"Clicked {button} at ({pt.X}, {pt.Y})";
    }

    [McpServerTool(Name = "type_text")]
    [Description("Type text using keyboard input. Supports special keys in braces: {ENTER}, {TAB}, {ESCAPE}, {BACKSPACE}, {DELETE}, {UP}, {DOWN}, {LEFT}, {RIGHT}, {HOME}, {END}, {PGUP}, {PGDN}, {F1}-{F12}.")]
    public static string TypeText(
        [Description("Text to type. Use {ENTER} for Enter, {TAB} for Tab, etc.")] string text,
        [Description("Optional: window to focus before typing.")] string? window = null)
    {
        PipelineHelper.Wake();
        if (!string.IsNullOrEmpty(window))
        {
            var (win, error) = WindowResolver.Resolve(window);
            if (win == null)
                return error!;
            FocusWindowInternal(win);
        }

        int charCount = TypeViaKeybdEvent(text);
        return $"Typed {charCount} character(s)";
    }

    [McpServerTool(Name = "send_keys")]
    [Description("Send keyboard shortcuts with modifier keys (Ctrl, Alt, Shift, Win). Use for hotkeys like 'ctrl+c', 'ctrl+shift+2', 'alt+f4', 'win+d'. For typing text, use type_text instead.")]
    public static string SendKeys(
        [Description("Key combination like 'ctrl+c', 'ctrl+shift+tab', 'alt+f4', 'win+d', 'ctrl+shift+2'. Modifiers: ctrl, alt, shift, win. Separate with '+'.")] string keys,
        [Description("Optional: window to focus before sending keys.")] string? window = null)
    {
        PipelineHelper.Wake();
        if (!string.IsNullOrEmpty(window))
        {
            var (win, error) = WindowResolver.Resolve(window);
            if (win == null)
                return error!;
            FocusWindowInternal(win);
        }

        var parts = keys.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "No keys specified.";

        // Separate modifiers from the main key
        var modifiers = new List<byte>();
        string? mainKey = null;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers.Add(VK_CONTROL);
                    break;
                case "alt":
                    modifiers.Add(VK_MENU);
                    break;
                case "shift":
                    modifiers.Add(VK_SHIFT);
                    break;
                case "win" or "windows" or "super":
                    modifiers.Add(VK_LWIN);
                    break;
                default:
                    mainKey = part;
                    break;
            }
        }

        if (mainKey == null)
            return $"No main key found in '{keys}'. Format: 'ctrl+c', 'alt+f4', etc.";

        // Resolve main key to VK code
        byte vk = ResolveKeyCode(mainKey);
        if (vk == 0)
            return $"Unknown key: '{mainKey}'. Use letter (a-z), digit (0-9), F1-F12, or special key names (enter, tab, escape, delete, home, end, pgup, pgdn, up, down, left, right, space).";

        // Press modifiers down (with scancode for apps that need it)
        foreach (var mod in modifiers)
        {
            keybd_event(mod, (byte)MapVirtualKey(mod, 0), 0, 0);
            Thread.Sleep(30);
        }

        Thread.Sleep(50);

        // Press and release main key
        byte scanMain = (byte)MapVirtualKey(vk, 0);
        keybd_event(vk, scanMain, 0, 0);
        Thread.Sleep(30);
        keybd_event(vk, scanMain, KEYEVENTF_KEYUP, 0);

        Thread.Sleep(50);

        // Release modifiers in reverse order
        for (int i = modifiers.Count - 1; i >= 0; i--)
        {
            keybd_event(modifiers[i], (byte)MapVirtualKey(modifiers[i], 0), KEYEVENTF_KEYUP, 0);
            Thread.Sleep(30);
        }

        var modNames = parts.Take(parts.Length - 1).Select(p => p.ToUpperInvariant());
        return $"Sent {string.Join("+", modNames)}+{mainKey.ToUpperInvariant()}";
    }

    /// <summary>Resolve a key name to a virtual key code.</summary>
    private static byte ResolveKeyCode(string key)
    {
        // Single character: letter or digit
        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c >= 'A' && c <= 'Z') return (byte)c;           // VK_A..VK_Z = 0x41..0x5A
            if (c >= '0' && c <= '9') return (byte)c;           // VK_0..VK_9 = 0x30..0x39
        }

        // Named keys
        return key.ToUpperInvariant() switch
        {
            "ENTER" or "RETURN"     => 0x0D,
            "TAB"                   => 0x09,
            "ESCAPE" or "ESC"       => 0x1B,
            "BACKSPACE" or "BS"     => 0x08,
            "DELETE" or "DEL"       => 0x2E,
            "SPACE"                 => 0x20,
            "UP"                    => 0x26,
            "DOWN"                  => 0x28,
            "LEFT"                  => 0x25,
            "RIGHT"                 => 0x27,
            "HOME"                  => 0x24,
            "END"                   => 0x23,
            "PAGEUP" or "PGUP"      => 0x21,
            "PAGEDOWN" or "PGDN"    => 0x22,
            "INSERT" or "INS"       => 0x2D,
            "PRINTSCREEN" or "PRTSC" => 0x2C,
            "F1"  => 0x70, "F2"  => 0x71, "F3"  => 0x72, "F4"  => 0x73,
            "F5"  => 0x74, "F6"  => 0x75, "F7"  => 0x76, "F8"  => 0x77,
            "F9"  => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
            _ => 0
        };
    }

    [McpServerTool(Name = "set_value")]
    [Description("Set the value of a UI element using UIA ValuePattern. Works for text fields, combo boxes, etc. Only works with UIA elements (eid:u:...).")]
    public static string SetValue(
        [Description("UIA element ID (eid:u:...)")] string eid,
        [Description("Value to set")] string value)
    {
        PipelineHelper.Wake();
        if (!ElementId.IsUia(eid))
            return "set_value only works with UIA elements (eid:u:...). For OCR elements, use type_text instead.";

        var result = ElementResolver.ResolveUia(eid);
        if (!result.Success)
            return result.Error!;

        var node = result.Value!;
        var valuePattern = node.TryGetPattern<ValuePattern>(ValuePattern.Pattern);
        if (valuePattern == null)
            return $"Element does not support ValuePattern. Use type_text instead.\n{ElementSerializer.FormatNode(node)}";

        try
        {
            valuePattern.SetValue(value);
            return $"Set value to \"{value}\" on {ElementSerializer.FormatNode(node)}";
        }
        catch (Exception ex)
        {
            return $"SetValue failed: {ex.Message}";
        }
    }

    [McpServerTool(Name = "focus_window")]
    [Description("Bring a window to the foreground. Uses AttachThreadInput for reliable focus switching.")]
    public static string FocusWindow(
        [Description("Window specifier: 'active', process name, title substring, or hex hwnd.")] string window)
    {
        PipelineHelper.Wake();
        var (win, error) = WindowResolver.Resolve(window);
        if (win == null)
            return error!;

        FocusWindowInternal(win);

        var success = GetForegroundWindow() == win.Hwnd;
        return success
            ? $"Focused {ElementSerializer.FormatWindowHeader(win)}"
            : $"Attempted focus on {ElementSerializer.FormatWindowHeader(win)} (OS may have blocked foreground switch).";
    }

    [McpServerTool(Name = "scroll")]
    [Description("Scroll within a window or element. Uses UIA ScrollPattern if available, otherwise sends mouse wheel events. Use size='page' for large scrolls (like Page Down).")]
    public static async Task<string> Scroll(
        [Description("Direction: 'up', 'down', 'left', 'right'")] string direction,
        [Description("Number of scroll steps (default: 3)")] int steps = 3,
        [Description("Scroll size: 'small' (default, ~20px per step) or 'page' (full page per step via LargeIncrement)")] string size = "small",
        [Description("Optional: element ID to scroll within.")] string? eid = null,
        [Description("Optional: window to scroll in (if no eid).")] string? window = null)
    {
        PipelineHelper.Wake();
        // Try UIA ScrollPattern on element
        if (!string.IsNullOrEmpty(eid) && ElementId.IsUia(eid))
        {
            var result = ElementResolver.ResolveUia(eid);
            if (result.Success)
            {
                var scrollPattern = result.Value!.TryGetPattern<ScrollPattern>(ScrollPattern.Pattern);
                if (scrollPattern != null)
                {
                    try
                    {
                        bool large = size.Equals("page", StringComparison.OrdinalIgnoreCase);
                        for (int i = 0; i < steps; i++)
                        {
                            switch (direction.ToLower())
                            {
                                case "up": scrollPattern.ScrollVertical(large ? ScrollAmount.LargeDecrement : ScrollAmount.SmallDecrement); break;
                                case "down": scrollPattern.ScrollVertical(large ? ScrollAmount.LargeIncrement : ScrollAmount.SmallIncrement); break;
                                case "left": scrollPattern.ScrollHorizontal(large ? ScrollAmount.LargeDecrement : ScrollAmount.SmallDecrement); break;
                                case "right": scrollPattern.ScrollHorizontal(large ? ScrollAmount.LargeIncrement : ScrollAmount.SmallIncrement); break;
                            }
                        }
                        var sizeLabel = large ? "page" : "small";
                        return $"Scrolled {direction} {steps}x ({sizeLabel}) via ScrollPattern on {ElementSerializer.FormatNode(result.Value)}";
                    }
                    catch { /* fall through to mouse wheel */ }
                }
            }
        }

        // Auto-find ScrollPattern in window (when no eid but window is specified)
        if (string.IsNullOrEmpty(eid) && !string.IsNullOrEmpty(window))
        {
            var (win, _) = WindowResolver.Resolve(window);
            if (win != null)
            {
                var uiaRoot = await SceneBuilder.Instance.GetOrLoadUiaRootAsync(win);
                if (uiaRoot != null)
                {
                    var scrollNode = FindScrollableNode(uiaRoot);
                    if (scrollNode != null)
                    {
                        var scrollPattern = scrollNode.TryGetPattern<ScrollPattern>(ScrollPattern.Pattern);
                        if (scrollPattern != null)
                        {
                            try
                            {
                                bool large = size.Equals("page", StringComparison.OrdinalIgnoreCase);
                                for (int i = 0; i < steps; i++)
                                {
                                    switch (direction.ToLower())
                                    {
                                        case "up": scrollPattern.ScrollVertical(large ? ScrollAmount.LargeDecrement : ScrollAmount.SmallDecrement); break;
                                        case "down": scrollPattern.ScrollVertical(large ? ScrollAmount.LargeIncrement : ScrollAmount.SmallIncrement); break;
                                        case "left": scrollPattern.ScrollHorizontal(large ? ScrollAmount.LargeDecrement : ScrollAmount.SmallDecrement); break;
                                        case "right": scrollPattern.ScrollHorizontal(large ? ScrollAmount.LargeIncrement : ScrollAmount.SmallIncrement); break;
                                    }
                                }
                                var sizeLabel = large ? "page" : "small";
                                return $"Scrolled {direction} {steps}x ({sizeLabel}) via ScrollPattern on {ElementSerializer.FormatNode(scrollNode)}";
                            }
                            catch { /* fall through to mouse wheel */ }
                        }
                    }
                }
            }
        }

        // Mouse wheel fallback — find target position
        Point target;
        if (!string.IsNullOrEmpty(eid))
        {
            var ptResult = ElementResolver.ResolveToScreenPoint(eid);
            if (!ptResult.Success)
                return ptResult.Error!;
            target = ptResult.Value;
        }
        else
        {
            var (win, error) = WindowResolver.Resolve(window ?? "active");
            if (win == null)
                return error!;
            target = new Point(win.Bounds.X + win.Bounds.Width / 2, win.Bounds.Y + win.Bounds.Height / 2);
        }

        // Move cursor and send wheel via mouse_event (one event per step)
        SetCursorPos(target.X, target.Y);
        Thread.Sleep(50);

        bool vertical = direction.Equals("up", StringComparison.OrdinalIgnoreCase)
                     || direction.Equals("down", StringComparison.OrdinalIgnoreCase);
        bool negative = direction.Equals("up", StringComparison.OrdinalIgnoreCase)
                     || direction.Equals("left", StringComparison.OrdinalIgnoreCase);

        uint flag = vertical ? MOUSEEVENTF_WHEEL : MOUSEEVENTF_HWHEEL;
        bool largeMouse = size.Equals("page", StringComparison.OrdinalIgnoreCase);
        int perStep = largeMouse ? 120 * 10 : 120; // page = ~10 lines per step
        uint delta = (uint)(negative ? -perStep : perStep);

        for (int i = 0; i < steps; i++)
        {
            mouse_event(flag, 0, 0, delta, 0);
            if (steps > 1) Thread.Sleep(50);
        }

        return $"Scrolled {direction} {steps}x ({(largeMouse ? "page" : "small")}) at ({target.X}, {target.Y})";
    }

    [McpServerTool(Name = "get_clipboard")]
    [Description("Get the current clipboard text content.")]
    public static string GetClipboard()
    {
        string? text = null;
        var thread = new Thread(() =>
        {
            try { text = System.Windows.Forms.Clipboard.GetText(); }
            catch { text = null; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(2000);

        return string.IsNullOrEmpty(text)
            ? "Clipboard is empty or does not contain text."
            : text;
    }

    [McpServerTool(Name = "set_clipboard")]
    [Description("Set the clipboard text content.")]
    public static string SetClipboard(
        [Description("Text to copy to clipboard")] string text)
    {
        var thread = new Thread(() =>
        {
            try { System.Windows.Forms.Clipboard.SetText(text); }
            catch { }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(2000);

        return $"Copied {text.Length} character(s) to clipboard.";
    }

    [McpServerTool(Name = "point_at")]
    [Description("Show the pointer finger pointing at a UI element or screen position. Visual indicator only — no click. Use to show the user what you mean.")]
    public static string PointAtElement(
        [Description("Element ID to point at (eid:u:..., eid:o:..., or eid:r:...). Optional if x/y given.")] string? eid = null,
        [Description("Optional: screen X coordinate (if no eid).")] int? x = null,
        [Description("Optional: screen Y coordinate (if no eid).")] int? y = null)
    {
        PipelineHelper.Wake();
        var finger = FingerOverlay.Instance;
        if (finger == null)
            return "Finger overlay not available.";

        if (!string.IsNullOrEmpty(eid))
        {
            var ptResult = ElementResolver.ResolveToScreenPoint(eid);
            if (!ptResult.Success)
                return ptResult.Error!;

            // Bring target window to front so finger is visible on uncovered window
            if (ptResult.Window != null && GetForegroundWindow() != ptResult.Window.Hwnd)
            {
                FocusWindowInternal(ptResult.Window);
            }

            var pt = ptResult.Value;
            finger.PointAt(pt.X, pt.Y);
            return $"Pointing at ({pt.X}, {pt.Y})";
        }

        if (x.HasValue && y.HasValue)
        {
            finger.PointAt(x.Value, y.Value);
            return $"Pointing at ({x.Value}, {y.Value})";
        }

        return "Provide either an element ID (eid) or screen coordinates (x, y).";
    }

    [McpServerTool(Name = "hide_finger")]
    [Description("Hide the finger overlay. Removes the finger from screen.")]
    public static string HideFinger()
    {
        var finger = FingerOverlay.Instance;
        if (finger == null)
            return "Finger overlay not available.";

        finger.Hide();
        return "Finger hidden.";
    }

    [McpServerTool(Name = "let_user_point")]
    [Description("Let the user drag the pointer finger to a screen position. The finger stays visible after drop. Returns coordinates and what's at that position (window, region, text). Right-click or ESC to cancel.")]
    public static async Task<string> LetUserPoint(
        [Description("Optional: prompt message to show the user.")] string? prompt = null)
    {
        PipelineHelper.Wake();
        var finger = FingerOverlay.Instance;
        if (finger == null)
            return "Finger overlay not available.";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await finger.LetUserPointAsync(cts.Token);

        if (result == null)
            return "User cancelled.";

        var pt = result.Value;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"User pointed at ({pt.X}, {pt.Y})");

        // Find the window under the finger (exclude own process to skip overlay)
        uint ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        var targetWindow = WinfoMain.Desktop.DesktopQueryService.FindWindowAt(
            new System.Drawing.Point(pt.X, pt.Y), ownPid);

        if (targetWindow != null)
        {
            // Run pipeline on target window as if it were the active window
            await PipelineHelper.RunPipelineAsync(targetWindow);

            var queryResult = WinfoMain.Desktop.DesktopQueryService.QueryWindowAt(
                targetWindow, new System.Drawing.Point(pt.X, pt.Y));

            sb.AppendLine($"Window: [{queryResult.Window.ProcessName}] \"{queryResult.Window.Title}\"");
            if (queryResult.UiaElement != null)
                sb.AppendLine($"UIA Element: {WinfoMain.MCP.ElementSerializer.FormatNode(queryResult.UiaElement)}");
            if (queryResult.Region != null)
                sb.AppendLine($"Region: [{queryResult.Region.Type}] \"{queryResult.Region.Name}\"");
            if (queryResult.Word != null)
                sb.AppendLine($"Text: \"{queryResult.Word}\"");
            else if (queryResult.Line != null)
                sb.AppendLine($"Line: \"{queryResult.Line}\"");
        }

        return sb.ToString().TrimEnd();
    }

    // === Input simulation via legacy APIs (keybd_event / mouse_event) ===

    private static void ClickAt(int x, int y, string button)
    {
        SetCursorPos(x, y);
        Thread.Sleep(50);

        uint downFlag, upFlag;
        switch (button.ToLower())
        {
            case "right":
                downFlag = MOUSEEVENTF_RIGHTDOWN;
                upFlag = MOUSEEVENTF_RIGHTUP;
                break;
            default: // left and double
                downFlag = MOUSEEVENTF_LEFTDOWN;
                upFlag = MOUSEEVENTF_LEFTUP;
                break;
        }

        int clicks = button.Equals("double", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        for (int i = 0; i < clicks; i++)
        {
            mouse_event(downFlag, 0, 0, 0, 0);
            Thread.Sleep(30);
            mouse_event(upFlag, 0, 0, 0, 0);
            if (clicks > 1 && i == 0) Thread.Sleep(50);
        }
    }

    /// <summary>
    /// Type text via keybd_event with VK codes + scancodes.
    /// Works in all apps including OpenGL (Blender) which ignore KEYEVENTF_UNICODE.
    /// Uses VkKeyScanW to resolve characters to VK + shift state.
    /// </summary>
    private static int TypeViaKeybdEvent(string text)
    {
        int charCount = 0;
        int i = 0;

        while (i < text.Length)
        {
            // Special keys in braces: {ENTER}, {TAB}, etc.
            if (text[i] == '{')
            {
                var end = text.IndexOf('}', i + 1);
                if (end > i)
                {
                    var key = text[(i + 1)..end].ToUpperInvariant();
                    var vk = MapSpecialKey(key);
                    if (vk != 0)
                    {
                        byte scan = (byte)MapVirtualKey(vk, 0);
                        keybd_event(vk, scan, 0, 0);
                        Thread.Sleep(20);
                        keybd_event(vk, scan, KEYEVENTF_KEYUP, 0);
                        Thread.Sleep(20);
                    }
                    i = end + 1;
                    charCount++;
                    continue;
                }
            }

            // Regular character: resolve via VkKeyScanW
            short vkScan = VkKeyScanW(text[i]);
            if (vkScan != -1)
            {
                byte vkCode = (byte)(vkScan & 0xFF);
                byte shiftState = (byte)((vkScan >> 8) & 0xFF);
                byte scanCode = (byte)MapVirtualKey(vkCode, 0);

                // Press modifiers if needed
                bool needShift = (shiftState & 1) != 0;
                bool needCtrl  = (shiftState & 2) != 0;
                bool needAlt   = (shiftState & 4) != 0;

                if (needShift) keybd_event(VK_SHIFT, (byte)MapVirtualKey(VK_SHIFT, 0), 0, 0);
                if (needCtrl)  keybd_event(VK_CONTROL, (byte)MapVirtualKey(VK_CONTROL, 0), 0, 0);
                if (needAlt)   keybd_event(VK_MENU, (byte)MapVirtualKey(VK_MENU, 0), 0, 0);

                keybd_event(vkCode, scanCode, 0, 0);
                Thread.Sleep(20);
                keybd_event(vkCode, scanCode, KEYEVENTF_KEYUP, 0);

                if (needAlt)   keybd_event(VK_MENU, (byte)MapVirtualKey(VK_MENU, 0), KEYEVENTF_KEYUP, 0);
                if (needCtrl)  keybd_event(VK_CONTROL, (byte)MapVirtualKey(VK_CONTROL, 0), KEYEVENTF_KEYUP, 0);
                if (needShift) keybd_event(VK_SHIFT, (byte)MapVirtualKey(VK_SHIFT, 0), KEYEVENTF_KEYUP, 0);
            }
            else
            {
                // Fallback for chars without VK mapping (rare Unicode): use KEYEVENTF_UNICODE
                var scan = (byte)(text[i] & 0xFF);
                keybd_event(0, scan, KEYEVENTF_UNICODE, 0);
                keybd_event(0, scan, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, 0);
            }

            i++;
            charCount++;
            Thread.Sleep(20);
        }

        return charCount;
    }

    /// <summary>Focus window using AttachThreadInput trick for reliable foreground switch.</summary>
    private static void FocusWindowInternal(DesktopWindow win)
    {
        if (IsIconic(win.Hwnd))
            ShowWindow(win.Hwnd, SW_RESTORE);

        var currentThreadId = GetCurrentThreadId();
        var foregroundHwnd = GetForegroundWindow();
        var foregroundThreadId = GetWindowThreadProcessId(foregroundHwnd, out _);
        GetWindowThreadProcessId(win.Hwnd, out var targetPid);

        AllowSetForegroundWindow((int)targetPid);

        bool attached = false;
        try
        {
            if (currentThreadId != foregroundThreadId)
                attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);

            SetForegroundWindow(win.Hwnd);
            BringWindowToTop(win.Hwnd);
            ShowWindow(win.Hwnd, SW_SHOW);
        }
        finally
        {
            if (attached)
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
        }

        Thread.Sleep(100);
    }

    private static byte MapSpecialKey(string name) => name switch
    {
        "ENTER" or "RETURN"     => 0x0D,
        "TAB"                   => 0x09,
        "ESCAPE" or "ESC"       => 0x1B,
        "BACKSPACE" or "BS"     => 0x08,
        "DELETE" or "DEL"       => 0x2E,
        "UP"                    => 0x26,
        "DOWN"                  => 0x28,
        "LEFT"                  => 0x25,
        "RIGHT"                 => 0x27,
        "HOME"                  => 0x24,
        "END"                   => 0x23,
        "PAGEUP" or "PGUP"      => 0x21,
        "PAGEDOWN" or "PGDN"    => 0x22,
        "F1"  => 0x70, "F2"  => 0x71, "F3"  => 0x72, "F4"  => 0x73,
        "F5"  => 0x74, "F6"  => 0x75, "F7"  => 0x76, "F8"  => 0x77,
        "F9"  => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
        _ => 0
    };

    // === Constants ===
    private const uint MOUSEEVENTF_LEFTDOWN  = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP    = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP   = 0x0010;
    private const uint MOUSEEVENTF_WHEEL     = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL    = 0x1000;
    private const uint KEYEVENTF_KEYUP       = 0x0002;
    private const uint KEYEVENTF_UNICODE     = 0x0004;
    private const byte VK_SHIFT              = 0x10;
    private const byte VK_CONTROL            = 0x11;
    private const byte VK_MENU               = 0x12; // Alt
    private const byte VK_LWIN               = 0x5B;
    private const int SW_RESTORE = 9;
    private const int SW_SHOW    = 5;

    // === P/Invoke ===

    /// <summary>
    /// Find the largest scrollable node in the UIA tree (by area).
    /// Prefers the main content scroll viewer over small nav panels.
    /// </summary>
    private static UIATreeNode? FindScrollableNode(UIATreeNode root)
    {
        UIATreeNode? best = null;
        int bestArea = 0;
        FindScrollableRecursive(root, ref best, ref bestArea);
        return best;
    }

    private static void FindScrollableRecursive(UIATreeNode node, ref UIATreeNode? best, ref int bestArea)
    {
        if (node.SupportedPatterns.Contains("Scroll"))
        {
            var r = node.BoundingRectangle;
            int area = r.Width * r.Height;
            if (area > bestArea)
            {
                best = node;
                bestArea = area;
            }
        }
        foreach (var child in node.Children)
            FindScrollableRecursive(child, ref best, ref bestArea);
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, nuint dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char ch);
}
