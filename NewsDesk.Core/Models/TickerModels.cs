using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NewsDesk.Core.Models;

public class TickerItem
{
    public int Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Category { get; set; } = "NEWS";
    public int OrderIndex { get; set; }
    public bool IsActive { get; set; } = true;
}

public class TickerConfig : INotifyPropertyChanged
{
    private bool _isVisible = true;
    private bool _isPaused;
    private int _ribbonHeight = 61;
    private int _fontSize = 26;
    private string _textColor = "#FFFFFF";
    private string _backgroundColor = "#0E1A2F";
    private bool _isBreakingStyle;
    private int _speedPixelsPerSecond = 90;
    private string _separator = "|";
    private string _headerTag = "EMPYREAN NEWS";

    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }
    public bool IsPaused { get => _isPaused; set => Set(ref _isPaused, value); }
    public int RibbonHeight { get => _ribbonHeight; set => Set(ref _ribbonHeight, value); }
    public int FontSize { get => _fontSize; set => Set(ref _fontSize, value); }
    public string TextColor { get => _textColor; set => Set(ref _textColor, value); }
    public string BackgroundColor { get => _backgroundColor; set => Set(ref _backgroundColor, value); }
    public bool IsBreakingStyle { get => _isBreakingStyle; set => Set(ref _isBreakingStyle, value); }
    public int SpeedPixelsPerSecond { get => _speedPixelsPerSecond; set => Set(ref _speedPixelsPerSecond, value); }
    public string Separator { get => _separator; set => Set(ref _separator, value); }
    public string HeaderTag { get => _headerTag; set => Set(ref _headerTag, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public TickerConfig Clone() => new()
    {
        IsVisible = IsVisible,
        IsPaused = IsPaused,
        RibbonHeight = RibbonHeight,
        FontSize = FontSize,
        TextColor = TextColor,
        BackgroundColor = BackgroundColor,
        IsBreakingStyle = IsBreakingStyle,
        SpeedPixelsPerSecond = SpeedPixelsPerSecond,
        Separator = Separator,
        HeaderTag = HeaderTag
    };
}
