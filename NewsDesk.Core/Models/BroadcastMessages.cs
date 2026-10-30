using System.Text.Json.Serialization;

namespace NewsDesk.Core.Models;

/// <summary>
/// Authoritative state representing current on-air and preview content.
/// </summary>
public class BroadcastState
{
    public MediaItem? OnAirMedia { get; set; }
    public MediaItem? PreviewMedia { get; set; }

    /// <summary>
    /// Full-screen graphic slide currently on air (distinct from the LiveGraphic lower third).
    /// Exactly one of OnAirMedia / OnAirGraphic is set after a take.
    /// </summary>
    public HeadlineDraft? OnAirGraphic { get; set; }
    public HeadlineDraft? LiveGraphic { get; set; }
    public HeadlineDraft? PreviewGraphic { get; set; }
    public TickerConfig LiveTickerConfig { get; set; } = new();
    public List<TickerItem> LiveTickerItems { get; set; } = [];
    public bool IsLiveOnAir { get; set; } = false;
    public bool IsStandbyActive { get; set; } = true;
    public bool IsIntroActive { get; set; } = false;
    public bool IsLiveIndicatorVisible { get; set; } = true;

    // Overlays that sit on top of whatever scene is on air
    public bool IsVoiceOverActive { get; set; } = false;
    public bool AreSpeakersLive { get; set; } = false;
    public bool IsSceneCaptionVisible { get; set; } = true;
    public TransitionType ActiveTransition { get; set; } = TransitionType.Fade;
    public bool IsTransitioning { get; set; } = false;

    // Playback state
    public bool IsPlaying { get; set; } = false;
    public double CurrentTime { get; set; } = 0;
    public double Duration { get; set; } = 0;
}

/// <summary>
/// Structured host-to-page commands sent to WebView2.
/// </summary>
public class BroadcastCommand
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public object? Data { get; set; }
}

public class LoadSceneData
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "video"; // "video", "graphic", "standby"

    [JsonPropertyName("mediaUrl")]
    public string? MediaUrl { get; set; }

    [JsonPropertyName("mediaId")]
    public int? MediaId { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("transition")]
    public string Transition { get; set; } = "fade";

    [JsonPropertyName("transitionDurationMs")]
    public int TransitionDurationMs { get; set; } = 500;

    [JsonPropertyName("autoPlay")]
    public bool AutoPlay { get; set; } = true;

    [JsonPropertyName("graphic")]
    public object? Graphic { get; set; }

    /// <summary>Name strap for a person in the frame (pictures and videos). Empty = none.</summary>
    [JsonPropertyName("captionName")]
    public string CaptionName { get; set; } = string.Empty;

    [JsonPropertyName("captionTitle")]
    public string CaptionTitle { get; set; } = string.Empty;
}

public class TakeLiveCommandData
{
    [JsonPropertyName("transition")]
    public string Transition { get; set; } = "fade";

    [JsonPropertyName("transitionDurationMs")]
    public int TransitionDurationMs { get; set; } = 500;
}

public class PlayIntroCommandData
{
    [JsonPropertyName("videoUrl")]
    public string? VideoUrl { get; set; }

    [JsonPropertyName("audioUrl")]
    public string? AudioUrl { get; set; }

    [JsonPropertyName("volume")]
    public double Volume { get; set; } = 1.0;

    [JsonPropertyName("useBuiltInMotion")]
    public bool UseBuiltInMotion { get; set; } = true;

    [JsonPropertyName("postIntroAction")]
    public string PostIntroAction { get; set; } = "opening_headline";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "EMPYREAN NEWS — THE FAREWELL EDITION";
}

public class IntroConfig
{
    public string VideoPath { get; set; } = string.Empty;
    public string AudioPath { get; set; } = string.Empty;
    public double Volume { get; set; } = 1.0;
    public bool UseBuiltInMotion { get; set; } = true;
    public string PostIntroAction { get; set; } = "opening_headline"; // "opening_headline", "standby", "first_story"
}

/// <summary>
/// Voice-over: an audio track narrated over the program. Program audio is ducked
/// to DuckLevel while it plays; an optional on-screen tag names the narrator.
/// </summary>
public class VoiceOverConfig
{
    public string AudioPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Designation { get; set; } = string.Empty;
    public double Volume { get; set; } = 1.0;
    public double DuckLevel { get; set; } = 0.25;
    public bool ShowTag { get; set; } = true;
    public bool Loop { get; set; } = false;
}

public class VoiceOverCommandData
{
    [JsonPropertyName("audioUrl")]
    public string? AudioUrl { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("designation")]
    public string Designation { get; set; } = string.Empty;

    [JsonPropertyName("volume")]
    public double Volume { get; set; } = 1.0;

    [JsonPropertyName("duckLevel")]
    public double DuckLevel { get; set; } = 0.25;

    [JsonPropertyName("showTag")]
    public bool ShowTag { get; set; } = true;

    [JsonPropertyName("loop")]
    public bool Loop { get; set; }
}

/// <summary>
/// One of the two floating speaker windows. SourceKind: "none", "file" (video or
/// picture) or "camera" (webcam number CameraIndex, zero-based).
/// </summary>
public class SpeakerBoxConfig
{
    public string SourceKind { get; set; } = "none";
    public string SourcePath { get; set; } = string.Empty;
    public int CameraIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Designation { get; set; } = string.Empty;
    public bool PlayAudio { get; set; } = true;
    public bool IsEnabled { get; set; } = true;

    public bool HasSource => SourceKind == "camera"
        || (SourceKind == "file" && !string.IsNullOrWhiteSpace(SourcePath));
}

public class SpeakerBoxCommandData
{
    [JsonPropertyName("visible")]
    public bool Visible { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "none"; // "video", "image", "camera", "none"

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("cameraIndex")]
    public int CameraIndex { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("designation")]
    public string Designation { get; set; } = string.Empty;

    [JsonPropertyName("playAudio")]
    public bool PlayAudio { get; set; }
}

public class ShowSpeakersCommandData
{
    [JsonPropertyName("layout")]
    public string Layout { get; set; } = "side_by_side"; // "side_by_side", "corners"

    [JsonPropertyName("boxes")]
    public List<SpeakerBoxCommandData> Boxes { get; set; } = [];
}
