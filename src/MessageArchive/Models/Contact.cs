namespace MessageArchive.Models;

public class Contact
{
    public int Id { get; set; }
    public string Display { get; set; } = string.Empty;
    public string Handle { get; set; } = string.Empty;
    public int ItemCount { get; set; }
    
    /// <summary>
    /// Returns masked phone number like +1 (***) ***-0137
    /// </summary>
    public string MaskedHandle
    {
        get
        {
            if (string.IsNullOrEmpty(Handle)) return "Unknown";
            if (Handle.Length <= 4) return Handle;
            
            // Show last 4 characters, mask the rest
            var visible = Handle[^4..];
            var masked = new string('*', Handle.Length - 4);
            return masked + visible;
        }
    }
    
    public string DisplayWithCount => $"{Display} ({ItemCount})";
}
