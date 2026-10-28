using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using NewsDesk.App.Services;
using NewsDesk.App.Views;
using NewsDesk.Core.Data;
using NewsDesk.Core.Models;
using NewsDesk.Core.Server;
using NewsDesk.Core.Services;

namespace NewsDesk.App.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly DatabaseService _db;
    private readonly MediaHttpServer _server;
    private readonly BroadcastStateManager _stateManager;
    private readonly DisplayManager _displayManager;
    private readonly Dispatcher _dispatcher;

    private BroadcastWindow? _broadcastWindow;

    // Signals that the canvas has loaded media into the standby slot (safe to take live)
    private TaskCompletionSource<bool>? _scenePreparedTcs;

    // Actual default rundown playlist id (seeded row may not be Id 1)
    private int _rundownPlaylistId = 1;

    // Collections
    public ObservableCollection<MediaItem> MediaLibrary { get; } = [];
    public ObservableCollection<MediaItem> FilteredMediaLibrary { get; } = [];
    public ObservableCollection<PlaylistItem> RundownItems { get; } = [];
    public ObservableCollection<HeadlineDraft> HeadlineDrafts { get; } = [];
    public ObservableCollection<TickerItem> TickerDraftItems { get; } = [];
    public ObservableCollection<DisplayInfo> ConnectedDisplays { get; } = [];

    // Filter properties
    private string _searchFilter = string.Empty;
    public string SearchFilter
    {
        get => _searchFilter;
        set
        {
            if (SetProperty(ref _searchFilter, value))
                ApplyMediaFilter();
        }
    }

    private string _categoryFilter = "All";
    public string CategoryFilter
    {
        get => _categoryFilter;
        set
        {
            if (SetProperty(ref _categoryFilter, value))
                ApplyMediaFilter();
        }
    }

    public List<string> AvailableCategories { get; } = ["All", ..StandardCategories.All];
    public List<TransitionType> AvailableTransitions { get; } = Enum.GetValues<TransitionType>().ToList();

    // Selected items
    private MediaItem? _selectedMedia;
    public MediaItem? SelectedMedia
    {
        get => _selectedMedia;
        set => SetProperty(ref _selectedMedia, value);
    }

    private PlaylistItem? _selectedRundownItem;
    public PlaylistItem? SelectedRundownItem
    {
        get => _selectedRundownItem;
        set => SetProperty(ref _selectedRundownItem, value);
    }

    private DisplayInfo? _selectedDisplay;
    public DisplayInfo? SelectedDisplay
    {
        get => _selectedDisplay;
        set => SetProperty(ref _selectedDisplay, value);
    }

    // Graphics Studio draft
    private HeadlineDraft _editingHeadline = new();
    public HeadlineDraft EditingHeadline
    {
        get => _editingHeadline;
        set => SetProperty(ref _editingHeadline, value);
    }

    // Ticker Studio draft
    private TickerConfig _draftTickerConfig = new();
    public TickerConfig DraftTickerConfig
    {
        get => _draftTickerConfig;
        set => SetProperty(ref _draftTickerConfig, value);
    }

    private string _newTickerItemText = string.Empty;
    public string NewTickerItemText
    {
        get => _newTickerItemText;
        set => SetProperty(ref _newTickerItemText, value);
    }

    private string _newTickerItemCategory = "NEWS";
    public string NewTickerItemCategory
    {
        get => _newTickerItemCategory;
        set => SetProperty(ref _newTickerItemCategory, value);
    }

    // Breaking News Fast Input
    private string _quickBreakingHeadline = "MAJOR INCIDENT DECLARED IN METROPOLITAN REGION";
    public string QuickBreakingHeadline
    {
        get => _quickBreakingHeadline;
        set => SetProperty(ref _quickBreakingHeadline, value);
    }

    private string _quickBreakingSubtitle = "Emergency response teams and civil services mobilized on site";
    public string QuickBreakingSubtitle
    {
        get => _quickBreakingSubtitle;
        set => SetProperty(ref _quickBreakingSubtitle, value);
    }

    // Broadcast Status & Telemetry
    public bool IsLiveOnAir => _stateManager.State.IsLiveOnAir;
    public bool IsStandbyActive => _stateManager.State.IsStandbyActive;
    public bool IsLiveIndicatorVisible => _stateManager.State.IsLiveIndicatorVisible;
    public bool IsTransitioning => _stateManager.State.IsTransitioning;
    public TransitionType ActiveTransition => _stateManager.State.ActiveTransition;

    public MediaItem? OnAirMedia => _stateManager.State.OnAirMedia;
    public MediaItem? PreviewMedia => _stateManager.State.PreviewMedia;
    public HeadlineDraft? LiveGraphic => _stateManager.State.LiveGraphic;
    public HeadlineDraft? PreviewGraphic => _stateManager.State.PreviewGraphic;

    private double _playbackCurrentTime;
    public double PlaybackCurrentTime
    {
        get => _playbackCurrentTime;
        set => SetProperty(ref _playbackCurrentTime, value);
    }

    private double _playbackDuration;
    public double PlaybackDuration
    {
        get => _playbackDuration;
        set => SetProperty(ref _playbackDuration, value);
    }

    private string _playbackTimeText = "00:00 / 00:00";
    public string PlaybackTimeText
    {
        get => _playbackTimeText;
        set => SetProperty(ref _playbackTimeText, value);
    }

    private string _statusNotification = "System initialized and ready for manual broadcast.";
    public string StatusNotification
    {
        get => _statusNotification;
        set => SetProperty(ref _statusNotification, value);
    }

    /// <summary>
    /// True while the operator drags the timeline; suppresses incoming timeupdate
    /// values so the thumb doesn't fight the user.
    /// </summary>
    public bool SuppressPlaybackTimeUpdates { get; set; }

    private bool _isBroadcastWindowOpen = false;
    public bool IsBroadcastWindowOpen
    {
        get => _isBroadcastWindowOpen;
        set => SetProperty(ref _isBroadcastWindowOpen, value);
    }

    private string _transitionPreviewStatus = "Ready";
    public string TransitionPreviewStatus
    {
        get => _transitionPreviewStatus;
        set => SetProperty(ref _transitionPreviewStatus, value);
    }

    // Relay Commands
    public RelayCommand ImportVideoCommand { get; }
    public RelayCommand DeleteMediaCommand { get; }
    public RelayCommand SelectForPreviewCommand { get; }
    public RelayCommand AddToRundownCommand { get; }
    public RelayCommand RemoveFromRundownCommand { get; }
    public RelayCommand MoveRundownUpCommand { get; }
    public RelayCommand MoveRundownDownCommand { get; }
    public RelayCommand TakeLiveRundownCommand { get; }

    public RelayCommand TakeLiveCommand { get; }
    public RelayCommand PlayCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ReplayCommand { get; }
    public RelayCommand SeekCommand { get; }
    public RelayCommand ToggleStandbyCommand { get; }
    public RelayCommand ToggleLiveIndicatorCommand { get; }

    public RelayCommand SaveHeadlineDraftCommand { get; }
    public RelayCommand LoadHeadlineDraftCommand { get; }
    public RelayCommand DeleteHeadlineDraftCommand { get; }
    public RelayCommand PreviewHeadlineCommand { get; }
    public RelayCommand TakeLiveHeadlineCommand { get; }
    public RelayCommand UpdateLiveHeadlineCommand { get; }
    public RelayCommand ClearLiveHeadlineCommand { get; }

    public RelayCommand AddTickerItemCommand { get; }
    public RelayCommand DeleteTickerItemCommand { get; }
    public RelayCommand PublishTickerCommand { get; }
    public RelayCommand ToggleTickerPauseCommand { get; }

    public RelayCommand QuickBreakingLiveCommand { get; }
    public RelayCommand QuickBreakingPreviewCommand { get; }

    public RelayCommand LaunchBroadcastCommand { get; }
    public RelayCommand CloseBroadcastCommand { get; }
    public RelayCommand RefreshDisplaysCommand { get; }
    public RelayCommand TestTransitionCommand { get; }

    public MainViewModel(
        DatabaseService db,
        MediaHttpServer server,
        BroadcastStateManager stateManager,
        DisplayManager displayManager)
    {
        _db = db;
        _server = server;
        _stateManager = stateManager;
        _displayManager = displayManager;
        _dispatcher = Dispatcher.CurrentDispatcher;

        // Register StateManager event listeners
        _stateManager.StateChanged += (s, e) => _dispatcher.Invoke(OnStateChanged);
        _stateManager.OperatorNotification += (s, msg) => _dispatcher.Invoke(() => StatusNotification = msg);

        _displayManager.DisplaysChanged += (s, e) => _dispatcher.Invoke(RefreshDisplays);

        // Commands
        ImportVideoCommand = new RelayCommand(ImportVideo);
        DeleteMediaCommand = new RelayCommand(DeleteMedia, () => SelectedMedia != null);
        SelectForPreviewCommand = new RelayCommand(SelectForPreview);
        AddToRundownCommand = new RelayCommand(AddToRundown, () => SelectedMedia != null);
        RemoveFromRundownCommand = new RelayCommand(RemoveFromRundown, () => SelectedRundownItem != null);
        MoveRundownUpCommand = new RelayCommand(MoveRundownUp, () => SelectedRundownItem != null);
        MoveRundownDownCommand = new RelayCommand(MoveRundownDown, () => SelectedRundownItem != null);
        TakeLiveRundownCommand = new RelayCommand(TakeLiveRundown, () => SelectedRundownItem != null);

        TakeLiveCommand = new RelayCommand(TakeLive);
        PlayCommand = new RelayCommand(() => SendCanvasCommand("play"));
        PauseCommand = new RelayCommand(() => SendCanvasCommand("pause"));
        StopCommand = new RelayCommand(() => SendCanvasCommand("stop"));
        ReplayCommand = new RelayCommand(() => SendCanvasCommand("replay"));
        SeekCommand = new RelayCommand(param => {
            if (param is double sec)
            {
                SendCanvasCommand("seek", sec);
            }
        });

        ToggleStandbyCommand = new RelayCommand(ToggleStandby);
        ToggleLiveIndicatorCommand = new RelayCommand(ToggleLiveIndicator);

        SaveHeadlineDraftCommand = new RelayCommand(SaveHeadlineDraft);
        LoadHeadlineDraftCommand = new RelayCommand(param => {
            if (param is HeadlineDraft d) EditingHeadline = d;
        });
        DeleteHeadlineDraftCommand = new RelayCommand(DeleteHeadlineDraft);
        PreviewHeadlineCommand = new RelayCommand(PreviewHeadline);
        TakeLiveHeadlineCommand = new RelayCommand(TakeLiveHeadline);
        UpdateLiveHeadlineCommand = new RelayCommand(UpdateLiveHeadline);
        ClearLiveHeadlineCommand = new RelayCommand(ClearLiveHeadline);

        AddTickerItemCommand = new RelayCommand(AddTickerItem);
        DeleteTickerItemCommand = new RelayCommand(DeleteTickerItem);
        PublishTickerCommand = new RelayCommand(PublishTicker);
        ToggleTickerPauseCommand = new RelayCommand(ToggleTickerPause);

        QuickBreakingLiveCommand = new RelayCommand(QuickBreakingLive);
        QuickBreakingPreviewCommand = new RelayCommand(QuickBreakingPreview);

        LaunchBroadcastCommand = new RelayCommand(LaunchBroadcast);
        CloseBroadcastCommand = new RelayCommand(CloseBroadcast);
        RefreshDisplaysCommand = new RelayCommand(RefreshDisplays);
        TestTransitionCommand = new RelayCommand(TestTransition);

        LoadInitialData();
    }

    private void LoadInitialData()
    {
        // Load Media
        var media = _db.GetAllMedia();
        MediaLibrary.Clear();
        foreach (var m in media)
        {
            _server.RegisterApprovedMedia(m.Id, m.FilePath);
            MediaLibrary.Add(m);
        }
        ApplyMediaFilter();

        // Load Rundown
        var rundown = _db.GetDefaultPlaylist();
        _rundownPlaylistId = rundown.Id;
        RundownItems.Clear();
        foreach (var r in rundown.Items)
        {
            RundownItems.Add(r);
        }

        // Load Headlines
        var drafts = _db.GetAllHeadlineDrafts();
        HeadlineDrafts.Clear();
        foreach (var d in drafts) HeadlineDrafts.Add(d);
        if (HeadlineDrafts.Count > 0) EditingHeadline = HeadlineDrafts[0];

        // Load Ticker
        var ticker = _db.GetAllTickerItems();
        TickerDraftItems.Clear();
        foreach (var t in ticker) TickerDraftItems.Add(t);

        // Load Displays
        RefreshDisplays();
    }

    private void RefreshDisplays()
    {
        var displays = _displayManager.GetConnectedDisplays();
        ConnectedDisplays.Clear();
        foreach (var d in displays) ConnectedDisplays.Add(d);

        // Default to secondary/projector if available, otherwise primary
        SelectedDisplay = _displayManager.GetSecondaryOrProjectorDisplay();
        StatusNotification = $"Detected {displays.Count} display(s). Projector target: {SelectedDisplay?.DisplayName}";
    }

    private void ApplyMediaFilter()
    {
        FilteredMediaLibrary.Clear();
        var query = MediaLibrary.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SearchFilter))
        {
            query = query.Where(m => m.Title.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase)
                                  || m.Description.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (CategoryFilter != "All")
        {
            query = query.Where(m => m.Category.Equals(CategoryFilter, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in query)
        {
            FilteredMediaLibrary.Add(item);
        }
    }

    private void OnStateChanged()
    {
        OnPropertyChanged(nameof(IsLiveOnAir));
        OnPropertyChanged(nameof(IsStandbyActive));
        OnPropertyChanged(nameof(IsLiveIndicatorVisible));
        OnPropertyChanged(nameof(IsTransitioning));
        OnPropertyChanged(nameof(ActiveTransition));
        OnPropertyChanged(nameof(OnAirMedia));
        OnPropertyChanged(nameof(PreviewMedia));
        OnPropertyChanged(nameof(LiveGraphic));
        OnPropertyChanged(nameof(PreviewGraphic));
    }

    #region Media & Playback Actions

    private void ImportVideo()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import News Video to Media Library",
            Filter = "Video Files (*.mp4;*.webm;*.mov;*.m4v)|*.mp4;*.webm;*.mov;*.m4v|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            foreach (var file in dialog.FileNames)
            {
                var title = Path.GetFileNameWithoutExtension(file);
                var item = new MediaItem
                {
                    Title = title,
                    FilePath = file,
                    DurationSeconds = 0,
                    Category = StandardCategories.NationalNews,
                    AssignedTransition = TransitionType.Fade,
                    IsAvailable = true,
                    CreatedAt = DateTime.UtcNow
                };

                int newId = _db.AddMedia(item);
                item.Id = newId;
                _server.RegisterApprovedMedia(newId, file);
                MediaLibrary.Insert(0, item);
            }
            ApplyMediaFilter();
            StatusNotification = $"Imported {dialog.FileNames.Length} video(s) into library.";
        }
    }

    private void DeleteMedia()
    {
        if (SelectedMedia == null) return;

        var result = MessageBox.Show(
            $"Remove '{SelectedMedia.Title}' from the media library?\n\n(Note: This removes the catalog entry only; your original video file will not be deleted.)",
            "Confirm Remove Entry",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _server.UnregisterMedia(SelectedMedia.Id);
            _db.DeleteMedia(SelectedMedia.Id);
            MediaLibrary.Remove(SelectedMedia);
            ApplyMediaFilter();
            SelectedMedia = null;
            StatusNotification = "Media entry removed from library.";
        }
    }

    public void UpdateMediaTransition(MediaItem media, TransitionType transition)
    {
        if (media == null) return;
        media.AssignedTransition = transition;
        _stateManager.AssignTransition(media.Id, transition);
        StatusNotification = $"Updated transition for '{media.Title}' to {transition}.";
    }

    private void SelectForPreview(object? param)
    {
        var media = param as MediaItem ?? SelectedMedia;
        if (media == null) return;

        PreparePreview(media);
    }

    private void PreparePreview(MediaItem media)
    {
        _stateManager.SelectPreviewMedia(media);

        // Arm the prepared handshake for this specific load (a stale pending one simply times out)
        _scenePreparedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Cue media in standby slot on broadcast canvas
        var mediaUrl = $"{_server.BaseUrl}media/{media.Id}";
        SendCanvasCommand("load_scene", new LoadSceneData
        {
            Type = "video",
            MediaUrl = mediaUrl,
            MediaId = media.Id,
            Title = media.Title,
            Transition = TransitionConfig.ToCommandName(media.AssignedTransition),
            TransitionDurationMs = TransitionConfig.GetDurationMs(media.AssignedTransition),
            AutoPlay = true
        });

        StatusNotification = $"Prepared in Preview: '{media.Title}' [Assigned: {media.AssignedTransition}]. Current broadcast untouched.";
    }

    private async void TakeLive()
    {
        if (_stateManager.ExecuteTakeLive())
        {
            // Avoid switching to a standby scene whose media is still loading (short guard)
            await WaitScenePreparedSafeAsync(1200);

            // No projector window: nothing will ever acknowledge the transition,
            // so clear the flag instead of locking out future takes.
            if (_broadcastWindow == null)
            {
                _stateManager.ResetTransition();
            }

            var trans = _stateManager.State.ActiveTransition;
            var duration = TransitionConfig.GetDurationMs(trans);

            SendCanvasCommand("take_live", new TakeLiveCommandData
            {
                Transition = TransitionConfig.ToCommandName(trans),
                TransitionDurationMs = duration
            });

            SendCanvasCommand("toggle_standby", false);
            StatusNotification = $"ON AIR: Switching to '{_stateManager.State.OnAirMedia?.Title}' via {trans} ({duration}ms).";
        }
    }

    private void ToggleStandby()
    {
        bool newStandby = !_stateManager.State.IsStandbyActive;
        _stateManager.SetStandby(newStandby);
        SendCanvasCommand("toggle_standby", newStandby);
        StatusNotification = newStandby ? "STANDBY screen active on projector." : "Standby dismissed. Live broadcast active.";
    }

    private void ToggleLiveIndicator()
    {
        bool newVisible = !_stateManager.State.IsLiveIndicatorVisible;
        _stateManager.ToggleLiveIndicator(newVisible);
        SendCanvasCommand("configure_live_badge", newVisible);
        StatusNotification = newVisible ? "LIVE indicator shown on broadcast." : "LIVE indicator hidden from broadcast.";
    }

    #endregion

    #region Rundown Playlist Actions

    private void AddToRundown()
    {
        if (SelectedMedia == null) return;
        int nextOrder = RundownItems.Count;
        _db.AddPlaylistItem(_rundownPlaylistId, SelectedMedia.Id, nextOrder);
        RundownItems.Add(new PlaylistItem
        {
            PlaylistId = _rundownPlaylistId,
            MediaItemId = SelectedMedia.Id,
            OrderIndex = nextOrder,
            MediaItem = SelectedMedia
        });
        StatusNotification = $"Added '{SelectedMedia.Title}' to news rundown.";
    }

    private void RemoveFromRundown()
    {
        if (SelectedRundownItem == null) return;
        _db.RemovePlaylistItem(SelectedRundownItem.Id);
        RundownItems.Remove(SelectedRundownItem);
        ReorderRundown();
    }

    private void MoveRundownUp()
    {
        if (SelectedRundownItem == null) return;
        int idx = RundownItems.IndexOf(SelectedRundownItem);
        if (idx > 0)
        {
            RundownItems.Move(idx, idx - 1);
            ReorderRundown();
        }
    }

    private void MoveRundownDown()
    {
        if (SelectedRundownItem == null) return;
        int idx = RundownItems.IndexOf(SelectedRundownItem);
        if (idx < RundownItems.Count - 1)
        {
            RundownItems.Move(idx, idx + 1);
            ReorderRundown();
        }
    }

    private void ReorderRundown()
    {
        for (int i = 0; i < RundownItems.Count; i++)
        {
            RundownItems[i].OrderIndex = i;
        }
        _db.UpdatePlaylistOrder(RundownItems.ToList());
    }

    private async void TakeLiveRundown()
    {
        if (SelectedRundownItem?.MediaItem == null) return;

        PreparePreview(SelectedRundownItem.MediaItem);
        await WaitScenePreparedSafeAsync(5000);
        TakeLive();
    }

    /// <summary>
    /// Waits until the canvas confirms the standby scene is loaded, or the timeout elapses.
    /// </summary>
    private async Task<bool> WaitScenePreparedSafeAsync(int timeoutMs)
    {
        var tcs = _scenePreparedTcs;
        if (tcs == null || _broadcastWindow == null) return true;

        var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (finished == tcs.Task) return true;

        // Timed out: drop the stale signal so later takes don't stack up waiting on it
        if (ReferenceEquals(_scenePreparedTcs, tcs))
        {
            _scenePreparedTcs = null;
        }
        return false;
    }

    #endregion

    #region Graphics & Headlines

    private void SaveHeadlineDraft()
    {
        int id = _db.SaveHeadlineDraft(EditingHeadline);
        EditingHeadline.Id = id;
        if (!HeadlineDrafts.Contains(EditingHeadline))
        {
            HeadlineDrafts.Insert(0, EditingHeadline);
        }
        StatusNotification = $"Saved graphic draft '{EditingHeadline.Title}'.";
    }

    private void DeleteHeadlineDraft(object? param)
    {
        var draft = param as HeadlineDraft ?? EditingHeadline;
        if (draft.Id > 0)
        {
            _db.DeleteHeadlineDraft(draft.Id);
            HeadlineDrafts.Remove(draft);
            EditingHeadline = new HeadlineDraft();
            StatusNotification = "Graphic draft deleted.";
        }
    }

    private void PreviewHeadline()
    {
        _stateManager.SelectPreviewGraphic(EditingHeadline);
        SendCanvasCommand("load_scene", new LoadSceneData
        {
            Type = "graphic",
            Title = EditingHeadline.Title,
            Graphic = EditingHeadline
        });
        StatusNotification = $"Previewing graphic slide '{EditingHeadline.Title}' on Preview monitor.";
    }

    private void TakeLiveHeadline()
    {
        _stateManager.PublishLiveGraphic(EditingHeadline);
        SendCanvasCommand("publish_headline", EditingHeadline);
        StatusNotification = $"PUBLISHED LIVE: Lower-third graphic '{EditingHeadline.Title}'.";
    }

    private void UpdateLiveHeadline()
    {
        _stateManager.PublishLiveGraphic(EditingHeadline);
        SendCanvasCommand("publish_headline", EditingHeadline);
        StatusNotification = $"UPDATED ON AIR: Lower-third graphic '{EditingHeadline.Title}'.";
    }

    private void ClearLiveHeadline()
    {
        _stateManager.ClearLiveGraphic();
        SendCanvasCommand("clear_headline");
        StatusNotification = "Cleared live lower-third graphic from broadcast.";
    }

    #endregion

    #region News Ticker

    private void AddTickerItem()
    {
        if (string.IsNullOrWhiteSpace(NewTickerItemText)) return;
        var item = new TickerItem
        {
            Text = NewTickerItemText.Trim(),
            Category = string.IsNullOrWhiteSpace(NewTickerItemCategory) ? "NEWS" : NewTickerItemCategory.Trim().ToUpper(),
            OrderIndex = TickerDraftItems.Count,
            IsActive = true
        };

        int id = _db.SaveTickerItem(item);
        item.Id = id;
        TickerDraftItems.Add(item);
        NewTickerItemText = string.Empty;
        StatusNotification = "Added headline to ticker draft.";
    }

    private void DeleteTickerItem(object? param)
    {
        if (param is TickerItem item)
        {
            _db.DeleteTickerItem(item.Id);
            TickerDraftItems.Remove(item);
            StatusNotification = "Removed item from ticker draft.";
        }
    }

    private void PublishTicker()
    {
        var activeItems = TickerDraftItems.Where(t => t.IsActive).ToList();
        _stateManager.PublishLiveTicker(DraftTickerConfig, activeItems);

        SendCanvasCommand("publish_ticker", new
        {
            config = DraftTickerConfig,
            items = activeItems
        });

        StatusNotification = $"Published {activeItems.Count} headline(s) to live scrolling ticker without interrupting broadcast.";
    }

    private void ToggleTickerPause()
    {
        DraftTickerConfig.IsPaused = !DraftTickerConfig.IsPaused;
        OnPropertyChanged(nameof(DraftTickerConfig));
        PublishTicker();
    }

    #endregion

    #region Quick Breaking News

    private void QuickBreakingPreview()
    {
        var breakingGraphic = new HeadlineDraft
        {
            Title = QuickBreakingHeadline,
            Subtitle = QuickBreakingSubtitle,
            Category = "BREAKING NEWS",
            IsBreaking = true,
            BackgroundStyle = "breaking"
        };
        EditingHeadline = breakingGraphic;
        PreviewHeadline();
    }

    private void QuickBreakingLive()
    {
        var breakingGraphic = new HeadlineDraft
        {
            Title = QuickBreakingHeadline,
            Subtitle = QuickBreakingSubtitle,
            Category = "BREAKING NEWS",
            IsBreaking = true,
            BackgroundStyle = "breaking"
        };

        // Update live graphic immediately
        _stateManager.PublishLiveGraphic(breakingGraphic);
        SendCanvasCommand("publish_headline", breakingGraphic);

        // Switch ticker into urgent breaking mode
        DraftTickerConfig.IsBreakingStyle = true;
        DraftTickerConfig.HeaderTag = "BREAKING";
        PublishTicker();

        StatusNotification = "BREAKING NEWS MODE ACTIVATED ON AIR!";
    }

    #endregion

    #region Transitions Test & Preview

    private async void TestTransition(object? param)
    {
        if (param is not TransitionType trans) return;

        TransitionPreviewStatus = $"Executing 500ms {trans} transition test...";
        StatusNotification = $"Simulating {trans} transition...";

        // Send simulated transition to broadcast or preview (test overlay only — does NOT
        // swap the active slot or interrupt the on-air broadcast)
        SendCanvasCommand("test_transition", new TakeLiveCommandData
        {
            Transition = TransitionConfig.ToCommandName(trans),
            TransitionDurationMs = TransitionConfig.GetDurationMs(trans)
        });

        await Task.Delay(550);
        TransitionPreviewStatus = $"Completed {trans} (Duration: {TransitionConfig.GetDurationMs(trans)}ms).";
    }

    #endregion

    #region Display & Broadcast Window

    private void LaunchBroadcast()
    {
        if (_broadcastWindow != null && _broadcastWindow.IsLoaded)
        {
            _broadcastWindow.ApplyDisplayPlacement(SelectedDisplay);
            _broadcastWindow.Activate();
            return;
        }

        _broadcastWindow = new BroadcastWindow(_server.BaseUrl, SelectedDisplay);

        _broadcastWindow.TimeUpdated += (s, args) =>
        {
            _dispatcher.Invoke(() =>
            {
                // Keep the authoritative playback flag in sync (Space toggles from this)
                _stateManager.State.IsPlaying = !args.Paused;

                if (SuppressPlaybackTimeUpdates) return;

                PlaybackCurrentTime = args.CurrentTime;
                PlaybackDuration = args.Duration;

                var cur = TimeSpan.FromSeconds(args.CurrentTime);
                var dur = TimeSpan.FromSeconds(args.Duration);
                PlaybackTimeText = $"{cur:mm\\:ss} / {dur:mm\\:ss}";
            });
        };

        _broadcastWindow.TransitionFinished += (s, args) =>
        {
            _dispatcher.Invoke(() =>
            {
                _stateManager.CompleteTransition();
                StatusNotification = $"Transition {args.Transition} completed successfully in 500ms.";
            });
        };

        _broadcastWindow.CanvasReady += (s, args) =>
        {
            _dispatcher.Invoke(() =>
            {
                // Clear any transition flag stuck from a broadcast that ran without this window
                _stateManager.ResetTransition();

                PublishTicker();
                SendCanvasCommand("toggle_standby", _stateManager.State.IsStandbyActive);
                SendCanvasCommand("configure_live_badge", _stateManager.State.IsLiveIndicatorVisible);
            });
        };

        _broadcastWindow.ScenePrepared += (s, args) =>
        {
            _dispatcher.Invoke(() =>
            {
                var tcs = _scenePreparedTcs;
                _scenePreparedTcs = null;
                tcs?.TrySetResult(true);
            });
        };

        _broadcastWindow.Closed += (s, e) =>
        {
            _broadcastWindow = null;
            IsBroadcastWindowOpen = false;
            StatusNotification = "Broadcast output window closed.";
        };

        _broadcastWindow.Show();
        IsBroadcastWindowOpen = true;
        StatusNotification = $"Broadcast screen launched fullscreen on: {SelectedDisplay?.Description}";
    }

    private void CloseBroadcast()
    {
        if (_broadcastWindow != null)
        {
            _broadcastWindow.Close();
            _broadcastWindow = null;
            IsBroadcastWindowOpen = false;
        }
    }

    private void SendCanvasCommand(string command, object? data = null)
    {
        _broadcastWindow?.SendCommand(command, data);
    }

    #endregion
}
