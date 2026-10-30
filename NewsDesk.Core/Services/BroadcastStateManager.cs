using NewsDesk.Core.Data;
using NewsDesk.Core.Models;

namespace NewsDesk.Core.Services;

public class BroadcastStateManager
{
    private readonly DatabaseService _db;
    public BroadcastState State { get; } = new();

    public event EventHandler? StateChanged;
    public event EventHandler<string>? OperatorNotification;

    public BroadcastStateManager(DatabaseService db)
    {
        _db = db;
        InitializeState();
    }

    private void InitializeState()
    {
        // Load default ticker items
        var tickerItems = _db.GetAllTickerItems();
        State.LiveTickerItems = tickerItems.Where(t => t.IsActive).ToList();
        State.LiveTickerConfig = new TickerConfig();
        State.IsStandbyActive = true;
        State.IsLiveOnAir = false;
        State.IsLiveIndicatorVisible = true;
    }

    /// <summary>
    /// Loads a video or graphic into PREVIEW.
    /// In accordance with Section 5.3 and 11:
    /// Selecting or previewing a video MUST NEVER interrupt the on-air broadcast.
    /// </summary>
    public void SelectPreviewMedia(MediaItem media)
    {
        if (media == null) return;

        if (!File.Exists(media.FilePath))
        {
            media.IsAvailable = false;
            _db.UpdateMedia(media);
            OperatorNotification?.Invoke(this, $"Warning: File not found for media '{media.Title}' at '{media.FilePath}'.");
        }

        // The canvas has a single standby slot: cueing media replaces any cued graphic
        State.PreviewMedia = media;
        State.PreviewGraphic = null;
        State.ActiveTransition = media.AssignedTransition;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Sets a graphic draft into preview without affecting live broadcast.
    /// </summary>
    public void SelectPreviewGraphic(HeadlineDraft draft)
    {
        if (draft == null) return;

        // The canvas has a single standby slot: cueing a graphic replaces any cued media
        State.PreviewGraphic = draft;
        State.PreviewMedia = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Explicitly switches prepared preview content to on-air.
    /// </summary>
    public bool ExecuteTakeLive()
    {
        if (State.IsTransitioning)
        {
            OperatorNotification?.Invoke(this, "A transition is currently in progress. Please wait for completion.");
            return false;
        }

        if (State.PreviewMedia == null && State.PreviewGraphic == null)
        {
            OperatorNotification?.Invoke(this, "No content is prepared in Preview. Select an item first.");
            return false;
        }

        if (State.PreviewMedia != null && !File.Exists(State.PreviewMedia.FilePath))
        {
            OperatorNotification?.Invoke(this, $"Cannot Take Live: Media file '{State.PreviewMedia.Title}' is inaccessible.");
            return false;
        }

        State.IsTransitioning = true;
        State.IsStandbyActive = false;
        State.IsLiveOnAir = true;

        if (State.PreviewMedia != null)
        {
            State.OnAirMedia = State.PreviewMedia;
            State.OnAirGraphic = null;
            State.ActiveTransition = State.PreviewMedia.AssignedTransition;
        }
        else
        {
            // A graphic slide replaces the on-air video entirely
            State.OnAirGraphic = State.PreviewGraphic;
            State.OnAirMedia = null;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void CompleteTransition()
    {
        State.IsTransitioning = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Clears a stuck transitioning flag (e.g. when no broadcast window is attached).
    /// </summary>
    public void ResetTransition()
    {
        State.IsTransitioning = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Updates or assigns transition to a media item.
    /// Per Section 10.4: Changing an assignment must not automatically activate the video.
    /// </summary>
    public void AssignTransition(int mediaId, TransitionType transition)
    {
        _db.UpdateMediaTransition(mediaId, transition);
        if (State.PreviewMedia != null && State.PreviewMedia.Id == mediaId)
        {
            State.PreviewMedia.AssignedTransition = transition;
            State.ActiveTransition = transition;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Publishes graphic to air explicitly.
    /// </summary>
    public void PublishLiveGraphic(HeadlineDraft draft)
    {
        State.LiveGraphic = draft;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearLiveGraphic()
    {
        State.LiveGraphic = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Publishes ticker to air without interrupting current video or graphics.
    /// </summary>
    public void PublishLiveTicker(TickerConfig config, List<TickerItem> items)
    {
        State.LiveTickerConfig = config.Clone();
        State.LiveTickerItems = new List<TickerItem>(items.Where(i => i.IsActive));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetStandby(bool active)
    {
        State.IsStandbyActive = active;
        if (active)
        {
            State.IsLiveOnAir = false;
            State.IsTransitioning = false;
            State.IsIntroActive = false;
            State.IsVoiceOverActive = false;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetIntroActive(bool active)
    {
        State.IsIntroActive = active;
        if (active)
        {
            State.IsStandbyActive = false;
            State.IsLiveOnAir = true;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetVoiceOverActive(bool active)
    {
        State.IsVoiceOverActive = active;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetSpeakersLive(bool live)
    {
        State.AreSpeakersLive = live;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetSceneCaptionVisible(bool visible)
    {
        State.IsSceneCaptionVisible = visible;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleLiveIndicator(bool visible)
    {
        State.IsLiveIndicatorVisible = visible;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
