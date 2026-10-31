using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NewsDesk.Core.Models;

/// <summary>
/// A story in the rundown: its headline and summary (the lower-third text), a tag,
/// the main video or picture, and extra media for the same story. Taking the story
/// live puts the main media on air with its lower third. Stories ticked
/// InHeadlines also make up the headlines segment, in rundown order, where each
/// stays on air for DurationSeconds.
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
    public string Title
    {
        get => _title;
        set { if (Set(ref _title, value)) Raise(nameof(ListTitle)); }
    }

    /// <summary>What the rundown shows: the headline, or the media title while none is written.</summary>
    public string ListTitle => !string.IsNullOrWhiteSpace(Title) ? Title
        : MediaItem != null ? MediaItem.Title
        : "(new story — write a headline)";

    private bool _inHeadlines = true;
    /// <summary>Whether this story is part of the headlines segment.</summary>
    public bool InHeadlines { get => _inHeadlines; set => Set(ref _inHeadlines, value); }

    private string _location = string.Empty;
    /// <summary>Place tag on the story's lower third (e.g. "MAIN HALL").</summary>
    public string Location { get => _location; set => Set(ref _location, value); }

    private bool _isBreaking;
    /// <summary>Lower third in the red breaking-news style.</summary>
    public bool IsBreaking { get => _isBreaking; set => Set(ref _isBreaking, value); }

    private string _speakersText = string.Empty;
    /// <summary>
    /// People who speak in this story, one per line as "Name | title". Each can be
    /// switched onto the lower third while the story is on air.
    /// </summary>
    public string SpeakersText
    {
        get => _speakersText;
        set { if (Set(ref _speakersText, value)) Raise(nameof(Speakers)); }
    }

    public List<StorySpeaker> Speakers =>
        HeadlineListText.Parse(SpeakersText).Select(s => new StorySpeaker(s.Title, s.Caption)).ToList();

    /// <summary>Other videos/pictures for this story: replace the shot, voice-over, or a speaker window.</summary>
    public ObservableCollection<MediaItem> ExtraMedia { get; } = [];

    /// <summary>Extra media ids as stored; resolved against the library into ExtraMedia on load.</summary>
    public List<int> ExtraMediaIds { get; set; } = [];

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
            Raise(nameof(ListTitle));
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

/// <summary>A person in a story, shown on its lower third as "name • title".</summary>
public record StorySpeaker(string Name, string Title)
{
    public string Label => string.IsNullOrWhiteSpace(Title) ? Name : $"{Name} — {Title}";
}
