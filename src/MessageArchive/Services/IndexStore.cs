using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using MessageArchive.Models;

namespace MessageArchive.Services;

/// <summary>
/// Manages our local index database
/// </summary>
public class IndexStore : IDisposable
{
    private readonly string _dbPath;
    private SqliteConnection? _connection;
    
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MessageArchive", "vault.db");
    
    public IndexStore(string? dbPath = null)
    {
        _dbPath = dbPath ?? DefaultPath;
        
        // Ensure directory exists
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }
    
    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        await _connection!.OpenAsync();
        
       
        await EnsureFreshSchemaAsync();
        
        await CreateSchemaAsync();
    }

    private async Task EnsureFreshSchemaAsync()
    {
        try
        {
            // Check if contacts table exists and has message_count
            var contactColumns = (await _connection!.QueryAsync("PRAGMA table_info(contacts)")).ToList();
            bool needsRebuild = false;

            if (contactColumns.Any())
            {
                if (!contactColumns.Any(c => (string)((dynamic)c).name == "message_count"))
                    needsRebuild = true;
            }

            // Check if links table exists and has category
            var linkColumns = (await _connection!.QueryAsync("PRAGMA table_info(links)")).ToList();
            if (linkColumns.Any())
            {
                if (!linkColumns.Any(c => (string)((dynamic)c).name == "category"))
                    needsRebuild = true;
            }

            if (needsRebuild)
            {
                // Legacy schema detected, drop all tables to rebuild
                await _connection!.ExecuteAsync("DROP TABLE IF EXISTS contacts; DROP TABLE IF EXISTS messages; DROP TABLE IF EXISTS items; DROP TABLE IF EXISTS links; DROP TABLE IF EXISTS meta;");
            }
        }
        catch
        {
            // Ignore if tables don't exist yet
        }
    }
    
    private async Task CreateSchemaAsync()
    {
        var schema = @"
            CREATE TABLE IF NOT EXISTS meta (
                key TEXT PRIMARY KEY,
                value TEXT
            );
            
            CREATE TABLE IF NOT EXISTS contacts (
                id INTEGER PRIMARY KEY,
                display TEXT,
                handle TEXT,
                message_count INTEGER DEFAULT 0
            );
            
            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER PRIMARY KEY,
                contact_id INTEGER,
                text TEXT,
                ts INTEGER,
                is_from_me INTEGER DEFAULT 0,
                service TEXT,
                guid TEXT
            );
            
            CREATE TABLE IF NOT EXISTS links (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                contact_id INTEGER,
                ts INTEGER,
                url TEXT,
                domain TEXT,
                category TEXT,
                type TEXT
            );
            
            CREATE INDEX IF NOT EXISTS idx_messages_contact ON messages(contact_id, ts ASC);
            CREATE INDEX IF NOT EXISTS idx_links_contact ON links(contact_id, ts DESC);
        ";
        
        await _connection!.ExecuteAsync(schema);
    }
    
    public async Task ClearAllAsync()
    {
        await _connection!.ExecuteAsync("DELETE FROM contacts; DELETE FROM messages; DELETE FROM links; DELETE FROM meta;");
    }
    
    public async Task SetMetaAsync(string key, string value)
    {
        await _connection!.ExecuteAsync(
            "INSERT OR REPLACE INTO meta (key, value) VALUES (@key, @value)",
            new { key, value });
    }
    
    public async Task<string?> GetMetaAsync(string key)
    {
        return await _connection!.QueryFirstOrDefaultAsync<string>(
            "SELECT value FROM meta WHERE key = @key", new { key });
    }
    
    public async Task InsertContactsAsync(IEnumerable<Contact> contacts)
    {
        foreach (var contact in contacts)
        {
            await _connection!.ExecuteAsync(
                "INSERT OR REPLACE INTO contacts (id, display, handle, message_count) VALUES (@Id, @Display, @Handle, @ItemCount)",
                contact);
        }
    }
    
    public async Task InsertMessagesAsync(IEnumerable<MessageItem> messages)
    {
        const int batchSize = 1000;
        var batch = new List<MessageItem>(batchSize);
        
        foreach (var msg in messages)
        {
            batch.Add(msg);
            if (batch.Count >= batchSize)
            {
                await InsertMessagesBatchAsync(batch);
                batch.Clear();
            }
        }
        
        if (batch.Count > 0)
        {
            await InsertMessagesBatchAsync(batch);
        }
    }
    
    private async Task InsertMessagesBatchAsync(List<MessageItem> messages)
    {
        using var transaction = _connection!.BeginTransaction();
        
        foreach (var msg in messages)
        {
            await _connection!.ExecuteAsync(@"
                INSERT OR REPLACE INTO messages (id, contact_id, text, ts, is_from_me, service, guid)
                VALUES (@Id, @ContactId, @Text, @Timestamp, @IsFromMe, @Service, @Guid)",
                new
                {
                    msg.Id,
                    msg.ContactId,
                    msg.Text,
                    msg.Timestamp,
                    IsFromMe = msg.IsFromMe ? 1 : 0,
                    msg.Service,
                    msg.Guid
                });
        }
        
        transaction.Commit();
    }
    
    public async Task InsertLinksAsync(IEnumerable<LinkItem> links)
    {
        using var transaction = _connection!.BeginTransaction();
        
        foreach (var link in links)
        {
            await _connection!.ExecuteAsync(
                "INSERT INTO links (contact_id, ts, url, domain, category, type) VALUES (@ContactId, @Timestamp, @Url, @Domain, @Category, @Type)",
                link);
        }
        
        transaction.Commit();
    }
    
    public async Task<List<Contact>> GetContactsAsync()
    {
        var contacts = await _connection!.QueryAsync<Contact>(
            "SELECT id as Id, display as Display, handle as Handle, message_count as ItemCount FROM contacts ORDER BY message_count DESC");
        return contacts.ToList();
    }
    
    public async Task<List<MessageItem>> GetMessagesAsync(int contactId, int page = 0, int pageSize = 100)
    {
        var offset = page * pageSize;
        
        var messages = await _connection!.QueryAsync<MessageItem>(@"
            SELECT id as Id, contact_id as ContactId, text as Text, ts as Timestamp, 
                   is_from_me as IsFromMe, service as Service, guid as Guid
            FROM messages
            WHERE contact_id = @contactId
            ORDER BY ts ASC
            LIMIT @pageSize OFFSET @offset",
            new { contactId, pageSize, offset });
        
        return messages.ToList();
    }
    
    public async Task<int> GetMessageCountAsync(int contactId, bool includeSent = true)
    {
        var sql = "SELECT COUNT(*) FROM messages WHERE contact_id = @contactId";
        if (!includeSent) sql += " AND is_from_me = 0";
            
        return await _connection!.QueryFirstOrDefaultAsync<int>(sql, new { contactId });
    }
    
    public async Task<List<LinkItem>> GetLinksAsync(int contactId, int page = 0, int pageSize = 50)
    {
        
        var allLinks = await _connection!.QueryAsync<LinkItem>(@"
            SELECT id as Id, contact_id as ContactId, ts as Timestamp, url as Url, domain as Domain, category as Category, type as Type
            FROM links
            WHERE contact_id = @contactId
            ORDER BY ts DESC",
            new { contactId });
        
        
        string NormalizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            int slashCount = 0;
            for (int i = 0; i < url.Length; i++)
            {
                if (url[i] == '/')
                {
                    slashCount++;
                    if (slashCount == 5)
                    {
                        return url.Substring(0, i + 1);
                    }
                }
            }
            return url; 
        }
        
        
        var deduplicated = allLinks
            .GroupBy(l => NormalizeUrl(l.Url))
            .Select(g => g.First()) 
            .Skip(page * pageSize)
            .Take(pageSize)
            .ToList();
        
        return deduplicated;
    }
    
    public async Task<int> GetLinkCountAsync(int contactId)
    {
        return await _connection!.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM links WHERE contact_id = @contactId",
            new { contactId });
    }
    
    public async Task UpdateContactCountsAsync()
    {
        
        await _connection!.ExecuteAsync(@"
            UPDATE contacts SET message_count = (
                SELECT COUNT(*) FROM messages WHERE messages.contact_id = contacts.id
            ) + (
                SELECT COUNT(*) FROM links WHERE links.contact_id = contacts.id
            )");
    }
    
    public async Task<List<MessageItem>> GetMessagesForAnalyticsAsync(int contactId)
    {
        var messages = await _connection!.QueryAsync<MessageItem>(@"
            SELECT id as Id, contact_id as ContactId, text as Text, ts as Timestamp, 
                   is_from_me as IsFromMe, service as Service, guid as Guid
            FROM messages
            WHERE contact_id = @contactId
            ORDER BY ts ASC",
            new { contactId });
        
        return messages.ToList();
    }
    
    public async Task<List<MessageItem>> SearchMessagesAsync(string query)
    {
        var messages = await _connection!.QueryAsync<MessageItem>(@"
            SELECT id as Id, contact_id as ContactId, text as Text, ts as Timestamp, 
                   is_from_me as IsFromMe, service as Service, guid as Guid
            FROM messages
            WHERE text LIKE @query
            ORDER BY ts DESC
            LIMIT 100",
            new { query = $"%{query}%" });
        
        return messages.ToList();
    }
    
    public void Dispose()
    {
        _connection?.Dispose();
    }
}
