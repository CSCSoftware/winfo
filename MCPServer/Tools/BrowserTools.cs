namespace WinfoMain.MCPServer.Tools;

using System.ComponentModel;
using ModelContextProtocol.Server;
using WinfoMain.Browser;

[McpServerToolType]
public static class BrowserTools
{
    [McpServerTool(Name = "open_url")]
    [Description("Open a URL in the default or specified browser.")]
    public static string OpenUrl(
        [Description("URL to open (https:// prefix added automatically if missing)")] string url,
        [Description("Optional: browser to use ('chrome', 'msedge', 'firefox'). Default: system default.")] string? browser = null)
    {
        PipelineHelper.Wake();
        return BrowserService.Instance.OpenUrl(url, browser);
    }

    [McpServerTool(Name = "get_browser_url")]
    [Description("Read the current URL from a browser's address bar using the Accessibility Tree.")]
    public static string GetBrowserUrl(
        [Description("Optional: browser ('chrome', 'msedge', 'firefox'). Default: auto-detect first running browser.")] string? browser = null)
    {
        PipelineHelper.Wake();
        return BrowserService.Instance.GetCurrentUrl(browser);
    }

    [McpServerTool(Name = "list_browser_tabs")]
    [Description("List all open tabs in a browser window.")]
    public static string ListBrowserTabs(
        [Description("Optional: browser ('chrome', 'msedge', 'firefox'). Default: auto-detect.")] string? browser = null)
    {
        PipelineHelper.Wake();
        return BrowserService.Instance.ListTabs(browser);
    }

    [McpServerTool(Name = "switch_browser_tab")]
    [Description("Switch to a browser tab by title substring or 1-based index. Uses SelectionItemPattern, InvokePattern, or mouse click fallback.")]
    public static string SwitchBrowserTab(
        [Description("Tab title substring or 1-based index number")] string title_or_index,
        [Description("Optional: browser ('chrome', 'msedge', 'firefox'). Default: auto-detect.")] string? browser = null)
    {
        PipelineHelper.Wake();
        return BrowserService.Instance.SwitchTab(title_or_index, browser);
    }
}
