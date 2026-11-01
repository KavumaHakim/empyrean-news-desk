using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NewsDesk.Core.Data;

namespace NewsDesk.Core.Services;

/// <summary>
/// Moves a whole show to another computer: one ".newsdesk" file holding the database
/// (stories, titles, captions, ticker, every setting) and every video, picture and
/// sound it uses. Opening it on the other computer copies the media into a folder
/// there and points the show at the new copies.
/// </summary>
public static class ShowPackage
{
    public const string Extension = ".newsdesk";
    private const string DbEntry = "newsdesk.db";
    private const string ManifestEntry = "manifest.json";
    private const string MediaFolder = "media/";

    /// <summary>An imported show waiting beside the live database; it replaces it at the next start.</summary>
    public static string PendingImportPath(string dbPath) => dbPath + ".import";

    public record ExportResult(int FileCount, long TotalBytes, IReadOnlyList<string> MissingFiles);
    public record ImportResult(int FileCount, IReadOnlyList<string> MissingFiles, string MediaFolder);

    private sealed class Manifest
    {
        public int Version { get; set; } = 1;
        public string CreatedOn { get; set; } = "";
        public string Computer { get; set; } = "";
        /// <summary>Path on the computer that made the package → entry inside the package.</summary>
        public Dictionary<string, string> Files { get; set; } = [];
        public List<string> Missing { get; set; } = [];
    }

    /// <summary>Writes the show to <paramref name="packagePath"/>. The live database is only read.</summary>
    public static ExportResult Export(DatabaseService db, string packagePath, IProgress<string>? progress = null)
    {
        var work = Path.Combine(Path.GetTempPath(), "NewsDeskExport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var dbCopy = Path.Combine(work, DbEntry);
            db.SaveCopyTo(dbCopy);

            var manifest = new Manifest
            {
                CreatedOn = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Computer = Environment.MachineName
            };
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in FindFilePaths(dbCopy))
            {
                if (!File.Exists(path)) { manifest.Missing.Add(path); continue; }
                manifest.Files[path] = MediaFolder + UniqueName(Path.GetFileName(path), usedNames);
            }

            var partial = packagePath + ".partial";
            if (File.Exists(partial)) File.Delete(partial);
            long total = 0;
            using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(dbCopy, DbEntry, CompressionLevel.Optimal);
                int n = 0;
                foreach (var (source, entry) in manifest.Files)
                {
                    progress?.Report($"Packing {++n} of {manifest.Files.Count}: {Path.GetFileName(source)}");
                    // Video, pictures and sound are already compressed: store them as they are (much faster)
                    zip.CreateEntryFromFile(source, entry, CompressionLevel.NoCompression);
                    total += new FileInfo(source).Length;
                }
                var manifestEntry = zip.CreateEntry(ManifestEntry);
                using var writer = new StreamWriter(manifestEntry.Open());
                writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            }
            File.Move(partial, packagePath, overwrite: true);
            return new ExportResult(manifest.Files.Count, total, manifest.Missing);
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    /// <summary>
    /// Unpacks a show: media goes to <paramref name="mediaFolder"/>, the show's database is
    /// re-pointed at those copies and left waiting at <see cref="PendingImportPath"/>, to
    /// replace the live database at the next start (after a backup of it).
    /// </summary>
    public static ImportResult Import(string packagePath, string mediaFolder, string dbPath, IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(mediaFolder);
        var pending = PendingImportPath(dbPath);
        var staging = pending + ".partial";

        using var zip = ZipFile.OpenRead(packagePath);
        var manifestEntry = zip.GetEntry(ManifestEntry) ?? throw new InvalidDataException("This is not a NEWS DESK show file (no manifest).");
        var dbEntry = zip.GetEntry(DbEntry) ?? throw new InvalidDataException("This show file has no database in it.");
        Manifest manifest;
        using (var reader = new StreamReader(manifestEntry.Open()))
            manifest = JsonSerializer.Deserialize<Manifest>(reader.ReadToEnd()) ?? throw new InvalidDataException("The show file's manifest is damaged.");

        dbEntry.ExtractToFile(staging, overwrite: true);

        var relink = new Dictionary<string, string>();
        int n = 0;
        foreach (var (oldPath, entryName) in manifest.Files)
        {
            var entry = zip.GetEntry(entryName);
            if (entry == null) continue;
            var target = Path.Combine(mediaFolder, Path.GetFileName(entryName));
            progress?.Report($"Copying {++n} of {manifest.Files.Count}: {Path.GetFileName(target)}");
            // Opening the same show twice must not duplicate gigabytes of video
            if (!File.Exists(target) || new FileInfo(target).Length != entry.Length)
                entry.ExtractToFile(target, overwrite: true);
            relink[oldPath] = target;
        }

        RelinkPaths(staging, relink);
        File.Move(staging, pending, overwrite: true);
        return new ImportResult(relink.Count, manifest.Missing, mediaFolder);
    }

    /// <summary>
    /// The portable copy: a show already unpacked in a folder (newsdesk.db, manifest.json,
    /// media\) that the app was started with. The media stay where they are; the show is
    /// pointed at them and swapped in once. Later starts from the same folder keep the
    /// operator's edits. Returns true when the show was (re)loaded.
    /// </summary>
    public static bool OpenShowFolder(string showFolder, string dbPath)
    {
        showFolder = Path.GetFullPath(showFolder);
        var manifestPath = Path.Combine(showFolder, ManifestEntry);
        var showDb = Path.Combine(showFolder, DbEntry);
        if (!File.Exists(manifestPath) || !File.Exists(showDb)) return false;

        // Already loaded from this very folder into this computer's show: just carry on
        var marker = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? ".", "portable-show.txt");
        if (File.Exists(marker) && string.Equals(File.ReadAllText(marker).Trim(), showFolder, StringComparison.OrdinalIgnoreCase))
            return false;

        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath))
                       ?? throw new InvalidDataException("The show folder's manifest is damaged.");
        var pending = PendingImportPath(dbPath);
        var staging = pending + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? ".");
        File.Copy(showDb, staging, overwrite: true);

        var relink = new Dictionary<string, string>();
        foreach (var (oldPath, entryName) in manifest.Files)
        {
            var target = Path.Combine(showFolder, entryName.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target)) relink[oldPath] = target;
        }
        RelinkPaths(staging, relink);
        File.Move(staging, pending, overwrite: true);
        ApplyPendingImport(dbPath);
        File.WriteAllText(marker, showFolder);
        return true;
    }

    /// <summary>
    /// Called at start-up before the database opens: swaps in an imported show.
    /// The show it replaces is kept in the Backups folder. Returns the backup path, or null.
    /// </summary>
    public static string? ApplyPendingImport(string dbPath)
    {
        var pending = PendingImportPath(dbPath);
        if (!File.Exists(pending)) return null;

        // Straight after "Restart now" the closing copy of the app may still hold the
        // database for a moment: wait for it rather than fail
        for (int attempt = 1; ; attempt++)
        {
            try { return SwapInImport(dbPath, pending); }
            catch (IOException) when (attempt < 20) { Thread.Sleep(500); }
        }
    }

    private static string? SwapInImport(string dbPath, string pending)
    {
        string? backup = null;
        if (File.Exists(dbPath))
        {
            var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? ".", "Backups");
            Directory.CreateDirectory(dir);
            backup = Path.Combine(dir, $"newsdesk_before-import_{DateTime.Now:yyyy-MM-dd_HHmmss}.db");
            File.Copy(dbPath, backup, overwrite: false);
        }
        foreach (var leftover in new[] { dbPath + "-wal", dbPath + "-shm" })
            if (File.Exists(leftover)) File.Delete(leftover);
        File.Move(pending, dbPath, overwrite: true);
        return backup;
    }

    /// <summary>Every text value in the database that is a full file path (media, pictures, intro, sounds...).</summary>
    internal static List<string> FindFilePaths(string dbFile)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var conn = Open(dbFile);
        foreach (var (table, column) in TextColumns(conn))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT DISTINCT \"{column}\" FROM \"{table}\" WHERE typeof(\"{column}\") = 'text';";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var value = reader.GetString(0).Trim();
                if (LooksLikeFilePath(value)) found.Add(value);
            }
        }
        return [.. found];
    }

    private static void RelinkPaths(string dbFile, Dictionary<string, string> relink)
    {
        if (relink.Count == 0) return;
        using var conn = Open(dbFile);
        using var tx = conn.BeginTransaction();
        foreach (var (table, column) in TextColumns(conn))
        {
            foreach (var (oldPath, newPath) in relink)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"UPDATE \"{table}\" SET \"{column}\" = $new WHERE \"{column}\" = $old COLLATE NOCASE;";
                cmd.Parameters.AddWithValue("$new", newPath);
                cmd.Parameters.AddWithValue("$old", oldPath);
                cmd.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    private static List<(string Table, string Column)> TextColumns(SqliteConnection conn)
    {
        var tables = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }
        var result = new List<(string, string)>();
        foreach (var table in tables)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info(\"{table}\");";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var type = reader.GetString(2);
                if (type.Contains("TEXT", StringComparison.OrdinalIgnoreCase) || type.Length == 0)
                    result.Add((table, reader.GetString(1)));
            }
        }
        return result;
    }

    private static bool LooksLikeFilePath(string value) =>
        value.Length > 3 && value.Length < 1024
        && value.IndexOfAny(Path.GetInvalidPathChars()) < 0
        && Path.IsPathFullyQualified(value)
        && Path.HasExtension(value);

    private static string UniqueName(string fileName, HashSet<string> used)
    {
        var name = fileName;
        for (int i = 2; !used.Add(name); i++)
            name = $"{Path.GetFileNameWithoutExtension(fileName)} ({i}){Path.GetExtension(fileName)}";
        return name;
    }

    private static SqliteConnection Open(string dbFile)
    {
        // No pooling: the file is moved or deleted straight after
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbFile, Pooling = false }.ToString());
        conn.Open();
        return conn;
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
