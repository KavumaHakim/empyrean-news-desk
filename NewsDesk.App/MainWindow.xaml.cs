using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NewsDesk.App.Services;
using NewsDesk.App.ViewModels;
using NewsDesk.Core.Data;
using NewsDesk.Core.Models;
using NewsDesk.Core.Server;
using NewsDesk.Core.Services;

namespace NewsDesk.App;

public partial class MainWindow : Window
{
    private readonly DatabaseService _db;
    private readonly MediaHttpServer _server;
    private readonly BroadcastStateManager _stateManager;
    private readonly DisplayManager _displayManager;
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _dashboardMonitorTimer;
    private bool _isPreviewScrubbing;
    private bool _isPreviewPlaying;
    private bool _programMonitorHasStarted;

    // What each monitor has loaded: the view model re-announces Preview/OnAir on every
    // state change (a take, a lower third, a layer…); only a different file reloads
    private string? _previewLoadedPath;
    private string? _programLoadedPath;

    // Preview opened on a still frame (monitor setting), not playing
    private bool _previewOpensPaused;
    private double _lastProgramMonitorTime;

    public MainWindow()
    {
        // Assets directory for broadcast canvas
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var assetsDir = Path.Combine(baseDir, "BroadcastCanvas");

        // A show opened from another computer replaces this one now, before it is in use
        // (the show it replaces is kept in Backups)
        try { NewsDesk.Core.Services.ShowPackage.ApplyPendingImport(DatabaseService.DefaultPath); }
        catch (Exception ex) { ErrorLog.Write("Open show", ex); }

        // Portable copy: "START NEWS DESK" passes --show-folder; the show and its videos
        // come up as they were on the computer that made the copy
        var args = Environment.GetCommandLineArgs();
        int showArg = Array.FindIndex(args, a => a.Equals("--show-folder", StringComparison.OrdinalIgnoreCase));
        if (showArg >= 0 && showArg + 1 < args.Length)
        {
            try { NewsDesk.Core.Services.ShowPackage.OpenShowFolder(args[showArg + 1], DatabaseService.DefaultPath); }
            catch (Exception ex)
            {
                ErrorLog.Write("Open portable show", ex);
                MessageBox.Show($"The show in this folder could not be loaded:\n{ex.Message}", "NEWS DESK",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        _db = new DatabaseService();
        _server = new MediaHttpServer(assetsDir, 8089);
        try
        {
            _server.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to start the local media server: {ex.Message}\n\nThe app will start, but video playback from the library will not be available.",
                "NEWS DESK Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        _stateManager = new BroadcastStateManager(_db);
        _displayManager = new DisplayManager();

        _viewModel = new MainViewModel(_db, _server, _stateManager, _displayManager);
        DataContext = _viewModel;

        InitializeComponent();

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        LoadPreviewMonitor();
        LoadProgramMonitor();
        _dashboardMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _dashboardMonitorTimer.Tick += DashboardMonitorTimer_Tick;
        _dashboardMonitorTimer.Start();

        PreviewTimelineSlider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
            new System.Windows.Controls.Primitives.DragStartedEventHandler((s, e) => _isPreviewScrubbing = true));
        PreviewTimelineSlider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            new System.Windows.Controls.Primitives.DragCompletedEventHandler((s, e) =>
            {
                _isPreviewScrubbing = false;
                SeekPreviewVideo();
            }));
        PreviewTimelineSlider.PreviewMouseLeftButtonUp += (s, e) => SeekPreviewVideo();

        // Wire timeline scrubbing: pause telemetry-driven thumb updates while dragging,
        // then seek the on-air video to the released position.
        TimelineSlider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
            new System.Windows.Controls.Primitives.DragStartedEventHandler(OnSeekDragStarted));
        TimelineSlider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            new System.Windows.Controls.Primitives.DragCompletedEventHandler(OnSeekDragCompleted));
        TimelineSlider.PreviewMouseLeftButtonUp += (s, e) => SeekToTimelineValue();

        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Closing += MainWindow_Closing;
    }

    private void OnSeekDragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        _viewModel.SuppressPlaybackTimeUpdates = true;
    }

    private void OnSeekDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _viewModel.SuppressPlaybackTimeUpdates = false;
        SeekToTimelineValue();
    }

    private void SeekToTimelineValue()
    {
        if (_viewModel == null) return;
        _viewModel.SuppressPlaybackTimeUpdates = false;
        _viewModel.SeekCommand.Execute(TimelineSlider.Value);
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Ignore if user is currently typing inside a TextBox
        if (e.OriginalSource is TextBox) return;

        if (_viewModel.HandleShortcutKey(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
        }
    }

    private void OnTransitionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel != null && sender is ComboBox cb && cb.SelectedItem is TransitionType trans && _viewModel.SelectedMedia != null)
        {
            _viewModel.UpdateMediaTransition(_viewModel.SelectedMedia, trans);
        }
    }

    private void PreviewVideoMonitor_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _viewModel.StatusNotification = "Preview monitor could not open the selected video. The projector preview remains protected.";
    }

    private void PreviewVideoMonitor_MediaOpened(object sender, RoutedEventArgs e)
    {
        _isPreviewPlaying = !_previewOpensPaused;
        PreviewPlayButton.Content = _isPreviewPlaying ? "PAUSE" : "PLAY";

        // Some files (damaged, or a codec Windows only half-understands) open without a
        // known length yet. Asking for it then throws, so use it only when it is there;
        // the monitor timer picks the length up as soon as it becomes known.
        var duration = PreviewVideoMonitor.NaturalDuration.HasTimeSpan
            ? PreviewVideoMonitor.NaturalDuration.TimeSpan.TotalSeconds
            : 0;
        PreviewTimelineSlider.Maximum = Math.Max(1, duration);
        PreviewTimeDisplay.Text = FormatTime(0, duration);
    }

    private void PreviewVideoMonitor_MediaEnded(object sender, RoutedEventArgs e)
    {
        _isPreviewPlaying = false;
        PreviewPlayButton.Content = "PLAY";
        PreviewTimeDisplay.Text = FormatTime(PreviewTimelineSlider.Maximum, PreviewTimelineSlider.Maximum);
    }

    private void ProgramVideoMonitor_MediaOpened(object sender, RoutedEventArgs e)
    {
        SyncProgramMonitor(force: true);
    }

    private void ProgramVideoMonitor_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _viewModel.StatusNotification = "Live monitor could not open the on-air media file. Projector output is unaffected.";
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.MonitorVideoMode))
        {
            // Applied straight away, mid-show: lighter settings stop the extra decoding now
            if (_viewModel.MonitorVideoMode != "all" && _isPreviewPlaying) PausePreviewMonitor();
            SyncProgramMonitor(force: true);
        }
        else if (e.PropertyName == nameof(MainViewModel.PreviewMedia))

        {
            LoadPreviewMonitor();
        }
        else if (e.PropertyName == nameof(MainViewModel.OnAirMedia))
        {
            // Only a real change of on-air media is a take: then stop the Preview copy so
            // the same clip isn't playing (and decoding) twice next to Program
            var path = _viewModel.OnAirMedia?.FilePath;
            bool tookNewMedia = !string.Equals(path, _programLoadedPath, StringComparison.OrdinalIgnoreCase);
            LoadProgramMonitor();
            if (tookNewMedia) PausePreviewMonitor();

        }
        else if (e.PropertyName == nameof(MainViewModel.PlaybackCurrentTime))
        {
            // The projector is authoritative, but continuously seeking the local
            // MediaElement to every telemetry tick makes it replay/flash around the
            // first second instead of playing. Only correct a material seek jump.
            var target = _viewModel.PlaybackCurrentTime;
            var jumped = Math.Abs(target - _lastProgramMonitorTime) > 1.5;
            _lastProgramMonitorTime = target;
            SyncProgramMonitor(force: jumped);
        }
        else if (e.PropertyName == nameof(MainViewModel.IsStandbyActive))
        {
            SyncProgramMonitor(force: true);
        }
        else if (e.PropertyName == nameof(MainViewModel.IsPlaying))
        {
            SyncProgramMonitor();
        }
    }

    private void LoadPreviewMonitor()
    {
        var path = _viewModel.PreviewMedia?.FilePath;
        if (string.Equals(path, _previewLoadedPath, StringComparison.OrdinalIgnoreCase)) return;
        _previewLoadedPath = path;
        try
        {
            PreviewVideoMonitor.Stop();
            PreviewVideoMonitor.Source = null;
            ShowStill(PreviewImageMonitor, null);
            _isPreviewPlaying = false;
            PreviewPlayButton.Content = "PLAY";
            PreviewTimelineSlider.Value = 0;
            PreviewTimelineSlider.Maximum = 1;
            PreviewTimeDisplay.Text = "00:00 / 00:00";

            if (string.IsNullOrWhiteSpace(path)) return;
            if (!File.Exists(path))
            {
                _viewModel.StatusNotification = "Preview file was not found on this computer.";
                return;
            }

            if (_viewModel.PreviewMedia!.IsImage)
            {
                ShowStill(PreviewImageMonitor, path);
                return;
            }

            PreviewVideoMonitor.Source = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            PreviewVideoMonitor.Volume = 0;

            // Only "All monitors" plays the cued clip; otherwise it opens on a still
            // frame (one decode) and PLAY checks it on demand
            _previewOpensPaused = _viewModel.MonitorVideoMode != "all";
            if (_previewOpensPaused) PreviewVideoMonitor.Pause();
            else PreviewVideoMonitor.Play();
        }
        catch (Exception ex)
        {
            _viewModel.StatusNotification = $"Preview monitor could not load the selected video: {ex.Message}";
        }
    }

    private void LoadProgramMonitor()
    {
        var path = _viewModel.OnAirMedia?.FilePath;
        if (string.Equals(path, _programLoadedPath, StringComparison.OrdinalIgnoreCase)) return;
        _programLoadedPath = path;
        try
        {
            ProgramVideoMonitor.Stop();
            ProgramVideoMonitor.Source = null;
            ShowStill(ProgramImageMonitor, null);
            _programMonitorHasStarted = false;
            _lastProgramMonitorTime = 0;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            if (_viewModel.OnAirMedia!.IsImage)
            {
                ShowStill(ProgramImageMonitor, path);
                return;
            }

            ProgramVideoMonitor.Source = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            ProgramVideoMonitor.Volume = 0;

            // "Projector only": open on a still frame; the projector alone plays the clip
            if (_viewModel.IsProgramMonitorStill) ProgramVideoMonitor.Pause();
            // Start and seek after MediaOpened. Calling Play/Position while a new
            // source is still opening is unreliable for WPF MediaElement.
        }
        catch (Exception ex)
        {
            _viewModel.StatusNotification = $"Live monitor could not load the on-air video: {ex.Message}";
        }
    }

    /// <summary>
    /// Shows a picture on a dashboard monitor (null hides it). Loaded fully into
    /// memory so the file is never locked while it is on air.
    /// </summary>
    private void ShowStill(System.Windows.Controls.Image target, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            target.Source = null;
            target.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.DecodePixelWidth = 960;
            bitmap.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            target.Source = bitmap;
            target.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            target.Source = null;
            target.Visibility = Visibility.Collapsed;
            _viewModel.StatusNotification = $"Monitor could not show the picture: {ex.Message}";
        }
    }

    // Double-click a source to cue it (OBS-style)
    private void MediaGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedMedia != null) _viewModel.SelectForPreviewCommand.Execute(null);
    }

    private void RundownGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // A story cues with its lower third ready for TAKE
        if (_viewModel.CueStoryCommand.CanExecute(null)) _viewModel.CueStoryCommand.Execute(null);
    }

    private void DashboardMonitorTimer_Tick(object? sender, EventArgs e)

    {
        if (!_isPreviewScrubbing)
        {
            var duration = PreviewVideoMonitor.NaturalDuration.HasTimeSpan
                ? PreviewVideoMonitor.NaturalDuration.TimeSpan.TotalSeconds
                : 0;
            PreviewTimelineSlider.Maximum = Math.Max(1, duration);
            if (PreviewVideoMonitor.Source != null)
            {
                PreviewTimelineSlider.Value = Math.Clamp(PreviewVideoMonitor.Position.TotalSeconds, 0, PreviewTimelineSlider.Maximum);
            }
            PreviewTimeDisplay.Text = FormatTime(PreviewVideoMonitor.Position.TotalSeconds, duration);
        }

        SyncProgramMonitor();
    }

    private void SyncProgramMonitor(bool force = false)
    {
        if (ProgramVideoMonitor.Source == null || string.IsNullOrWhiteSpace(_viewModel.OnAirMedia?.FilePath)) return;

        // "Projector only": the monitor holds its still and never decodes alongside the projector
        if (_viewModel.IsProgramMonitorStill)
        {
            ProgramVideoMonitor.Pause();
            return;
        }

        if (_viewModel.IsStandbyActive || !_viewModel.IsPlaying)
        {
            ProgramVideoMonitor.Pause();
            return;
        }

        if (force || !_programMonitorHasStarted)
        {
            try
            {
                ProgramVideoMonitor.Position = TimeSpan.FromSeconds(Math.Max(0, _viewModel.PlaybackCurrentTime));
                ProgramVideoMonitor.Play();
                _programMonitorHasStarted = true;
            }
            catch (InvalidOperationException) { }
        }
    }

    private void PreviewPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewVideoMonitor.Source == null) return;
        if (_isPreviewPlaying)
        {
            PausePreviewMonitor();
        }
        else
        {
            PreviewVideoMonitor.Play();
            _isPreviewPlaying = true;
            PreviewPlayButton.Content = "PAUSE";
        }
    }

    private void PausePreviewMonitor()
    {
        if (PreviewVideoMonitor.Source == null) return;
        PreviewVideoMonitor.Pause();
        _isPreviewPlaying = false;
        PreviewPlayButton.Content = "PLAY";
    }

    private void PreviewReplay_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewVideoMonitor.Source == null) return;
        try
        {
            PreviewVideoMonitor.Stop();
            PreviewVideoMonitor.Position = TimeSpan.Zero;
            PreviewVideoMonitor.Play();
            _isPreviewPlaying = true;
        }
        catch (InvalidOperationException)
        {
            // The clip is still opening or could not open; the preview simply stays put
        }
    }

    private void SeekPreviewVideo()
    {
        if (PreviewVideoMonitor.Source == null) return;
        try { PreviewVideoMonitor.Position = TimeSpan.FromSeconds(PreviewTimelineSlider.Value); }
        catch (InvalidOperationException) { }
    }

    private static string FormatTime(double current, double duration)
    {
        var currentTime = TimeSpan.FromSeconds(Math.Max(0, current));
        var durationTime = TimeSpan.FromSeconds(Math.Max(0, duration));
        return $"{currentTime:mm\\:ss} / {durationTime:mm\\:ss}";
    }

    private void OnTestTransitionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && Enum.TryParse<TransitionType>(tagStr, out var trans))
        {
            _viewModel.TestTransitionCommand.Execute(trans);
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_viewModel.IsLiveOnAir)
        {
            var res = MessageBox.Show(
                "A live broadcast is currently ON AIR on the projector!\n\nAre you sure you want to stop the broadcast and close NEWS DESK?",
                "Confirm Broadcast Shutdown",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _viewModel.CloseAllOutputs();
        _dashboardMonitorTimer.Stop();
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _server.Dispose();
        _displayManager.Dispose();
        _db.Dispose();
    }
}
