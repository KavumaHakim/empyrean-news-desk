using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NewsDesk.Core.Models;

/// <summary>
/// One sound-effect pad: a named audio file (or a video's soundtrack) fired on air
/// from its button or its hotkey (Ctrl+Slot).
/// </summary>
public class SoundPad : INotifyPropertyChanged
{
    public const int Count = 8;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>1–8; also the Ctrl+number hotkey.</summary>
    public int Slot { get; init; }

    public string Hotkey => $"Ctrl+{Slot}";

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) Raise(nameof(Label)); }
    }

    private string _filePath = string.Empty;
    public string FilePath
    {
        get => _filePath;
        set
        {
            if (!Set(ref _filePath, value)) return;
            Raise(nameof(HasSound));
            Raise(nameof(FileLabel));
            Raise(nameof(Label));
        }
    }

    private double _volume = 1.0;
    public double Volume { get => _volume; set => Set(ref _volume, Math.Clamp(value, 0, 1)); }

    private bool _isPlaying;
    /// <summary>True while the sound is playing on air.</summary>
    public bool IsPlaying { get => _isPlaying; set => Set(ref _isPlaying, value); }

    public bool HasSound => !string.IsNullOrWhiteSpace(FilePath);

    public string FileLabel => HasSound ? Path.GetFileName(FilePath) : "No sound loaded";

    /// <summary>Button text: the pad's name, else the file name, else "Pad n".</summary>
    public string Label => !string.IsNullOrWhiteSpace(Name) ? Name
        : HasSound ? Path.GetFileNameWithoutExtension(FilePath)
        : $"Pad {Slot}";

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
