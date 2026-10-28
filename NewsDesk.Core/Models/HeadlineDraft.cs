namespace NewsDesk.Core.Models;

public enum GraphicsTemplateType
{
    MainHeadline,
    BreakingNews,
    TopStory,
    NewsUpdate,
    FullScreenSlide,
    ImageHeadline,
    ReporterLowerThird,
    IntervieweeLowerThird
}

public class HeadlineDraft
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Category { get; set; } = StandardCategories.NationalNews;
    public string Location { get; set; } = string.Empty;
    public string ReporterName { get; set; } = string.Empty;
    public string ReporterTitle { get; set; } = string.Empty;
    public GraphicsTemplateType TemplateType { get; set; } = GraphicsTemplateType.MainHeadline;
    public string BackgroundStyle { get; set; } = "navy"; // navy, breaking, slate
    public string ImagePath { get; set; } = string.Empty;
    public bool IsBreaking { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
