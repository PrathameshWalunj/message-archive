namespace MessageArchive.Models;

public class MessageItem
{
    public int Id { get; set; }
    public int ContactId { get; set; }
    public string Text { get; set; } = string.Empty;
    public long Timestamp { get; set; }
    public bool IsFromMe { get; set; }
    public string Service { get; set; } = string.Empty; // iMessage or SMS
    public string? Guid { get; set; }
    
    
    public string DisplayTime => DateTimeOffset.FromUnixTimeSeconds(Timestamp).LocalDateTime.ToString("g");
    public string Alignment => IsFromMe ? "Right" : "Left";
    public string BubbleColor => IsFromMe ? "#007AFF" : "#E9E9EB";
    public string TextColor => IsFromMe ? "White" : "Black";
}
