using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using MessageArchive.Models;
using System.Linq;

namespace MessageArchive.Services;

/// <summary>
/// Parses sms.db to extract contacts and messages.
/// Focuses purely on text and conversational data.
/// </summary>
public partial class SmsParser
{
    private readonly string _smsDbPath;
    
    // URL regex: handles http, https, and www
    [GeneratedRegex(@"https?://[^\s<>""]+|www\.[^\s<>""]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UrlRegex();

    // Naked domains regex: handles example.com, test.net
    [GeneratedRegex(@"\b[a-zA-Z0-9.-]+\.(?:com|net|org|edu|gov|io|co|in|me)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex NakedUrlRegex();
    
    public SmsParser(string smsDbPath, ManifestMapper _) // Keep parameter for compatibility but manifest mapper is no longer used for text
    {
        _smsDbPath = smsDbPath;
    }
    
    public async Task<List<Contact>> GetContactsAsync()
    {
        var contacts = new List<Contact>();
        
        await using var connection = new SqliteConnection($"Data Source={_smsDbPath};Mode=ReadOnly");
        await connection.OpenAsync();
        
        // Query to get handles with message counts
        var query = @"
            SELECT 
                h.ROWID as id,
                h.id as handle,
                COALESCE(h.uncanonicalized_id, h.id) as display,
                COUNT(m.ROWID) as message_count
            FROM handle h
            LEFT JOIN message m ON h.ROWID = m.handle_id
            GROUP BY h.ROWID
            ORDER BY message_count DESC";
        
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = query;
        
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                contacts.Add(new Contact
                {
                    Id = reader.GetInt32(0),
                    Handle = reader.GetString(1),
                    Display = reader.IsDBNull(2) ? reader.GetString(1) : reader.GetString(2),
                    ItemCount = reader.GetInt32(3)
                });
            }
        }
        catch
        {
            // Fallback for different schema versions
            contacts = await GetContactsFallbackAsync(connection);
        }
        
        return contacts;
    }
    
    private async Task<List<Contact>> GetContactsFallbackAsync(SqliteConnection connection)
    {
        var contacts = new List<Contact>();
        
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ROWID, id FROM handle";
        
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            contacts.Add(new Contact
            {
                Id = reader.GetInt32(0),
                Handle = reader.GetString(1),
                Display = reader.GetString(1),
                ItemCount = 0
            });
        }
        
        return contacts;
    }
    
    public async Task<List<MessageItem>> GetMessagesAsync(IProgress<string>? progress = null)
    {
        var messages = new List<MessageItem>();
        
        await using var connection = new SqliteConnection($"Data Source={_smsDbPath};Mode=ReadOnly");
        await connection.OpenAsync();
        
        progress?.Report("Reading messages from database...");
        
        var query = @"
            SELECT 
                m.ROWID,
                m.handle_id,
                m.text,
                m.unix_ts,
                m.is_from_me,
                m.service,
                m.guid,
                m.attributedBody
            FROM (
                SELECT 
                    ROWID, handle_id, text, is_from_me, service, guid, attributedBody,
                    CASE 
                        WHEN LENGTH(CAST(date AS TEXT)) <= 10 THEN date + 978307200
                        ELSE date / 1000000000 + 978307200 
                    END as unix_ts
                FROM message
            ) m";
        
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = query;
        
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            var count = 0;
            
            while (await reader.ReadAsync())
            {
                var msgText = reader.IsDBNull(2) ? null : reader.GetString(2);
                
                // If text is null, try recovering from attributedBody blob
                if (string.IsNullOrEmpty(msgText) && !reader.IsDBNull(7))
                {
                    msgText = RecoverTextFromBlob(reader.GetFieldValue<byte[]>(7));
                }

                // Check if we have links to show as fallback
                var displayLinks = new List<string>();
                if (string.IsNullOrEmpty(msgText))
                {
                    // Scan attributedBody (index 7) for URLs to show as text
                    if (!reader.IsDBNull(7))
                    {
                        var blobStr = Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(7));
                        foreach (Match match in UrlRegex().Matches(blobStr)) displayLinks.Add(match.Value);
                    }
                    
                    if (displayLinks.Any()) msgText = displayLinks.First();
                }

                messages.Add(new MessageItem
                {
                    Id = reader.GetInt32(0),
                    ContactId = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    Text = msgText ?? "[Attachment]",
                    Timestamp = reader.GetInt64(3),
                    IsFromMe = reader.GetInt32(4) != 0,
                    Service = reader.IsDBNull(5) ? "iMessage" : reader.GetString(5),
                    Guid = reader.IsDBNull(6) ? null : reader.GetString(6)
                });
                
                count++;
                if (count % 10000 == 0)
                {
                    progress?.Report($"Processed {count} messages...");
                }
            }
        }
        catch (Exception ex)
        {
            progress?.Report($"Error reading messages: {ex.Message}");
        }
        
        return messages;
    }
    
    public async Task<List<LinkItem>> ExtractLinksAsync(IProgress<string>? progress = null)
    {
        var links = new List<LinkItem>();
        
        await using var connection = new SqliteConnection($"Data Source={_smsDbPath};Mode=ReadOnly");
        await connection.OpenAsync();
        
        progress?.Report("Extracting links from messages...");
        
        var query = @"
            SELECT 
                m.ROWID,
                m.handle_id,
                m.text,
                m.unix_ts,
                m.attributedBody,
                m.payload_data
            FROM (
                SELECT 
                    ROWID, handle_id, text, attributedBody, payload_data,
                    CASE 
                        WHEN LENGTH(CAST(date AS TEXT)) <= 10 THEN date + 978307200
                        ELSE date / 1000000000 + 978307200 
                    END as unix_ts
                FROM message
            ) m";
        
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = query;
        
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            var regex = UrlRegex();
            
            while (await reader.ReadAsync())
            {
                var text = reader.IsDBNull(2) ? null : reader.GetString(2);
                var handleId = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                var timestamp = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                
                var foundUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 1. Process regular text
                if (!string.IsNullOrEmpty(text))
                {
                    foreach (Match match in regex.Matches(text)) foundUrls.Add(match.Value);
                }

                // 2. Process attributedBody/payload_data blobs
                void ScanBlob(int index)
                {
                    if (reader.IsDBNull(index)) return;
                    var bytes = reader.GetFieldValue<byte[]>(index);
                    var blobStr = Encoding.UTF8.GetString(bytes);
                    foreach (Match match in regex.Matches(blobStr)) foundUrls.Add(CleanExtractedUrl(match.Value));
                    // Also check for naked domains in blobs
                    foreach (Match match in NakedUrlRegex().Matches(blobStr)) foundUrls.Add(CleanExtractedUrl(match.Value));
                }

                ScanBlob(4); // attributedBody
                ScanBlob(5); // payload_data
                
                foreach (var url in foundUrls)
                {
                    var cleanUrl = CleanExtractedUrl(url);
                    if (string.IsNullOrWhiteSpace(cleanUrl)) continue;
                    
                    var domain = ExtractDomain(cleanUrl);
                    var category = CategorizeUrl(domain);
                    
                    links.Add(new LinkItem
                    {
                        ContactId = handleId,
                        Url = cleanUrl,
                        Domain = domain,
                        Category = category,
                        Timestamp = timestamp,
                        Type = "Link"
                    });
                }
            }
        }
        catch { }
        
        progress?.Report($"Found {links.Count} items (links/entities)");
        return links;
    }

    private static string CategorizeUrl(string domain)
    {
        if (domain.Contains("youtube.com") || domain.Contains("youtu.be")) return "YouTube";
        if (domain.Contains("spotify.com")) return "Spotify";
        if (domain.Contains("instagram.com")) return "Instagram";
        if (domain.Contains("twitter.com") || domain.Contains("x.com")) return "Twitter";
        if (domain.Contains("reddit.com")) return "Reddit";
        return "Other";
    }

    private static void ExtractEntities(string text, int contactId, long timestamp, List<LinkItem> links)
    {
        // Phone numbers
        var phones = Regex.Matches(text, @"\b(\+?\d{1,3}[-.\s]?)?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b");
        foreach (Match m in phones)
            links.Add(new LinkItem { ContactId = contactId, Url = m.Value, Domain = "Phone", Category = "Extracted", Timestamp = timestamp, Type = "Phones" });
    }
    
    /// <summary>
    /// Cleans extracted URLs from iMessage serialization garbage/binary prefixes.
    /// Strips query params with garbage and truncates URLs to max 5 path segments.
    /// </summary>
    private static string CleanExtractedUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        
        var cleaned = url;
        
        // Strip leading non-URL characters until we hit 'http' or a valid domain start
        var httpIdx = cleaned.IndexOf("http", StringComparison.OrdinalIgnoreCase);
        if (httpIdx > 0)
        {
            cleaned = cleaned.Substring(httpIdx);
        }
        else if (!cleaned.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            // For naked domains, strip leading garbage chars
            int startIdx = 0;
            for (int i = 0; i < cleaned.Length && i < 10; i++)
            {
                char c = cleaned[i];
                if (char.IsLetter(c) && c < 128)
                {
                    startIdx = i;
                    break;
                }
            }
            if (startIdx > 0) cleaned = cleaned.Substring(startIdx);
        }
        
        // Remove query string if it contains garbage (iMessage serialization markers)
        var queryIdx = cleaned.IndexOf('?');
        if (queryIdx > 0)
        {
            var queryPart = cleaned.Substring(queryIdx);
            // Check for iMessage garbage markers in query string
            if (queryPart.Contains("%EF%BF%BD") || 
                queryPart.Contains("__kIM") || 
                queryPart.Contains("NSNumber") || 
                queryPart.Contains("NSObject") ||
                queryPart.Contains("$classname") ||
                queryPart.Contains("classnameX") ||
                queryPart.Length > 100) // Suspiciously long query strings
            {
                cleaned = cleaned.Substring(0, queryIdx);
            }
        }
        
        // Truncate URL to max 5 path segments (e.g., https://www.instagram.com/reel/C1Pc2stSXuL/)
        // Count slashes: scheme:// = 2 slashes, then domain, then path segments
        int slashCount = 0;
        int truncateIdx = cleaned.Length;
        for (int i = 0; i < cleaned.Length; i++)
        {
            if (cleaned[i] == '/')
            {
                slashCount++;
                if (slashCount == 6) // After 5th slash (0-indexed: scheme=1,2, then domain, then 3,4,5)
                {
                    truncateIdx = i;
                    break;
                }
            }
        }
        if (truncateIdx < cleaned.Length)
        {
            cleaned = cleaned.Substring(0, truncateIdx + 1); // Include the trailing slash
        }
        
        // Remove trailing garbage (non-URL chars)
        int endIdx = cleaned.Length;
        for (int i = cleaned.Length - 1; i >= 0; i--)
        {
            char c = cleaned[i];
            if (char.IsLetterOrDigit(c) || c == '/' || c == '-' || c == '_' || c == '.')
            {
                endIdx = i + 1;
                break;
            }
        }
        if (endIdx < cleaned.Length) cleaned = cleaned.Substring(0, endIdx);
        
        return cleaned;
    }
    
    private static string ExtractDomain(string url)
    {
        try
        {
            var cleanUrl = CleanExtractedUrl(url);
            if (!cleanUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                cleanUrl = "https://" + cleanUrl;
            }
            var uri = new Uri(cleanUrl);
            return uri.Host;
        }
        catch
        {
            return url;
        }
    }

    private static string? RecoverTextFromBlob(byte[] bytes)
    {
        try
        {
            if (bytes.Length < 20) return null;
            
            string raw = Encoding.UTF8.GetString(bytes);
            if (!raw.Contains("streamtyped")) return null;

            int startIdx = -1;
            string[] markers = { "NSString", "NSMutableString" };
            foreach(var m in markers) {
                int idx = raw.LastIndexOf(m);
                if (idx != -1) { startIdx = idx + m.Length; break; }
            }
            if (startIdx == -1) return null;

            int endIdx = raw.Length;
            string[] endMarkers = { "NSDictionary", "NSAttribute", "NSKeyedArchiver", "__kIM" };
            foreach (var em in endMarkers) {
                int eIdx = raw.IndexOf(em, startIdx);
                if (eIdx != -1 && eIdx < endIdx) endIdx = eIdx;
            }

            string segment = raw.Substring(startIdx, endIdx - startIdx);
            
            int firstRealChar = -1;
            for (int i = 0; i < segment.Length; i++)
            {
                char c = segment[i];
                // 'h' covers https
                if (c > 128 || char.IsLetterOrDigit(c) || c == '(' || c == '"' || c == '{' || c == '[' || c == 'h')
                {
                    // Skip Apple serialization type stubs
                    if (c == 0x95 || c == 0x84 || c == 0x01 || c == 0x1F || c == 0x02 || c == 0x03 || c == 0x04) continue;
                    
                    firstRealChar = i;
                    break;
                }
            }
            
            if (firstRealChar == -1) return null;
            string cleaned = segment.Substring(firstRealChar);

            var result = new StringBuilder();
            bool lastWasEmoji = false;
            foreach (var c in cleaned)
            {
                if (c == '\uFFFC' || c == '\uFFFD') continue;
                
                // If we hit a low-ASCII control char after an emoji, it's likely a serialization marker
                // Emojis often end with binary length markers in streamtyped format
                if (lastWasEmoji && c < 32 && c != '\n' && c != '\r' && c != '\t') break;

                if (c < 32 && c != '\n' && c != '\r' && c != '\t') continue;
                
                result.Append(c);
                lastWasEmoji = (c > 128);
            }
            
            var final = result.ToString().Trim();
            
            // Clean specific serialization artifacts often found around emojis/links
            final = Regex.Replace(final, @"^[+\-\d]{1,2}[A-Za-z\d]? ", ""); 
            final = Regex.Replace(final, @"^[+ilI\$@\.]{1,3}(?=[A-Za-z\d\u00A0-\uFFFF])", "");
            
            // Strip trailing "il" or other short binary markers leftover from dictionary start
            final = Regex.Replace(final, @"[+ilI\$@\. ]{1,3}$", "");

            if (final.StartsWith("+") && final.Contains("http")) 
                final = final.Substring(1).TrimStart('1','2','3','4','5','6','7','8','9','0',' ');

            if (final.Contains("__kIM") || final.Contains("NSKeyedArchiver") || final.Length < 1) return null;
            
            return final;
        }
        catch { return null; }
    }
}
