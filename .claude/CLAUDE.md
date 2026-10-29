# winfo - Windows UI MCP server

winfo is an MCP server (stdio) that lets AI agents see and operate the Windows desktop: windows, UI Automation
trees, OCR text, clipboard, browser tabs, registry and system info, plus mouse/keyboard/window control.
Public open-source project (MIT, CSC-Software GmbH). Optional companion of the desktop app Vibe Aigent.

## Structure

```
WinfoMain/      core library: desktop model and pipeline (UIA + OCR scene engine), element ids/resolvers,
                browser, URL memory (SQLite), system info, finger overlay (shows the user where the agent points)
MCPServer/      the MCP server exe (Program.cs + Tools/*.cs = MCP tools via [McpServerTool] attributes)
Test/           WinForms inspector app for debugging the pipeline
Assets/         images (own artwork)
```

## Rules

- **English only**: code, comments, tool descriptions, messages, docs.
- Tool descriptions are read by LLMs: keep them precise and state side effects.
- **Input control** (click, type, keys, scroll, set_value, window changes, clipboard writes) acts on the user's real
  desktop. Never add such a tool without marking it clearly in its description; clients are expected to ask the user
  before every call.
- No personal data, absolute personal paths or machine-specific settings in the repo.
- Data: `%APPDATA%\winfo\urls.db` (URL memory). The old folder `%APPDATA%\WinfoMCP` is only read once for migration.

## Build & publish

```bash
dotnet build WinfoMain.sln -c Release
dotnet publish MCPServer -c Release -p:PublishProfile=win-x64   # self-contained single exe in publish\
```

Trimming is not possible (Windows Forms, UIA COM interop). Target: `net8.0-windows10.0.19041.0`.
