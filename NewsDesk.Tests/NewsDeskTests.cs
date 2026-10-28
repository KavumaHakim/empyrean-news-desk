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
    public void MimeTypes_AreCorrectlyResolved(string filename, string expectedMime)
    {
        string mime = MediaHttpServer.GetMimeType(filename);
        Assert.Equal(expectedMime, mime);
    }
}
