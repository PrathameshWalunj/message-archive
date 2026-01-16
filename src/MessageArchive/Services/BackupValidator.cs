using System.IO;
using Microsoft.Data.Sqlite;

namespace MessageArchive.Services;

public class BackupInfo
{
    public string Path { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public bool IsEncrypted { get; set; }
    public string? ErrorMessage { get; set; }
    public int ConversationCount { get; set; }
    public int AttachmentCount { get; set; }
}

public class BackupValidator
{
    private const string ManifestDbName = "Manifest.db";
    private const string SmsDbRelativePath = "Library/SMS/sms.db";
    
    public async Task<BackupInfo> ValidateBackupAsync(string backupPath)
    {
        var info = new BackupInfo { Path = backupPath };
        
        // Check if folder exists
        if (!Directory.Exists(backupPath))
        {
            info.IsValid = false;
            info.ErrorMessage = "The specified folder does not exist.";
            return info;
        }
        
        // Check for Manifest.db
        var manifestPath = Path.Combine(backupPath, ManifestDbName);
        if (!File.Exists(manifestPath))
        {
            info.IsValid = false;
            info.ErrorMessage = "Manifest.db was not found. The selected folder is not a valid iPhone backup.";
            return info;
        }
        
        try
        {
            // Try to open Manifest.db and locate sms.db
            var manifestMapper = new ManifestMapper(backupPath);
            var smsDbPath = await manifestMapper.GetSmsDbPathAsync();
            
            if (string.IsNullOrEmpty(smsDbPath) || !File.Exists(smsDbPath))
            {
                info.IsValid = false;
                info.ErrorMessage = "Messages database not found. This backup may be incomplete.";
                return info;
            }
            
            // Check if sms.db is readable (not encrypted)
            if (!await IsSqliteReadableAsync(smsDbPath))
            {
                info.IsValid = false;
                info.IsEncrypted = true;
                info.ErrorMessage = "This backup is encrypted. Encrypted backups are not supported.";
                return info;
            }
            
            // Get counts for display
            var (conversations, attachments) = await GetCountsAsync(smsDbPath);
            info.ConversationCount = conversations;
            info.AttachmentCount = attachments;
            info.IsValid = true;
        }
        catch (Exception ex)
        {
            info.IsValid = false;
            info.ErrorMessage = $"An error occurred while reading the backup: {ex.Message}";
        }
        
        return info;
    }
    
    private async Task<bool> IsSqliteReadableAsync(string dbPath)
    {
        try
        {
            Console.WriteLine($"[DEBUG] Checking if SQLite is readable: {dbPath}");
            if (!File.Exists(dbPath))
            {
                Console.WriteLine($"[DEBUG] File does not exist: {dbPath}");
                return false;
            }

            // Read first 16 bytes to check SQLite header
            var header = new byte[16];
            await using (var fs = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var bytesRead = await fs.ReadAsync(header, 0, 16);
                if (bytesRead < 16) 
                {
                    Console.WriteLine($"[DEBUG] Could only read {bytesRead} bytes from {dbPath}");
                    return false;
                }
            }
            
            // SQLite file header starts with "SQLite format 3\0"
            var headerString = System.Text.Encoding.ASCII.GetString(header);
            bool isReadable = headerString.StartsWith("SQLite format 3");
            
            if (!isReadable)
            {
                Console.WriteLine($"[DEBUG] Header mismatch! Found: '{headerString[..Math.Min(headerString.Length, 15)]}'");
            }
            
            return isReadable;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] Error reading SQLite header: {ex.Message}");
            return false;
        }
    }
    
    private async Task<(int conversations, int attachments)> GetCountsAsync(string smsDbPath)
    {
        try
        {
            await using var connection = new SqliteConnection($"Data Source={smsDbPath};Mode=ReadOnly");
            await connection.OpenAsync();
            
            // Count unique handles (contacts)
            await using var handleCmd = connection.CreateCommand();
            handleCmd.CommandText = "SELECT COUNT(DISTINCT id) FROM handle";
            var conversations = Convert.ToInt32(await handleCmd.ExecuteScalarAsync() ?? 0);
            
            // Count attachments
            await using var attachCmd = connection.CreateCommand();
            attachCmd.CommandText = "SELECT COUNT(*) FROM attachment";
            var attachments = Convert.ToInt32(await attachCmd.ExecuteScalarAsync() ?? 0);
            
            return (conversations, attachments);
        }
        catch
        {
            return (0, 0);
        }
    }
}
