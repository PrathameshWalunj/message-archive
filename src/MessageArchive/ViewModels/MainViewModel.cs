using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MessageArchive.Models;
using MessageArchive.Services;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using System.Linq;

namespace MessageArchive.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly BackupValidator _validator = new();
    private IndexStore? _indexStore;
    private ExportService? _exportService;
    private ManifestMapper? _manifestMapper;
    
    [ObservableProperty]
    private bool _isWelcomeVisible = true;
    
    [ObservableProperty]
    private bool _isGalleryVisible = false;
    
    [ObservableProperty]
    private bool _isAnalyticsVisible = false;

    [ObservableProperty]
    private bool _isLinksVisible = false;
    
    [ObservableProperty]
    private ChatAnalytics? _currentAnalytics;

    [ObservableProperty]
    private string _currentLinksCategory = "All";

    partial void OnCurrentLinksCategoryChanged(string value)
    {
        _ = LoadFilteredLinksAsync();
    }

    [ObservableProperty]
    private bool _isLoading = false;
    
    [ObservableProperty]
    private string _loadingMessage = string.Empty;
    
    [ObservableProperty]
    private string _backupPath = string.Empty;
    
    [ObservableProperty]
    private string _statusMessage = string.Empty;
    
    [ObservableProperty]
    private ObservableCollection<Contact> _contacts = new();
    
    [ObservableProperty]
    private Contact? _selectedContact;
    
    [ObservableProperty]
    private string _lastExceptionDetails = string.Empty;
    
    [ObservableProperty]
    private string _contactSearchText = string.Empty;
    
    [ObservableProperty]
    private string _messageSearchText = string.Empty;
    
    [ObservableProperty]
    private ObservableCollection<MessageItem> _currentMessages = new();
    
    [ObservableProperty]
    private int _currentMatchIndex = 0;
    
    [ObservableProperty]
    private int _totalMatches = 0;
    
    [ObservableProperty]
    private MessageItem? _highlightedMessage;
    
    private List<int> _matchIndices = new();
    
    [ObservableProperty]
    private ObservableCollection<LinkItem> _currentLinks = new();
    
    private List<Contact> _allContacts = new();
    
    partial void OnContactSearchTextChanged(string value)
    {
        FilterContacts();
    }
    
    [ObservableProperty]
    private bool _includeSentItems = true;
    
    partial void OnIncludeSentItemsChanged(bool value)
    {
        if (SelectedContact != null)
        {
            _ = LoadContactMessagesAsync(SelectedContact);
        }
    }

    partial void OnSelectedContactChanged(Contact? value)
    {
        if (value != null)
        {
            _ = LoadContactDataAsync(value);
        }
    }

    [RelayCommand]
    private void ShowChat() { IsGalleryVisible = true; IsAnalyticsVisible = false; IsLinksVisible = false; }

    [RelayCommand]
    private void ShowAnalytics() { IsGalleryVisible = false; IsAnalyticsVisible = true; IsLinksVisible = false; }

    [RelayCommand]
    private void ShowLinks() { IsGalleryVisible = false; IsAnalyticsVisible = false; IsLinksVisible = true; }
    
    [RelayCommand]
    private async Task SelectBackupFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select iPhone Backup Folder (OFFLINE)",
            Multiselect = false
        };
        
        if (dialog.ShowDialog() == true)
        {
            await ScanBackupAsync(dialog.FolderName);
        }
    }
    
    [RelayCommand]
    private async Task ScanBackupAsync(string path)
    {
        IsLoading = true;
        LoadingMessage = "Validating backup folder...";
        await Task.Delay(50);
        
        try
        {
            var info = await Task.Run(async () => await _validator.ValidateBackupAsync(path));
            
            if (!info.IsValid)
            {
                StatusMessage = info.ErrorMessage ?? "Backup error";
                LastExceptionDetails = $"Path: {path}\nError: {info.ErrorMessage}";
                IsLoading = false;
                return;
            }
            
            BackupPath = path;
            
            _indexStore?.Dispose();
            _indexStore = new IndexStore();
            await _indexStore.InitializeAsync();
            await _indexStore.ClearAllAsync();
            
            _manifestMapper = new ManifestMapper(path);
            var progress = new Progress<string>(msg => LoadingMessage = msg);
            
            var smsDbPath = await _manifestMapper.GetSmsDbPathAsync();
            if (string.IsNullOrEmpty(smsDbPath))
            {
                StatusMessage = "messagesForStats database (sms.db) not found.";
                IsLoading = false;
                return;
            }
            
            var smsParser = new SmsParser(smsDbPath, _manifestMapper);
            
            LoadingMessage = "Extracting contacts...";
            var contacts = await Task.Run(async () => await smsParser.GetContactsAsync());
            await _indexStore.InsertContactsAsync(contacts);
            
            LoadingMessage = "Indexing all messagesForStats...";
            var messagesForStats = await Task.Run(async () => await smsParser.GetMessagesAsync(progress));
            await _indexStore.InsertMessagesAsync(messagesForStats);
            
            LoadingMessage = "Extracting shared links...";
            var links = await Task.Run(async () => await smsParser.ExtractLinksAsync(progress));
            await _indexStore.InsertLinksAsync(links);
            
            await _indexStore.UpdateContactCountsAsync();
            await _indexStore.SetMetaAsync("backup_path", path);
            
            _exportService = new ExportService(_indexStore);
            _allContacts = await _indexStore.GetContactsAsync();
            FilterContacts();
            
            IsWelcomeVisible = false;
            IsGalleryVisible = true;
            StatusMessage = "Ready";
            
            if (Contacts.Count > 0) SelectedContact = Contacts[0];
        }
        catch (Exception ex)
        {
            StatusMessage = "An error occurred during scanning. Click 'Copy Details' for technical info.";
            LastExceptionDetails = ex.ToString();
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void CopyErrorDetails()
    {
        if (!string.IsNullOrEmpty(LastExceptionDetails))
        {
            System.Windows.Clipboard.SetText(LastExceptionDetails);
            StatusMessage = "Tech details copied to clipboard.";
        }
    }
    
    private void FilterContacts()
    {
        Contacts.Clear();
        var baseFilter = _allContacts.Where(c => c.ItemCount > 0);
        
        var filtered = string.IsNullOrWhiteSpace(ContactSearchText)
            ? baseFilter
            : baseFilter.Where(c => 
                c.Display.Contains(ContactSearchText, StringComparison.OrdinalIgnoreCase) ||
                c.Handle.Contains(ContactSearchText, StringComparison.OrdinalIgnoreCase));

        // Prioritize genuine contacts (phone numbers/emails) over shortcodes/promotional IDs
        // Genuine = handles length > 8 or containing + sign or @
        var sorted = filtered
            .OrderByDescending(c => IsGenuineContact(c.Handle))
            .ThenByDescending(c => c.ItemCount);
        
        foreach (var contact in sorted)
        {
            Contacts.Add(contact);
        }
    }

    private static bool IsGenuineContact(string handle)
    {
        if (string.IsNullOrEmpty(handle)) return false;
        if (handle.Contains("@")) return true; // Email is genuine
        if (handle.StartsWith("+")) return true; // Int'l phone is genuine
        
        // Clean up digits
        var digitsOnly = new string(handle.Where(char.IsDigit).ToArray());
        return digitsOnly.Length >= 10; // Genuine local phone or long number
    }
    
    private async Task LoadContactDataAsync(Contact contact)
    {
        if (_indexStore == null) return;
        
        IsLoading = true;
        
        await LoadContactMessagesAsync(contact);
        await LoadContactAnalyticsAsync(contact);
        
        IsLoading = false;
    }

    private async Task LoadContactMessagesAsync(Contact contact)
    {
        CurrentMessages.Clear();
        CurrentLinks.Clear();
        _matchIndices.Clear();
        TotalMatches = 0;
        CurrentMatchIndex = 0;
        
        var messagesForStats = await _indexStore!.GetMessagesAsync(contact.Id, 0, 50000); 
        var filtered = IncludeSentItems ? messagesForStats : messagesForStats.Where(m => !m.IsFromMe);
        
        foreach (var msg in filtered)
        {
            CurrentMessages.Add(msg);
        }
        
        await LoadFilteredLinksAsync();
    }

    private async Task LoadFilteredLinksAsync()
    {
        if (_indexStore == null || SelectedContact == null) return;
        
        CurrentLinks.Clear();
        var links = await _indexStore.GetLinksAsync(SelectedContact.Id, 0, 1000);
        
        IEnumerable<LinkItem> filtered = links;
        if (CurrentLinksCategory != "All")
        {
            filtered = links.Where(l => l.Category == CurrentLinksCategory);
        }
        else
        {
            // For "All", we include everything
            filtered = links;
        }
        // If "All", we show everything including Phones/Codes
        
        foreach (var link in filtered)
        {
            CurrentLinks.Add(link);
        }
    }

    private bool IsReaction(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        // Apple Tapback prefixes - handle both localized and standard variants
        return text.StartsWith("Loved ", StringComparison.OrdinalIgnoreCase) || 
               text.StartsWith("Liked ", StringComparison.OrdinalIgnoreCase) || 
               text.StartsWith("Disliked ", StringComparison.OrdinalIgnoreCase) || 
               text.StartsWith("Laughed at ", StringComparison.OrdinalIgnoreCase) || 
               text.StartsWith("Emphasized ", StringComparison.OrdinalIgnoreCase) || 
               text.StartsWith("Questioned ", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Reacted ", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("“") && (text.Contains("Loved") || text.Contains("Liked"));
    }

    private async Task LoadContactAnalyticsAsync(Contact contact)
    {
        var allMessages = await _indexStore!.GetMessagesForAnalyticsAsync(contact.Id);
        var linksCount = await _indexStore!.GetLinkCountAsync(contact.Id);
        
        if (allMessages.Count == 0 && linksCount == 0) return;

        // Filter reactions for analytics calculations (streaks, top words, etc.)
        var messagesForStats = allMessages.Where(m => !IsReaction(m.Text)).ToList();
        if (messagesForStats.Count == 0) messagesForStats = allMessages;

        // Total = messages + links (to match contacts view ItemCount)
        var analytics = new ChatAnalytics
        {
            ContactName = contact.Display,
            TotalMessages = allMessages.Count + linksCount,
            SentByMe = allMessages.Count(m => m.IsFromMe),
            SentByThem = allMessages.Count(m => !m.IsFromMe) + linksCount // Links are typically received
        };

        // Longest streak
        var dates = messagesForStats.Select(m => DateTimeOffset.FromUnixTimeSeconds(m.Timestamp).Date).Distinct().OrderBy(d => d).ToList();
        int currentStreak = 0;
        int maxStreak = 0;
        DateTime? streakStart = null;
        DateTime? maxStreakStart = null;
        DateTime? maxStreakEnd = null;

        for (int i = 0; i < dates.Count; i++)
        {
            if (i > 0 && (dates[i] - dates[i - 1]).TotalDays == 1)
            {
                currentStreak++;
            }
            else
            {
                if (currentStreak > maxStreak)
                {
                    maxStreak = currentStreak;
                    maxStreakStart = streakStart;
                    maxStreakEnd = dates[i - 1];
                }
                currentStreak = 1;
                streakStart = dates[i];
            }
        }
        if (currentStreak > maxStreak)
        {
            maxStreak = currentStreak;
            maxStreakStart = streakStart;
            maxStreakEnd = dates.Last();
        }

        analytics.LongestStreakDays = maxStreak;
        analytics.StreakStart = maxStreakStart;
        analytics.StreakEnd = maxStreakEnd;

        // Peak day
        var peak = messagesForStats.GroupBy(m => DateTimeOffset.FromUnixTimeSeconds(m.Timestamp).Date)
                          .OrderByDescending(g => g.Count())
                          .FirstOrDefault();
        if (peak != null)
        {
            analytics.MostActiveDay = peak.Key;
            analytics.MostActiveDayCount = peak.Count();
        }

        // Longest message - ignore reactions and metadata fragments
        // Use EXPLICIT pools for Me and Them to avoid any cross-contamination
        var poolMe = messagesForStats.Where(m => m.IsFromMe && 
            !string.IsNullOrEmpty(m.Text) && 
            !m.Text.Contains("streamtyped") && 
            !m.Text.Contains("bplist00") &&
            !m.Text.Contains("NSKeyedArchiver") &&
            !m.Text.Contains("__kIM") &&
            !IsReaction(m.Text)).ToList();

        var poolThem = messagesForStats.Where(m => !m.IsFromMe && 
            !string.IsNullOrEmpty(m.Text) && 
            !m.Text.Contains("streamtyped") && 
            !m.Text.Contains("bplist00") &&
            !m.Text.Contains("NSKeyedArchiver") &&
            !m.Text.Contains("__kIM") &&
            !IsReaction(m.Text)).ToList();

        analytics.LongestMessageMe = poolMe.OrderByDescending(m => m.Text?.Length ?? 0).FirstOrDefault();
        analytics.LongestMessageThem = poolThem.OrderByDescending(m => m.Text?.Length ?? 0).FirstOrDefault();

        
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { 
            "the", "a", "an", "and", "or", "but", "is", "are", "was", "were", "to", "in", "of", "it", "i", "you", "that", "this", "my", "your",
            "for", "on", "with", "as", "at", "by", "is", "it's", "i'm", "can", "if", "have", "has", "do", "did", "so", "up", "out", "about", "who"
        };
        // Precise emoji regex: surrogate pairs for emoji blocks + specific dingbats/symbols
        var emojiRegex = new Regex(@"(\u2702|\u2705|\u2708|\u2709|\u270a|\u270b|\u270c|\u270d|\u270f|\u2712|\u2714|\u2716|\u271d|\u2721|\u2728|\u2733|\u2734|\u2744|\u2747|\u274c|\u274e|\u2753|\u2754|\u2755|\u2757|\u2763|\u2764|\u2795|\u2796|\u2797|\u27a1|\u27b0|\u27bf|\u2934|\u2935|\u2b05|\u2b06|\u2b07|\u2b1b|\u2b1c|\u2b50|\u2b55|\u3030|\u303d|\u3297|\u3299|[\ud83c\ud83d\ud83e][\ud000-\udfff])");
        
        void ProcessText(bool fromMe, out List<WordFrequency> shortWords, out List<WordFrequency> longWords, out List<WordFrequency> topEmojis)
        {
            var filteredText = messagesForStats
                .Where(m => m.IsFromMe == fromMe && !string.IsNullOrEmpty(m.Text))
                .Where(m => !m.Text!.StartsWith("Loved ", StringComparison.OrdinalIgnoreCase) &&
                           !m.Text!.StartsWith("Liked ", StringComparison.OrdinalIgnoreCase) &&
                           !m.Text!.StartsWith("Laughed at ", StringComparison.OrdinalIgnoreCase) &&
                           !m.Text!.StartsWith("Emphasized ", StringComparison.OrdinalIgnoreCase) &&
                           !m.Text!.StartsWith("Questioned ", StringComparison.OrdinalIgnoreCase) &&
                           !m.Text!.StartsWith("Reacted ", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Text!)
                .ToList();

            var combinedText = string.Join(" ", filteredText);
            
            var allWords = Regex.Split(combinedText.ToLower(), @"\W+")
                                .Where(w => w.Length > 2 && !stopWords.Contains(w))
                                .GroupBy(w => w)
                                .OrderByDescending(g => g.Count())
                                .ToList();
            
            
            shortWords = allWords
                .Where(g => g.Key.Length < 5)
                .Take(5)
                .Select(g => new WordFrequency { Word = g.Key, Count = g.Count() })
                .ToList();
            
            
            var excludeWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
            { 
                "attachment", "media", "liked", "loved", "emphasized", "laughed", "questioned", "image", "photo", "video"
            };
            longWords = allWords
                .Where(g => g.Key.Length >= 5 && !excludeWords.Contains(g.Key))
                .Take(5)
                .Select(g => new WordFrequency { Word = g.Key, Count = g.Count() })
                .ToList();

            topEmojis = emojiRegex.Matches(combinedText)
                                  .Cast<Match>()
                                  .GroupBy(m => m.Value)
                                  .OrderByDescending(g => g.Count())
                                  .Take(5)
                                  .Select(g => new WordFrequency { Word = g.Key, Count = g.Count() })
                                  .ToList();
        }

        ProcessText(true, out var shortWordsMe, out var longWordsMe, out var emojisMe);
        ProcessText(false, out var shortWordsThem, out var longWordsThem, out var emojisThem);

        analytics.ShortWordsMe = shortWordsMe;
        analytics.LongWordsMe = longWordsMe;
        analytics.TopEmojisMe = emojisMe;
        analytics.ShortWordsThem = shortWordsThem;
        analytics.LongWordsThem = longWordsThem;
        analytics.TopEmojisThem = emojisThem;

        CurrentAnalytics = analytics;
    }
    
    [RelayCommand]
    private async Task SearchMessagesAsync()
    {
        if (_indexStore == null || string.IsNullOrWhiteSpace(MessageSearchText)) 
        {
            _matchIndices.Clear();
            TotalMatches = 0;
            CurrentMatchIndex = 0;
            return;
        }

        // If a contact is selected, we perform "Find in Conversation"
        if (SelectedContact != null)
        {
            _matchIndices.Clear();
            var term = MessageSearchText.ToLower();

            for (int i = 0; i < CurrentMessages.Count; i++)
            {
                if (CurrentMessages[i].Text.ToLower().Contains(term))
                {
                    _matchIndices.Add(i);
                }
            }
            
            TotalMatches = _matchIndices.Count;
            if (TotalMatches > 0)
            {
                CurrentMatchIndex = 1;
                JumpToMatch(0);
            }
            else
            {
                CurrentMatchIndex = 0;
                StatusMessage = $"No matches for '{MessageSearchText}' in this chat.";
            }
            return;
        }
        
        // Global search if no contact selected
        IsLoading = true;
        LoadingMessage = "Searching messagesForStats...";
        
        try
        {
            var results = await _indexStore.SearchMessagesAsync(MessageSearchText);
            CurrentMessages.Clear();
            _matchIndices.Clear();
            TotalMatches = results.Count;
            CurrentMatchIndex = results.Count > 0 ? 1 : 0;
            
            foreach (var msg in results)
            {
                CurrentMessages.Add(msg);
            }
            
            StatusMessage = $"Found {results.Count} global matches for '{MessageSearchText}'";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void FindNext()
    {
        if (TotalMatches == 0 || _matchIndices.Count == 0) return;
        
        var nextIndex = (CurrentMatchIndex % TotalMatches);
        CurrentMatchIndex = nextIndex + 1;
        JumpToMatch(nextIndex);
    }

    [RelayCommand]
    private void FindPrevious()
    {
        if (TotalMatches == 0 || _matchIndices.Count == 0) return;
        
        var prevIndex = (CurrentMatchIndex - 2 + TotalMatches) % TotalMatches;
        CurrentMatchIndex = prevIndex + 1;
        JumpToMatch(prevIndex);
    }

    private void JumpToMatch(int matchIdx)
    {
        if (matchIdx < 0 || matchIdx >= _matchIndices.Count) return;
        
        var msgIdx = _matchIndices[matchIdx];
        if (msgIdx < 0 || msgIdx >= CurrentMessages.Count) return;

        HighlightedMessage = CurrentMessages[msgIdx];
        StatusMessage = $"Match {CurrentMatchIndex} of {TotalMatches}";
    }

    [RelayCommand]
    private void OpenLink(LinkItem link)
    {
        try
        {
            var url = link.Url;
            
            // Clean URL in case it has iMessage serialization garbage
            // Find the actual URL start
            var httpIdx = url.IndexOf("http", StringComparison.OrdinalIgnoreCase);
            if (httpIdx > 0)
            {
                url = url.Substring(httpIdx);
            }
            else if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && 
                     !url.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                // Skip leading garbage for naked domains
                for (int i = 0; i < url.Length && i < 10; i++)
                {
                    char c = url[i];
                    if (char.IsLetter(c) && c < 128)
                    {
                        url = url.Substring(i);
                        break;
                    }
                }
            }
            
            // Ensure we have a valid URL scheme
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }
            
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { }
    }
    
    [RelayCommand]
    private async Task ExportAnalyticsAsync()
    {
        if (_exportService == null || CurrentAnalytics == null) return;

        var dialog = new SaveFileDialog
        {
            Title = "Export Analytics as PDF",
            FileName = $"{CurrentAnalytics.ContactName}_Analytics",
            Filter = "PDF Document (*.pdf)|*.pdf",
            DefaultExt = ".pdf"
        };
        
        if (dialog.ShowDialog() == true)
        {
            IsLoading = true;
            LoadingMessage = "Generating Analytics PDF...";
            
            var result = await _exportService.ExportAnalyticsAsync(dialog.FileName, CurrentAnalytics);
            
            IsLoading = false;
            if (result.Success)
            {
                StatusMessage = $"Analytics export complete: {Path.GetFileName(dialog.FileName)}";
            }
            else
            {
                StatusMessage = $"Analytics export failed: {string.Join(", ", result.Errors)}";
            }
        }
    }
    
    [RelayCommand]
    private async Task ExportChatAsync(string format)
    {
        if (_exportService == null || SelectedContact == null || CurrentMessages.Count == 0) return;
        
        var filter = format.ToLower() switch
        {
            "pdf" => "PDF Document (*.pdf)|*.pdf",
            "json" => "JSON Data (*.json)|*.json",
            "md" => "Markdown File (*.md)|*.md",
            "txt" => "Text File (*.txt)|*.txt",
            _ => "All Files (*.*)|*.*"
        };

        var dialog = new SaveFileDialog
        {
            Title = $"Export Chat as {format.ToUpper()}",
            FileName = $"{SelectedContact.Display}_Transcript",
            Filter = filter,
            DefaultExt = $".{format.ToLower()}"
        };
        
        if (dialog.ShowDialog() == true)
        {
            IsLoading = true;
            LoadingMessage = $"Exporting to {format.ToUpper()}...";
            StatusMessage = "Exporting...";
            
            // Force UI update before async work
            await Task.Delay(50);
            
            var result = await _exportService.ExportToFileAsync(
                dialog.FileName, 
                format,
                SelectedContact.Display, 
                CurrentMessages, 
                CurrentLinks);
            
            IsLoading = false;
            if (result.Success)
            {
                StatusMessage = $"Export complete: {Path.GetFileName(dialog.FileName)}";
                var openFolder = System.Windows.MessageBox.Show(
                    $"Successfully exported {result.ExportedCount} messagesForStats to:\n{dialog.FileName}\n\nOpen containing folder?",
                    "Export Complete",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Information);
                    
                if (openFolder == System.Windows.MessageBoxResult.Yes)
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
                }
            }
            else
            {
                StatusMessage = $"Export failed: {string.Join(", ", result.Errors)}";
                System.Windows.MessageBox.Show(
                    $"Export failed:\n{string.Join("\n", result.Errors)}",
                    "Export Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
    }
    
    public void Dispose()
    {
        _indexStore?.Dispose();
    }
}
