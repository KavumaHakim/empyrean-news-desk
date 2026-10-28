using System.Text.Json.Serialization;

namespace NewsDesk.Core.Models;

/// <summary>
/// Authoritative state representing current on-air and preview content.
/// </summary>
public class BroadcastState
{
    public MediaItem? OnAirMedia { get; set; }
    public MediaItem? PreviewMedia { get; set; }
    public HeadlineDraft? LiveGraphic { get; set; }
    public HeadlineDraft? PreviewGraphic { get; set; }
    public TickerConfig LiveTickerConfig { get; set; } = new();
    public List<TickerItem> LiveTickerItems { get; set; } = [];
    public bool IsLiveOnAir { get; set; } = false;
    public bool IsStandbyActive { get; set; } = true;
    public bool IsLiveIndicatorVisible { get; set; } = true;
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
}

public class TakeLiveCommandData
{
    [JsonPropertyName("transition")]
    public string Transition { get; set; } = "fade";

    [JsonPropertyName("transitionDurationMs")]
    public int TransitionDurationMs { get; set; } = 500;
}
