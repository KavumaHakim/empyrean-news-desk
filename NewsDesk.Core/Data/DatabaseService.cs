using System.Data;
using Microsoft.Data.Sqlite;
using NewsDesk.Core.Models;

namespace NewsDesk.Core.Data;

public class DatabaseService : IDisposable
{
    private readonly string _connectionString;
    private readonly string _dbPath;

    /// <summary>Where the operator's show lives when no other path is given.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NewsDesk", "newsdesk.db");

    public string DatabasePath => _dbPath;

    public DatabaseService(string? customDbPath = null)
    {
        if (string.IsNullOrEmpty(customDbPath))
        {
            _dbPath = DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
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

            CREATE TABLE IF NOT EXISTS HeadlineSequence (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                OrderIndex INTEGER NOT NULL DEFAULT 0,
                Title TEXT NOT NULL,
                Subtitle TEXT NOT NULL DEFAULT '',
                Category TEXT NOT NULL DEFAULT 'HEADLINES',
                DurationSeconds INTEGER NOT NULL DEFAULT 8,
                MediaItemId INTEGER NULL,
                FOREIGN KEY(MediaItemId) REFERENCES MediaItems(Id) ON DELETE SET NULL
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

        MigrateSchema(conn);
        SeedDefaults(conn);
    }

    /// <summary>
    /// Adds columns introduced after the first release to databases created by older builds.
    /// </summary>
    private static void MigrateSchema(SqliteConnection conn)
    {
        AddMissingColumns(conn, "MediaItems",
            ("CaptionName", "TEXT NOT NULL DEFAULT ''"),
            ("CaptionTitle", "TEXT NOT NULL DEFAULT ''"),
            ("OriginalPath", "TEXT NOT NULL DEFAULT ''"));

        // Headline entries became rundown stories: headlines-segment flag + extra media
        AddMissingColumns(conn, "HeadlineSequence",
            ("InHeadlines", "INTEGER NOT NULL DEFAULT 1"),
            ("ExtraMediaIds", "TEXT NOT NULL DEFAULT ''"),
            ("Location", "TEXT NOT NULL DEFAULT ''"),
            ("IsBreaking", "INTEGER NOT NULL DEFAULT 0"),
            ("SpeakersText", "TEXT NOT NULL DEFAULT ''"),
            ("ExtraLowerThirds", "TEXT NOT NULL DEFAULT ''"));
    }

    private static void AddMissingColumns(SqliteConnection conn, string table, params (string Name, string Definition)[] columns)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = conn.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table});";
            using var reader = info.ExecuteReader();
            while (reader.Read()) existing.Add(reader.GetString(1));
        }

        foreach (var (name, definition) in columns)
        {
            if (existing.Contains(name)) continue;
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {name} {definition};";
            alter.ExecuteNonQuery();
        }
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
                    ('Friendships for life', '', 0, 1),
                    ('From classrooms to the future', '', 1, 1),
                    ('The best (and funniest) moments', '', 2, 1),
                    ('One class. Always.', '', 3, 1),
                    ('Valedictorian address scheduled for 20:00 — audience taking seats', 'CAMPUS', 4, 1);
                ";
                insertCmd.ExecuteNonQuery();
            }
        }

        // Seed Sample Media if library empty
        using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "SELECT COUNT(*) FROM MediaItems;";
            var count = (long)(checkCmd.ExecuteScalar() ?? 0);
            if (count == 0)
            {
                var candidates = new[]
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SampleMedia"),
                    Path.Combine(Directory.GetCurrentDirectory(), "SampleMedia"),
                    // Repo root when run from bin\<config>\<tfm>\ during development
                    Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "SampleMedia"))
                };
                string? foundDir = candidates.FirstOrDefault(Directory.Exists);
                if (foundDir != null)
                {
                    var file1 = Path.Combine(foundDir, "Sample_News_Report.mp4");
                    var file2 = Path.Combine(foundDir, "Sample_Breaking_News.mp4");
                    if (File.Exists(file1))
                    {
                        using var insertCmd = conn.CreateCommand();
                        insertCmd.CommandText = @"
                            INSERT INTO MediaItems (Title, FilePath, DurationSeconds, Category, AssignedTransition, Description, IsAvailable, CreatedAt)
                            VALUES ('Special Report: Economic Outlook', $f1, 15, 'National News', 1, 'Sample broadcast report', 1, $now);
                        ";
                        insertCmd.Parameters.AddWithValue("$f1", file1);
                        insertCmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
                        insertCmd.ExecuteNonQuery();
                    }
                    if (File.Exists(file2))
                    {
                        using var insertCmd = conn.CreateCommand();
                        insertCmd.CommandText = @"
                            INSERT INTO MediaItems (Title, FilePath, DurationSeconds, Category, AssignedTransition, Description, IsAvailable, CreatedAt)
                            VALUES ('Breaking News: Live Dispatch', $f2, 15, 'Breaking News', 0, 'Sample breaking news video', 1, $now);
                        ";
                        insertCmd.Parameters.AddWithValue("$f2", file2);
                        insertCmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
                        insertCmd.ExecuteNonQuery();
                    }
                }
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
                    ('BREAKING: FINAL EXAMS OFFICIALLY CONCLUDED', 'Students celebrate completion of senior academic milestones', 'Breaking News', 'EXAM HALLS', 'Marcus Vance', 'Chief Field Reporter', 1, 'breaking', 1, $now),
                    ('TRIBUTE: THE TEACHERS WHO GUIDED THE JOURNEY', 'Faculty moments and heartfelt farewell dedications', 'Education', 'FACULTY LOUNGE', 'Sarah Jenkins', 'Senior Correspondent', 0, 'navy', 0, $now),
                    ('INTERVIEW: HEAD PREFECT ON THE CLASS LEGACY', 'Reflections on leadership, solidarity, and the road ahead', 'Interview', 'CAMPUS PLAZA', 'Elena Rostova', 'Guest Speaker', 7, 'slate', 0, $now),
                    ('FAREWELL SPECIAL', 'Empyrean class celebrates the end of an incredible journey', 'Special Report', '', '', '', 0, 'navy', 0, $now),
                    ('AN ERA COMES TO AN END', 'One class. Countless memories. One unforgettable farewell.', 'Special Report', 'MAIN AUDITORIUM', 'Senior Bureau', 'Farewell Correspondent', 0, 'navy', 0, $now);
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

                using var pItemsCmd = conn.CreateCommand();
                pItemsCmd.CommandText = @"
                    INSERT INTO PlaylistItems (PlaylistId, MediaItemId, OrderIndex)
                    SELECT 1, Id, (Id - 1) FROM MediaItems ORDER BY Id ASC;
                ";
                pItemsCmd.ExecuteNonQuery();
            }
        }
    }

    #region Media Items

    public List<MediaItem> GetAllMedia()
    {
        var list = new List<MediaItem>();
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Title, FilePath, DurationSeconds, Category, AssignedTransition, Description, IsAvailable, CreatedAt, CaptionName, CaptionTitle, OriginalPath FROM MediaItems ORDER BY Id DESC;";

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
                CreatedAt = DateTime.Parse(reader.GetString(8)),
                CaptionName = reader.IsDBNull(9) ? "" : reader.GetString(9),
                CaptionTitle = reader.IsDBNull(10) ? "" : reader.GetString(10),
                OriginalPath = reader.IsDBNull(11) ? "" : reader.GetString(11)
            });
        }
        return list;
    }

    public MediaItem? GetMediaById(int id)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Title, FilePath, DurationSeconds, Category, AssignedTransition, Description, IsAvailable, CreatedAt, CaptionName, CaptionTitle, OriginalPath FROM MediaItems WHERE Id = $id;";
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
                CreatedAt = DateTime.Parse(reader.GetString(8)),
                CaptionName = reader.IsDBNull(9) ? "" : reader.GetString(9),
                CaptionTitle = reader.IsDBNull(10) ? "" : reader.GetString(10),
                OriginalPath = reader.IsDBNull(11) ? "" : reader.GetString(11)
            };
        }
        return null;
    }

    public int AddMedia(MediaItem item)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO MediaItems (Title, FilePath, DurationSeconds, Category, AssignedTransition, Description, IsAvailable, CreatedAt, CaptionName, CaptionTitle, OriginalPath)
            VALUES ($title, $filePath, $duration, $category, $transition, $description, $isAvailable, $createdAt, $captionName, $captionTitle, $originalPath);
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
        cmd.Parameters.AddWithValue("$captionName", item.CaptionName ?? string.Empty);
        cmd.Parameters.AddWithValue("$captionTitle", item.CaptionTitle ?? string.Empty);
        cmd.Parameters.AddWithValue("$originalPath", item.OriginalPath ?? string.Empty);

        var newId
 = Convert.ToInt32(cmd.ExecuteScalar());
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
                IsAvailable = $isAvailable,
                CaptionName = $captionName,
                CaptionTitle = $captionTitle,
                OriginalPath = $originalPath
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
        cmd.Parameters.AddWithValue("$captionName", item.CaptionName ?? string.Empty);
        cmd.Parameters.AddWithValue("$captionTitle", item.CaptionTitle ?? string.Empty);
        cmd.Parameters.AddWithValue("$originalPath", item.OriginalPath ?? string.Empty);
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

    #region Headline Sequence

    /// <summary>
    /// Returns the headlines segment in running order. Only MediaItemId is
    /// loaded; the caller resolves it against its media library.
    /// </summary>
    public List<HeadlineSequenceItem> GetHeadlineSequence()
    {
        var list = new List<HeadlineSequenceItem>();
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, OrderIndex, Title, Subtitle, Category, DurationSeconds, MediaItemId, InHeadlines, ExtraMediaIds,
                   Location, IsBreaking, SpeakersText, ExtraLowerThirds
            FROM HeadlineSequence
            ORDER BY OrderIndex ASC, Id ASC;
        ";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new HeadlineSequenceItem
            {
                Id = reader.GetInt32(0),
                OrderIndex = reader.GetInt32(1),
                Title = reader.GetString(2),
                Subtitle = reader.GetString(3),
                Category = reader.GetString(4),
                DurationSeconds = reader.GetInt32(5),
                MediaItemId = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                InHeadlines = reader.IsDBNull(7) || reader.GetInt32(7) != 0,
                ExtraMediaIds = ParseIds(reader.IsDBNull(8) ? "" : reader.GetString(8)),
                Location = reader.IsDBNull(9) ? "" : reader.GetString(9),
                IsBreaking = !reader.IsDBNull(10) && reader.GetInt32(10) != 0,
                SpeakersText = reader.IsDBNull(11) ? "" : reader.GetString(11),
                ExtraLowerThirds = ParseLowerThirds(reader.IsDBNull(12) ? "" : reader.GetString(12))
            });
        }
        return list;
    }

    public int SaveHeadlineSequenceItem(HeadlineSequenceItem item)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();

        if (item.Id > 0)
        {
            cmd.CommandText = @"
                UPDATE HeadlineSequence
                SET OrderIndex = $order,
                    Title = $title,
                    Subtitle = $subtitle,
                    Category = $category,
                    DurationSeconds = $duration,
                    MediaItemId = $mediaId,
                    InHeadlines = $inHeadlines,
                    ExtraMediaIds = $extraIds,
                    Location = $location,
                    IsBreaking = $isBreaking,
                    SpeakersText = $speakers,
                    ExtraLowerThirds = $extraL3
                WHERE Id = $id;
            ";
            cmd.Parameters.AddWithValue("$id", item.Id);
        }
        else
        {
            cmd.CommandText = @"
                INSERT INTO HeadlineSequence (OrderIndex, Title, Subtitle, Category, DurationSeconds, MediaItemId, InHeadlines, ExtraMediaIds, Location, IsBreaking, SpeakersText, ExtraLowerThirds)
                VALUES ($order, $title, $subtitle, $category, $duration, $mediaId, $inHeadlines, $extraIds, $location, $isBreaking, $speakers, $extraL3);
                SELECT last_insert_rowid();
            ";
        }

        cmd.Parameters.AddWithValue("$order", item.OrderIndex);
        cmd.Parameters.AddWithValue("$title", item.Title ?? string.Empty);
        cmd.Parameters.AddWithValue("$subtitle", item.Subtitle ?? string.Empty);
        cmd.Parameters.AddWithValue("$category", item.Category ?? string.Empty);
        cmd.Parameters.AddWithValue("$duration", item.DurationSeconds);
        cmd.Parameters.AddWithValue("$mediaId", (object?)item.MediaItemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$inHeadlines", item.InHeadlines ? 1 : 0);
        // Saved from the resolved list when there is one, so removals stick
        bool resolved = item.ExtraMedia.Count > 0 || item.ExtraMediaIds.Count == 0;
        var extraIds = resolved ? item.ExtraMedia.Select(m => m.Media.Id) : item.ExtraMediaIds;
        var extraL3 = resolved
            ? item.ExtraMedia.Where(m => m.Title.Length > 0 || m.Subtitle.Length > 0)
                .ToDictionary(m => m.Media.Id, m => new StoryMediaText(m.Title, m.Subtitle))
            : item.ExtraLowerThirds;
        cmd.Parameters.AddWithValue("$extraL3", extraL3.Count == 0 ? "" : System.Text.Json.JsonSerializer.Serialize(extraL3));
        cmd.Parameters.AddWithValue("$extraIds", string.Join(",", extraIds));
        cmd.Parameters.AddWithValue("$location", item.Location ?? string.Empty);
        cmd.Parameters.AddWithValue("$isBreaking", item.IsBreaking ? 1 : 0);
        cmd.Parameters.AddWithValue("$speakers", item.SpeakersText ?? string.Empty);


        if (item.Id > 0)
        {
            cmd.ExecuteNonQuery();
            return item.Id;
        }

        item.Id = Convert.ToInt32(cmd.ExecuteScalar());
        return item.Id;
    }

    /// <summary>
    /// Writes a complete, consistent copy of the database to a Backups folder beside
    /// it and returns the copy's path. Taken before any change to the operator's work.
    /// </summary>
    public string CreateBackup(string label)
    {
        var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_dbPath)) ?? ".", "Backups");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"newsdesk_{label}_{DateTime.Now:yyyy-MM-dd_HHmmss}.db");

        SaveCopyTo(target);
        return target;
    }

    /// <summary>Writes a complete, consistent copy of the database to <paramref name="target"/>.</summary>
    public void SaveCopyTo(string target)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $target;";
        cmd.Parameters.AddWithValue("$target", target);
        cmd.ExecuteNonQuery();
    }

    private static Dictionary<int, StoryMediaText> ParseLowerThirds(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, StoryMediaText>>(json) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    private static List<int> ParseIds(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToList();

    public void DeleteHeadlineSequenceItem(int id)

    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM HeadlineSequence WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void UpdateHeadlineSequenceOrder(List<HeadlineSequenceItem> items)
    {
        using var conn = CreateConnection();
        using var trans = conn.BeginTransaction();
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = trans;
                cmd.CommandText = "UPDATE HeadlineSequence SET OrderIndex = $order WHERE Id = $id;";
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

    public IntroConfig GetIntroConfig()
    {
        var config = new IntroConfig();
        config.VideoPath = GetSetting("Intro_VideoPath", string.Empty) ?? string.Empty;
        config.AudioPath = GetSetting("Intro_AudioPath", string.Empty) ?? string.Empty;
        if (double.TryParse(GetSetting("Intro_Volume", "1.0"), out var vol))
            config.Volume = vol;
        if (bool.TryParse(GetSetting("Intro_UseBuiltInMotion", "True"), out var useBuiltIn))
            config.UseBuiltInMotion = useBuiltIn;
        config.PostIntroAction = GetSetting("Intro_PostIntroAction", "opening_headline") ?? "opening_headline";
        return config;
    }

    public void SaveIntroConfig(IntroConfig config)
    {
        SetSetting("Intro_VideoPath", config.VideoPath ?? string.Empty);
        SetSetting("Intro_AudioPath", config.AudioPath ?? string.Empty);
        SetSetting("Intro_Volume", config.Volume.ToString("0.00"));
        SetSetting("Intro_UseBuiltInMotion", config.UseBuiltInMotion.ToString());
        SetSetting("Intro_PostIntroAction", config.PostIntroAction ?? "opening_headline");
    }

    public VoiceOverConfig GetVoiceOverConfig()
    {
        var config = new VoiceOverConfig
        {
            AudioPath = GetSetting("VO_AudioPath", string.Empty) ?? string.Empty,
            Name = GetSetting("VO_Name", string.Empty) ?? string.Empty,
            Designation = GetSetting("VO_Designation", string.Empty) ?? string.Empty
        };
        if (double.TryParse(GetSetting("VO_Volume", "1.0"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vol))
            config.Volume = vol;
        if (double.TryParse(GetSetting("VO_DuckLevel", "0.25"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var duck))
            config.DuckLevel = duck;
        if (bool.TryParse(GetSetting("VO_ShowTag", "True"), out var showTag))
            config.ShowTag = showTag;
        if (bool.TryParse(GetSetting("VO_Loop", "False"), out var loop))
            config.Loop = loop;
        return config;
    }

    public void SaveVoiceOverConfig(VoiceOverConfig config)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        SetSetting("VO_AudioPath", config.AudioPath ?? string.Empty);
        SetSetting("VO_Name", config.Name ?? string.Empty);
        SetSetting("VO_Designation", config.Designation ?? string.Empty);
        SetSetting("VO_Volume", config.Volume.ToString("0.00", inv));
        SetSetting("VO_DuckLevel", config.DuckLevel.ToString("0.00", inv));
        SetSetting("VO_ShowTag", config.ShowTag.ToString());
        SetSetting("VO_Loop", config.Loop.ToString());
    }

    /// <summary>Sound-effect pad settings, keyed by slot (1–8).</summary>
    public SoundPad GetSoundPad(int slot)
    {
        var prefix = $"Sfx{slot}_";
        var pad = new SoundPad
        {
            Slot = slot,
            Name = GetSetting(prefix + "Name", string.Empty) ?? string.Empty,
            FilePath = GetSetting(prefix + "Path", string.Empty) ?? string.Empty
        };
        if (double.TryParse(GetSetting(prefix + "Volume", "1.0"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var volume))
            pad.Volume = volume;
        return pad;
    }

    public void SaveSoundPad(SoundPad pad)
    {
        var prefix = $"Sfx{pad.Slot}_";
        SetSetting(prefix + "Name", pad.Name ?? string.Empty);
        SetSetting(prefix + "Path", pad.FilePath ?? string.Empty);
        SetSetting(prefix + "Volume", pad.Volume.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Speaker box settings, keyed by box number (1 or 2).</summary>
    public SpeakerBoxConfig GetSpeakerBoxConfig(int box)
    {
        var prefix = $"Speaker{box}_";
        var config = new SpeakerBoxConfig
        {
            SourceKind = GetSetting(prefix + "SourceKind", "none") ?? "none",
            SourcePath = GetSetting(prefix + "SourcePath", string.Empty) ?? string.Empty,
            Name = GetSetting(prefix + "Name", string.Empty) ?? string.Empty,
            Designation = GetSetting(prefix + "Designation", string.Empty) ?? string.Empty
        };
        if (int.TryParse(GetSetting(prefix + "CameraIndex", (box - 1).ToString()), out var cam))
            config.CameraIndex = cam;
        if (bool.TryParse(GetSetting(prefix + "PlayAudio", "True"), out var audio))
            config.PlayAudio = audio;
        if (bool.TryParse(GetSetting(prefix + "IsEnabled", "True"), out var enabled))
            config.IsEnabled = enabled;
        return config;
    }

    public void SaveSpeakerBoxConfig(int box, SpeakerBoxConfig config)
    {
        var prefix = $"Speaker{box}_";
        SetSetting(prefix + "SourceKind", config.SourceKind ?? "none");
        SetSetting(prefix + "SourcePath", config.SourcePath ?? string.Empty);
        SetSetting(prefix + "Name", config.Name ?? string.Empty);
        SetSetting(prefix + "Designation", config.Designation ?? string.Empty);
        SetSetting(prefix + "CameraIndex", config.CameraIndex.ToString());
        SetSetting(prefix + "PlayAudio", config.PlayAudio.ToString());
        SetSetting(prefix + "IsEnabled", config.IsEnabled.ToString());
    }

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
