namespace MessageArchive.Models;

public class ChatAnalytics
{
    public string ContactName { get; set; } = string.Empty;
    public int TotalMessages { get; set; }
    public int SentByMe { get; set; }
    public int SentByThem { get; set; }
    public double SentByMePercent => TotalMessages == 0 ? 0 : (double)SentByMe / TotalMessages;
    public double SentByThemPercent => TotalMessages == 0 ? 0 : (double)SentByThem / TotalMessages;

    public DateTime? MostActiveDay { get; set; }
    public int MostActiveDayCount { get; set; }

    public int LongestStreakDays { get; set; }
    public DateTime? StreakStart { get; set; }
    public DateTime? StreakEnd { get; set; }

    public MessageItem? LongestMessageMe { get; set; }
    public MessageItem? LongestMessageThem { get; set; }

   
    public List<WordFrequency> ShortWordsMe { get; set; } = new();
    public List<WordFrequency> ShortWordsThem { get; set; } = new();
    
    
    public List<WordFrequency> LongWordsMe { get; set; } = new();
    public List<WordFrequency> LongWordsThem { get; set; } = new();

    public List<WordFrequency> TopEmojisMe { get; set; } = new();
    public List<WordFrequency> TopEmojisThem { get; set; } = new();
}

public class WordFrequency
{
    public string Word { get; set; } = string.Empty;
    public int Count { get; set; }
}
