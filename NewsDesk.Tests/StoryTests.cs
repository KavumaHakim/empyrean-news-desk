using NewsDesk.Core.Data;
using NewsDesk.Core.Models;

namespace NewsDesk.Tests;

public class StoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"newsdesk_story_{Guid.NewGuid()}");
    private readonly DatabaseService _db;

    public StoryTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new DatabaseService(Path.Combine(_dir, "newsdesk.db"));
    }

    [Fact]
    public void Story_SavesHeadlineFlagAndExtraMedia()
    {
        int main = _db.AddMedia(new MediaItem { Title = "Main", FilePath = @"C:\m.mp4" });
        int broll = _db.AddMedia(new MediaItem { Title = "B-roll", FilePath = @"C:\b.mp4" });
        int photo = _db.AddMedia(new MediaItem { Title = "Photo", FilePath = @"C:\p.jpg" });

        var story = new HeadlineSequenceItem
        {
            Title = "COLOUR FEST",
            Subtitle = "First colour festival",
            InHeadlines = false,
            MediaItem = new MediaItem { Id = main }
        };
        story.ExtraMedia.Add(new MediaItem { Id = broll });
        story.ExtraMedia.Add(new MediaItem { Id = photo });
        _db.SaveHeadlineSequenceItem(story);

        var loaded = _db.GetHeadlineSequence().Single(s => s.Id == story.Id);
        Assert.False(loaded.InHeadlines);
        Assert.Equal(main, loaded.MediaItemId);
        Assert.Equal([broll, photo], loaded.ExtraMediaIds);

        // Removing extra media sticks
        story.ExtraMedia.RemoveAt(0);
        _db.SaveHeadlineSequenceItem(story);
        Assert.Equal([photo], _db.GetHeadlineSequence().Single(s => s.Id == story.Id).ExtraMediaIds);
    }

    [Fact]
    public void ExistingHeadlines_StayInTheHeadlines_AfterUpgrade()
    {
        // A headline saved before stories existed has no flag: it must stay in the segment
        var item = new HeadlineSequenceItem { Title = "MELON HEIST", Subtitle = "Three men, one melon" };
        _db.SaveHeadlineSequenceItem(item);

        var loaded = _db.GetHeadlineSequence().Single();
        Assert.True(loaded.InHeadlines);
        Assert.Equal("Three men, one melon", loaded.Subtitle);
        Assert.Empty(loaded.ExtraMediaIds);
    }

    [Fact]
    public void Story_SavesLocationBreakingAndSpeakers()
    {
        var story = new HeadlineSequenceItem
        {
            Title = "PRIZE GIVING",
            Location = "Main Hall",
            IsBreaking = true,
            SpeakersText = "Amina Okello | Head Prefect\nMr. Kato | Class Teacher\n\nGuest Speaker"
        };
        _db.SaveHeadlineSequenceItem(story);

        var loaded = _db.GetHeadlineSequence().Single(s => s.Id == story.Id);
        Assert.Equal("Main Hall", loaded.Location);
        Assert.True(loaded.IsBreaking);
        Assert.Equal(
            [new StorySpeaker("Amina Okello", "Head Prefect"), new StorySpeaker("Mr. Kato", "Class Teacher"), new StorySpeaker("Guest Speaker", "")],
            loaded.Speakers);
        Assert.Equal("Amina Okello — Head Prefect", loaded.Speakers[0].Label);
        Assert.Equal("Guest Speaker", loaded.Speakers[2].Label);
    }

    [Fact]
    public void ListTitle_FallsBackToMediaTitle_UntilAHeadlineIsWritten()
    {
        var story = new HeadlineSequenceItem { MediaItem = new MediaItem { Title = "CANDIDATES" } };
        Assert.Equal("CANDIDATES", story.ListTitle);

        story.Title = "BIGGEST SENIOR SIX CLASS EVER";
        Assert.Equal("BIGGEST SENIOR SIX CLASS EVER", story.ListTitle);
    }

    [Fact]
    public void Backup_IsACompleteCopyOfTheOperatorsWork()
    {
        _db.SaveHeadlineSequenceItem(new HeadlineSequenceItem { Title = "SPORTS NEWS", Subtitle = "Nationals" });
        _db.SetSetting("Speakers_Layout", "pip_right");

        var backup = _db.CreateBackup("test");

        Assert.True(File.Exists(backup));
        Assert.StartsWith(Path.Combine(_dir, "Backups"), backup);
        var copy = new DatabaseService(backup);
        Assert.Equal("SPORTS NEWS", copy.GetHeadlineSequence().Single().Title);
        Assert.Equal("pip_right", copy.GetSetting("Speakers_Layout"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
