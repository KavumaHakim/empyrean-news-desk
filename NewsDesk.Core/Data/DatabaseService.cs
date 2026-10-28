using System.Data;
using Microsoft.Data.Sqlite;
using NewsDesk.Core.Models;

namespace NewsDesk.Core.Data;

public class DatabaseService : IDisposable
{
    private readonly string _connectionString;
    private readonly string _dbPath;

    public DatabaseService(string? customDbPath = null)
    {
        if (string.IsNullOrEmpty(customDbPath))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(appData, "NewsDesk");
            Directory.CreateDirectory(dir);
            _dbPath = Path.Combine(dir, "newsdesk.db");
        }
        else
        {
            _dbPath = customDbPath;
            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        InitializeDatabase();
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // SQLite disables FK enforcement per connection by default;
        // without this ON DELETE CASCADE never runs.
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();

        return conn;
    }

    private void InitializeDatabase()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS MediaItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Title TEXT NOT NULL,
                FilePath TEXT NOT NULL,
                DurationSeconds REAL NOT NULL DEFAULT 0,
                Category TEXT NOT NULL DEFAULT 'National News',
                AssignedTransition INTEGER NOT NULL DEFAULT 1,
                Description TEXT DEFAULT '',
                IsAvailable INTEGER NOT NULL DEFAULT 1,
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Playlists (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS PlaylistItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PlaylistId INTEGER NOT NULL,
                MediaItemId INTEGER NOT NULL,
                OrderIndex INTEGER NOT NULL,
                FOREIGN KEY(PlaylistId) REFERENCES Playlists(Id) ON DELETE CASCADE,
                FOREIGN KEY(MediaItemId) REFERENCES MediaItems(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS HeadlineDrafts (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Title TEXT NOT NULL,
                Subtitle TEXT DEFAULT '',
                Category TEXT NOT NULL DEFAULT 'National News',
                Location TEXT DEFAULT '',
                ReporterName TEXT DEFAULT '',
                ReporterTitle TEXT DEFAULT '',
                TemplateType INTEGER NOT NULL DEFAULT 0,
                BackgroundStyle TEXT NOT NULL DEFAULT 'navy',
                ImagePath TEXT DEFAULT '',
                IsBreaking INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS TickerItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Text TEXT NOT NULL,
                Category TEXT NOT NULL DEFAULT 'NEWS',
                OrderIndex INTEGER NOT NULL DEFAULT 0,
                IsActive INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
        ";
        cmd.ExecuteNonQuery();

        SeedDefaults(conn);
    }

    private void SeedDefaults(SqliteConnection conn)
    {
        // Check if Ticker has items
        using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "SELECT COUNT(*) FROM TickerItems;";
            var count = (long)(checkCmd.ExecuteScalar() ?? 0);
            if (count == 0)
            {
                using var insertCmd = conn.CreateCommand();
                insertCmd.CommandText = @"
                    INSERT INTO TickerItems (Text, Category, OrderIndex, IsActive) VALUES
                    ('WELCOME TO NEWS DESK — LIVE BROADCAST CONTROL SYSTEM OPERATIONAL', 'NEWS DESK', 0, 1),
                    ('PROJECTOR EXTENDED DISPLAY READY — HDMI 1080P/4K OUTPUT ACTIVE', 'SYSTEM', 1, 1),
                    ('MANUAL BROADCASTING MODE ENGAGED — READY FOR OPERATOR SELECTION', 'ON AIR', 2, 1),
                    ('MARKETS UPDATE: DOW JONES RISES 140 POINTS AS TECH RALLIES', 'BUSINESS', 3, 1),
                    ('WEATHER FORECAST: MILD CONDITIONS ACROSS METROPOLITAN REGION', 'WEATHER', 4, 1);
                ";
                insertCmd.ExecuteNonQuery();
            }
        }

        // Check if Headline drafts have samples
        using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "SELECT COUNT(*) FROM HeadlineDrafts;";
            var count = (long)(checkCmd.ExecuteScalar() ?? 0);
            if (count == 0)
            {
                using var insertCmd = conn.CreateCommand();
                insertCmd.CommandText = @"
                    INSERT INTO HeadlineDrafts (Title, Subtitle, Category, Location, ReporterName, ReporterTitle, TemplateType, BackgroundStyle, IsBreaking, CreatedAt) VALUES
                    ('SPECIAL REPORT: REGIONAL ECONOMIC OUTLOOK', 'Analysts project sustained GDP expansion through Q4', 'Special Reports', 'CAPITAL CITY', 'Sarah Jenkins', 'Senior Correspondent', 0, 'navy', 0, $now),
                    ('BREAKING: EMERGENCY DECLARATION ISSUED', 'Civil protection agencies mobilize resources along coast', 'Breaking News', 'COASTAL DISTRICT', 'Marcus Vance', 'Chief Field Reporter', 1, 'breaking', 1, $now),
                    ('IN-DEPTH INTERVIEW: TECH SUMMIT 2026', 'Artificial intelligence and high-throughput data processing', 'Business', 'STUDIO A', 'Dr. Elena Rostova', 'AI Systems Director', 7, 'slate', 0, $now);
                ";
                insertCmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
                insertCmd.ExecuteNonQuery();
            }
        }

        // Ensure default playlist exists
        using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "SELECT COUNT(*) FROM Playlists;";
            var count = (long)(checkCmd.ExecuteScalar() ?? 0);
            if (count == 0)
            {
                using var insertCmd = conn.CreateCommand();
                insertCmd.CommandText = "INSERT INTO Playlists (Name, CreatedAt) VALUES ('Default News Rundown', $now);";
                insertCmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
                insertCmd.ExecuteNonQuery();
            }
        }
    }

    #region Media Items

    public List<MediaItem> GetAllMedia()
    {
        var list = new List<MediaItem>();
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Title, FilePath, DurationSeconds, Category, AssignedTransition, Description, IsAvailable, CreatedAt FROM MediaItems ORDER BY Id DESC;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var filePath = reader.GetString(2);
            var isAvailable = File.Exists(filePath);

            list.Add(new MediaItem
            {
                Id = reader.GetInt32(0),
                Title = reader.GetString(1),
                FilePath = filePath,
                DurationSeconds = reader.GetDouble(3),
                Category = reader.GetString(4),
                AssignedTransition = (TransitionType)reader.GetInt32(5),
                Description = reader.IsDBNull(6) ? "" : reader.GetString(6),
                IsAvailable = isAvailable,
                CreatedAt = DateTime.Parse(reader.GetString(8))
            });
        }
        return list;
    }

    public MediaItem? GetMediaById(int id)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Title, FilePath, DurationSeconds, Category, AssignedTransition, Description, IsAvailable, CreatedAt FROM MediaItems WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            var filePath = reader.GetString(2);
            return new MediaItem
            {
                Id = reader.GetInt32(0),
                Title = reader.GetString(1),
                FilePath = filePath,
                DurationSeconds = reader.GetDouble(3),
                Category = reader.GetString(4),
                AssignedTransition = (TransitionType)reader.GetInt32(5),
                Description = reader.IsDBNull(6) ? "" : reader.GetString(6),
                IsAvailable = File.Exists(filePath),
                CreatedAt = DateTime.Parse(reader.GetString(8))
            };
        }
        return null;
    }

    public int AddMedia(MediaItem item)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO MediaItems (Title, FilePath, DurationSeconds, Category, AssignedTransition, Description, IsAvailable, CreatedAt)
            VALUES ($title, $filePath, $duration, $category, $transition, $description, $isAvailable, $createdAt);
            SELECT last_insert_rowid();
        ";
        cmd.Parameters.AddWithValue("$title", item.Title);
        cmd.Parameters.AddWithValue("$filePath", item.FilePath);
        cmd.Parameters.AddWithValue("$duration", item.DurationSeconds);
        cmd.Parameters.AddWithValue("$category", item.Category);
        cmd.Parameters.AddWithValue("$transition", (int)item.AssignedTransition);
        cmd.Parameters.AddWithValue("$description", item.Description);
        cmd.Parameters.AddWithValue("$isAvailable", item.IsAvailable ? 1 : 0);
        cmd.Parameters.AddWithValue("$createdAt", item.CreatedAt.ToString("o"));

        var newId = Convert.ToInt32(cmd.ExecuteScalar());
        item.Id = newId;
        return newId;
    }

    public void UpdateMedia(MediaItem item)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE MediaItems
            SET Title = $title,
                FilePath = $filePath,
                DurationSeconds = $duration,
                Category = $category,
                AssignedTransition = $transition,
                Description = $description,
                IsAvailable = $isAvailable
            WHERE Id = $id;
        ";
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$title", item.Title);
        cmd.Parameters.AddWithValue("$filePath", item.FilePath);
        cmd.Parameters.AddWithValue("$duration", item.DurationSeconds);
        cmd.Parameters.AddWithValue("$category", item.Category);
        cmd.Parameters.AddWithValue("$transition", (int)item.AssignedTransition);
        cmd.Parameters.AddWithValue("$description", item.Description);
        cmd.Parameters.AddWithValue("$isAvailable", item.IsAvailable ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void UpdateMediaTransition(int mediaId, TransitionType transition)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE MediaItems SET AssignedTransition = $transition WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", mediaId);
        cmd.Parameters.AddWithValue("$transition", (int)transition);
        cmd.ExecuteNonQuery();
    }

    public void DeleteMedia(int id)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM MediaItems WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    #endregion

    #region Headline Drafts

    public List<HeadlineDraft> GetAllHeadlineDrafts()
    {
        var list = new List<HeadlineDraft>();
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Title, Subtitle, Category, Location, ReporterName, ReporterTitle, TemplateType, BackgroundStyle, ImagePath, IsBreaking, CreatedAt FROM HeadlineDrafts ORDER BY Id DESC;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new HeadlineDraft
            {
                Id = reader.GetInt32(0),
                Title = reader.GetString(1),
                Subtitle = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Category = reader.GetString(3),
                Location = reader.IsDBNull(4) ? "" : reader.GetString(4),
                ReporterName = reader.IsDBNull(5) ? "" : reader.GetString(5),
                ReporterTitle = reader.IsDBNull(6) ? "" : reader.GetString(6),
                TemplateType = (GraphicsTemplateType)reader.GetInt32(7),
                BackgroundStyle = reader.GetString(8),
                ImagePath = reader.IsDBNull(9) ? "" : reader.GetString(9),
                IsBreaking = reader.GetInt32(10) == 1,
                CreatedAt = DateTime.Parse(reader.GetString(11))
            });
        }
        return list;
    }

    public int SaveHeadlineDraft(HeadlineDraft draft)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        if (draft.Id > 0)
        {
            cmd.CommandText = @"
                UPDATE HeadlineDrafts
                SET Title = $title,
                    Subtitle = $subtitle,
                    Category = $category,
                    Location = $location,
                    ReporterName = $reporterName,
                    ReporterTitle = $reporterTitle,
                    TemplateType = $templateType,
                    BackgroundStyle = $bgStyle,
                    ImagePath = $imagePath,
                    IsBreaking = $isBreaking
                WHERE Id = $id;
            ";
            cmd.Parameters.AddWithValue("$id", draft.Id);
        }
        else
        {
            cmd.CommandText = @"
                INSERT INTO HeadlineDrafts (Title, Subtitle, Category, Location, ReporterName, ReporterTitle, TemplateType, BackgroundStyle, ImagePath, IsBreaking, CreatedAt)
                VALUES ($title, $subtitle, $category, $location, $reporterName, $reporterTitle, $templateType, $bgStyle, $imagePath, $isBreaking, $createdAt);
                SELECT last_insert_rowid();
            ";
            cmd.Parameters.AddWithValue("$createdAt", draft.CreatedAt.ToString("o"));
        }

        cmd.Parameters.AddWithValue("$title", draft.Title);
        cmd.Parameters.AddWithValue("$subtitle", draft.Subtitle);
        cmd.Parameters.AddWithValue("$category", draft.Category);
        cmd.Parameters.AddWithValue("$location", draft.Location);
        cmd.Parameters.AddWithValue("$reporterName", draft.ReporterName);
        cmd.Parameters.AddWithValue("$reporterTitle", draft.ReporterTitle);
        cmd.Parameters.AddWithValue("$templateType", (int)draft.TemplateType);
        cmd.Parameters.AddWithValue("$bgStyle", draft.BackgroundStyle);
        cmd.Parameters.AddWithValue("$imagePath", draft.ImagePath);
        cmd.Parameters.AddWithValue("$isBreaking", draft.IsBreaking ? 1 : 0);

        if (draft.Id > 0)
        {
            cmd.ExecuteNonQuery();
            return draft.Id;
        }
        else
        {
            var newId = Convert.ToInt32(cmd.ExecuteScalar());
            draft.Id = newId;
            return newId;
        }
    }

    public void DeleteHeadlineDraft(int id)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM HeadlineDrafts WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    #endregion

    #region Ticker Items

    public List<TickerItem> GetAllTickerItems()
    {
        var list = new List<TickerItem>();
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Text, Category, OrderIndex, IsActive FROM TickerItems ORDER BY OrderIndex ASC;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new TickerItem
            {
                Id = reader.GetInt32(0),
                Text = reader.GetString(1),
                Category = reader.GetString(2),
                OrderIndex = reader.GetInt32(3),
                IsActive = reader.GetInt32(4) == 1
            });
        }
        return list;
    }

    public int SaveTickerItem(TickerItem item)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        if (item.Id > 0)
        {
            cmd.CommandText = @"
                UPDATE TickerItems
                SET Text = $text, Category = $category, OrderIndex = $orderIndex, IsActive = $isActive
                WHERE Id = $id;
            ";
            cmd.Parameters.AddWithValue("$id", item.Id);
        }
        else
        {
            cmd.CommandText = @"
                INSERT INTO TickerItems (Text, Category, OrderIndex, IsActive)
                VALUES ($text, $category, $orderIndex, $isActive);
                SELECT last_insert_rowid();
            ";
        }

        cmd.Parameters.AddWithValue("$text", item.Text);
        cmd.Parameters.AddWithValue("$category", item.Category);
        cmd.Parameters.AddWithValue("$orderIndex", item.OrderIndex);
        cmd.Parameters.AddWithValue("$isActive", item.IsActive ? 1 : 0);

        if (item.Id > 0)
        {
            cmd.ExecuteNonQuery();
            return item.Id;
        }
        else
        {
            var newId = Convert.ToInt32(cmd.ExecuteScalar());
            item.Id = newId;
            return newId;
        }
    }

    public void DeleteTickerItem(int id)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM TickerItems WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    #endregion

    #region Playlists / Rundown

    public Playlist GetDefaultPlaylist()
    {
        using var conn = CreateConnection();
        var playlist = new Playlist { Id = 1, Name = "Broadcast Rundown" };

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Name, CreatedAt FROM Playlists LIMIT 1;";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                playlist.Id = reader.GetInt32(0);
                playlist.Name = reader.GetString(1);
                playlist.CreatedAt = DateTime.Parse(reader.GetString(2));
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT pi.Id, pi.PlaylistId, pi.MediaItemId, pi.OrderIndex,
                       m.Title, m.FilePath, m.DurationSeconds, m.Category, m.AssignedTransition, m.Description, m.IsAvailable, m.CreatedAt
                FROM PlaylistItems pi
                JOIN MediaItems m ON pi.MediaItemId = m.Id
                WHERE pi.PlaylistId = $playlistId
                ORDER BY pi.OrderIndex ASC;
            ";
            cmd.Parameters.AddWithValue("$playlistId", playlist.Id);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var filePath = reader.GetString(5);
                var media = new MediaItem
                {
                    Id = reader.GetInt32(2),
                    Title = reader.GetString(4),
                    FilePath = filePath,
                    DurationSeconds = reader.GetDouble(6),
                    Category = reader.GetString(7),
                    AssignedTransition = (TransitionType)reader.GetInt32(8),
                    Description = reader.IsDBNull(9) ? "" : reader.GetString(9),
                    IsAvailable = File.Exists(filePath),
                    CreatedAt = DateTime.Parse(reader.GetString(11))
                };

                playlist.Items.Add(new PlaylistItem
                {
                    Id = reader.GetInt32(0),
                    PlaylistId = reader.GetInt32(1),
                    MediaItemId = reader.GetInt32(2),
                    OrderIndex = reader.GetInt32(3),
                    MediaItem = media
                });
            }
        }

        return playlist;
    }

    public void AddPlaylistItem(int playlistId, int mediaItemId, int orderIndex)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO PlaylistItems (PlaylistId, MediaItemId, OrderIndex)
            VALUES ($pId, $mId, $order);
        ";
        cmd.Parameters.AddWithValue("$pId", playlistId);
        cmd.Parameters.AddWithValue("$mId", mediaItemId);
        cmd.Parameters.AddWithValue("$order", orderIndex);
        cmd.ExecuteNonQuery();
    }

    public void RemovePlaylistItem(int playlistItemId)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM PlaylistItems WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", playlistItemId);
        cmd.ExecuteNonQuery();
    }

    public void UpdatePlaylistOrder(List<PlaylistItem> items)
    {
        using var conn = CreateConnection();
        using var trans = conn.BeginTransaction();
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = trans;
                cmd.CommandText = "UPDATE PlaylistItems SET OrderIndex = $order WHERE Id = $id;";
                cmd.Parameters.AddWithValue("$order", i);
                cmd.Parameters.AddWithValue("$id", items[i].Id);
                cmd.ExecuteNonQuery();
            }
            trans.Commit();
        }
        catch
        {
            trans.Rollback();
            throw;
        }
    }

    #endregion

    #region Settings

    public string? GetSetting(string key, string? defaultValue = null)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key = $key;";
        cmd.Parameters.AddWithValue("$key", key);

        var result = cmd.ExecuteScalar();
        return result?.ToString() ?? defaultValue;
    }

    public void SetSetting(string key, string value)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Settings (Key, Value) VALUES ($key, $val)
            ON CONFLICT(Key) DO UPDATE SET Value = $val;
        ";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$val", value);
        cmd.ExecuteNonQuery();
    }

    #endregion

    public void Dispose()
    {
        // Connection pooling handles cleanup
    }
}
