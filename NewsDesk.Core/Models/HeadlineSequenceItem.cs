using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NewsDesk.Core.Models;

/// <summary>
/// One entry in the headlines segment: a headline shown over its own video or
/// picture. Played in order, each entry stays on air for DurationSeconds.
/// </summary>
public class HeadlineSequenceItem : INotifyPropertyChanged
{
    public const int DefaultDurationSeconds = 8;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Id { get; set; }

    private int _orderIndex;
    public int OrderIndex
    {
        get => _orderIndex;
        set { if (Set(ref _orderIndex, value)) Raise(nameof(Number)); }
    }

    /// <summary>1-based position shown to the operator.</summary>
    public int Number => OrderIndex + 1;

    private string _title = string.Empty;
    public string Title { get => _title; set => Set(ref _title, value); }

    private string _subtitle = string.Empty;
    public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value); }

    private string _category = "HEADLINES";
    public string Category { get => _category; set => Set(ref _category, value); }

    private int _durationSeconds = DefaultDurationSeconds;
    /// <summary>Seconds on air. For a video, 0 plays the clip to its end.</summary>
    public int DurationSeconds { get => _durationSeconds; set => Set(ref _durationSeconds, Math.Max(0, value)); }

    public int? MediaItemId { get; set; }

    private MediaItem? _mediaItem;
    /// <summary>Background video or picture; null shows a full-screen headline slide instead.</summary>
    public MediaItem? MediaItem
    {
        get => _mediaItem;
        set
        {
            MediaItemId = value?.Id;
            if (!Set(ref _mediaItem, value)) return;
            Raise(nameof(MediaLabel));
            Raise(nameof(ThumbnailPath));
        }
    }

    public string MediaLabel => MediaItem == null
        ? "Headline slide (no media)"
        : $"{MediaItem.KindLabel}: {MediaItem.Title}";

    /// <summary>Picture file for the list thumbnail (videos have none).</summary>
    public string? ThumbnailPath => MediaItem is { IsImage: true } m ? m.FilePath : null;

    private bool _isOnAir;
    /// <summary>True while this entry is the one on air during headline playback.</summary>
    public bool IsOnAir { get => _isOnAir; set => Set(ref _isOnAir, value); }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
