using NewsDesk.Core.Data;
using NewsDesk.Core.Models;
using NewsDesk.Core.Server;
using NewsDesk.Core.Services;

namespace NewsDesk.Tests;

public class TransitionConfigTests
{
    [Fact]
    public void CutTransition_HasZeroDuration()
    {
        int duration = TransitionConfig.GetDurationMs(TransitionType.Cut);
        Assert.Equal(0, duration);
    }

    [Theory]
    [InlineData(TransitionType.Fade)]
    [InlineData(TransitionType.Dissolve)]
    [InlineData(TransitionType.SlideLeft)]
    [InlineData(TransitionType.SlideRight)]
    [InlineData(TransitionType.Wipe)]
    [InlineData(TransitionType.Zoom)]
    public void AnimatedTransitions_HaveStrictly500MsDuration(TransitionType type)
    {
        int duration = TransitionConfig.GetDurationMs(type);
        Assert.Equal(500, duration);
    }

    [Theory]
    [InlineData(TransitionType.Cut, "transition-cut")]
    [InlineData(TransitionType.Fade, "transition-fade")]
    [InlineData(TransitionType.Dissolve, "transition-dissolve")]
    [InlineData(TransitionType.SlideLeft, "transition-slide-left")]
    [InlineData(TransitionType.SlideRight, "transition-slide-right")]
    [InlineData(TransitionType.Wipe, "transition-wipe")]
    [InlineData(TransitionType.Zoom, "transition-zoom")]
    public void TransitionCssClass_MatchesSpecification(TransitionType type, string expectedClass)
    {
        string cssClass = TransitionConfig.ToCssClass(type);
        Assert.Equal(expectedClass, cssClass);
    }

    [Theory]
    [InlineData(TransitionType.Cut, "cut")]
    [InlineData(TransitionType.Fade, "fade")]
    [InlineData(TransitionType.Dissolve, "dissolve")]
    [InlineData(TransitionType.SlideLeft, "slide-left")]
    [InlineData(TransitionType.SlideRight, "slide-right")]
    [InlineData(TransitionType.Wipe, "wipe")]
    [InlineData(TransitionType.Zoom, "zoom")]
    public void TransitionCommandName_MatchesCanvasJsTransitionMap(TransitionType type, string expectedName)
    {
        string commandName = TransitionConfig.ToCommandName(type);
        Assert.Equal(expectedName, commandName);
    }
}

public class DatabaseServiceTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly DatabaseService _db;

    public DatabaseServiceTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"newsdesk_test_{Guid.NewGuid()}.db");
        _db = new DatabaseService(_tempDbPath);
    }

    [Fact]
    public void MediaItem_CanBeAdded_Updated_AndRetrieved()
    {
        var item = new MediaItem
        {
            Title = "Morning Economy Report",
            FilePath = "C:\\dummy\\economy.mp4",
            DurationSeconds = 125.5,
            Category = StandardCategories.Business,
            AssignedTransition = TransitionType.SlideLeft,
            Description = "Inflation and jobs data analysis"
        };

        int id = _db.AddMedia(item);
        Assert.True(id > 0);

        var retrieved = _db.GetMediaById(id);
        Assert.NotNull(retrieved);
        Assert.Equal("Morning Economy Report", retrieved.Title);
        Assert.Equal(TransitionType.SlideLeft, retrieved.AssignedTransition);
        Assert.Equal(StandardCategories.Business, retrieved.Category);

        // Update transition
        _db.UpdateMediaTransition(id, TransitionType.Dissolve);
        var updated = _db.GetMediaById(id);
        Assert.NotNull(updated);
        Assert.Equal(TransitionType.Dissolve, updated.AssignedTransition);
    }

    [Fact]
    public void HeadlineDraft_CanBeSavedAndQueried()
    {
        var draft = new HeadlineDraft
        {
            Title = "CRITICAL WEATHER ADVISORY",
            Subtitle = "High winds forecast for coastal regions",
            Category = "Weather",
            Location = "NORTH HARBOR",
            ReporterName = "Dave Miller",
            IsBreaking = true,
            BackgroundStyle = "breaking"
        };

        int id = _db.SaveHeadlineDraft(draft);
        Assert.True(id > 0);

        var all = _db.GetAllHeadlineDrafts();
        Assert.Contains(all, d => d.Title == "CRITICAL WEATHER ADVISORY" && d.IsBreaking);
    }

    [Fact]
    public void HeadlineSequence_RoundTrips_Reorders_AndSurvivesMediaRemoval()
    {
        int mediaId = _db.AddMedia(new MediaItem { Title = "Flood aerials", FilePath = "C:\\dummy\\flood.jpg" });

        var first = new HeadlineSequenceItem { Title = "FLOODS HIT THE VALLEY", Subtitle = "Roads closed", OrderIndex = 0, DurationSeconds = 6 };
        first.MediaItemId = mediaId;
        var second = new HeadlineSequenceItem { Title = "SCHOOL WINS FINAL", OrderIndex = 1 };
        _db.SaveHeadlineSequenceItem(first);
        _db.SaveHeadlineSequenceItem(second);
        Assert.True(first.Id > 0 && second.Id > 0);

        var loaded = _db.GetHeadlineSequence();
        Assert.Equal(["FLOODS HIT THE VALLEY", "SCHOOL WINS FINAL"], loaded.Select(h => h.Title));
        Assert.Equal(mediaId, loaded[0].MediaItemId);
        Assert.Equal(6, loaded[0].DurationSeconds);
        Assert.Null(loaded[1].MediaItemId);

        _db.UpdateHeadlineSequenceOrder([second, first]);
        Assert.Equal("SCHOOL WINS FINAL", _db.GetHeadlineSequence()[0].Title);

        // Removing the picture keeps the headline, now without media
        _db.DeleteMedia(mediaId);
        var flood = _db.GetHeadlineSequence().Single(h => h.Id == first.Id);
        Assert.Null(flood.MediaItemId);

        _db.DeleteHeadlineSequenceItem(second.Id);
        Assert.Single(_db.GetHeadlineSequence());
    }

    [Fact]
    public void TickerItem_CanBeAddedAndOrdered()
    {
        var item1 = new TickerItem { Text = "Ticker Item Alpha", Category = "NEWS", OrderIndex = 0 };
        var item2 = new TickerItem { Text = "Ticker Item Beta", Category = "SPORTS", OrderIndex = 1 };

        _db.SaveTickerItem(item1);
        _db.SaveTickerItem(item2);

        var all = _db.GetAllTickerItems();
        Assert.True(all.Count >= 2);
        Assert.Contains(all, t => t.Text == "Ticker Item Alpha");
        Assert.Contains(all, t => t.Text == "Ticker Item Beta");
    }

    [Fact]
    public void Settings_CanBePersistedAndRetrieved()
    {
        _db.SetSetting("SelectedMonitor", "Display2");
        var val = _db.GetSetting("SelectedMonitor");
        Assert.Equal("Display2", val);
    }

    [Fact]
    public void MediaCaption_RoundTrips_ThroughAddAndUpdate()
    {
        var item = new MediaItem
        {
            Title = "Head Prefect Portrait",
            FilePath = "C:\\dummy\\prefect.jpg",
            CaptionName = "Amina Okello",
            CaptionTitle = "Head Prefect"
        };
        int id = _db.AddMedia(item);

        var retrieved = _db.GetMediaById(id);
        Assert.NotNull(retrieved);
        Assert.Equal("Amina Okello", retrieved.CaptionName);
        Assert.Equal("Head Prefect", retrieved.CaptionTitle);
        Assert.True(retrieved.IsImage);

        retrieved.CaptionTitle = "Class Captain";
        _db.UpdateMedia(retrieved);
        Assert.Equal("Class Captain", _db.GetAllMedia().Single(m => m.Id == id).CaptionTitle);
    }

    [Fact]
    public void OlderDatabase_GainsCaptionColumns_OnOpen()
    {
        // A database written by a build that predates captions
        var oldPath = Path.Combine(Path.GetTempPath(), $"newsdesk_old_{Guid.NewGuid()}.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={oldPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"CREATE TABLE MediaItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, Title TEXT NOT NULL, FilePath TEXT NOT NULL,
                DurationSeconds REAL NOT NULL DEFAULT 0, Category TEXT NOT NULL DEFAULT 'National News',
                AssignedTransition INTEGER NOT NULL DEFAULT 1, Description TEXT DEFAULT '',
                IsAvailable INTEGER NOT NULL DEFAULT 1, CreatedAt TEXT NOT NULL);
                INSERT INTO MediaItems (Title, FilePath, CreatedAt) VALUES ('Legacy', 'C:\\legacy.mp4', '2026-01-01T00:00:00Z');";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var db = new DatabaseService(oldPath);
        var legacy = db.GetAllMedia().Single(m => m.Title == "Legacy");
        Assert.Equal(string.Empty, legacy.CaptionName);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(oldPath); } catch { }
    }

    [Fact]
    public void VoiceOverAndSpeakerConfigs_RoundTrip()
    {
        _db.SaveVoiceOverConfig(new VoiceOverConfig
        {
            AudioPath = "C:\\vo\\narration.mp3",
            Name = "Mr. Kato",
            Designation = "Class Teacher",
            Volume = 0.8,
            DuckLevel = 0.15,
            ShowTag = false,
            Loop = true
        });
        var vo = _db.GetVoiceOverConfig();
        Assert.Equal("Mr. Kato", vo.Name);
        Assert.Equal(0.15, vo.DuckLevel, 3);
        Assert.False(vo.ShowTag);
        Assert.True(vo.Loop);

        _db.SaveSpeakerBoxConfig(2, new SpeakerBoxConfig
        {
            SourceKind = "camera",
            CameraIndex = 1,
            Name = "Guest Speaker",
            Designation = "Alumni President",
            PlayAudio = false
        });
        var box2 = _db.GetSpeakerBoxConfig(2);
        Assert.Equal("camera", box2.SourceKind);
        Assert.Equal(1, box2.CameraIndex);
        Assert.Equal("Alumni President", box2.Designation);
        Assert.False(box2.PlayAudio);
        Assert.True(box2.HasSource);

        // Untouched box keeps defaults
        var box1 = _db.GetSpeakerBoxConfig(1);
        Assert.Equal("none", box1.SourceKind);
        Assert.False(box1.HasSource);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            if (File.Exists(_tempDbPath))
            {
                File.Delete(_tempDbPath);
            }
        }
        catch { }
    }
}

public class BroadcastStateManagerTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly DatabaseService _db;
    private readonly BroadcastStateManager _stateManager;

    public BroadcastStateManagerTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"newsdesk_state_test_{Guid.NewGuid()}.db");
        _db = new DatabaseService(_tempDbPath);
        _stateManager = new BroadcastStateManager(_db);
    }

    [Fact]
    public void SelectingMedia_LoadsToPreview_WithoutChangingOnAir()
    {
        // 1. Arrange a live on-air item
        var liveMedia = new MediaItem { Id = 1, Title = "Live Story", FilePath = "C:\\live.mp4" };
        var incomingMedia = new MediaItem { Id = 2, Title = "Incoming Next Story", FilePath = "C:\\next.mp4" };

        _stateManager.State.OnAirMedia = liveMedia;
        _stateManager.State.IsLiveOnAir = true;

        // 2. Act: Operator selects the incoming story
        _stateManager.SelectPreviewMedia(incomingMedia);

        // 3. Assert: Specification requirement (Sections 5.3 & 11):
        // Selecting another video MUST NEVER replace the on-air broadcast!
        Assert.Equal("Live Story", _stateManager.State.OnAirMedia?.Title);
        Assert.Equal("Incoming Next Story", _stateManager.State.PreviewMedia?.Title);
        Assert.True(_stateManager.State.IsLiveOnAir);
    }

    [Fact]
    public void TakeLive_ReplacesOnAir_WithPreviewContent()
    {
        // Create an actual temporary file to test availability check
        var tempFile = Path.GetTempFileName();
        try
        {
            var previewMedia = new MediaItem
            {
                Id = 10,
                Title = "Breaking Investigation",
                FilePath = tempFile,
                AssignedTransition = TransitionType.Zoom
            };

            _stateManager.SelectPreviewMedia(previewMedia);
            Assert.Equal("Breaking Investigation", _stateManager.State.PreviewMedia?.Title);

            bool success = _stateManager.ExecuteTakeLive();

            Assert.True(success);
            Assert.Equal("Breaking Investigation", _stateManager.State.OnAirMedia?.Title);
            Assert.Equal(TransitionType.Zoom, _stateManager.State.ActiveTransition);
            Assert.True(_stateManager.State.IsLiveOnAir);
            Assert.False(_stateManager.State.IsStandbyActive);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void CueingGraphic_ReplacesCuedMedia_AndViceVersa()
    {
        var media = new MediaItem { Id = 3, Title = "Cued Video", FilePath = "C:\\cued.mp4" };
        var graphic = new HeadlineDraft { Title = "Cued Slide" };

        _stateManager.SelectPreviewMedia(media);
        _stateManager.SelectPreviewGraphic(graphic);
        Assert.Null(_stateManager.State.PreviewMedia);
        Assert.Equal("Cued Slide", _stateManager.State.PreviewGraphic?.Title);

        _stateManager.SelectPreviewMedia(media);
        Assert.Null(_stateManager.State.PreviewGraphic);
        Assert.Equal("Cued Video", _stateManager.State.PreviewMedia?.Title);
    }

    [Fact]
    public void TakeLive_AfterCueingGraphicOverVideo_PutsGraphicOnAir()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var video = new MediaItem { Id = 11, Title = "Old Video", FilePath = tempFile, AssignedTransition = TransitionType.Zoom };
            var slide = new HeadlineDraft { Title = "Full Screen Slide" };

            // Video goes on air, then operator cues a graphic slide
            _stateManager.SelectPreviewMedia(video);
            Assert.True(_stateManager.ExecuteTakeLive());
            _stateManager.CompleteTransition();

            _stateManager.SelectPreviewMedia(video);
            _stateManager.SelectPreviewGraphic(slide);
            Assert.True(_stateManager.ExecuteTakeLive());

            Assert.Null(_stateManager.State.OnAirMedia);
            Assert.Equal("Full Screen Slide", _stateManager.State.OnAirGraphic?.Title);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void TakeLive_Video_ClearsOnAirGraphic()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            _stateManager.SelectPreviewGraphic(new HeadlineDraft { Title = "Slide" });
            Assert.True(_stateManager.ExecuteTakeLive());
            _stateManager.CompleteTransition();

            _stateManager.SelectPreviewMedia(new MediaItem { Id = 12, Title = "Next Video", FilePath = tempFile });
            Assert.True(_stateManager.ExecuteTakeLive());

            Assert.Null(_stateManager.State.OnAirGraphic);
            Assert.Equal("Next Video", _stateManager.State.OnAirMedia?.Title);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void TickerDraft_DoesNotAffectLiveTicker_UntilPublished()
    {
        var initialLiveItems = new List<TickerItem>
        {
            new() { Id = 1, Text = "LIVE TICKER HEADLINE", IsActive = true }
        };
        _stateManager.State.LiveTickerItems = initialLiveItems;

        // Operator prepares new draft
        var draftConfig = new TickerConfig { IsBreakingStyle = true, HeaderTag = "BREAKING" };
        var draftItems = new List<TickerItem>
        {
            new() { Id = 2, Text = "DRAFT ONLY HEADLINE", IsActive = true }
        };

        // Live ticker should still have initial item
        Assert.Equal("LIVE TICKER HEADLINE", _stateManager.State.LiveTickerItems[0].Text);

        // Publish explicitly
        _stateManager.PublishLiveTicker(draftConfig, draftItems);

        // Now live ticker is updated
        Assert.Equal("DRAFT ONLY HEADLINE", _stateManager.State.LiveTickerItems[0].Text);
        Assert.True(_stateManager.State.LiveTickerConfig.IsBreakingStyle);
    }

    [Fact]
    public void Standby_StopsVoiceOver_ButKeepsSpeakerWindowsState()
    {
        _stateManager.SetStandby(false);
        _stateManager.SetVoiceOverActive(true);
        _stateManager.SetSpeakersLive(true);

        _stateManager.SetStandby(true);

        Assert.False(_stateManager.State.IsVoiceOverActive);
        Assert.True(_stateManager.State.AreSpeakersLive);
    }

    [Fact]
    public void Overlays_DoNotChangeOnAirOrPreviewContent()
    {
        var picture = new MediaItem { Id = 7, Title = "Class Photo", FilePath = "C:\\photo.png", CaptionName = "Class of 2026" };
        _stateManager.SelectPreviewMedia(picture);

        _stateManager.SetVoiceOverActive(true);
        _stateManager.SetSpeakersLive(true);
        _stateManager.SetSceneCaptionVisible(false);

        Assert.Same(picture, _stateManager.State.PreviewMedia);
        Assert.Null(_stateManager.State.OnAirMedia);
        Assert.False(_stateManager.State.IsSceneCaptionVisible);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath);
        }
        catch { }
    }
}

public class MediaHttpServerTests
{
    [Theory]
    [InlineData("video.mp4", "video/mp4")]
    [InlineData("clip.webm", "video/webm")]
    [InlineData("index.html", "text/html; charset=utf-8")]
    [InlineData("style.css", "text/css; charset=utf-8")]
    [InlineData("app.js", "application/javascript; charset=utf-8")]
    [InlineData("portrait.webp", "image/webp")]
    [InlineData("photo.JPG", "image/jpeg")]
    [InlineData("narration.m4a", "audio/mp4")]

    public void MimeTypes_AreCorrectlyResolved(string filename, string expectedMime)
    {
        string mime = MediaHttpServer.GetMimeType(filename);
        Assert.Equal(expectedMime, mime);
    }
}
