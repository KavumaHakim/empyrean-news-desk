using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using NewsDesk.App.Services;
using NewsDesk.Core.Models;

namespace NewsDesk.App.Views;

public partial class BroadcastWindow : Window
{
    private readonly string _serverBaseUrl;
    private bool _isInitialized = false;

    public event EventHandler<VideoTimeUpdateEventArgs>? TimeUpdated;
    public event EventHandler<TransitionCompleteEventArgs>? TransitionFinished;
    public event EventHandler<ScenePreparedEventArgs>? ScenePrepared;
    public event EventHandler? CanvasReady;
    public event EventHandler? MediaEnded;

    public bool IsCanvasReady => _isInitialized;

    public BroadcastWindow(string serverBaseUrl, DisplayInfo? targetDisplay = null)
    {
        InitializeComponent();
        _serverBaseUrl = serverBaseUrl;

        ApplyDisplayPlacement(targetDisplay);
        Loaded += OnLoaded;
    }

    public void ApplyDisplayPlacement(DisplayInfo? targetDisplay)
    {
        if (targetDisplay != null)
        {
            WindowState = WindowState.Normal;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = targetDisplay.X;
            Top = targetDisplay.Y;
            Width = targetDisplay.Width;
            Height = targetDisplay.Height;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowState = WindowState.Normal;
            Width = 1280;
            Height = 720;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userDataFolder = Path.Combine(appData, "NewsDesk", "WebView2UserData");
            Directory.CreateDirectory(userDataFolder);

            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await WebViewBroadcast.EnsureCoreWebView2Async(env);

            WebViewBroadcast.CoreWebView2.Settings.AreDevToolsEnabled = false;
            WebViewBroadcast.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            WebViewBroadcast.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebViewBroadcast.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

            WebViewBroadcast.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

            WebViewBroadcast.Source = new Uri(new Uri(_serverBaseUrl), "index.html");
            _isInitialized = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WebView2 failed to initialize: {ex.Message}", "Broadcast Output Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var rawJson = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString();

            if (type == "ready")
            {
                CanvasReady?.Invoke(this, EventArgs.Empty);
            }
            else if (type == "timeupdate" && root.TryGetProperty("data", out var dataProp))
            {
                double current = dataProp.GetProperty("currentTime").GetDouble();
                double duration = dataProp.GetProperty("duration").GetDouble();
                bool paused = dataProp.GetProperty("paused").GetBoolean();
                bool ended = dataProp.GetProperty("ended").GetBoolean();

                TimeUpdated?.Invoke(this, new VideoTimeUpdateEventArgs(current, duration, paused, ended));
            }
            else if (type == "transition_complete" && root.TryGetProperty("data", out var transProp))
            {
                string slot = transProp.GetProperty("activeSlot").GetString() ?? "";
                string trans = transProp.GetProperty("transition").GetString() ?? "";
                TransitionFinished?.Invoke(this, new TransitionCompleteEventArgs(slot, trans));
            }
            else if (type == "scene_prepared" && root.TryGetProperty("data", out var prepProp))
            {
                string prepSlot = prepProp.GetProperty("slot").GetString() ?? "";
                ScenePrepared?.Invoke(this, new ScenePreparedEventArgs(prepSlot));
            }
            else if (type == "ended")
            {
                MediaEnded?.Invoke(this, EventArgs.Empty);
            }
        }
        catch
        {
            // Ignore telemetry parse errors
        }
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public void SendCommand(string command, object? data = null)
    {
        if (WebViewBroadcast.CoreWebView2 == null) return;

        var payload = new BroadcastCommand
        {
            Command = command,
            Data = data
        };

        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        WebViewBroadcast.CoreWebView2.PostWebMessageAsJson(json);
    }
}

public class VideoTimeUpdateEventArgs : EventArgs
{
    public double CurrentTime { get; }
    public double Duration { get; }
    public bool Paused { get; }
    public bool Ended { get; }

    public VideoTimeUpdateEventArgs(double current, double duration, bool paused, bool ended)
    {
        CurrentTime = current;
        Duration = duration;
        Paused = paused;
        Ended = ended;
    }
}

public class TransitionCompleteEventArgs : EventArgs
{
    public string ActiveSlot { get; }
    public string Transition { get; }

    public TransitionCompleteEventArgs(string activeSlot, string transition)
    {
        ActiveSlot = activeSlot;
        Transition = transition;
    }
}

public class ScenePreparedEventArgs : EventArgs
{
    public string Slot { get; }

    public ScenePreparedEventArgs(string slot)
    {
        Slot = slot;
    }
}
