using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    public MainWindow()
    {
        // Assets directory for broadcast canvas
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var assetsDir = Path.Combine(baseDir, "BroadcastCanvas");

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

        switch (e.Key)
        {
            case Key.F5:
                _viewModel.TakeLiveCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F6:
                _viewModel.ToggleStandbyCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F7:
                _viewModel.QuickBreakingLiveCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Space:
                if (_stateManager.State.IsPlaying)
                    _viewModel.PauseCommand.Execute(null);
                else
                    _viewModel.PlayCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                _viewModel.EmergencyStandby();
                e.Handled = true;
                break;
        }
    }

    private void OnTransitionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel != null && sender is ComboBox cb && cb.SelectedItem is TransitionType trans && _viewModel.SelectedMedia != null)
        {
            _viewModel.UpdateMediaTransition(_viewModel.SelectedMedia, trans);
        }
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

        _viewModel.CloseBroadcastCommand.Execute(null);
        _server.Dispose();
        _displayManager.Dispose();
        _db.Dispose();
    }
}
