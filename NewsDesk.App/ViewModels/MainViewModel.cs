using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
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

    // Fullscreen projector output, and an optional resizable TEST RUN pop-out on the operator screen
    private BroadcastWindow? _projectorWindow;
    private BroadcastWindow? _popoutWindow;

    /// <summary>
    /// The canvas the host takes handshakes, telemetry and sound from: the projector
    /// when it is open, otherwise the pop-out (so a test run behaves exactly like air).
    /// </summary>
    private BroadcastWindow? _broadcastWindow => _projectorWindow ?? _popoutWindow;

    private bool IsDrivingCanvas(BroadcastWindow window) => ReferenceEquals(window, _broadcastWindow);

    private const string PopoutTestRunTitle = "TEST RUN — projector preview (this window is the output)";
    private const string PopoutMirrorTitle = "PROJECTOR MIRROR — muted copy of the live output";

        // Signals that the canvas has loaded media into the standby slot (safe to take live)
        private TaskCompletionSource<bool>? _scenePreparedTcs;

        // True once the canvas confirms the current preview content is ready in the
        // standby slot. Gate for enabling TAKE LIVE so it can never cut to black.
        private bool _isPreviewPrepared;

        // True when the operator cued Preview by hand since the last take. Rundown
        // automation (auto-cue / auto-advance) must never overwrite such a cue.
        private bool _manualCueSinceTake;

        // Identifies the most recent take so a stale watchdog can't clear a newer transition
        private int _takeSequence;

        // How long past the transition duration to wait for the canvas's
        // transition_complete before force-clearing the transitioning flag
        private const int TransitionWatchdogGraceMs = 1500;

        // Intro & theme: persisted config, playback flag, and an Esc override
        // that turns whatever post-intro action was chosen into standby
        private IntroConfig _introConfig = new();
        private bool _isIntroPlaying;
        private string? _introActionOverride;

        // Reserved media-server ids for the operator's intro files (library ids never reach these)
        private const int IntroVideoMediaId = 900_000_001;
        private const int IntroAudioMediaId = 900_000_002;
        private const int VoiceOverMediaId = 900_000_011;
        private const int SpeakerMediaIdBase = 900_000_020; // + box number
        private const int StoryVoiceOverMediaId = 900_000_012;

        // Camera formats (.mts, .cr3 …) are converted to playable copies before use
        private readonly MediaConverter _converter = new() { StillDecoder = WicStillDecoder.TryConvert };
        private readonly Dictionary<int, Task<bool>> _upgrades = [];

        // Bumped on every Preview cue, so a cue that waited on a conversion can tell it was superseded
        private int _cueRequest;


        // Voice-over & speaker windows (persisted in Settings)
        private VoiceOverConfig _voConfig = new();
        private readonly SpeakerBoxConfig[] _speakers = [new(), new()];

    // Actual default rundown playlist id (seeded row may not be Id 1)


    /// <summary>
    /// TAKE LIVE is only honoured when preview content exists, no transition is
    /// running, and — whenever a broadcast output is attached — the canvas has
    /// confirmed the standby scene is actually prepared (never cut to black).
    /// </summary>
    private bool CanTakeLive() =>
        !_stateManager.State.IsTransitioning
        && (_stateManager.State.PreviewMedia != null || _stateManager.State.PreviewGraphic != null)
        && (_broadcastWindow == null || _isPreviewPrepared);

    private void RefreshTakeLiveState()
    {
        OnPropertyChanged(nameof(IsTransitioning));
        CommandManager.InvalidateRequerySuggested();
    }

    // Collections
    public ObservableCollection<MediaItem> MediaLibrary { get; } = [];
    public ObservableCollection<MediaItem> FilteredMediaLibrary { get; } = [];

    public ObservableCollection<HeadlineDraft> HeadlineDrafts { get; } = [];
    public ObservableCollection<HeadlineSequenceItem> HeadlineSequence { get; } = [];
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
    public List<string> MediaKinds { get; } = ["All media", "Videos", "Pictures"];

    private string _kindFilter = "All media";
    public string KindFilter
    {
        get => _kindFilter;
        set
        {
            if (SetProperty(ref _kindFilter, value))
                ApplyMediaFilter();
        }
    }
    public List<TransitionType> AvailableTransitions { get; } = Enum.GetValues<TransitionType>().ToList();

    // Graphics studio: template treatments & slide palette options
    public List<GraphicsTemplateType> HeadlineTemplates { get; } = Enum.GetValues<GraphicsTemplateType>().ToList();
    public List<string> GraphicPalettes { get; } = ["navy", "breaking", "slate"];

    // Selected items
    private MediaItem? _selectedMedia;
    public MediaItem? SelectedMedia
    {
        get => _selectedMedia;
        set => SetProperty(ref _selectedMedia, value);
    }


    private HeadlineSequenceItem? _selectedHeadlineItem;
    public HeadlineSequenceItem? SelectedHeadlineItem
    {
        get => _selectedHeadlineItem;
        set => SetProperty(ref _selectedHeadlineItem, value);
    }

    // Headlines segment playback
    private bool _isHeadlinesPlaying;
    public bool IsHeadlinesPlaying
    {
        get => _isHeadlinesPlaying;
        private set
        {
            if (SetProperty(ref _isHeadlinesPlaying, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    private bool _headlinesAutoAdvance = true;
    /// <summary>When off, each headline holds until the operator presses NEXT.</summary>
    public bool HeadlinesAutoAdvance
    {
        get => _headlinesAutoAdvance;
        set
        {
            if (!SetProperty(ref _headlinesAutoAdvance, value) || !IsHeadlinesPlaying) return;
            if (value) StartHeadlineTimer(CurrentHeadline);
            else _headlineTimer?.Stop();
        }
    }

    private string _headlinesStatusText = "Headlines idle";
    public string HeadlinesStatusText
    {
        get => _headlinesStatusText;
        set => SetProperty(ref _headlinesStatusText, value);
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
    public bool IsPlaying => _stateManager.State.IsPlaying;
    public bool IsLiveIndicatorVisible => _stateManager.State.IsLiveIndicatorVisible;
    public bool IsTransitioning => _stateManager.State.IsTransitioning;
    public TransitionType ActiveTransition => _stateManager.State.ActiveTransition;

    public MediaItem? OnAirMedia => _stateManager.State.OnAirMedia;
    public HeadlineDraft? OnAirGraphic => _stateManager.State.OnAirGraphic;
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

    private bool _isPopoutOpen;
    public bool IsPopoutOpen
    {
        get => _isPopoutOpen;
        set => SetProperty(ref _isPopoutOpen, value);
    }

    private string _transitionPreviewStatus = "Ready";
    public string TransitionPreviewStatus
    {
        get => _transitionPreviewStatus;
        set => SetProperty(ref _transitionPreviewStatus, value);
    }

    // Intro & theme studio
    public List<KeyValuePair<string, string>> IntroPostActions { get; } =
    [
        new("opening_headline", "Opening headline (Graphics Studio draft)"),
        new("first_story", "First rundown story"),
        new("all_headlines", "All headlines screen (over the first headline's media)"),
        new("standby", "Back to standby slate")
    ];

    public string IntroPostAction
    {
        get => _introConfig.PostIntroAction;
        set
        {
            if (_introConfig.PostIntroAction == value) return;
            _introConfig.PostIntroAction = value;
            _db.SaveIntroConfig(_introConfig);
            OnPropertyChanged();
        }
    }

    public double IntroVolume
    {
        get => _introConfig.Volume;
        set
        {
            var v = Math.Clamp(value, 0, 1);
            if (Math.Abs(_introConfig.Volume - v) < 0.001) return;
            _introConfig.Volume = v;
            _db.SaveIntroConfig(_introConfig);
            SendCanvasCommand("set_intro_volume", v);
            OnPropertyChanged();
        }
    }

    public string IntroVideoDisplay => string.IsNullOrEmpty(_introConfig.VideoPath)
        ? "Built-in title sequence (no video chosen)"
        : _introConfig.VideoPath;

    public string IntroAudioDisplay => string.IsNullOrEmpty(_introConfig.AudioPath)
        ? (string.IsNullOrEmpty(_introConfig.VideoPath) ? "Built-in original news theme" : "Video's own soundtrack")
        : _introConfig.AudioPath;

    private string _introStatusText = "Intro idle";
    public string IntroStatusText
    {
        get => _introStatusText;
        set => SetProperty(ref _introStatusText, value);
    }

    // Which editor panel the studio shows (Graphics, Headlines, Ticker, Voice-over, Speakers, Intro, Transitions, Output)
    private int _editorTabIndex;
    public int EditorTabIndex
    {
        get => _editorTabIndex;
        set => SetProperty(ref _editorTabIndex, value);
    }

    // Overlay status for the "on air" dock
    public bool IsVoiceOverActive => _stateManager.State.IsVoiceOverActive;
    public bool AreSpeakersLive => _stateManager.State.AreSpeakersLive;
    public bool IsSceneCaptionVisible => _stateManager.State.IsSceneCaptionVisible;
    public bool IsTickerVisible => _stateManager.State.LiveTickerConfig.IsVisible;
    public bool IsLowerThirdLive => _stateManager.State.LiveGraphic != null;

    // Audio mixer: program (on-air video) level
    private double _programVolume = 1.0;
    public double ProgramVolume
    {
        get => _programVolume;
        set
        {
            var v = Math.Clamp(value, 0, 1);
            if (!SetProperty(ref _programVolume, v)) return;
            _db.SetSetting("Mixer_ProgramVolume", v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            SendCanvasCommand("set_volume", v);
        }
    }

    // Voice-over
    public string VoiceOverAudioDisplay => string.IsNullOrEmpty(_voConfig.AudioPath)
        ? "No track — live narrator (ducks program audio, shows the tag)"
        : Path.GetFileName(_voConfig.AudioPath);

    public string VoiceOverName
    {
        get => _voConfig.Name;
        set { _voConfig.Name = value ?? string.Empty; _db.SaveVoiceOverConfig(_voConfig); OnPropertyChanged(); }
    }

    public string VoiceOverDesignation
    {
        get => _voConfig.Designation;
        set { _voConfig.Designation = value ?? string.Empty; _db.SaveVoiceOverConfig(_voConfig); OnPropertyChanged(); }
    }

    public bool VoiceOverShowTag
    {
        get => _voConfig.ShowTag;
        set { _voConfig.ShowTag = value; _db.SaveVoiceOverConfig(_voConfig); OnPropertyChanged(); }
    }

    public bool VoiceOverLoop
    {
        get => _voConfig.Loop;
        set { _voConfig.Loop = value; _db.SaveVoiceOverConfig(_voConfig); OnPropertyChanged(); }
    }

    public double VoiceOverVolume
    {
        get => _voConfig.Volume;
        set
        {
            var v = Math.Clamp(value, 0, 1);
            if (Math.Abs(_voConfig.Volume - v) < 0.001) return;
            _voConfig.Volume = v;
            _db.SaveVoiceOverConfig(_voConfig);
            SendCanvasCommand("set_voice_over_levels", new { volume = v });
            OnPropertyChanged();
        }
    }

    /// <summary>Program audio level while a voice-over (or a speaker clip with sound) plays.</summary>
    public double VoiceOverDuckLevel
    {
        get => _voConfig.DuckLevel;
        set
        {
            var v = Math.Clamp(value, 0, 1);
            if (Math.Abs(_voConfig.DuckLevel - v) < 0.001) return;
            _voConfig.DuckLevel = v;
            _db.SaveVoiceOverConfig(_voConfig);
            SendCanvasCommand("set_voice_over_levels", new { duckLevel = v });
            OnPropertyChanged();
        }
    }

    // Speaker windows
    public List<KeyValuePair<string, string>> SpeakerLayouts { get; } =
    [
        new("side_by_side", "Side by side (two big windows)"),
        new("pip_right", "Picture-in-picture (right side)")
    ];

    public List<KeyValuePair<int, string>> CameraChoices { get; } =
    [
        new(0, "Camera 1"), new(1, "Camera 2"), new(2, "Camera 3"), new(3, "Camera 4")
    ];

    private string _speakerLayout = "side_by_side";
    public string SpeakerLayout
    {
        get => _speakerLayout;
        set
        {
            if (!SetProperty(ref _speakerLayout, value)) return;
            _db.SetSetting("Speakers_Layout", value);
            if (AreSpeakersLive) ShowSpeakers();
        }
    }

    public SpeakerBoxConfig Speaker1 => _speakers[0];
    public SpeakerBoxConfig Speaker2 => _speakers[1];
    public string Speaker1SourceDisplay => DescribeSpeakerSource(_speakers[0]);
    public string Speaker2SourceDisplay => DescribeSpeakerSource(_speakers[1]);

    private static string DescribeSpeakerSource(SpeakerBoxConfig box) => box.SourceKind switch
    {
        "camera" => $"Live camera {box.CameraIndex + 1}",
        "file" when !string.IsNullOrEmpty(box.SourcePath) => Path.GetFileName(box.SourcePath),
        _ => "No source yet"
    };

    public RelayCommand ToggleVoiceOverCommand { get; }
    public RelayCommand StartVoiceOverCommand { get; }
    public RelayCommand StopVoiceOverCommand { get; }
    public RelayCommand HoldProgramSoundCommand { get; }
    public RelayCommand SelectVoiceOverAudioCommand { get; }
    public RelayCommand ClearVoiceOverAudioCommand { get; }

    public RelayCommand ShowSpeakersCommand { get; }
    public RelayCommand HideSpeakersCommand { get; }
    public RelayCommand ToggleSpeakersCommand { get; }
    public RelayCommand SelectSpeakerFileCommand { get; }
    public RelayCommand UseSpeakerCameraCommand { get; }
    public RelayCommand ClearSpeakerCommand { get; }
    public RelayCommand SaveSpeakersCommand { get; }

    public RelayCommand ToggleLowerThirdCommand { get; }
    public RelayCommand ToggleSceneCaptionCommand { get; }
    public RelayCommand ToggleTickerVisibleCommand { get; }
    public RelayCommand SaveMediaDetailsCommand { get; }
    public RelayCommand OpenEditorCommand { get; }

    public RelayCommand PlayIntroCommand { get; }
    public RelayCommand SkipIntroCommand { get; }
    public RelayCommand SelectIntroVideoCommand { get; }
    public RelayCommand ClearIntroVideoCommand { get; }
    public RelayCommand SelectIntroAudioCommand { get; }
    public RelayCommand ClearIntroAudioCommand { get; }

    // Relay Commands
    public RelayCommand ImportVideoCommand { get; }
    public RelayCommand RenameMediaCommand { get; }
    public RelayCommand DeleteMediaCommand { get; }
    public RelayCommand SelectForPreviewCommand { get; }
    public RelayCommand AddToRundownCommand { get; }
    public RelayCommand CueStoryCommand { get; }
    public RelayCommand ReplaceWithStoryMediaCommand { get; }
    public RelayCommand VoiceOverStoryMediaCommand { get; }
    public RelayCommand Window1StoryMediaCommand { get; }
    public RelayCommand Window2StoryMediaCommand { get; }
    public RelayCommand AddStoryExtraMediaCommand { get; }
    public RelayCommand RemoveStoryExtraMediaCommand { get; }

    // The story whose media is on air: its lower third is up, its extra media are the quick options
    private HeadlineSequenceItem? _onAirStory;
    public HeadlineSequenceItem? OnAirStory
    {
        get => _onAirStory;
        private set => SetProperty(ref _onAirStory, value);
    }

    // The story cued into Preview; becomes OnAirStory when it is taken
    private HeadlineSequenceItem? _cuedStory;

    // The lower third a story put up (so a story switch never clears the operator's own graphic)
    private HeadlineDraft? _storyLowerThird;

    // The speaker currently named on the story's lower third (null = headline only)
    private StorySpeaker? _storySpeaker;

    public RelayCommand ShowStorySpeakerCommand { get; }
    public RelayCommand StoryHeadlineOnlyCommand { get; }

    private bool _storyLowerThirdAuto = true;
    /// <summary>Taking a story puts its headline and summary up as the lower third.</summary>
    public bool StoryLowerThirdAuto
    {
        get => _storyLowerThirdAuto;
        set
        {
            if (!SetProperty(ref _storyLowerThirdAuto, value)) return;
            _db.SetSetting("Stories_AutoLowerThird", value.ToString());
        }
    }
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

    public RelayCommand AddHeadlineItemCommand { get; }
    public RelayCommand RemoveHeadlineItemCommand { get; }
    public RelayCommand MoveHeadlineUpCommand { get; }
    public RelayCommand MoveHeadlineDownCommand { get; }
    public RelayCommand UseSelectedMediaForHeadlineCommand { get; }
    public RelayCommand ClearHeadlineMediaCommand { get; }
    public RelayCommand CueHeadlineItemCommand { get; }
    public RelayCommand PlayHeadlinesCommand { get; }
    public RelayCommand EditAllHeadlinesCommand { get; }
    public RelayCommand ToggleHeadlinesBoardCommand { get; }

    // "Today's headlines" board: every headline and caption on screen at once
    private bool _isHeadlinesBoardLive;
    public bool IsHeadlinesBoardLive
    {
        get => _isHeadlinesBoardLive;
        private set => SetProperty(ref _isHeadlinesBoardLive, value);
    }

    private string _headlinesBoardTitle = "TODAY'S HEADLINES";
    public string HeadlinesBoardTitle
    {
        get => _headlinesBoardTitle;
        set
        {
            if (!SetProperty(ref _headlinesBoardTitle, value)) return;
            _db.SetSetting("Headlines_BoardTitle", value ?? string.Empty);
        }
    }
    public RelayCommand NextHeadlineCommand { get; }
    public RelayCommand PreviousHeadlineCommand { get; }
    public RelayCommand StopHeadlinesCommand { get; }
    public RelayCommand ToggleHeadlinesCommand { get; }

    public RelayCommand AddTickerItemCommand { get; }
    public RelayCommand DeleteTickerItemCommand { get; }
    public RelayCommand PublishTickerCommand { get; }
    public RelayCommand ToggleTickerPauseCommand { get; }

    public RelayCommand QuickBreakingLiveCommand { get; }
    public RelayCommand QuickBreakingPreviewCommand { get; }

    public RelayCommand LaunchBroadcastCommand { get; }
    public RelayCommand CloseBroadcastCommand { get; }
    public RelayCommand TogglePopoutCommand { get; }
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
        RenameMediaCommand = new RelayCommand(RenameMedia, param => param is MediaItem media && media.IsImage && !string.IsNullOrWhiteSpace(media.Title));
        DeleteMediaCommand = new RelayCommand(DeleteMedia, () => SelectedMedia != null);
        SelectForPreviewCommand = new RelayCommand(SelectForPreview);
        AddToRundownCommand = new RelayCommand(AddHeadlineItem, () => SelectedMedia != null);
        TakeLiveRundownCommand = new RelayCommand(TakeLiveRundown, () => SelectedHeadlineItem != null && !IsHeadlinesPlaying);
        CueStoryCommand = new RelayCommand(() => { if (SelectedHeadlineItem != null) { CueStory(SelectedHeadlineItem); _manualCueSinceTake = true; } },
            () => SelectedHeadlineItem != null && !IsHeadlinesPlaying);
        ReplaceWithStoryMediaCommand = new RelayCommand(param => { if (param is MediaItem m) _ = ReplaceWithStoryMediaAsync(m); });
        VoiceOverStoryMediaCommand = new RelayCommand(param => { if (param is MediaItem m) StartVoiceOverWithMedia(m); });
        Window1StoryMediaCommand = new RelayCommand(param => { if (param is MediaItem m) ShowStoryMediaInWindow(1, m); });
        Window2StoryMediaCommand = new RelayCommand(param => { if (param is MediaItem m) ShowStoryMediaInWindow(2, m); });
        AddStoryExtraMediaCommand = new RelayCommand(AddStoryExtraMedia, () => SelectedHeadlineItem != null && SelectedMedia != null);
        ShowStorySpeakerCommand = new RelayCommand(param =>
        {
            if (param is not StorySpeaker speaker || SelectedHeadlineItem is not { } story) return;
            PublishStoryLowerThird(story, speaker);
            StatusNotification = $"LOWER THIRD: {speaker.Label} — {story.ListTitle}";
        });
        StoryHeadlineOnlyCommand = new RelayCommand(() =>
        {
            if (SelectedHeadlineItem is not { } story) return;
            PublishStoryLowerThird(story);
            StatusNotification = $"LOWER THIRD: headline only — {story.ListTitle}";
        }, () => SelectedHeadlineItem != null);
        RemoveStoryExtraMediaCommand = new RelayCommand(param =>
        {
            if (param is not MediaItem m || SelectedHeadlineItem == null) return;
            SelectedHeadlineItem.ExtraMedia.Remove(m);
        });

        TakeLiveCommand = new RelayCommand(TakeLive, () => CanTakeLive());
        PlayCommand = new RelayCommand(PlayLive);
        PauseCommand = new RelayCommand(PauseLive);
        StopCommand = new RelayCommand(StopLive);
        ReplayCommand = new RelayCommand(ReplayLive);
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

        AddHeadlineItemCommand = new RelayCommand(AddHeadlineItem);
        RemoveHeadlineItemCommand = new RelayCommand(RemoveHeadlineItem, () => SelectedHeadlineItem != null && !IsHeadlinesPlaying);
        MoveHeadlineUpCommand = new RelayCommand(() => MoveHeadline(-1), () => SelectedHeadlineItem != null && !IsHeadlinesPlaying);
        MoveHeadlineDownCommand = new RelayCommand(() => MoveHeadline(1), () => SelectedHeadlineItem != null && !IsHeadlinesPlaying);
        UseSelectedMediaForHeadlineCommand = new RelayCommand(UseSelectedMediaForHeadline, () => SelectedHeadlineItem != null && SelectedMedia != null);
        ClearHeadlineMediaCommand = new RelayCommand(ClearHeadlineMedia, () => SelectedHeadlineItem?.MediaItem != null);
        CueHeadlineItemCommand = new RelayCommand(CueHeadlineItem, () => SelectedHeadlineItem != null && !IsHeadlinesPlaying);
        PlayHeadlinesCommand = new RelayCommand(() => _ = PlayHeadlinesAsync(), () => HeadlineSequence.Count > 0 && !IsHeadlinesPlaying);
        EditAllHeadlinesCommand = new RelayCommand(EditAllHeadlines, () => !IsHeadlinesPlaying);
        ToggleHeadlinesBoardCommand = new RelayCommand(() =>
        {
            if (IsHeadlinesBoardLive) HideHeadlinesBoard();
            else ShowHeadlinesBoard();
        }, () => IsHeadlinesBoardLive || (HeadlineSequence.Count > 0 && !IsHeadlinesPlaying));
        NextHeadlineCommand = new RelayCommand(() => _ = StepHeadlinesAsync(1), () => IsHeadlinesPlaying);
        PreviousHeadlineCommand = new RelayCommand(() => _ = StepHeadlinesAsync(-1), () => IsHeadlinesPlaying && _headlineIndex > 0);
        StopHeadlinesCommand = new RelayCommand(() => StopHeadlines("Headlines stopped."), () => IsHeadlinesPlaying);
        ToggleHeadlinesCommand = new RelayCommand(() =>
        {
            if (IsHeadlinesPlaying) StopHeadlines("Headlines stopped.");
            else _ = PlayHeadlinesAsync();
        }, () => IsHeadlinesPlaying || HeadlineSequence.Count > 0);

        AddTickerItemCommand = new RelayCommand(AddTickerItem);
        DeleteTickerItemCommand = new RelayCommand(DeleteTickerItem);
        PublishTickerCommand = new RelayCommand(PublishTicker);
        ToggleTickerPauseCommand = new RelayCommand(ToggleTickerPause);

        QuickBreakingLiveCommand = new RelayCommand(QuickBreakingLive);
        QuickBreakingPreviewCommand = new RelayCommand(QuickBreakingPreview);

        LaunchBroadcastCommand = new RelayCommand(LaunchBroadcast);
        CloseBroadcastCommand = new RelayCommand(CloseBroadcast);
        TogglePopoutCommand = new RelayCommand(TogglePopout);
        RefreshDisplaysCommand = new RelayCommand(RefreshDisplays);
        TestTransitionCommand = new RelayCommand(TestTransition);

        PlayIntroCommand = new RelayCommand(PlayIntro);
        SkipIntroCommand = new RelayCommand(SkipIntro, () => _isIntroPlaying);
        SelectIntroVideoCommand = new RelayCommand(SelectIntroVideo);
        ClearIntroVideoCommand = new RelayCommand(() => SetIntroMedia(video: true, path: string.Empty));
        SelectIntroAudioCommand = new RelayCommand(SelectIntroAudio);
        ClearIntroAudioCommand = new RelayCommand(() => SetIntroMedia(video: false, path: string.Empty));

        ToggleVoiceOverCommand = new RelayCommand(() => { if (IsVoiceOverActive) StopVoiceOver(); else StartVoiceOver(); });
        StartVoiceOverCommand = new RelayCommand(StartVoiceOver);
        StopVoiceOverCommand = new RelayCommand(StopVoiceOver, () => IsVoiceOverActive);
        HoldProgramSoundCommand = new RelayCommand(HoldProgramSound);
        SelectVoiceOverAudioCommand = new RelayCommand(SelectVoiceOverAudio);
        ClearVoiceOverAudioCommand = new RelayCommand(() => SetVoiceOverAudio(string.Empty));

        ShowSpeakersCommand = new RelayCommand(ShowSpeakers);
        HideSpeakersCommand = new RelayCommand(HideSpeakers, () => AreSpeakersLive);
        ToggleSpeakersCommand = new RelayCommand(() => { if (AreSpeakersLive) HideSpeakers(); else ShowSpeakers(); });
        SelectSpeakerFileCommand = new RelayCommand(param => SelectSpeakerFile(ParseBox(param)));
        UseSpeakerCameraCommand = new RelayCommand(param => UseSpeakerCamera(ParseBox(param)));
        ClearSpeakerCommand = new RelayCommand(param => ClearSpeaker(ParseBox(param)));
        SaveSpeakersCommand = new RelayCommand(SaveSpeakers);

        ToggleLowerThirdCommand = new RelayCommand(() => { if (IsLowerThirdLive) ClearLiveHeadline(); else TakeLiveHeadline(); });
        ToggleSceneCaptionCommand = new RelayCommand(ToggleSceneCaption);
        ToggleTickerVisibleCommand = new RelayCommand(ToggleTickerVisible);
        SaveMediaDetailsCommand = new RelayCommand(SaveMediaDetails, () => SelectedMedia != null);
        OpenEditorCommand = new RelayCommand(param =>
        {
            if (int.TryParse(param?.ToString(), out var index)) EditorTabIndex = index;
        });

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


        // Load Headlines
        var drafts = _db.GetAllHeadlineDrafts();
        HeadlineDrafts.Clear();
        foreach (var d in drafts) HeadlineDrafts.Add(d);
        if (HeadlineDrafts.Count > 0) EditingHeadline = HeadlineDrafts[0];

        _headlinesBoardTitle = _db.GetSetting("Headlines_BoardTitle", "TODAY'S HEADLINES") ?? "TODAY'S HEADLINES";

        // Load the headlines segment, resolving each entry's video/picture from the library
        HeadlineSequence.Clear();

        foreach (var h in _db.GetHeadlineSequence())
        {
            var mediaId = h.MediaItemId;
            h.MediaItem = mediaId == null ? null : MediaLibrary.FirstOrDefault(m => m.Id == mediaId);
            foreach (var extraId in h.ExtraMediaIds)
            {
                if (MediaLibrary.FirstOrDefault(m => m.Id == extraId) is { } extra) h.ExtraMedia.Add(extra);
            }
            WatchStory(h);
            HeadlineSequence.Add(h);
        }
        ImportOldRundownIntoStories();
        RenumberHeadlines();
        _storyLowerThirdAuto = !bool.TryParse(_db.GetSetting("Stories_AutoLowerThird", "True"), out var autoL3) || autoL3;

        // Load Ticker
        var ticker = _db.GetAllTickerItems();
        TickerDraftItems.Clear();
        foreach (var t in ticker) TickerDraftItems.Add(t);

        // Load intro & theme settings
        _introConfig = _db.GetIntroConfig();
        RegisterIntroMedia();

        // Voice-over, speaker windows and mixer
        _voConfig = _db.GetVoiceOverConfig();
        _speakers[0] = _db.GetSpeakerBoxConfig(1);
        _speakers[1] = _db.GetSpeakerBoxConfig(2);
        _speakerLayout = _db.GetSetting("Speakers_Layout", "side_by_side") ?? "side_by_side";
        if (double.TryParse(_db.GetSetting("Mixer_ProgramVolume", "1.0"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var programVolume))
            _programVolume = Math.Clamp(programVolume, 0, 1);
        RegisterOverlayMedia();

        // Load Displays
        RefreshDisplays();

        // Library items still pointing at raw camera files get converted in the background
        _ = UpgradeCameraItemsAsync();
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

        if (KindFilter == "Videos") query = query.Where(m => !m.IsImage);
        else if (KindFilter == "Pictures") query = query.Where(m => m.IsImage);

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
        OnPropertyChanged(nameof(OnAirGraphic));
        OnPropertyChanged(nameof(PreviewMedia));
        OnPropertyChanged(nameof(LiveGraphic));
        OnPropertyChanged(nameof(PreviewGraphic));
        OnPropertyChanged(nameof(PreviewTransition));
        OnPropertyChanged(nameof(IsVoiceOverActive));
        OnPropertyChanged(nameof(AreSpeakersLive));
        OnPropertyChanged(nameof(IsSceneCaptionVisible));
        OnPropertyChanged(nameof(IsTickerVisible));
        OnPropertyChanged(nameof(IsLowerThirdLive));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Transition selected in the Preview monitor for the pending take.
    /// Persists the assignment (same semantics as the Media Library combo) without
    /// racing the selection binding in code-behind.
    /// </summary>
    public TransitionType PreviewTransition
    {
        get => _stateManager.State.PreviewMedia?.AssignedTransition ?? _stateManager.State.ActiveTransition;
        set
        {
            if (_stateManager.State.PreviewMedia == null) return;
            UpdateMediaTransition(_stateManager.State.PreviewMedia, value);
            OnPropertyChanged();
        }
    }

    #region Media & Playback Actions

    private const string AllMediaFilterLabel = "Videos & Pictures (incl. camera .mts, .cr3 …)";

    private async void ImportVideo()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Videos or Pictures to the Media Library",
            Filter = $"{AllMediaFilterLabel}|{MediaConverter.DialogPattern(video: true, stills: true)}"
                   + $"|Videos|{MediaConverter.DialogPattern(video: true, stills: false)}"
                   + $"|Pictures|{MediaConverter.DialogPattern(video: false, stills: true)}"
                   + "|All Files (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() != true) return;

        int imported = 0;
        var failures = new List<string>();
        var files = dialog.FileNames;
        for (int i = 0; i < files.Length; i++)
        {
            var file = files[i];
            var label = files.Length > 1 ? $" ({i + 1}/{files.Length})" : string.Empty;

            var playable = await ConvertForBroadcastAsync(file, label);
            if (playable == null)
            {
                failures.Add(Path.GetFileName(file));
                continue;
            }

            var item = new MediaItem
            {
                Title = Path.GetFileNameWithoutExtension(file),
                FilePath = playable,
                OriginalPath = playable == file ? string.Empty : file,
                DurationSeconds = 0,
                Category = StandardCategories.NationalNews,
                AssignedTransition = TransitionType.Fade,
                IsAvailable = true,
                CreatedAt = DateTime.UtcNow
            };

            item.Id = _db.AddMedia(item);
            _server.RegisterApprovedMedia(item.Id, playable);
            MediaLibrary.Insert(0, item);
            ApplyMediaFilter();
            imported++;
        }

        StatusNotification = failures.Count == 0
            ? $"Imported {imported} item(s) into the library. Pictures of people: add a caption name and designation below the list."
            : $"Imported {imported} item(s); could not import {string.Join(", ", failures)}. {StatusNotification}";
    }

    /// <summary>
    /// Converts every library item that still points at a raw camera file, one at a time.
    /// </summary>
    private async Task UpgradeCameraItemsAsync()
    {
        foreach (var media in MediaLibrary.Where(m => MediaConverter.NeedsConversion(m.FilePath) && File.Exists(m.FilePath)).ToList())
        {
            await UpgradeLibraryItemAsync(media);
        }
    }

    private async Task CueAfterConversionAsync(MediaItem media, int request)
    {
        if (!await UpgradeLibraryItemAsync(media)) return;

        // Only cue it if the operator hasn't cued something else while it converted
        if (request == _cueRequest) PreparePreview(media);
        else StatusNotification = $"'{media.Title}' is converted and ready — cue it again to use it.";
    }

    /// <summary>
    /// Swaps a library item from its raw camera file to a converted copy, everywhere
    /// the app holds it (library, rundown copies, the media server). One conversion
    /// per item even when startup and a cue ask at the same time.
    /// </summary>
    private Task<bool> UpgradeLibraryItemAsync(MediaItem media)
    {
        if (!MediaConverter.NeedsConversion(media.FilePath)) return Task.FromResult(true);
        if (_upgrades.TryGetValue(media.Id, out var running)) return running;

        var task = UpgradeCoreAsync(media);
        _upgrades[media.Id] = task;
        return task;

        async Task<bool> UpgradeCoreAsync(MediaItem item)
        {
            try
            {
                var original = item.FilePath;
                var playable = await ConvertForBroadcastAsync(original);
                if (playable == null) return false;

                var copies = MediaLibrary.Where(m => m.Id == item.Id)
                    .Append(item)
                    .Where(m => m != null)
                    .Distinct();
                foreach (var copy in copies)
                {
                    copy!.OriginalPath = original;
                    copy.FilePath = playable;
                    copy.IsAvailable = true;
                }

                _db.UpdateMedia(item);
                _server.RegisterApprovedMedia(item.Id, playable);
                ApplyMediaFilter();
                return true;
            }
            finally
            {
                _upgrades.Remove(item.Id);
            }
        }
    }

    /// <summary>
    /// Returns a canvas-playable path for the file, converting camera formats first
    /// (status bar shows progress). Null when conversion failed; the reason is shown.
    /// </summary>
    private async Task<string?> ConvertForBroadcastAsync(string file, string label = "")
    {
        if (!MediaConverter.NeedsConversion(file)) return file;

        var name = Path.GetFileName(file);
        var kind = MediaConverter.IsCameraVideo(file) ? "camera video" : "camera picture";
        StatusNotification = $"Converting {kind} '{name}'{label} for broadcast…";
        var progress = new Progress<string>(pct => StatusNotification = $"Converting {kind} '{name}'{label} for broadcast… {pct}");

        try
        {
            var playable = await _converter.EnsurePlayableAsync(file, progress);
            StatusNotification = $"Converted '{name}' — ready to use (original file untouched).";
            return playable;
        }
        catch (Exception ex)
        {
            StatusNotification = $"Could not convert '{name}': {ex.Message}";
            return null;
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
            foreach (var h in HeadlineSequence.Where(h => ReferenceEquals(h.MediaItem, SelectedMedia)))
                h.MediaItem = null; // the database already nulled the link
            MediaLibrary.Remove(SelectedMedia);
            ApplyMediaFilter();
            SelectedMedia = null;
            StatusNotification = "Media entry removed from library.";
        }
    }

    private void RenameMedia(object? param)
    {
        if (param is not MediaItem media || !media.IsImage || string.IsNullOrWhiteSpace(media.Title)) return;

        SelectedMedia = media;
        var dialog = new RenameMediaDialog(media.Title)
        {
            Owner = Application.Current?.MainWindow
        };

        if (dialog.ShowDialog() != true || string.Equals(media.Title, dialog.NewName, StringComparison.Ordinal)) return;

        media.Title = dialog.NewName;
        _db.UpdateMedia(media);


        ApplyMediaFilter();
        OnPropertyChanged(nameof(PreviewMedia));
        OnPropertyChanged(nameof(OnAirMedia));

        if (_stateManager.State.PreviewMedia?.Id == media.Id)
            PreparePreview(media);

        StatusNotification = $"Renamed picture to '{media.Title}'. The original file was not changed.";
    }

    /// <summary>
    /// Saves the selected item's person caption; re-cues it if it is in Preview
    /// so the prepared scene shows the new caption.
    /// </summary>
    private void SaveMediaDetails()
    {
        if (SelectedMedia == null) return;
        var selected = SelectedMedia;
        selected.CaptionName = selected.CaptionName?.Trim() ?? string.Empty;
        selected.CaptionTitle = selected.CaptionTitle?.Trim() ?? string.Empty;
        _db.UpdateMedia(selected);

        if (_stateManager.State.PreviewMedia?.Id == selected.Id)
        {
            PreparePreview(selected);
        }

        StatusNotification = string.IsNullOrEmpty(selected.CaptionName)
            ? $"Saved '{selected.Title}' (no person caption)."
            : $"Saved caption for '{selected.Title}': {selected.CaptionName} — {selected.CaptionTitle}. It shows when the item goes on air.";
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

        _cuedStory = null; // a plain source, not a story: the lower third stays as it is
        PreparePreview(media);
        _manualCueSinceTake = true;
    }

    private void PreparePreview(MediaItem media)
    {
        int request = ++_cueRequest;

        // A raw camera file (e.g. .mts imported before conversion existed) would play
        // in the dashboard but never on the projector: convert it first, then cue.
        if (MediaConverter.NeedsConversion(media.FilePath))
        {
            _ = CueAfterConversionAsync(media, request);
            return;
        }

        _stateManager.SelectPreviewMedia(media);

        // A fresh cue invalidates the previous prepared handshake; TAKE LIVE
        // stays disabled until the canvas confirms this media is loaded.
        _isPreviewPrepared = false;
        RefreshTakeLiveState();

        // Arm the prepared handshake for this specific load (a stale pending one simply times out)
        _scenePreparedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Cue media in standby slot on broadcast canvas
        SendCanvasCommand("load_scene", MediaSceneData(media));

        StatusNotification = $"Cueing preview: '{media.Title}' [Effect: {media.AssignedTransition}] — TAKE LIVE unlocks when prepared. Current broadcast untouched.";
    }

    private async void TakeLive()
    {
        if (_stateManager.State.IsTransitioning)
        {
            StatusNotification = "A transition is currently in progress. Please wait for completion.";
            return;
        }

        if (_stateManager.State.PreviewMedia == null && _stateManager.State.PreviewGraphic == null)
        {
            StatusNotification = "No content is cued in Preview. Load a story or graphic first.";
            return;
        }

        // A broadcast window is attached: never cut to a standby scene that is
        // still loading. Give the prepared handshake up to 6 seconds to confirm
        // BEFORE any on-air state is committed, so an aborted take needs no rollback.
        if (_broadcastWindow != null && !_isPreviewPrepared)
        {
            StatusNotification = "TAKE LIVE held — the standby scene is still loading…";
            var prepared = await WaitScenePreparedSafeAsync(6000);
            if (!prepared && _broadcastWindow != null && !_isPreviewPrepared)
            {
                StatusNotification = "TAKE LIVE ABORTED — the previewed media never confirmed ready. Re-cue the story and try again.";
                RefreshTakeLiveState();
                return;
            }
        }

        if (!_stateManager.ExecuteTakeLive()) return;

        // A story went on air: its lower third and media options follow it
        ApplyTakenStory();

        // Everything cued so far has now been consumed by this take
        _manualCueSinceTake = false;
        int takeId = ++_takeSequence;

        // Without a projector window nothing will acknowledge the transition,
        // so clear the flag instead of locking out future takes.
        if (_broadcastWindow == null)
        {
            _stateManager.ResetTransition();
        }

        // The standby slot is consumed by this take: re-cue before the next Take Live.
        _isPreviewPrepared = false;
        RefreshTakeLiveState();

        var trans = _stateManager.State.ActiveTransition;
        var duration = TransitionConfig.GetDurationMs(trans);

        SendCanvasCommand("take_live", new TakeLiveCommandData
        {
            Transition = TransitionConfig.ToCommandName(trans),
            TransitionDurationMs = duration
        });

        SendCanvasCommand("toggle_standby", false);
        var onAirTitle = _stateManager.State.OnAirMedia?.Title ?? _stateManager.State.OnAirGraphic?.Title;
        StatusNotification = $"ON AIR: Switching to '{onAirTitle}' via {trans} ({duration}ms).";

        if (_broadcastWindow != null)
        {
            await ClearStuckTransitionAsync(takeId, duration + TransitionWatchdogGraceMs);
        }
    }

    /// <summary>
    /// Watchdog: if the canvas never reports transition_complete (WebView reload,
    /// renderer crash, script error), clear the flag so TAKE LIVE isn't locked out.
    /// </summary>
    private async Task ClearStuckTransitionAsync(int takeId, int timeoutMs)
    {
        await Task.Delay(timeoutMs);
        if (takeId != _takeSequence || !_stateManager.State.IsTransitioning) return;

        _stateManager.ResetTransition();
        RefreshTakeLiveState();
        StatusNotification = "WARNING: the projector never confirmed the transition. TAKE LIVE re-enabled — check the broadcast output.";
    }

    private void ToggleStandby()
    {
        bool newStandby = !_stateManager.State.IsStandbyActive;

        // The next headline take would dismiss standby again: end the segment first
        if (newStandby && IsHeadlinesPlaying) StopHeadlines("Headlines stopped — standby.");

        _stateManager.SetStandby(newStandby);
        SendCanvasCommand("toggle_standby", newStandby);
        StatusNotification = newStandby ? "STANDBY screen active on projector." : "Standby dismissed. Live broadcast active.";
    }

    /// <summary>
    /// Operator keyboard shortcuts, shared by the control room and the broadcast
    /// window so they work whichever window has focus. Returns true if handled.
    /// </summary>
    public bool HandleShortcutKey(Key key)
    {
        switch (key)
        {
            case Key.F5:
                TakeLiveCommand.Execute(null);
                return true;
            case Key.F6:
                ToggleStandbyCommand.Execute(null);
                return true;
            case Key.F7:
                QuickBreakingLiveCommand.Execute(null);
                return true;
            case Key.F8:
                // Emergency filler: the theme intro straight to air
                PlayIntroCommand.Execute(null);
                return true;
            case Key.F9:
                // The all-headlines page on / off
                if (ToggleHeadlinesBoardCommand.CanExecute(null)) ToggleHeadlinesBoardCommand.Execute(null);
                else StatusNotification = "All headlines: tick HL on at least one story (and stop the headlines segment) first.";
                return true;
            case Key.Space:
                if (_stateManager.State.IsPlaying)
                    PauseCommand.Execute(null);
                else
                    PlayCommand.Execute(null);
                return true;
            case Key.Escape:
                EmergencyStandby();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Emergency standby: always cuts TO standby, never away from it (Esc key).
    /// </summary>
    public void EmergencyStandby()
    {
        // During the intro, Esc cuts the intro short and lands on standby
        if (_isIntroPlaying)
        {
            _introActionOverride = "standby";
            SendCanvasCommand("skip_intro");
            StatusNotification = "EMERGENCY STANDBY: intro cut short.";
            return;
        }

        if (!_stateManager.State.IsStandbyActive)
        {
            ToggleStandby();
        }
        else
        {
            StatusNotification = "EMERGENCY STANDBY: broadcast already on standby slate.";
        }
    }

    private void ToggleLiveIndicator()
    {
        bool newVisible = !_stateManager.State.IsLiveIndicatorVisible;
        _stateManager.ToggleLiveIndicator(newVisible);
        SendCanvasCommand("configure_live_badge", newVisible);
        StatusNotification = newVisible ? "LIVE indicator shown on broadcast." : "LIVE indicator hidden from broadcast.";
    }

    #endregion

    #region Rundown Stories

    // The rundown is the list of stories (HeadlineSequence). Each story is written once:
    // its headline + summary are the lower third when it airs, and the headlines
    // segment when it is ticked IN HEADLINES.

    private int HeadlineCount => HeadlineSequence.Count(h => h.InHeadlines);

    private int HeadlinePosition(HeadlineSequenceItem item) =>
        HeadlineSequence.Where(h => h.InHeadlines).ToList().IndexOf(item) + 1;

    /// <summary>Saves a story whenever its text, flags or extra media change.</summary>
    private void WatchStory(HeadlineSequenceItem story)
    {
        story.PropertyChanged += OnHeadlineItemChanged;
        story.ExtraMedia.CollectionChanged += (s, e) =>
        {
            if (story.Id > 0) _db.SaveHeadlineSequenceItem(story);
        };
    }

    /// <summary>
    /// One-time move of the old media-only rundown into stories, so nothing the
    /// operator built before stories existed is lost. Rundown media already used
    /// by a story is not duplicated.
    /// </summary>
    private void ImportOldRundownIntoStories()
    {
        if (_db.GetSetting("Stories_RundownImported") == "1") return;

        // Keep a full copy of the operator's work from before the merge, just in case
        try { _db.CreateBackup("before-stories"); }
        catch (Exception ex) { StatusNotification = $"Could not save a backup before updating the rundown: {ex.Message}"; return; }


        int imported = 0;
        foreach (var old in _db.GetDefaultPlaylist().Items)
        {
            var media = MediaLibrary.FirstOrDefault(m => m.Id == old.MediaItemId);
            if (media == null || HeadlineSequence.Any(h => h.MediaItem?.Id == media.Id)) continue;

            var story = new HeadlineSequenceItem
            {
                OrderIndex = HeadlineSequence.Count,
                MediaItem = media,
                InHeadlines = false // no headline written yet
            };
            _db.SaveHeadlineSequenceItem(story);
            WatchStory(story);
            HeadlineSequence.Add(story);
            imported++;
        }

        _db.SetSetting("Stories_RundownImported", "1");
        if (imported > 0)
            StatusNotification = $"Your rundown is now a story list: {imported} item(s) added. Write each story's headline and summary once — it becomes the lower third and the headline.";
    }

    /// <summary>Cues a story: its main video/picture, or a full-screen headline slide when it has none.</summary>
    private void CueStory(HeadlineSequenceItem story)
    {
        if (story.MediaItem != null) PreparePreview(story.MediaItem);
        else PreviewHeadlineWith(BuildHeadlineDraft(story, fullSlide: true));
        _cuedStory = story;
    }

    /// <summary>Called by TAKE: the cued story becomes the on-air story and brings its lower third.</summary>
    private void ApplyTakenStory()
    {
        var story = _cuedStory;
        _cuedStory = null;
        if (story == null) return;

        if (OnAirStory != null && !IsHeadlinesPlaying) OnAirStory.IsOnAir = false;
        OnAirStory = story;
        story.IsOnAir = true;
        if (!IsHeadlinesPlaying) SelectedHeadlineItem = story;

        // A slide already shows the headline full-screen; media gets the lower third
        if (StoryLowerThirdAuto && story.MediaItem != null && !string.IsNullOrWhiteSpace(story.Title))
            PublishStoryLowerThird(story);
        else
            ClearStoryLowerThird();
    }

    /// <summary>
    /// The story's lower third: headline + summary, location and breaking style,
    /// plus the speaker currently chosen for it ("name • title"), if any.
    /// </summary>
    private void PublishStoryLowerThird(HeadlineSequenceItem story, StorySpeaker? speaker = null)
    {
        _storySpeaker = speaker;
        var lowerThird = new HeadlineDraft
        {
            Title = story.Title,
            Subtitle = story.Subtitle,
            Category = string.IsNullOrWhiteSpace(story.Category) ? "NEWS" : story.Category,
            Location = story.Location,
            ReporterName = speaker?.Name ?? string.Empty,
            ReporterTitle = speaker?.Title ?? string.Empty,
            IsBreaking = story.IsBreaking,
            TemplateType = speaker != null ? GraphicsTemplateType.IntervieweeLowerThird : GraphicsTemplateType.MainHeadline,
            BackgroundStyle = story.IsBreaking ? "breaking" : "navy"
        };
        _storyLowerThird = lowerThird;
        _stateManager.PublishLiveGraphic(lowerThird);
        SendCanvasCommand("publish_headline", lowerThird);
    }

    private void ClearStoryLowerThird()
    {
        if (_storyLowerThird != null && ReferenceEquals(_stateManager.State.LiveGraphic, _storyLowerThird))
        {
            _stateManager.ClearLiveGraphic();
            SendCanvasCommand("clear_headline");
        }
        _storyLowerThird = null;
        _storySpeaker = null;
    }

    /// <summary>The story after the one on air, or null at the end of the rundown.</summary>
    private HeadlineSequenceItem? FindNextStory()
    {
        int index = OnAirStory == null ? -1 : HeadlineSequence.IndexOf(OnAirStory);
        if (index < 0)
        {
            // On-air media that came from a story taken some other way
            var onAir = _stateManager.State.OnAirMedia;
            index = onAir == null ? -1 : HeadlineSequence.ToList().FindIndex(h => h.MediaItem?.Id == onAir.Id);
        }
        return index >= 0 && index + 1 < HeadlineSequence.Count ? HeadlineSequence[index + 1] : null;
    }

    /// <summary>
    /// When the on-air video ends, advances to the next story and takes it live
    /// (with its lower third) using its own transition.
    /// </summary>
    private async Task AutoAdvanceRundownAsync()
    {
        var current = _stateManager.State.OnAirMedia;
        if (current == null || IsHeadlinesPlaying) return;

        // The operator has their own cue lined up: leave it alone and let them take it
        if (_manualCueSinceTake)
        {
            StatusNotification = "On-air media ended. Your cued Preview was left untouched — TAKE LIVE when ready.";
            return;
        }

        var next = FindNextStory();
        if (next == null)
        {
            StatusNotification = "On-air media ended. No further story in the rundown — standby is available.";
            return;
        }

        StatusNotification = $"MEDIA ENDED: moving on to the next story '{next.ListTitle}'.";
        CueStory(next);
        await WaitScenePreparedSafeAsync(5000);

        // The operator cued something else while we waited: don't take their cue live for them
        if (_manualCueSinceTake)
        {
            StatusNotification = "Auto-advance cancelled — a manual cue was made. TAKE LIVE when ready.";
            return;
        }
        TakeLive();
    }

    /// <summary>TAKE LIVE NOW for the selected story.</summary>
    private async void TakeLiveRundown()
    {
        var story = SelectedHeadlineItem;
        if (story == null) return;

        CueStory(story);
        _manualCueSinceTake = true;
        await WaitScenePreparedSafeAsync(5000);
        TakeLive();
    }

    /// <summary>
    /// Swaps the on-air shot for another video/picture of the same story. The
    /// story stays on air, so its lower third stays up.
    /// </summary>
    private async Task ReplaceWithStoryMediaAsync(MediaItem media)
    {
        _cuedStory = null;
        PreparePreview(media);
        _manualCueSinceTake = true;
        await WaitScenePreparedSafeAsync(5000);
        TakeLive();
        StatusNotification = $"Shot replaced with '{media.Title}' — the story's lower third stays up.";
    }

    /// <summary>Plays a story video's sound (or an audio file) under whatever is on air.</summary>
    private void StartVoiceOverWithMedia(MediaItem media)
    {
        if (media.IsImage)
        {
            StatusNotification = $"'{media.Title}' is a picture — it has no sound for a voice-over. Use WIN 1 / WIN 2 or REPLACE.";
            return;
        }
        if (_broadcastWindow == null)
        {
            StatusNotification = "Open the broadcast output (projector) before starting a voice-over.";
            return;
        }
        if (_stateManager.State.IsStandbyActive)
        {
            StatusNotification = "Voice-over held — the standby slate is on air. Take a story live first.";
            return;
        }

        _server.UnregisterMedia(StoryVoiceOverMediaId);
        _server.RegisterApprovedMedia(StoryVoiceOverMediaId, media.FilePath);
        SendCanvasCommand("set_volume", ProgramVolume);
        SendCanvasCommand("start_voice_over", new VoiceOverCommandData
        {
            AudioUrl = $"{_server.BaseUrl}media/{StoryVoiceOverMediaId}?v={File.GetLastWriteTimeUtc(media.FilePath).Ticks}",
            Name = _voConfig.Name,
            Designation = _voConfig.Designation,
            Volume = _voConfig.Volume,
            DuckLevel = _voConfig.DuckLevel,
            ShowTag = _voConfig.ShowTag && !string.IsNullOrWhiteSpace(_voConfig.Name),
            Loop = false
        });
        _stateManager.SetVoiceOverActive(true);
        StatusNotification = $"VOICE-OVER: sound of '{media.Title}' under the program (ducked to {_voConfig.DuckLevel:P0}). STOP VO ends it.";
    }

    /// <summary>Shows a story's video/picture in speaker window 1 or 2 over the program.</summary>
    private void ShowStoryMediaInWindow(int box, MediaItem media)
    {
        var config = _speakers[box - 1];
        config.SourceKind = "file";
        config.SourcePath = media.FilePath;
        config.IsEnabled = true;
        _db.SaveSpeakerBoxConfig(box, config);
        RegisterOverlayMedia();
        OnPropertyChanged(box == 1 ? nameof(Speaker1SourceDisplay) : nameof(Speaker2SourceDisplay));
        ShowSpeakers();
    }

    private void AddStoryExtraMedia()
    {
        var story = SelectedHeadlineItem;
        var media = SelectedMedia;
        if (story == null || media == null) return;

        if (ReferenceEquals(story.MediaItem, media) || story.ExtraMedia.Contains(media))
        {
            StatusNotification = $"'{media.Title}' is already part of this story.";
            return;
        }
        story.ExtraMedia.Add(media);
        StatusNotification = $"Added '{media.Title}' to the story's extra media (replace, voice-over or window while it is on air).";
    }

    /// <summary>
    /// Waits until the canvas confirms the standby scene is loaded, or the timeout elapses.
    /// </summary>
    private async Task<bool> WaitScenePreparedSafeAsync(int timeoutMs)
    {
        var tcs = _scenePreparedTcs;
        if (tcs == null || _broadcastWindow == null) return true;

        var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (finished == tcs.Task) return await tcs.Task;

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
        _cuedStory = null;
        PreviewHeadlineWith(EditingHeadline);
        _manualCueSinceTake = true;
    }

    /// <summary>
    /// Puts any graphic draft into Preview state and cues it on the canvas
    /// without touching the live broadcast or live lower third.
    /// </summary>
    private void PreviewHeadlineWith(HeadlineDraft draft)
    {
        ++_cueRequest; // supersedes any cue still waiting on a conversion
        _stateManager.SelectPreviewGraphic(draft);

        // Prepared handshake also applies to graphic scenes (canvas confirms instantly)
        _isPreviewPrepared = false;
        RefreshTakeLiveState();
        _scenePreparedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        SendCanvasCommand("load_scene", new LoadSceneData
        {
            Type = "graphic",
            Title = draft.Title,
            Graphic = draft
        });
        StatusNotification = $"Previewing graphic slide '{draft.Title}' — TAKE LIVE unlocks when prepared.";
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

    #region Headlines Segment

    // Auto-advance timer for the headline currently on air
    private DispatcherTimer? _headlineTimer;
    private int _headlineIndex = -1;

    // Bumped on every step and stop so an older step still awaiting the canvas bails out
    private int _headlineRun;

    // The lower third the segment put on air (so stopping never clears an operator's own graphic)
    private HeadlineDraft? _headlineBanner;

    // True while the canvas shows a headline as text-left / media-right
    private bool _isHeadlineSideOn;

    private HeadlineSequenceItem? CurrentHeadline =>
        _headlineIndex >= 0 && _headlineIndex < HeadlineSequence.Count ? HeadlineSequence[_headlineIndex] : null;

    private void AddHeadlineItem()
    {
        var item = new HeadlineSequenceItem
        {
            Title = SelectedMedia?.Title.ToUpperInvariant() ?? "NEW HEADLINE",
            OrderIndex = HeadlineSequence.Count,
            MediaItem = SelectedMedia
        };
        _db.SaveHeadlineSequenceItem(item);
        WatchStory(item);
        HeadlineSequence.Add(item);
        SelectedHeadlineItem = item;

        StatusNotification = SelectedMedia == null
            ? "Added a headline slide. Type the headline, or pick a video/picture in SOURCES and press USE SELECTED SOURCE."
            : $"Added headline over '{SelectedMedia.Title}'. Type the headline text — it saves automatically.";
    }

    private void RemoveHeadlineItem()
    {
        var item = SelectedHeadlineItem;
        if (item == null) return;

        item.PropertyChanged -= OnHeadlineItemChanged;
        _db.DeleteHeadlineSequenceItem(item.Id);
        int index = HeadlineSequence.IndexOf(item);
        HeadlineSequence.Remove(item);
        RenumberHeadlines();
        _db.UpdateHeadlineSequenceOrder(HeadlineSequence.ToList());

        SelectedHeadlineItem = HeadlineSequence.Count == 0 ? null : HeadlineSequence[Math.Min(index, HeadlineSequence.Count - 1)];
        StatusNotification = $"Removed headline '{item.Title}'.";
    }

    private void MoveHeadline(int delta)
    {
        var item = SelectedHeadlineItem;
        if (item == null) return;

        int index = HeadlineSequence.IndexOf(item);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= HeadlineSequence.Count) return;

        HeadlineSequence.Move(index, target);
        RenumberHeadlines();
        _db.UpdateHeadlineSequenceOrder(HeadlineSequence.ToList());
        SelectedHeadlineItem = item;
    }

    private void RenumberHeadlines()
    {
        for (int i = 0; i < HeadlineSequence.Count; i++) HeadlineSequence[i].OrderIndex = i;
    }

    // Edits made in the editor save straight away so nothing is lost on close
    private void OnHeadlineItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not HeadlineSequenceItem { Id: > 0 } item) return;
        if (e.PropertyName is nameof(HeadlineSequenceItem.Title)
            or nameof(HeadlineSequenceItem.Subtitle)
            or nameof(HeadlineSequenceItem.Category)
            or nameof(HeadlineSequenceItem.DurationSeconds)
            or nameof(HeadlineSequenceItem.MediaItem)
            or nameof(HeadlineSequenceItem.InHeadlines)
            or nameof(HeadlineSequenceItem.Location)
            or nameof(HeadlineSequenceItem.IsBreaking)
            or nameof(HeadlineSequenceItem.SpeakersText))
        {
            _db.SaveHeadlineSequenceItem(item);
        }

        // Write once, update on air: editing the story that is up refreshes its lower third
        if (ReferenceEquals(item, OnAirStory) && _storyLowerThird != null
            && e.PropertyName is nameof(HeadlineSequenceItem.Title) or nameof(HeadlineSequenceItem.Subtitle)
                or nameof(HeadlineSequenceItem.Category) or nameof(HeadlineSequenceItem.Location)
                or nameof(HeadlineSequenceItem.IsBreaking) or nameof(HeadlineSequenceItem.SpeakersText)
            && ReferenceEquals(_stateManager.State.LiveGraphic, _storyLowerThird))
        {
            // Keep the speaker on screen if they are still listed (their title may have been corrected)
            var speaker = _storySpeaker == null ? null
                : item.Speakers.FirstOrDefault(s => s.Name == _storySpeaker.Name);
            PublishStoryLowerThird(item, speaker);
        }

    }

    private void UseSelectedMediaForHeadline()
    {
        if (SelectedHeadlineItem == null || SelectedMedia == null) return;
        SelectedHeadlineItem.MediaItem = SelectedMedia;
        StatusNotification = $"Headline {SelectedHeadlineItem.Number} will play over '{SelectedMedia.Title}'.";
    }

    private void ClearHeadlineMedia()
    {
        if (SelectedHeadlineItem == null) return;
        SelectedHeadlineItem.MediaItem = null;
        StatusNotification = $"Headline {SelectedHeadlineItem.Number} will show as a full-screen headline slide.";
    }

    /// <summary>
    /// Cues one headline's video or picture (or its slide) into Preview so the
    /// operator can check it on the Preview monitor before the segment runs.
    /// </summary>
    private void CueHeadlineItem()
    {
        if (SelectedHeadlineItem == null) return;
        CueHeadline(SelectedHeadlineItem);
        _manualCueSinceTake = true;
    }

    private void CueHeadline(HeadlineSequenceItem item)
    {
        _cuedStory = null; // the headlines segment puts up its own headline text
        if (item.MediaItem != null) PreparePreview(item.MediaItem);
        else PreviewHeadlineWith(BuildHeadlineDraft(item, fullSlide: true));
    }

    private static HeadlineDraft BuildHeadlineDraft(HeadlineSequenceItem item, bool fullSlide) => new()
    {
        Title = item.Title,
        Subtitle = item.Subtitle,
        Category = string.IsNullOrWhiteSpace(item.Category) ? "HEADLINES" : item.Category,
        TemplateType = fullSlide ? GraphicsTemplateType.FullScreenSlide : GraphicsTemplateType.TopStory,
        BackgroundStyle = "navy"
    };

    private async Task PlayHeadlinesAsync()
    {
        if (!HeadlineSequence.Any(h => h.InHeadlines) || IsHeadlinesPlaying) return;

        if (IsHeadlinesBoardLive) HideHeadlinesBoard(); // the segment uses the same side panel
        IsHeadlinesPlaying = true;
        _manualCueSinceTake = false;
        await GoToHeadlineAsync(0, 1);
    }

    private async Task StepHeadlinesAsync(int delta)
    {
        if (!IsHeadlinesPlaying) return;
        int target = Math.Max(0, _headlineIndex + delta);
        await GoToHeadlineAsync(target, delta < 0 ? -1 : 1);
    }

    /// <summary>
    /// Puts the headline at <paramref name="index"/> on air. An entry that cannot
    /// play (missing file, media never ready) is skipped in the direction of travel;
    /// running past the end finishes the segment.
    /// </summary>
    private async Task GoToHeadlineAsync(int index, int direction)
    {
        int run = ++_headlineRun;
        _headlineTimer?.Stop();

        while (index >= 0 && index < HeadlineSequence.Count)
        {
            var item = HeadlineSequence[index];
            if (!item.InHeadlines)
            {
                index += direction; // a rundown story that isn't part of the headlines
                continue;
            }
            bool landed = await TryTakeHeadlineAsync(item, index, run);
            if (run != _headlineRun) return; // stopped or stepped while we waited

            if (landed)
            {
                if (CurrentHeadline != null) CurrentHeadline.IsOnAir = false;
                _headlineIndex = index;
                item.IsOnAir = true;
                SelectedHeadlineItem = item;
                CommandManager.InvalidateRequerySuggested();
                StartHeadlineTimer(item);
                return;
            }
            index += direction;
        }

        StopHeadlines(direction > 0
            ? "Headlines complete — the last story is still on air."
            : "Headlines stopped.");
    }

    private async Task<bool> TryTakeHeadlineAsync(HeadlineSequenceItem item, int index, int run)
    {
        HeadlinesStatusText = $"Cueing {HeadlinePosition(item)}/{HeadlineCount}: {item.Title}";
        CueHeadline(item);

        bool prepared = await WaitScenePreparedSafeAsync(6000);
        if (run != _headlineRun) return false;
        if (!prepared && _broadcastWindow != null && !_isPreviewPrepared)
        {
            StatusNotification = $"HEADLINES: '{item.Title}' never became ready on the projector — skipped.";
            return false;
        }

        // Let the previous headline's transition finish before the next take
        for (int i = 0; i < 40 && _stateManager.State.IsTransitioning; i++)
        {
            await Task.Delay(50);
            if (run != _headlineRun) return false;
        }

        var state = _stateManager.State;
        object? cued = (object?)state.PreviewMedia ?? state.PreviewGraphic;
        TakeLive();

        bool landed = cued != null && ReferenceEquals((object?)state.OnAirMedia ?? state.OnAirGraphic, cued);
        if (!landed)
        {
            StatusNotification = $"HEADLINES: could not take '{item.Title}' live — skipped.";
            return false;
        }

        // A picture or video goes in a frame on the right with the headline text beside it;
        // a slide already shows the headline full-screen
        if (item.MediaItem != null) PublishHeadlineBanner(item);
        else ClearHeadlineBanner();

        HeadlinesStatusText = $"ON AIR {HeadlinePosition(item)}/{HeadlineCount}: {item.Title}";
        return true;
    }

    private void PublishHeadlineBanner(HeadlineSequenceItem item)
    {
        _isHeadlineSideOn = true;
        SendCanvasCommand("show_headline_side", new
        {
            number = HeadlinePosition(item),
            total = HeadlineCount,
            tag = string.IsNullOrWhiteSpace(item.Category) ? "HEADLINES" : item.Category,
            title = item.Title,
            caption = item.Subtitle
        });
    }

    private void ClearHeadlineBanner()
    {
        if (_isHeadlineSideOn)
        {
            _isHeadlineSideOn = false;
            SendCanvasCommand("hide_headline_side");
        }
        if (_headlineBanner != null && ReferenceEquals(_stateManager.State.LiveGraphic, _headlineBanner))
        {
            _stateManager.ClearLiveGraphic();
            SendCanvasCommand("clear_headline");
        }
        _headlineBanner = null;
    }

    private void StartHeadlineTimer(HeadlineSequenceItem? item)
    {
        _headlineTimer?.Stop();
        if (item == null || !HeadlinesAutoAdvance) return;

        int seconds = item.DurationSeconds;
        if (seconds <= 0)
        {
            // 0 on a video means "play to the end": MediaEnded advances instead
            if (item.MediaItem is { IsImage: false }) return;
            seconds = HeadlineSequenceItem.DefaultDurationSeconds;
        }

        if (_headlineTimer == null)
        {
            _headlineTimer = new DispatcherTimer();
            _headlineTimer.Tick += (s, e) =>
            {
                _headlineTimer.Stop();
                _ = StepHeadlinesAsync(1);
            };
        }
        _headlineTimer.Interval = TimeSpan.FromSeconds(seconds);
        _headlineTimer.Start();
    }

    /// <summary>A headline video that finishes before its time is up moves straight on.</summary>
    private void OnHeadlineMediaEnded()
    {
        var current = CurrentHeadline;
        if (current?.MediaItem == null || !HeadlinesAutoAdvance) return;
        if (!ReferenceEquals(_stateManager.State.OnAirMedia, current.MediaItem)) return;
        _ = StepHeadlinesAsync(1);
    }

    /// <summary>
    /// Edit the whole segment as text (one "HEADLINE | caption" per line). Line N
    /// updates headline N in place, so its video or picture and seconds are kept.
    /// </summary>
    private void EditAllHeadlines()
    {
        var dialog = new BulkHeadlinesDialog(HeadlineListText.Format(HeadlineSequence))
        {
            Owner = Application.Current?.MainWindow
        };
        if (dialog.ShowDialog() != true) return;

        var lines = dialog.Headlines;
        int updated = 0, added = 0, removed = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            var (title, caption) = lines[i];
            if (i < HeadlineSequence.Count)
            {
                var item = HeadlineSequence[i];
                if (item.Title == title && item.Subtitle == caption) continue;
                item.Title = title;      // each change saves through OnHeadlineItemChanged
                item.Subtitle = caption;
                updated++;
            }
            else
            {
                var item = new HeadlineSequenceItem { Title = title, Subtitle = caption, OrderIndex = i };
                _db.SaveHeadlineSequenceItem(item);
                WatchStory(item);
                HeadlineSequence.Add(item);
                added++;
            }
        }

        while (HeadlineSequence.Count > lines.Count)
        {
            var extra = HeadlineSequence[^1];
            extra.PropertyChanged -= OnHeadlineItemChanged;
            _db.DeleteHeadlineSequenceItem(extra.Id);
            HeadlineSequence.RemoveAt(HeadlineSequence.Count - 1);
            removed++;
        }

        RenumberHeadlines();
        _db.UpdateHeadlineSequenceOrder(HeadlineSequence.ToList());
        if (SelectedHeadlineItem != null && !HeadlineSequence.Contains(SelectedHeadlineItem))
            SelectedHeadlineItem = HeadlineSequence.LastOrDefault();
        CommandManager.InvalidateRequerySuggested();

        if (IsHeadlinesBoardLive) ShowHeadlinesBoard(); // board on air follows the edit
        StatusNotification = $"Headlines saved: {updated} changed, {added} added, {removed} removed — {HeadlineSequence.Count} in total.";
    }

    private void ShowHeadlinesBoard()
    {
        if (HeadlineCount == 0)
        {
            StatusNotification = "No headlines yet — use TYPE ALL… to enter them, or tick IN HEADLINES on a story.";

            return;
        }
        if (_broadcastWindow == null)
        {
            StatusNotification = "Open the broadcast output (projector) before showing the headlines.";
            return;
        }

        SendCanvasCommand("show_headlines_board", new
        {
            title = string.IsNullOrWhiteSpace(HeadlinesBoardTitle) ? "TODAY'S HEADLINES" : HeadlinesBoardTitle.Trim(),
            items = HeadlineSequence.Where(h => h.InHeadlines).Select(h => new { title = h.Title, caption = h.Subtitle }).ToList()
        });
        IsHeadlinesBoardLive = true;
        CommandManager.InvalidateRequerySuggested();
        StatusNotification = $"ALL HEADLINES ON AIR: {HeadlineCount} headline(s) on screen together.";
    }

    private void HideHeadlinesBoard()
    {
        SendCanvasCommand("hide_headlines_board");
        IsHeadlinesBoardLive = false;
        CommandManager.InvalidateRequerySuggested();
        StatusNotification = "Headlines board cleared from air.";
    }

    private void StopHeadlines(string message)
    {

        _headlineRun++;
        _headlineTimer?.Stop();
        foreach (var h in HeadlineSequence) h.IsOnAir = false;
        ClearHeadlineBanner();
        _headlineIndex = -1;
        IsHeadlinesPlaying = false;
        HeadlinesStatusText = message;
        StatusNotification = message;
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

    private void ToggleTickerVisible()
    {
        DraftTickerConfig.IsVisible = !_stateManager.State.LiveTickerConfig.IsVisible;
        PublishTicker();
        StatusNotification = DraftTickerConfig.IsVisible ? "Ticker shown on air." : "Ticker hidden from air.";
    }

    private void ToggleSceneCaption()
    {
        bool visible = !_stateManager.State.IsSceneCaptionVisible;
        _stateManager.SetSceneCaptionVisible(visible);
        SendCanvasCommand("set_scene_captions", visible);
        StatusNotification = visible ? "Person name captions shown on pictures and videos." : "Person name captions hidden.";
    }

    private void ToggleTickerPause()
    {
        DraftTickerConfig.IsPaused = !DraftTickerConfig.IsPaused;
        OnPropertyChanged(nameof(DraftTickerConfig));
        PublishTicker();
    }

    private void SetLocalPlaybackState(bool isPlaying, double? position = null)
    {
        _stateManager.State.IsPlaying = isPlaying;
        OnPropertyChanged(nameof(IsPlaying));
        if (position.HasValue)
            PlaybackCurrentTime = Math.Max(0, position.Value);
    }

    private void PlayLive()
    {
        SendCanvasCommand("play");
        SetLocalPlaybackState(true);
    }

    private void PauseLive()
    {
        SendCanvasCommand("pause");
        SetLocalPlaybackState(false);
    }

    private void StopLive()
    {
        SendCanvasCommand("stop");
        SetLocalPlaybackState(false, 0);
    }

    private void ReplayLive()
    {
        SendCanvasCommand("replay");
        SetLocalPlaybackState(true, 0);
    }

    #endregion

    #region Quick Breaking News

    private void QuickBreakingPreview()
    {
        try
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
        catch (Exception ex)
        {
            StatusNotification = $"BREAKING NEWS preview failed: {ex.Message}";
        }
    }

    private void QuickBreakingLive()
    {
        try
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
        catch (Exception ex)
        {
            StatusNotification = $"BREAKING NEWS activation failed: {ex.Message}";
        }
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

    #region Intro & Theme

    private void PlayIntro()
    {
        if (_broadcastWindow == null)
        {
            IntroStatusText = "No broadcast output";
            StatusNotification = "Open the broadcast output (projector) before playing the intro.";
            return;
        }

        if (_stateManager.State.IsTransitioning)
        {
            StatusNotification = "A transition is in progress — try the intro again in a moment.";
            return;
        }

        RegisterIntroMedia();
        bool hasVideo = !string.IsNullOrEmpty(_introConfig.VideoPath) && File.Exists(_introConfig.VideoPath);
        bool hasAudio = !string.IsNullOrEmpty(_introConfig.AudioPath) && File.Exists(_introConfig.AudioPath);

        // Pre-cue the opening scene now so it is prepared by the time the intro dissolves
        switch (_introConfig.PostIntroAction)
        {
            case "opening_headline":
                PreviewHeadlineWith(EditingHeadline);
                _manualCueSinceTake = true;
                break;
            case "first_story" when HeadlineSequence.FirstOrDefault() is { } firstStory:
                CueStory(firstStory);
                _manualCueSinceTake = true;
                break;
            case "all_headlines" when HeadlineSequence.FirstOrDefault(h => h.InHeadlines && h.MediaItem != null)?.MediaItem is { } boardMedia:
                // The media framed beside the list; no story lower third, the list already says it all
                _cuedStory = null;
                PreparePreview(boardMedia);
                _manualCueSinceTake = true;
                break;
        }

        _introActionOverride = null;
        _isIntroPlaying = true;
        CommandManager.InvalidateRequerySuggested();
        _stateManager.SetIntroActive(true);

        SendCanvasCommand("toggle_standby", false);
        SendCanvasCommand("play_intro", new PlayIntroCommandData
        {
            VideoUrl = hasVideo ? $"{_server.BaseUrl}media/{IntroVideoMediaId}" : null,
            AudioUrl = hasAudio ? $"{_server.BaseUrl}media/{IntroAudioMediaId}" : null,
            Volume = _introConfig.Volume,
            UseBuiltInMotion = true,
            PostIntroAction = _introConfig.PostIntroAction
        });

        IntroStatusText = hasVideo ? "INTRO ON AIR (custom video)" : "INTRO ON AIR (built-in sequence)";
        StatusNotification = "INTRO ON AIR — Esc or SKIP ends it early.";
    }

    private void SkipIntro()
    {
        if (!_isIntroPlaying) return;
        SendCanvasCommand("skip_intro");
        StatusNotification = "Skipping intro…";
    }

    /// <summary>
    /// The intro's closing fade has begun: take the opening scene live underneath it.
    /// </summary>
    private void OnIntroEnded(string action)
    {
        if (!_isIntroPlaying) return;
        _isIntroPlaying = false;
        CommandManager.InvalidateRequerySuggested();
        _stateManager.SetIntroActive(false);

        action = _introActionOverride ?? action;
        _introActionOverride = null;

        bool hasCue = _stateManager.State.PreviewMedia != null || _stateManager.State.PreviewGraphic != null;
        if (action == "all_headlines" && HeadlineCount > 0)
        {
            if (hasCue)
            {
                TakeLive();
            }
            else
            {
                // No headline has media: the list still comes up, over the studio backdrop
                _stateManager.SetStandby(false);
                SendCanvasCommand("toggle_standby", false);
            }
            ShowHeadlinesBoard();
            IntroStatusText = "Intro finished — all headlines on screen";
        }
        else if (action is "opening_headline" or "first_story" && hasCue)

        {
            IntroStatusText = "Intro finished — opening scene on air";
            TakeLive();
        }
        else
        {
            IntroStatusText = "Intro finished — standby";
            _stateManager.SetStandby(true);
            SendCanvasCommand("toggle_standby", true);
            StatusNotification = "Intro finished. Standby slate on air.";
        }
    }

    private async void SelectIntroVideo()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose Intro / Theme Video",
            Filter = $"Videos (incl. camera .mts)|{MediaConverter.DialogPattern(video: true, stills: false)}|All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        var playable = await ConvertForBroadcastAsync(dialog.FileName);
        if (playable != null) SetIntroMedia(video: true, path: playable);
    }

    private void SelectIntroAudio()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose Intro Theme Music",
            Filter = "Audio Files (*.mp3;*.wav)|*.mp3;*.wav|All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true) SetIntroMedia(video: false, path: dialog.FileName);
    }

    private void SetIntroMedia(bool video, string path)
    {
        if (video) _introConfig.VideoPath = path;
        else _introConfig.AudioPath = path;

        _db.SaveIntroConfig(_introConfig);
        RegisterIntroMedia();
        OnPropertyChanged(nameof(IntroVideoDisplay));
        OnPropertyChanged(nameof(IntroAudioDisplay));
        StatusNotification = string.IsNullOrEmpty(path)
            ? $"Intro {(video ? "video" : "music")} cleared — the built-in {(video ? "sequence" : "theme")} will be used."
            : $"Intro {(video ? "video" : "music")} set: {Path.GetFileName(path)}";
    }

    /// <summary>
    /// Only approved ids are served to the canvas, so register (or drop) the intro files.
    /// </summary>
    private void RegisterIntroMedia()
    {
        _server.UnregisterMedia(IntroVideoMediaId);
        _server.UnregisterMedia(IntroAudioMediaId);
        if (!string.IsNullOrEmpty(_introConfig.VideoPath)) _server.RegisterApprovedMedia(IntroVideoMediaId, _introConfig.VideoPath);
        if (!string.IsNullOrEmpty(_introConfig.AudioPath)) _server.RegisterApprovedMedia(IntroAudioMediaId, _introConfig.AudioPath);
    }

    #endregion

    #region Voice-over & Speaker Windows

    private void StartVoiceOver()
    {
        if (_broadcastWindow == null)
        {
            StatusNotification = "Open the broadcast output (projector) before starting a voice-over.";
            return;
        }
        if (_stateManager.State.IsStandbyActive)
        {
            StatusNotification = "Voice-over held — the standby slate is on air. Take a story live first.";
            return;
        }

        RegisterOverlayMedia();
        bool hasAudio = !string.IsNullOrEmpty(_voConfig.AudioPath) && File.Exists(_voConfig.AudioPath);
        if (!string.IsNullOrEmpty(_voConfig.AudioPath) && !hasAudio)
        {
            StatusNotification = $"Voice-over track not found: {_voConfig.AudioPath}";
            return;
        }

        SendCanvasCommand("set_volume", ProgramVolume);
        SendCanvasCommand("start_voice_over", new VoiceOverCommandData
        {
            AudioUrl = hasAudio ? $"{_server.BaseUrl}media/{VoiceOverMediaId}?v={File.GetLastWriteTimeUtc(_voConfig.AudioPath).Ticks}" : null,
            Name = _voConfig.Name,
            Designation = _voConfig.Designation,
            Volume = _voConfig.Volume,
            DuckLevel = _voConfig.DuckLevel,
            ShowTag = _voConfig.ShowTag,
            Loop = _voConfig.Loop
        });
        _stateManager.SetVoiceOverActive(true);
        StatusNotification = hasAudio
            ? $"VOICE-OVER ON AIR: '{Path.GetFileName(_voConfig.AudioPath)}' — program audio ducked to {_voConfig.DuckLevel:P0}."
            : $"LIVE NARRATOR VOICE-OVER: program audio ducked to {_voConfig.DuckLevel:P0}. Press STOP when the narration ends.";
    }

    /// <summary>
    /// Keeps the on-air video's own sound running as the voice-over, so pictures or
    /// other clips can be taken live over it (news "sound bite with cover shots").
    /// The program goes silent; the held sound ends with the clip or on STOP.
    /// </summary>
    private void HoldProgramSound()
    {
        if (_broadcastWindow == null)
        {
            StatusNotification = "Open the broadcast output (projector) before holding the program sound.";
            return;
        }
        if (_stateManager.State.IsStandbyActive || _stateManager.State.OnAirMedia is not { } onAir || onAir.IsImage)
        {
            StatusNotification = "Take a video live first — its sound is what gets held as the voice-over.";
            return;
        }

        SendCanvasCommand("set_volume", ProgramVolume);
        SendCanvasCommand("start_voice_over_from_program", new VoiceOverCommandData
        {
            Name = _voConfig.Name,
            Designation = _voConfig.Designation,
            Volume = _voConfig.Volume,
            ShowTag = _voConfig.ShowTag && !string.IsNullOrWhiteSpace(_voConfig.Name)
        });
        _stateManager.SetVoiceOverActive(true);
        StatusNotification = $"HOLDING SOUND of '{onAir.Title}' as voice-over — cue pictures or video and TAKE them over it. STOP VO ends it.";
    }

    private void StopVoiceOver()
    {
        SendCanvasCommand("stop_voice_over");
        _stateManager.SetVoiceOverActive(false);
        StatusNotification = "Voice-over stopped — program audio restored.";
    }

    /// <summary>
    /// The voice-over track can be plain audio or any video: a video plays its
    /// soundtrack only, so the interview audio runs under whatever is on air.
    /// </summary>
    private async void SelectVoiceOverAudio()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose Voice-over Track (audio, or a video whose sound you want)",
            Filter = $"Audio or Video|{MediaConverter.DialogPattern(video: true, stills: false, audio: true)}"
                   + $"|Audio Files|{MediaConverter.DialogPattern(video: false, stills: false, audio: true)}"
                   + $"|Video Files (sound only)|{MediaConverter.DialogPattern(video: true, stills: false)}"
                   + "|All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        var playable = await ConvertForBroadcastAsync(dialog.FileName);
        if (playable != null) SetVoiceOverAudio(playable);
    }

    private void SetVoiceOverAudio(string path)
    {
        _voConfig.AudioPath = path;
        _db.SaveVoiceOverConfig(_voConfig);
        RegisterOverlayMedia();
        OnPropertyChanged(nameof(VoiceOverAudioDisplay));
        StatusNotification = string.IsNullOrEmpty(path)
            ? "Voice-over track cleared — VO now works as a live narrator (ducks audio, shows the tag)."
            : $"Voice-over track set: {Path.GetFileName(path)}";
    }

    private static int ParseBox(object? param) => param?.ToString() == "2" ? 2 : 1;

    private async void SelectSpeakerFile(int box)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Choose a clip or picture for speaker window {box}",
            Filter = $"{AllMediaFilterLabel}|{MediaConverter.DialogPattern(video: true, stills: true)}|All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        var playable = await ConvertForBroadcastAsync(dialog.FileName);
        if (playable == null) return;

        var config = _speakers[box - 1];
        config.SourceKind = "file";
        config.SourcePath = playable;
        SpeakerSourceChanged(box);
    }


    private void UseSpeakerCamera(int box)
    {
        _speakers[box - 1].SourceKind = "camera";
        SpeakerSourceChanged(box);
    }

    private void ClearSpeaker(int box)
    {
        var config = _speakers[box - 1];
        config.SourceKind = "none";
        config.SourcePath = string.Empty;
        SpeakerSourceChanged(box);
    }

    private void SpeakerSourceChanged(int box)
    {
        _db.SaveSpeakerBoxConfig(box, _speakers[box - 1]);
        RegisterOverlayMedia();
        OnPropertyChanged(box == 1 ? nameof(Speaker1SourceDisplay) : nameof(Speaker2SourceDisplay));
        if (AreSpeakersLive) ShowSpeakers();
        else StatusNotification = $"Speaker window {box}: {DescribeSpeakerSource(_speakers[box - 1])}.";
    }

    /// <summary>Persists caption/camera edits and pushes them live if the windows are on air.</summary>
    private void SaveSpeakers()
    {
        _db.SaveSpeakerBoxConfig(1, _speakers[0]);
        _db.SaveSpeakerBoxConfig(2, _speakers[1]);
        OnPropertyChanged(nameof(Speaker1SourceDisplay));
        OnPropertyChanged(nameof(Speaker2SourceDisplay));
        if (AreSpeakersLive) ShowSpeakers();
        else StatusNotification = "Speaker windows saved.";
    }

    private void ShowSpeakers()
    {
        _db.SaveSpeakerBoxConfig(1, _speakers[0]);
        _db.SaveSpeakerBoxConfig(2, _speakers[1]);
        RegisterOverlayMedia();

        var boxes = new List<SpeakerBoxCommandData>();
        for (int i = 0; i < _speakers.Length; i++)
        {
            var config = _speakers[i];
            bool fileOk = config.SourceKind == "file" && File.Exists(config.SourcePath);
            bool visible = config.IsEnabled && (config.SourceKind == "camera" || fileOk);
            string kind = config.SourceKind == "camera" ? "camera"
                : fileOk ? (MediaItem.ImageExtensions.Contains(Path.GetExtension(config.SourcePath).ToLowerInvariant()) ? "image" : "video")
                : "none";

            boxes.Add(new SpeakerBoxCommandData
            {
                Visible = visible,
                Kind = kind,
                Url = fileOk ? $"{_server.BaseUrl}media/{SpeakerMediaIdBase + i + 1}?v={File.GetLastWriteTimeUtc(config.SourcePath).Ticks}" : null,
                CameraIndex = config.CameraIndex,
                Name = config.Name,
                Designation = config.Designation,
                PlayAudio = config.PlayAudio
            });
        }

        int shown = boxes.Count(b => b.Visible);
        if (shown == 0)
        {
            StatusNotification = "No speaker window is ready. Give a window a clip, picture or camera and tick 'Show'.";
            return;
        }
        if (_broadcastWindow == null)
        {
            StatusNotification = "Open the broadcast output (projector) before showing speaker windows.";
            return;
        }

        SendCanvasCommand("show_speakers", new ShowSpeakersCommandData { Layout = SpeakerLayout, Boxes = boxes });
        _stateManager.SetSpeakersLive(true);
        StatusNotification = $"SPEAKERS ON AIR: {shown} window(s), {SpeakerLayouts.First(l => l.Key == SpeakerLayout).Value.ToLowerInvariant()}.";
    }

    private void HideSpeakers()
    {
        SendCanvasCommand("hide_speakers");
        _stateManager.SetSpeakersLive(false);
        StatusNotification = "Speaker windows cleared from air.";
    }

    /// <summary>Approve the voice-over and speaker files for the loopback media server.</summary>
    private void RegisterOverlayMedia()
    {
        _server.UnregisterMedia(VoiceOverMediaId);
        if (!string.IsNullOrEmpty(_voConfig.AudioPath)) _server.RegisterApprovedMedia(VoiceOverMediaId, _voConfig.AudioPath);

        for (int i = 0; i < _speakers.Length; i++)
        {
            int id = SpeakerMediaIdBase + i + 1;
            _server.UnregisterMedia(id);
            if (_speakers[i].SourceKind == "file" && !string.IsNullOrEmpty(_speakers[i].SourcePath))
                _server.RegisterApprovedMedia(id, _speakers[i].SourcePath);
        }
    }

    #endregion


    #region Display & Broadcast Window

    private void LaunchBroadcast()
    {
        if (_projectorWindow is { IsLoaded: true })
        {
            _projectorWindow.ApplyDisplayPlacement(SelectedDisplay);
            _projectorWindow.Activate();
            return;
        }

        var window = new BroadcastWindow(_server.BaseUrl, SelectedDisplay);
        _projectorWindow = window;

        // The projector takes over sound and handshakes; a test-run pop-out becomes its muted mirror
        if (_popoutWindow != null)
        {
            _popoutWindow.IsMuted = true;
            _popoutWindow.Title = PopoutMirrorTitle;
        }

        WireCanvasEvents(window);
        window.Show();
        IsBroadcastWindowOpen = true;
        StatusNotification = window.IsOnOperatorScreen
            ? "No projector selected — broadcast output opened on this screen (not topmost; Esc / F-keys still work, Alt+F4 closes it)."
            : $"Broadcast screen launched fullscreen on: {SelectedDisplay?.Description}";
    }

    /// <summary>
    /// Opens (or closes) the TEST RUN pop-out: the real broadcast canvas in a
    /// resizable window. Without a projector it IS the output, so a rehearsal
    /// runs every take, transition and overlay exactly as air would. Once the
    /// projector is open it receives the same commands as a muted mirror.
    /// </summary>
    private void TogglePopout()
    {
        if (_popoutWindow != null)
        {
            _popoutWindow.Close(); // Closed hands the output back and clears state
            return;
        }

        bool mirror = _projectorWindow != null;
        var window = new BroadcastWindow(_server.BaseUrl, isPopout: true)
        {
            IsMuted = mirror,
            Title = mirror ? PopoutMirrorTitle : PopoutTestRunTitle
        };
        _popoutWindow = window;

        WireCanvasEvents(window);
        window.Show();
        IsPopoutOpen = true;
        StatusNotification = mirror
            ? "Projector mirror opened: a muted copy of what the projector is showing."
            : "TEST RUN window opened — rehearse here: everything you take live plays in this window. LAUNCH PROJECTOR when ready and this window becomes a muted mirror.";
    }

    /// <summary>
    /// Subscribes the host to one canvas. Only the driving canvas may confirm
    /// scenes, finish transitions or report playback; a mirror's events are ignored.
    /// </summary>
    private void WireCanvasEvents(BroadcastWindow window)
    {
        window.TimeUpdated += (s, args) =>
        {
            _dispatcher.Invoke(() =>
            {
                if (!IsDrivingCanvas(window)) return;
                // Keep the authoritative playback flag in sync (Space toggles from this)
                _stateManager.State.IsPlaying = !args.Paused;
                OnPropertyChanged(nameof(IsPlaying));

                // Duration always refreshes, even mid-scrub, so the slider range stays valid
                PlaybackDuration = args.Duration;

                if (SuppressPlaybackTimeUpdates) return;

                PlaybackCurrentTime = args.CurrentTime;

                var cur = TimeSpan.FromSeconds(args.CurrentTime);
                var dur = TimeSpan.FromSeconds(args.Duration);
                PlaybackTimeText = $"{cur:mm\\:ss} / {dur:mm\\:ss}";
            });
        };

        window.TransitionFinished += (s, args) =>
        {
            _dispatcher.Invoke(() =>
            {
                if (!IsDrivingCanvas(window)) return;
                _stateManager.CompleteTransition();
                StatusNotification = $"Transition {args.Transition} completed successfully in 500ms.";
                AutoCueNextRundownStory();
            });
        };

        window.CanvasReady += (s, args) =>
        {
            _dispatcher.Invoke(() =>
            {
                if (!IsDrivingCanvas(window))
                {
                    SyncMirrorCanvas(window);
                    return;
                }

                // Clear any transition flag stuck from a broadcast that ran without this window
                _stateManager.ResetTransition();

                PublishTicker();
                SendCanvasCommand("toggle_standby", _stateManager.State.IsStandbyActive);
                SendCanvasCommand("configure_live_badge", _stateManager.State.IsLiveIndicatorVisible);
                SendCanvasCommand("set_volume", ProgramVolume);
                SendCanvasCommand("set_scene_captions", _stateManager.State.IsSceneCaptionVisible);

                // A reloaded canvas has lost its overlays: speakers come back, a VO does not
                // (an open pop-out still has the VO, so stop it there too and keep both canvases alike)
                if (_stateManager.State.IsVoiceOverActive)
                {
                    _stateManager.SetVoiceOverActive(false);
                    SendCanvasCommand("stop_voice_over");
                }
                if (_stateManager.State.AreSpeakersLive) ShowSpeakers();
                if (IsHeadlinesBoardLive) ShowHeadlinesBoard();

                // Cue-before-launch fix: re-send anything already sitting in Preview so
                // content cued before the projector opened is actually loaded on the canvas.
                RecuePreviewOnCanvas();
            });
        };

        window.ScenePrepared += (s, args) =>
        {
            _dispatcher.Invoke(() =>
            {
                if (!IsDrivingCanvas(window)) return;
                var tcs = _scenePreparedTcs;
                _scenePreparedTcs = null;
                tcs?.TrySetResult(true);

                // The standby slot now holds renderable content: unlock TAKE LIVE.
                _isPreviewPrepared = true;
                RefreshTakeLiveState();
                StatusNotification = "PREVIEW PREPARED — TAKE LIVE is unlocked.";
            });
        };

        window.PlaybackError += (s, message) =>
        {
            _dispatcher.Invoke(() =>
            {
                if (!IsDrivingCanvas(window)) return;
                // A failed preview load must unblock any pending TAKE LIVE wait
                // immediately and leave the command disabled. Never treat an
                // error as a prepared scene.
                if (_scenePreparedTcs != null && !_isPreviewPrepared)
                {
                    var tcs = _scenePreparedTcs;
                    _scenePreparedTcs = null;
                    _isPreviewPrepared = false;
                    tcs.TrySetResult(false);
                    RefreshTakeLiveState();
                }
                StatusNotification = $"PLAYBACK ERROR (projector): {message}. Check the media file path in the library.";
            });
        };

        window.IntroEnded += (s, action) =>
        {
            _dispatcher.Invoke(() => { if (IsDrivingCanvas(window)) OnIntroEnded(action); });
        };

        window.IntroError += (s, message) =>
        {
            _dispatcher.Invoke(() =>
            {
                if (!IsDrivingCanvas(window)) return;
                // The canvas closes the intro itself; land on standby rather than a half-ready scene
                _introActionOverride = "standby";
                IntroStatusText = "Intro failed";
                StatusNotification = $"INTRO ERROR: {message} Returning to standby.";
            });
        };

        window.VoiceOverEnded += (s, args) =>
        {
            _dispatcher.Invoke(() =>
            {
                if (!IsDrivingCanvas(window)) return;
                if (!_stateManager.State.IsVoiceOverActive) return;
                _stateManager.SetVoiceOverActive(false);
                StatusNotification = "Voice-over finished — program audio restored.";
            });
        };

        window.OverlayError += (s, message) =>
        {
            _dispatcher.Invoke(() => { if (IsDrivingCanvas(window)) StatusNotification = $"OVERLAY: {message}"; });
        };

        window.ShortcutKeyPressed += (s, key) =>
        {
            _dispatcher.Invoke(() => HandleShortcutKey(key));
        };

        window.MediaEnded += (s, args) =>
        {
            _dispatcher.Invoke(() =>
            {
                if (!IsDrivingCanvas(window)) return;
                if (IsHeadlinesPlaying) OnHeadlineMediaEnded();
                else _ = AutoAdvanceRundownAsync();
            });
        };

        window.Closed += (s, e) => OnCanvasClosed(window);
    }

    private void OnCanvasClosed(BroadcastWindow window)
    {
        bool wasDriving = IsDrivingCanvas(window);
        if (ReferenceEquals(window, _projectorWindow))
        {
            _projectorWindow = null;
            IsBroadcastWindowOpen = false;
        }
        if (ReferenceEquals(window, _popoutWindow))
        {
            _popoutWindow = null;
            IsPopoutOpen = false;
        }

        if (!wasDriving)
        {
            StatusNotification = "Projector mirror closed. The projector output is unaffected.";
            return;
        }

        // The pop-out has received every command the projector did: it carries on as the output
        if (_broadcastWindow is { } remaining)
        {
            remaining.IsMuted = false;
            remaining.Title = PopoutTestRunTitle;
            StatusNotification = "Projector closed — the TEST RUN window is now the output.";
            return;
        }

        if (_stateManager.State.IsVoiceOverActive) _stateManager.SetVoiceOverActive(false);
        if (_stateManager.State.AreSpeakersLive) _stateManager.SetSpeakersLive(false);
        IsHeadlinesBoardLive = false;
        _isHeadlineSideOn = false;
        if (_isIntroPlaying)
        {
            _isIntroPlaying = false;
            _stateManager.SetIntroActive(false);
            IntroStatusText = "Intro idle";
            CommandManager.InvalidateRequerySuggested();
        }
        StatusNotification = window.IsPopout ? "TEST RUN window closed." : "Broadcast output window closed.";
    }

    private void CloseBroadcast()
    {
        _projectorWindow?.Close(); // Closed updates state (and hands over to a pop-out)
    }

    /// <summary>Closes the projector and the pop-out (application shutdown).</summary>
    public void CloseAllOutputs()
    {
        _popoutWindow?.Close();
        _projectorWindow?.Close();
    }

    /// <summary>
    /// Brings a freshly opened mirror up to date with the projector: live layers,
    /// the scene on air (cut straight in, video seeked to the same point) and the
    /// cued Preview, so its next take matches. Sent to the mirror alone.
    /// </summary>
    private void SyncMirrorCanvas(BroadcastWindow mirror)
    {
        var state = _stateManager.State;
        mirror.SendCommand("publish_ticker", new { config = state.LiveTickerConfig, items = state.LiveTickerItems });
        mirror.SendCommand("configure_live_badge", state.IsLiveIndicatorVisible);
        mirror.SendCommand("set_volume", ProgramVolume);
        mirror.SendCommand("set_scene_captions", state.IsSceneCaptionVisible);

        if (!state.IsStandbyActive && BuildSceneData(state.OnAirMedia, state.OnAirGraphic) is { } onAir)
        {
            mirror.SendCommand("load_scene", onAir);
            mirror.SendCommand("take_live", new TakeLiveCommandData { Transition = "cut", TransitionDurationMs = 0 });
            if (state.OnAirMedia is { IsImage: false }) mirror.SendCommand("seek", PlaybackCurrentTime);
        }
        mirror.SendCommand("toggle_standby", state.IsStandbyActive);
        if (state.LiveGraphic != null) mirror.SendCommand("publish_headline", state.LiveGraphic);

        if (BuildSceneData(state.PreviewMedia, state.PreviewGraphic) is { } cued)
            mirror.SendCommand("load_scene", cued);
    }

    private LoadSceneData? BuildSceneData(MediaItem? media, HeadlineDraft? graphic)
    {
        if (media != null) return MediaSceneData(media);
        if (graphic != null) return new LoadSceneData { Type = "graphic", Title = graphic.Title, Graphic = graphic };
        return null;
    }

    private LoadSceneData MediaSceneData(MediaItem media) => new()
    {
        Type = media.IsImage ? "image" : "video",
        MediaUrl = $"{_server.BaseUrl}media/{media.Id}",
        MediaId = media.Id,
        Title = media.Title,
        Transition = TransitionConfig.ToCommandName(media.AssignedTransition),
        TransitionDurationMs = TransitionConfig.GetDurationMs(media.AssignedTransition),
        AutoPlay = true,
        CaptionName = media.CaptionName,
        CaptionTitle = media.CaptionTitle
    };

    /// <summary>
    /// Re-sends the current Preview content to a freshly opened broadcast canvas.
    /// Covers the cue-before-launch flow where the original load_scene was lost
    /// because no output window existed yet.
    /// </summary>
    private void RecuePreviewOnCanvas()
    {
        if (_stateManager.State.PreviewMedia is { } media)
        {
            PreparePreview(media);
        }
        else if (_stateManager.State.PreviewGraphic is { } graphic)
        {
            PreviewHeadlineWith(graphic);
        }
    }

    /// <summary>
    /// After the incoming slot has safely gone on air, cue the NEXT story in the
    /// news rundown into Preview so the operator is ready for the following take.
    /// </summary>
    private void AutoCueNextRundownStory()
    {
        if (OnAirStory == null || IsHeadlinesPlaying) return;

        // Never replace a cue the operator made during or after the take
        if (_manualCueSinceTake) return;

        var next = FindNextStory();
        if (next != null)
        {
            CueStory(next);
            StatusNotification = $"Transition complete. Next story '{next.ListTitle}' is cued — review and TAKE LIVE when ready.";
        }
    }

    /// <summary>Every command goes to the projector and to the pop-out, so both show the same programme.</summary>
    private void SendCanvasCommand(string command, object? data = null)
    {
        _projectorWindow?.SendCommand(command, data);
        _popoutWindow?.SendCommand(command, data);
    }

    #endregion
}
