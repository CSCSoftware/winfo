namespace WinfoMain.MCPServer.Tools;

using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using WinfoMain.Memory;

[McpServerToolType]
public static class UrlTools
{
    [McpServerTool(Name = "remember_url")]
    [Description("Save a URL for later recall. If the URL already exists, updates the visit count. Tags are comma-separated.")]
    public static string RememberUrl(
        [Description("URL to remember")] string url,
        [Description("Optional: descriptive title")] string? title = null,
        [Description("Optional: comma-separated tags (e.g. 'docs,api,reference')")] string? tags = null)
    {
        try
        {
            return UrlMemoryService.Instance.RememberUrl(url, title, tags);
        }
        catch (Exception ex)
        {
            return $"Error saving URL: {ex.Message}";
        }
    }

    [McpServerTool(Name = "recall_url")]
    [Description("Search saved URLs by topic. Matches against tags, titles, and URL substrings.")]
    public static string RecallUrl(
        [Description("Topic to search for (tag name, title substring, or URL substring)")] string topic)
    {
        try
        {
            var results = UrlMemoryService.Instance.RecallUrl(topic);
            if (results.Count == 0)
                return $"No URLs found for \"{topic}\".";

            var sb = new StringBuilder();
            sb.AppendLine($"Found {results.Count} URL(s) for \"{topic}\":");
            foreach (var (url, title, tags, visits, lastVisited) in results)
            {
                sb.Append($"  {url}");
                if (!string.IsNullOrEmpty(title))
                    sb.Append($"  \"{title}\"");
                if (!string.IsNullOrEmpty(tags))
                    sb.Append($"  [{tags}]");
                sb.AppendLine($"  ({visits} visits, last: {lastVisited})");
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Error recalling URL: {ex.Message}";
        }
    }

    [McpServerTool(Name = "list_urls")]
    [Description("List saved URLs, optionally filtered by tag.")]
    public static string ListUrls(
        [Description("Optional: filter by tag")] string? tag = null,
        [Description("Maximum number of URLs to return (default: 20)")] int limit = 20)
    {
        try
        {
            var results = UrlMemoryService.Instance.ListUrls(tag, limit);
            if (results.Count == 0)
            {
                return string.IsNullOrEmpty(tag)
                    ? "No saved URLs."
                    : $"No URLs tagged \"{tag}\".";
            }

            var sb = new StringBuilder();
            var header = string.IsNullOrEmpty(tag)
                ? "Saved URLs:"
                : $"URLs tagged '{tag}':";
            sb.AppendLine($"{header} ({results.Count})");
            foreach (var (url, title, tags, visits, lastVisited) in results)
            {
                sb.Append($"  {url}");
                if (!string.IsNullOrEmpty(title))
                    sb.Append($"  \"{title}\"");
                if (!string.IsNullOrEmpty(tags))
                    sb.Append($"  [{tags}]");
                sb.AppendLine($"  ({visits} visits, last: {lastVisited})");
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Error listing URLs: {ex.Message}";
        }
    }

    [McpServerTool(Name = "forget_url")]
    [Description("Delete a saved URL by exact URL match.")]
    public static string ForgetUrl(
        [Description("Exact URL to forget")] string url)
    {
        try
        {
            return UrlMemoryService.Instance.ForgetUrl(url)
                ? $"Forgot: {url}"
                : $"URL not found: {url}";
        }
        catch (Exception ex)
        {
            return $"Error forgetting URL: {ex.Message}";
        }
    }
}
