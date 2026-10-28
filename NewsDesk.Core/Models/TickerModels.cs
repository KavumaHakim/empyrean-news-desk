namespace NewsDesk.Core.Models;

public class TickerItem
{
    public int Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Category { get; set; } = "NEWS";
    public int OrderIndex { get; set; }
    public bool IsActive { get; set; } = true;
}

public class TickerConfig
{
    public bool IsVisible { get; set; } = true;
    public bool IsPaused { get; set; } = false;
    public int RibbonHeight { get; set; } = 48; // px
    public int FontSize { get; set; } = 20; // px
    public string TextColor { get; set; } = "#FFFFFF";
    public string BackgroundColor { get; set; } = "#0D1B2A";
    public bool IsBreakingStyle { get; set; } = false;
    public int SpeedPixelsPerSecond { get; set; } = 90; // smooth news crawl speed
    public string Separator { get; set; } = "●";
    public string HeaderTag { get; set; } = "HEADLINES";

    public TickerConfig Clone() => new()
    {
        IsVisible = this.IsVisible,
        IsPaused = this.IsPaused,
        RibbonHeight = this.RibbonHeight,
        FontSize = this.FontSize,
        TextColor = this.TextColor,
        BackgroundColor = this.BackgroundColor,
        IsBreakingStyle = this.IsBreakingStyle,
        SpeedPixelsPerSecond = this.SpeedPixelsPerSecond,
        Separator = this.Separator,
        HeaderTag = this.HeaderTag
    };
}
