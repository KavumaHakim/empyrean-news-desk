using Microsoft.Data.Sqlite;
using NewsDesk.Core.Data;
using NewsDesk.Core.Models;
using NewsDesk.Core.Services;

namespace NewsDesk.Tests;

public class ShowPackageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"newsdesk_pkg_{Guid.NewGuid()}");

    public ShowPackageTests() => Directory.CreateDirectory(_dir);

    private string MakeFile(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void SaveAndOpen_MovesStoriesSettingsAndMediaToTheNewComputer()
    {
        // The show on the first computer
        var video = MakeFile(@"pc1\videos\sports.mp4", "video-bytes");
        var sameName = MakeFile(@"pc1\other\sports.mp4", "another-video");
        var intro = MakeFile(@"pc1\intro.mp4", "intro-bytes");
        var sound = MakeFile(@"pc1\sfx\applause.mp3", "sound-bytes");
        string packagePath = Path.Combine(_dir, "show" + ShowPackage.Extension);

        ShowPackage.ExportResult saved;
        using (var db = new DatabaseService(Path.Combine(_dir, @"pc1\newsdesk.db")))
        {
            db.AddMedia(new MediaItem { Title = "SPORTS", FilePath = video, CaptionName = "Coach" });
            db.AddMedia(new MediaItem { Title = "SPORTS 2", FilePath = sameName });
            db.AddMedia(new MediaItem { Title = "GONE", FilePath = Path.Combine(_dir, @"pc1\deleted.mp4") });
            db.SetSetting("Intro_VideoPath", intro);
            db.SetSetting("Monitors_VideoMode", "projector");
            var pad = db.GetSoundPad(1);
            pad.Name = "Applause";
            pad.FilePath = sound;
            db.SaveSoundPad(pad);

            saved = ShowPackage.Export(db, packagePath);
            Assert.True(saved.FileCount >= 4);   // plus any sample clips a new database starts with
            Assert.Single(saved.MissingFiles);
        }

        // The second computer
        var pc2Db = Path.Combine(_dir, @"pc2\newsdesk.db");
        var pc2Media = Path.Combine(_dir, @"pc2\Show media");
        var opened = ShowPackage.Import(packagePath, pc2Media, pc2Db);
        Assert.Equal(saved.FileCount, opened.FileCount);
        Assert.False(File.Exists(pc2Db));                       // waits for the next start
        ShowPackage.ApplyPendingImport(pc2Db);

        using var moved = new DatabaseService(pc2Db);
        var media = moved.GetAllMedia();
        var sports = media.Single(m => m.Title == "SPORTS");
        var sports2 = media.Single(m => m.Title == "SPORTS 2");
        Assert.StartsWith(pc2Media, sports.FilePath);
        Assert.Equal("video-bytes", File.ReadAllText(sports.FilePath));
        Assert.Equal("another-video", File.ReadAllText(sports2.FilePath));   // same file name kept apart
        Assert.Equal("Coach", sports.CaptionName);
        Assert.Equal("intro-bytes", File.ReadAllText(moved.GetSetting("Intro_VideoPath")!));
        Assert.Equal("projector", moved.GetSetting("Monitors_VideoMode"));
        var movedPad = moved.GetSoundPad(1);
        Assert.Equal("Applause", movedPad.Name);
        Assert.Equal("sound-bytes", File.ReadAllText(movedPad.FilePath));
    }

    [Fact]
    public void Open_KeepsTheReplacedShowInBackups()
    {
        string packagePath = Path.Combine(_dir, "show" + ShowPackage.Extension);
        using (var db = new DatabaseService(Path.Combine(_dir, @"a\newsdesk.db")))
        {
            db.SetSetting("Headlines_BoardTitle", "FROM A");
            ShowPackage.Export(db, packagePath);
        }

        var targetDb = Path.Combine(_dir, @"b\newsdesk.db");
        using (var existing = new DatabaseService(targetDb))
            existing.SetSetting("Headlines_BoardTitle", "B's OWN SHOW");
        SqliteConnection.ClearAllPools();

        ShowPackage.Import(packagePath, Path.Combine(_dir, @"b\media"), targetDb);
        var backup = ShowPackage.ApplyPendingImport(targetDb);

        Assert.NotNull(backup);
        using (var restored = new DatabaseService(backup))
            Assert.Equal("B's OWN SHOW", restored.GetSetting("Headlines_BoardTitle"));
        using (var now = new DatabaseService(targetDb))
            Assert.Equal("FROM A", now.GetSetting("Headlines_BoardTitle"));
    }

    [Fact]
    public void Open_RejectsAFileThatIsNotAShow()
    {
        var bogus = MakeFile("notashow" + ShowPackage.Extension, "hello");
        Assert.ThrowsAny<Exception>(() => ShowPackage.Import(bogus, Path.Combine(_dir, "m"), Path.Combine(_dir, @"c\newsdesk.db")));
        Assert.False(File.Exists(ShowPackage.PendingImportPath(Path.Combine(_dir, @"c\newsdesk.db"))));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
