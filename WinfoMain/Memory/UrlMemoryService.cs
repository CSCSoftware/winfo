namespace WinfoMain.Memory;

using Microsoft.Data.Sqlite;

/// <summary>
/// Persistent URL memory using SQLite.
/// Stores URLs with titles and tags for cross-session recall.
/// Database: %APPDATA%\winfo\urls.db (taken over once from the old %APPDATA%\WinfoMCP folder, which stays untouched).
/// </summary>
public class UrlMemoryService : IDisposable
{
    public static UrlMemoryService Instance { get; } = new();

    private readonly SqliteConnection _connection;
    private readonly object _lock = new();

    private UrlMemoryService()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appDataDir = Path.Combine(roaming, "winfo");
        Directory.CreateDirectory(appDataDir);

        var dbPath = Path.Combine(appDataDir, "urls.db");
        CopyLegacyDatabase(Path.Combine(roaming, "WinfoMCP"), appDataDir);
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        InitializeDatabase();

        // PRAGMAs after schema setup — foreign_keys needs tables to exist on first run
        using var pragmaCmd = _connection.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode=WAL;";
        pragmaCmd.ExecuteNonQuery();
    }

    /// <summary>First start under the new name: copy the URL database of the old folder (incl. WAL files), never move it.</summary>
    private static void CopyLegacyDatabase(string legacyDir, string dir)
    {
        if (File.Exists(Path.Combine(dir, "urls.db")) || !File.Exists(Path.Combine(legacyDir, "urls.db"))) return;
        try
        {
            foreach (var name in new[] { "urls.db", "urls.db-wal", "urls.db-shm" })
                if (File.Exists(Path.Combine(legacyDir, name)))
                    File.Copy(Path.Combine(legacyDir, name), Path.Combine(dir, name));
        }
        catch (IOException) { /* start with an empty database */ }
    }

    /// <summary>
    /// Save or update a URL. Increments visit_count if URL already exists.
    /// </summary>
    public string RememberUrl(string url, string? title, string? tags)
    {
        lock (_lock)
        {
            // Check if URL exists
            using var checkCmd = _connection.CreateCommand();
            checkCmd.CommandText = "SELECT id, visit_count FROM urls WHERE url = @url";
            checkCmd.Parameters.AddWithValue("@url", url);

            using var reader = checkCmd.ExecuteReader();
            long urlId;

            if (reader.Read())
            {
                // Update existing
                urlId = reader.GetInt64(0);
                var visits = reader.GetInt64(1) + 1;
                reader.Close();

                using var updateCmd = _connection.CreateCommand();
                updateCmd.CommandText = "UPDATE urls SET last_visited = datetime('now'), visit_count = @visits";
                if (!string.IsNullOrEmpty(title))
                    updateCmd.CommandText += ", title = @title";
                updateCmd.CommandText += " WHERE id = @id";
                updateCmd.Parameters.AddWithValue("@visits", visits);
                updateCmd.Parameters.AddWithValue("@id", urlId);
                if (!string.IsNullOrEmpty(title))
                    updateCmd.Parameters.AddWithValue("@title", title);
                updateCmd.ExecuteNonQuery();
            }
            else
            {
                reader.Close();

                // Insert new
                using var insertCmd = _connection.CreateCommand();
                insertCmd.CommandText = "INSERT INTO urls (url, title) VALUES (@url, @title)";
                insertCmd.Parameters.AddWithValue("@url", url);
                insertCmd.Parameters.AddWithValue("@title", title ?? (object)DBNull.Value);
                insertCmd.ExecuteNonQuery();

                urlId = GetLastInsertId();
            }

            // Update tags
            if (!string.IsNullOrEmpty(tags))
            {
                // Remove old tags
                using var delTagsCmd = _connection.CreateCommand();
                delTagsCmd.CommandText = "DELETE FROM url_tags WHERE url_id = @id";
                delTagsCmd.Parameters.AddWithValue("@id", urlId);
                delTagsCmd.ExecuteNonQuery();

                // Add new tags
                var tagList = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var tag in tagList)
                {
                    using var tagCmd = _connection.CreateCommand();
                    tagCmd.CommandText = "INSERT OR IGNORE INTO url_tags (url_id, tag) VALUES (@id, @tag)";
                    tagCmd.Parameters.AddWithValue("@id", urlId);
                    tagCmd.Parameters.AddWithValue("@tag", tag.ToLowerInvariant());
                    tagCmd.ExecuteNonQuery();
                }
            }

            var tagDisplay = string.IsNullOrEmpty(tags) ? "" : $" [{tags}]";
            var titleDisplay = string.IsNullOrEmpty(title) ? "" : $" \"{title}\"";
            return $"Saved: {url}{titleDisplay}{tagDisplay}";
        }
    }

    /// <summary>
    /// Search URLs by tag, title, or URL substring.
    /// </summary>
    public List<(string url, string? title, string tags, int visits, string lastVisited)> RecallUrl(string topic)
    {
        lock (_lock)
        {
            var results = new List<(string, string?, string, int, string)>();
            var topicLower = topic.ToLowerInvariant();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT u.id, u.url, u.title, u.visit_count, u.last_visited,
                       GROUP_CONCAT(t.tag, ', ') as tags
                FROM urls u
                LEFT JOIN url_tags t ON u.id = t.url_id
                WHERE u.id IN (
                    SELECT DISTINCT u2.id FROM urls u2
                    LEFT JOIN url_tags t2 ON u2.id = t2.url_id
                    WHERE t2.tag LIKE @topic
                       OR u2.title LIKE @pattern
                       OR u2.url LIKE @pattern
                )
                GROUP BY u.id
                ORDER BY u.last_visited DESC
                LIMIT 20";
            cmd.Parameters.AddWithValue("@topic", topicLower);
            cmd.Parameters.AddWithValue("@pattern", $"%{topic}%");

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var url = reader.GetString(1);
                var title = reader.IsDBNull(2) ? null : reader.GetString(2);
                var visits = reader.GetInt32(3);
                var lastVisited = reader.GetString(4);
                var tags = reader.IsDBNull(5) ? "" : reader.GetString(5);

                results.Add((url, title, tags, visits, lastVisited));
            }

            return results;
        }
    }

    /// <summary>
    /// List all URLs, optionally filtered by tag.
    /// </summary>
    public List<(string url, string? title, string tags, int visits, string lastVisited)> ListUrls(string? tag, int limit)
    {
        lock (_lock)
        {
            var results = new List<(string, string?, string, int, string)>();

            using var cmd = _connection.CreateCommand();
            if (string.IsNullOrEmpty(tag))
            {
                cmd.CommandText = @"
                    SELECT u.id, u.url, u.title, u.visit_count, u.last_visited,
                           GROUP_CONCAT(t.tag, ', ') as tags
                    FROM urls u
                    LEFT JOIN url_tags t ON u.id = t.url_id
                    GROUP BY u.id
                    ORDER BY u.last_visited DESC
                    LIMIT @limit";
            }
            else
            {
                cmd.CommandText = @"
                    SELECT u.id, u.url, u.title, u.visit_count, u.last_visited,
                           GROUP_CONCAT(t.tag, ', ') as tags
                    FROM urls u
                    JOIN url_tags t ON u.id = t.url_id
                    WHERE u.id IN (
                        SELECT url_id FROM url_tags WHERE tag = @tag
                    )
                    GROUP BY u.id
                    ORDER BY u.last_visited DESC
                    LIMIT @limit";
                cmd.Parameters.AddWithValue("@tag", tag.ToLowerInvariant());
            }
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var url = reader.GetString(1);
                var title = reader.IsDBNull(2) ? null : reader.GetString(2);
                var visits = reader.GetInt32(3);
                var lastVisited = reader.GetString(4);
                var tags = reader.IsDBNull(5) ? "" : reader.GetString(5);

                results.Add((url, title, tags, visits, lastVisited));
            }

            return results;
        }
    }

    /// <summary>
    /// Delete a URL by exact URL match.
    /// </summary>
    public bool ForgetUrl(string url)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM urls WHERE url = @url";
            cmd.Parameters.AddWithValue("@url", url);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    private long GetLastInsertId()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT last_insert_rowid()";
        return (long)cmd.ExecuteScalar()!;
    }

    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS urls (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                url TEXT NOT NULL UNIQUE,
                title TEXT,
                created_at TEXT DEFAULT (datetime('now')),
                last_visited TEXT DEFAULT (datetime('now')),
                visit_count INTEGER DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS url_tags (
                url_id INTEGER REFERENCES urls(id) ON DELETE CASCADE,
                tag TEXT NOT NULL,
                PRIMARY KEY (url_id, tag)
            );
            CREATE INDEX IF NOT EXISTS idx_url_tags_tag ON url_tags(tag);";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
    }
}
