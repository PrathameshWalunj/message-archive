using CommunityToolkit.Mvvm.ComponentModel;

namespace MessageArchive.Models;

public partial class LinkItem : ObservableObject
{
    public int Id { get; set; }
    public int ContactId { get; set; }
    public long Timestamp { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Category { get; set; } = "Other";
    public string Type { get; set; } = "Link"; // Link, Phone, Code, Address
    
    public DateTime Date => DateTimeOffset.FromUnixTimeSeconds(Timestamp).LocalDateTime;
    
    [ObservableProperty]
    private bool _isSelected;
}
