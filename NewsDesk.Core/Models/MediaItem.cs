namespace NewsDesk.Core.Models;

public static class StandardCategories
{
    public const string BreakingNews = "Breaking News";
    public const string NationalNews = "National News";
    public const string InternationalNews = "International News";
    public const string Politics = "Politics";
    public const string Education = "Education";
    public const string Business = "Business";
    public const string Sports = "Sports";
    public const string Weather = "Weather";
    public const string SpecialReports = "Special Reports";

    public static readonly string[] All =
    [
        BreakingNews,
        NationalNews,
        InternationalNews,
        Politics,
        Education,
        Business,
        Sports,
        Weather,
        SpecialReports
    ];
}

public class MediaItem
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public string Category { get; set; } = StandardCategories.NationalNews;
    public TransitionType AssignedTransition { get; set; } = TransitionType.Fade;
    public string Description { get; set; } = string.Empty;
    public bool IsAvailable { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Name strap shown on the scene when the frame contains a person
    /// (e.g. "Jane Doe" / "Head Prefect"). Empty name = no caption.
    /// </summary>
    public string CaptionName { get; set; } = string.Empty;
    public string CaptionTitle { get; set; } = string.Empty;

    /// <summary>
    /// The camera file this item was converted from (e.g. an .mts or .cr3);
    /// empty when FilePath is the original. FilePath is always the playable copy.
    /// </summary>
    public string OriginalPath { get; set; } = string.Empty;

    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];

    /// <summary>Still pictures are shown as full-frame image scenes instead of video.</summary>
    public bool IsImage => ImageExtensions.Contains(Path.GetExtension(FilePath).ToLowerInvariant());

    public string KindLabel => IsImage ? "PICTURE" : "VIDEO";

    public string FormattedDuration
    {
        get
        {
            var ts = TimeSpan.FromSeconds(DurationSeconds);
            return ts.Hours > 0 
                ? ts.ToString(@"hh\:mm\:ss") 
                : ts.ToString(@"mm\:ss");
        }
    }
}
