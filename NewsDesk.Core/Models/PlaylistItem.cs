namespace NewsDesk.Core.Models;

public class PlaylistItem
{
    public int Id { get; set; }
    public int PlaylistId { get; set; }
    public int MediaItemId { get; set; }
    public int OrderIndex { get; set; }
    public MediaItem? MediaItem { get; set; }
}

public class Playlist
{
    public int Id { get; set; }
    public string Name { get; set; } = "Broadcast Rundown";
    public List<PlaylistItem> Items { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
