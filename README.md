# winfo

**winfo** is a Model Context Protocol (MCP) server for Windows that lets an AI assistant
see and operate the desktop. It reads the screen through UI Automation (UIA), merges in
on-device OCR (`Windows.Media.Ocr`) where UIA is blind, and can control windows, mouse
and keyboard.

It is an optional companion of the desktop app **Vibe Aigent**, but works with any MCP
client that supports the stdio transport.

## How it works

Each window is analyzed in a three-stage pipeline:

1. **UIA** - structure: regions, bounds, control types.
2. **OCR merge** - fills empty UIA regions with recognized text.
3. **Text probe** - replaces OCR text with exact `TextPattern` text in editors and terminals.

Windows with almost no UIA information fall back to a pure OCR layout. Elements are
addressed by stable ids: `eid:u:...` (UIA element), `eid:o:...` (OCR text), `eid:r:...`
(layout region).

## Tools

| Category | Tools |
|----------|-------|
| Read | `list_windows`, `describe_screen`, `describe_window`, `read_text`, `search_text` |
| UI tree | `get_tree`, `find_element`, `get_element_details`, `get_focused`, `hittest` |
| Input / action | `click_element`, `type_text`, `send_keys`, `set_value`, `focus_window`, `scroll`, `get_clipboard`, `set_clipboard` |
| Observe | `wait_for_element`, `get_delta`, `wait_for_delta` |
| Window management | `move_window`, `show_window`, `snap_window`, `list_monitors`, `close_window` |
| Browser | `open_url`, `get_browser_url`, `list_browser_tabs`, `switch_browser_tab` (Chrome, Edge, Firefox) |
| System | `list_installed_apps`, `find_app`, `system_info`, `list_processes`, `read_registry`, `search_registry` |
| URL memory | `remember_url`, `recall_url`, `list_urls`, `forget_url` (local SQLite database) |
| Pointer overlay | `point_at`, `let_user_point`, `hide_finger` |

Notes:

- `type_text` types plain text (with `{ENTER}`, `{TAB}` etc.) and also works in OpenGL
  applications; `send_keys` sends shortcuts such as `ctrl+c` or `alt+f4`.
- `point_at` shows an on-screen pointer at an element or position; `let_user_point` lets
  the user drag that pointer to something and returns the position and text below it.
- Some Electron/Chromium UIs expose little or no UIA structure; icon-only buttons may not
  be recognized by OCR. Keyboard shortcuts are the usual workaround.

## Requirements

- Windows 10 (version 2004 / build 19041) or Windows 11
- Nothing else to run the released exe: it is self-contained (the .NET runtime parts it needs are
  bundled). To build from source: .NET 8 SDK (target `net8.0-windows10.0.19041.0`)
- An OCR language pack for the languages you want recognized (installed with most
  Windows display languages)

## Build and publish

```bash
dotnet publish MCPServer -c Release -p:PublishProfile=win-x64
```

This produces a single self-contained `WinfoMain.MCPServer.exe` in the `publish` folder (only English
resources, native libraries bundled and compressed). Copy it to a location of your choice, for example
`C:\Tools\winfo\`. Trimming is not used: Windows Forms and the UI Automation COM interop do not support it.

For development, `dotnet build` is enough; the debug build needs the .NET 8 Desktop Runtime.

The solution `WinfoMain.sln` also contains `WinfoMain` (core library) and `Test`
(a WinForms inspector app for debugging the pipeline).

## Data

winfo stores only its URL memory (`remember_url` and friends) in `%APPDATA%\winfo\urls.db`.
Nothing is sent anywhere; screenshots and OCR results stay in memory.

## Register in an MCP client

winfo uses the stdio transport. A generic `mcp.json` entry:

```json
{
  "mcpServers": {
    "winfo": {
      "command": "C:\\Tools\\winfo\\WinfoMain.MCPServer.exe"
    }
  }
}
```

Windows only lets a process inspect and control windows of the same or lower integrity
level. Running the server non-elevated works for normal applications; windows of elevated
(administrator) applications may be invisible or not controllable.

## Safety

**winfo acts on the real desktop of the logged-in user. There is no sandbox.**

- **Input and change tools act immediately and for real:** `click_element`, `type_text`,
  `send_keys`, `set_value`, `scroll`, `focus_window`, `move_window`, `show_window`,
  `snap_window`, `close_window`, `set_clipboard`, `open_url`, `switch_browser_tab`.
  A wrong click or keystroke can send a message, delete data or close an unsaved
  document. MCP clients should **always ask the user for confirmation** before calling
  these tools, and should not auto-approve them.
- **Do not use input tools while the user is working.** Focus changes and synthetic
  keystrokes can land in whatever window currently has focus.
- **Prompt injection:** everything winfo reads (window titles, UI text, OCR text,
  browser content, clipboard, registry values) is untrusted data. A web page or document
  can contain text written to manipulate the AI. Clients must treat screen content as
  data, never as instructions.
- **Sensitive information:** read tools can expose anything visible on screen, in the
  clipboard or in the registry, including passwords, messages and personal data. Only
  connect winfo to clients and models you trust with that information.
- `get_clipboard` reads whatever the user last copied.

## License

MIT - see [LICENSE](LICENSE). Copyright (c) 2026 CSC-Software GmbH.
