using System.IO;
using Microsoft.Data.Sqlite;

namespace MessageArchive.Services;

/// <summary>
/// Maps logical iOS paths to actual blob paths in the backup folder.
/// iPhone backups store files by fileID (SHA1 hash), optionally sharded into XX/ subdirectories.
/// </summary>
public class ManifestMapper
{
    private readonly string _backupPath;
    private readonly string _manifestDbPath;
    private Dictionary<string, string>? _fileMap;
    
    public ManifestMapper(string backupPath)
    {
        _backupPath = backupPath;
        _manifestDbPath = Path.Combine(backupPath, "Manifest.db");
    }
    
    /// <summary>
    /// Builds the file mapping from Manifest.db
    /// </summary>
    public async Task BuildMapAsync(IProgress<string>? progress = null)
    {
        _fileMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        
        progress?.Report("Reading backup manifest...");
        
        await using var connection = new SqliteConnection($"Data Source={_manifestDbPath};Mode=ReadOnly");
        await connection.OpenAsync();
        
        await using var cmd = connection.CreateCommand();
        // Query ALL files - not just HomeDomain - to catch attachments in MediaDomain etc.
        cmd.CommandText = "SELECT fileID, relativePath, domain FROM Files WHERE relativePath IS NOT NULL";
        
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var fileId = reader.GetString(0);
            var relativePath = reader.IsDBNull(1) ? null : reader.GetString(1);
            var domain = reader.IsDBNull(2) ? "" : reader.GetString(2);
            
            if (string.IsNullOrEmpty(relativePath)) continue;
            
            var absolutePath = ResolveFilePath(fileId);
            if (!string.IsNullOrEmpty(absolutePath))
            {
                // Store with just the relative path
                _fileMap[relativePath] = absolutePath;
                
                // Also store with domain prefix for alternate lookups
                if (!string.IsNullOrEmpty(domain))
                {
                    _fileMap[$"{domain}-{relativePath}"] = absolutePath;
                }
            }
        }
        
        progress?.Report($"Found {_fileMap.Count} files in manifest");
    }
    
    /// <summary>
    /// Gets the absolute path to sms.db blob
    /// </summary>
    public async Task<string?> GetSmsDbPathAsync()
    {
        if (_fileMap == null)
        {
            await BuildMapAsync();
        }
        
        // Try common paths for sms.db
        var possiblePaths = new[]
        {
            "Library/SMS/sms.db",
            "HomeDomain-Library/SMS/sms.db"
        };
        
        foreach (var path in possiblePaths)
        {
            if (_fileMap!.TryGetValue(path, out var absolutePath))
            {
                Console.WriteLine($"[DEBUG] Found sms.db path: {path} -> {absolutePath}");
                return absolutePath;
            }
        }
        
        // Fallback: search by filename
        var smsEntry = _fileMap!.FirstOrDefault(kvp => 
            kvp.Key.EndsWith("sms.db", StringComparison.OrdinalIgnoreCase));
        
        if (!string.IsNullOrEmpty(smsEntry.Value))
        {
            Console.WriteLine($"[DEBUG] Found sms.db via fallback: {smsEntry.Key} -> {smsEntry.Value}");
        }
        else
        {
            Console.WriteLine("[DEBUG] Could not find any sms.db in manifest map.");
        }
        
        return smsEntry.Value;
    }
    
    /// <summary>
    /// Gets the absolute path for a relative iOS path
    /// </summary>
    public string? GetAbsolutePath(string relativePath)
    {
        if (_fileMap == null) return null;
        return _fileMap.TryGetValue(relativePath, out var path) ? path : null;
    }
    
    /// <summary>
    /// Gets the absolute path for a file ID directly
    /// </summary>
    public string? GetPathByFileId(string fileId)
    {
        return ResolveFilePath(fileId);
    }
    
    /// <summary>
    /// Gets all attachment paths from the manifest
    /// Returns the full map since attachments can be in various locations
    /// </summary>
    public async Task<Dictionary<string, string>> GetAttachmentPathsAsync()
    {
        if (_fileMap == null)
        {
            await BuildMapAsync();
        }
        
        // Return the full map - attachments can be in various places
        // The SmsParser will filter by what it finds in attachment table
        return new Dictionary<string, string>(_fileMap!, StringComparer.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Resolves a fileID to its absolute path in the backup.
    /// Handles both flat layout and sharded (XX/fileID) layout.
    /// </summary>
    private string? ResolveFilePath(string fileId)
    {
        if (string.IsNullOrEmpty(fileId)) return null;
        
        // Try sharded layout first (most common): XX/fileID where XX is first 2 chars
        if (fileId.Length >= 2)
        {
            var shardedPath = Path.Combine(_backupPath, fileId[..2], fileId);
            if (File.Exists(shardedPath))
            {
                return shardedPath;
            }
        }
        
        // Try flat layout
        var flatPath = Path.Combine(_backupPath, fileId);
        if (File.Exists(flatPath))
        {
            return flatPath;
        }
        
        return null;
    }
}
