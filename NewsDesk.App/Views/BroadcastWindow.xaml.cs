using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
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
    public event EventHandler<string>? PlaybackError;
    public event EventHandler? CanvasReady;
    public event EventHandler? MediaEnded;

    /// <summary>Intro's closing fade has started; payload is the post-intro action.</summary>
    public event EventHandler<string>? IntroEnded;
    public event EventHandler<string>? IntroError;

    /// <summary>The voice-over finished (track ended, failed, or standby cut it).</summary>
    public event EventHandler? VoiceOverEnded;

    /// <summary>A voice-over or speaker-window problem; never affects the cued scene.</summary>
    public event EventHandler<string>? OverlayError;

    /// <summary>
    /// Operator shortcut pressed while this window had focus (forwarded to the control room).
    /// </summary>
    public event EventHandler<ShortcutKeyEventArgs>? ShortcutKeyPressed;

    /// <summary>The page renderer crashed or froze and is being reloaded; payload is the failure kind.</summary>
    public event EventHandler<string>? CanvasRecovering;

    /// <summary>A sound pad finished (or was stopped); payload is its slot.</summary>
    public event EventHandler<int>? SoundPadEnded;

    /// <summary>
    /// True when the output sits on the operator's primary screen (no projector):
    /// the window is then neither topmost nor cursor-less, so the console stays reachable.
    /// </summary>
    public bool IsOnOperatorScreen { get; private set; }

    public bool IsCanvasReady => _isInitialized;

    /// <summary>
    /// A resizable test-run window on the operator screen rather than the projector output.
    /// </summary>
    public bool IsPopout { get; }

    private bool _isMuted;
    /// <summary>Silences every sound on this canvas (used while it only mirrors the projector).</summary>
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            if (WebViewBroadcast.CoreWebView2 != null) WebViewBroadcast.CoreWebView2.IsMuted = value;
        }
    }

    public BroadcastWindow(string serverBaseUrl, DisplayInfo? targetDisplay = null, bool isPopout = false)
    {
        InitializeComponent();
        _serverBaseUrl = serverBaseUrl;
        IsPopout = isPopout;

        if (isPopout) ApplyPopoutPlacement();
        else ApplyDisplayPlacement(targetDisplay);
        Loaded += OnLoaded;

        // WebView2 surfaces accelerator keys (Esc, F-keys) as WPF key events;
        // other keys arrive from the canvas as "key" web messages.
        PreviewKeyDown += (s, e) =>
        {
            bool soundPad = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                && e.Key is >= Key.D0 and <= Key.D8 or >= Key.NumPad0 and <= Key.NumPad8;
            if (soundPad || e.Key is Key.Escape or Key.F5 or Key.F6 or Key.F7 or Key.F8 or Key.F9 or Key.Space)
            {
                e.Handled = true;
                if (!e.IsRepeat) ShortcutKeyPressed?.Invoke(this, new ShortcutKeyEventArgs(e.Key, Keyboard.Modifiers));
            }
        };
    }

    /// <summary>
    /// Ordinary framed window on the operator screen: movable, resizable, never topmost.
    /// </summary>
    private void ApplyPopoutPlacement()
    {
        IsOnOperatorScreen = true;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        Topmost = false;
        Cursor = Cursors.Arrow;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = 960;
        Height = 540;
    }

    public void ApplyDisplayPlacement(DisplayInfo? targetDisplay)
    {
        IsOnOperatorScreen = targetDisplay == null || targetDisplay.IsPrimary;
        Topmost = !IsOnOperatorScreen;
        Cursor = IsOnOperatorScreen ? Cursors.Arrow : Cursors.None;
        SendCommand("set_cursor_visible", IsOnOperatorScreen);

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

            // Commands arrive from the host, never from a click on the projector, so
            // media (and the WebAudio intro theme) must be allowed to start with sound.
            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
            await WebViewBroadcast.EnsureCoreWebView2Async(env);
            WebViewBroadcast.CoreWebView2.IsMuted = _isMuted;

            WebViewBroadcast.CoreWebView2.Settings.AreDevToolsEnabled = false;
            WebViewBroadcast.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            WebViewBroadcast.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebViewBroadcast.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

            WebViewBroadcast.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

            // A crashed or frozen page renderer (a corrupt video can do this) would leave
            // the audience screen frozen or blank: reload it, and the control room
            // re-sends overlays and state when the page reports ready again.
            WebViewBroadcast.CoreWebView2.ProcessFailed += (s, args) =>
            {
                if (args.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
                    or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                    or CoreWebView2ProcessFailedKind.FrameRenderProcessExited)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        try { WebViewBroadcast.CoreWebView2?.Reload(); }
                        catch (Exception ex) { ErrorLog.Write("Projector reload", ex); }
                        CanvasRecovering?.Invoke(this, args.ProcessFailedKind.ToString());
                    });
                }
            };

            // Speaker windows can show a webcam: allow the camera for our own
            // loopback canvas only, without a prompt on the projector.
            WebViewBroadcast.CoreWebView2.PermissionRequested += (s, args) =>
            {
                if (args.PermissionKind == CoreWebView2PermissionKind.Camera
                    && args.Uri.StartsWith(_serverBaseUrl, StringComparison.OrdinalIgnoreCase))
                {
                    args.State = CoreWebView2PermissionState.Allow;
                }
            };

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
                SendCommand("set_cursor_visible", IsOnOperatorScreen);
                CanvasReady?.Invoke(this, EventArgs.Empty);
            }
            else if (type == "key" && root.TryGetProperty("data", out var keyProp))
            {
                string keyName = keyProp.GetProperty("key").GetString() ?? "";
                var modifiers = ModifierKeys.None;
                if (keyProp.TryGetProperty("ctrl", out var ctrlProp) && ctrlProp.ValueKind == JsonValueKind.True) modifiers |= ModifierKeys.Control;
                if (keyProp.TryGetProperty("shift", out var shiftProp) && shiftProp.ValueKind == JsonValueKind.True) modifiers |= ModifierKeys.Shift;

                Key? key = keyName switch
                {
                    { Length: 1 } d when char.IsDigit(d[0]) => Key.D0 + (d[0] - '0'),
                    "Escape" => Key.Escape,
                    "F5" => Key.F5,
                    "F6" => Key.F6,
                    "F7" => Key.F7,
                    "F8" => Key.F8,
                    "F9" => Key.F9,
                    " " => Key.Space,
                    _ => null
                };
                if (key.HasValue) ShortcutKeyPressed?.Invoke(this, new ShortcutKeyEventArgs(key.Value, modifiers));
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
            else if (type == "intro_ended")
            {
                string action = root.TryGetProperty("data", out var introProp)
                    && introProp.TryGetProperty("postIntroAction", out var actionProp)
                    ? (actionProp.GetString() ?? "standby")
                    : "standby";
                IntroEnded?.Invoke(this, action);
            }
            else if (type == "intro_error")
            {
                string message = root.TryGetProperty("data", out var introErr)
                    && introErr.TryGetProperty("message", out var introMsg)
                    ? (introMsg.GetString() ?? "Intro failed")
                    : "Intro failed";
                IntroError?.Invoke(this, message);
            }
            else if (type == "sfx_ended" && root.TryGetProperty("data", out var sfxProp)
                     && sfxProp.TryGetProperty("slot", out var slotProp) && slotProp.TryGetInt32(out var slot))
            {
                SoundPadEnded?.Invoke(this, slot);
            }
            else if (type == "vo_ended")
            {
                VoiceOverEnded?.Invoke(this, EventArgs.Empty);
            }
            else if (type == "overlay_error")
            {
                string message = root.TryGetProperty("data", out var ovErr)
                    && ovErr.TryGetProperty("message", out var ovMsg)
                    ? (ovMsg.GetString() ?? "Overlay error")
                    : "Overlay error";
                OverlayError?.Invoke(this, message);
            }
            else if (type == "error" && root.TryGetProperty("data", out var errProp))
            {
                string message = errProp.TryGetProperty("message", out var msgProp)
                    ? (msgProp.GetString() ?? "Playback error")
                    : "Playback error";
                PlaybackError?.Invoke(this, message);
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
        try
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
        catch (ObjectDisposedException)
        {
            // The projector may close between the readiness check and the post.
            // Commands are best-effort and must not crash the operator console.
        }
        catch (InvalidOperationException)
        {
            // WebView2 can briefly reject messages while it is initializing or closing.
        }
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

/// <summary>An operator shortcut with the modifier keys held (Ctrl+1 sound pad, Shift+F7 …).</summary>
public record ShortcutKeyEventArgs(Key Key, ModifierKeys Modifiers);
