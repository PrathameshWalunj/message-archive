using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using MessageArchive.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QuestPDF.Previewer;

namespace MessageArchive.Services;

/// <summary>
/// Handles exporting chat transcripts to Text or Markdown formats.
/// </summary>
public class ExportService
{
    private readonly IndexStore _indexStore;
    
    public ExportService(IndexStore indexStore)
    {
        _indexStore = indexStore;
        QuestPDF.Settings.License = LicenseType.Community;
    }
    
    public class ExportResult
    {
        public bool Success { get; set; }
        public string ExportPath { get; set; } = string.Empty;
        public int ExportedCount { get; set; }
        public List<string> Errors { get; set; } = new();
    }
    
    public async Task<ExportResult> ExportAnalyticsAsync(string filePath, ChatAnalytics analytics)
    {
        var result = new ExportResult();
        try
        {
            await Task.Run(() => GenerateAnalyticsPdf(filePath, analytics));
            result.Success = true;
            result.ExportPath = filePath;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Errors.Add($"Analytics export failed: {ex.Message}");
        }
        return result;
    }

    private void GenerateAnalyticsPdf(string path, ChatAnalytics analytics)
    {
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.5f, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(11).FontFamily(Fonts.SegoeUI));

                page.Header().Column(col => {
                    col.Item().Text($"Chat Analytics Report").FontSize(24).SemiBold().FontColor(Colors.Blue.Medium);
                    col.Item().Text($"Contact: {analytics.ContactName}").FontSize(14);
                    col.Item().PaddingTop(5).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                });

                page.Content().PaddingVertical(20).Column(column =>
                {
                    // Stats Row
                    column.Item().Row(row => {
                        row.RelativeItem().Column(c => {
                            c.Item().Text("MESSAGES SENT").FontSize(10).SemiBold().FontColor(Colors.Grey.Medium);
                            c.Item().Text($"Total: {analytics.TotalMessages:N0}").FontSize(16);
                            c.Item().Text($"You: {analytics.SentByMe:N0} ({analytics.SentByMePercent:P0})");
                            c.Item().Text($"{analytics.ContactName}: {analytics.SentByThem:N0} ({analytics.SentByThemPercent:P0})");
                        });
                        row.RelativeItem().Column(c => {
                            c.Item().Text("ACTIVITY PEAKS").FontSize(10).SemiBold().FontColor(Colors.Grey.Medium);
                            c.Item().Text($"Most Active: {analytics.MostActiveDay:D}");
                            c.Item().Text($"Peak Volume: {analytics.MostActiveDayCount:N0} msgs");
                            c.Item().PaddingTop(10).Text($"Longest Streak: {analytics.LongestStreakDays} days");
                        });
                    });

                    // Short Words
                    column.Item().PaddingTop(20).Text("TOP 5 SHORT WORDS (3-4 CHARS)").FontSize(12).SemiBold();
                    column.Item().Row(row => {
                        row.RelativeItem().Column(c => {
                            c.Item().PaddingTop(5).Text("You").Underline();
                            foreach(var w in analytics.ShortWordsMe) c.Item().Text($"{w.Word} ({w.Count})");
                        });
                        row.RelativeItem().Column(c => {
                            c.Item().PaddingTop(5).Text(analytics.ContactName).Underline();
                            foreach(var w in analytics.ShortWordsThem) c.Item().Text($"{w.Word} ({w.Count})");
                        });
                    });

                    // Long Words
                    column.Item().PaddingTop(15).Text("TOP 5 LONG WORDS (5+ CHARS)").FontSize(12).SemiBold();
                    column.Item().Row(row => {
                        row.RelativeItem().Column(c => {
                            c.Item().PaddingTop(5).Text("You").Underline();
                            foreach(var w in analytics.LongWordsMe) c.Item().Text($"{w.Word} ({w.Count})");
                        });
                        row.RelativeItem().Column(c => {
                            c.Item().PaddingTop(5).Text(analytics.ContactName).Underline();
                            foreach(var w in analytics.LongWordsThem) c.Item().Text($"{w.Word} ({w.Count})");
                        });
                    });

                    // Top Emojis
                    column.Item().PaddingTop(20).Text("TOP 5 EMOJIS").FontSize(12).SemiBold();
                    column.Item().Row(row => {
                        row.RelativeItem().Column(c => {
                            c.Item().PaddingTop(5).Text("You").Underline();
                            foreach(var e in analytics.TopEmojisMe) c.Item().Text($"{e.Word} ({e.Count})");
                        });
                        row.RelativeItem().Column(c => {
                            c.Item().PaddingTop(5).Text(analytics.ContactName).Underline();
                            foreach(var e in analytics.TopEmojisThem) c.Item().Text($"{e.Word} ({e.Count})");
                        });
                    });

                    // Longest Messages
                    column.Item().PaddingTop(20).Text("LONGEST MESSAGES").FontSize(12).SemiBold();
                    column.Item().Column(c => {
                        c.Item().PaddingTop(5).Text("You:").SemiBold().FontSize(10);
                        c.Item().PaddingLeft(10).Text(analytics.LongestMessageMe?.Text ?? "None").FontSize(9).Italic();
                        
                        c.Item().PaddingTop(10).Text($"{analytics.ContactName}:").SemiBold().FontSize(10);
                        c.Item().PaddingLeft(10).Text(analytics.LongestMessageThem?.Text ?? "None").FontSize(9).Italic();
                    });
                });

                page.Footer().AlignCenter().Text(x => {
                    x.Span("Generated by Message Archive - ");
                    x.Span(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                });
            });
        })
        .GeneratePdf(path);
    }

    public async Task<ExportResult> ExportToFileAsync(
        string filePath,
        string format,
        string contactName,
        IEnumerable<MessageItem> messages,
        IEnumerable<LinkItem>? links = null)
    {
        var result = new ExportResult();
        try
        {
            var orderedMessages = messages.OrderBy(x => x.Timestamp).ToList();
            
            switch (format.ToLower())
            {
                case "pdf":
                    GeneratePdfBinary(filePath, contactName, orderedMessages);
                    break;
                case "json":
                    await ExportToJsonFileAsync(filePath, contactName, orderedMessages);
                    break;
                case "md":
                case "markdown":
                    await ExportToMarkdownFileAsync(filePath, contactName, orderedMessages);
                    break;
                case "txt":
                case "text":
                    await ExportToTextFileAsync(filePath, contactName, orderedMessages);
                    break;
                default:
                    throw new ArgumentException($"Unsupported format: {format}");
            }
            
            result.Success = true;
            result.ExportPath = filePath;
            result.ExportedCount = orderedMessages.Count;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Errors.Add($"Export failed: {ex.Message}");
        }
        return result;
    }

    private async Task ExportToJsonFileAsync(string path, string contactName, List<MessageItem> messages)
    {
        var jsonData = new
        {
            Contact = contactName,
            ExportDate = DateTime.Now,
            MessageCount = messages.Count,
            Messages = messages.Select(m => new {
                m.Timestamp,
                m.DisplayTime,
                m.IsFromMe,
                m.Text,
                m.Service
            })
        };
        var jsonContent = JsonSerializer.Serialize(jsonData, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, jsonContent);
    }

    private async Task ExportToMarkdownFileAsync(string path, string contactName, List<MessageItem> messages)
    {
        var mdContent = new StringBuilder();
        mdContent.AppendLine($"# Chat with {contactName}");
        mdContent.AppendLine($"> Exported on {DateTime.Now:yyyy-MM-dd HH:mm}");
        mdContent.AppendLine();
        
        foreach (var m in messages)
        {
            var sender = m.IsFromMe ? "**Me**" : $"**{contactName}**";
            mdContent.AppendLine($"{sender} _({m.DisplayTime})_  ");
            mdContent.AppendLine($"{m.Text}  ");
            mdContent.AppendLine();
        }
        await File.WriteAllTextAsync(path, mdContent.ToString());
    }

    private async Task ExportToTextFileAsync(string path, string contactName, List<MessageItem> messages)
    {
        var txtContent = new StringBuilder();
        txtContent.AppendLine($"Transcript for: {contactName}");
        txtContent.AppendLine($"Export Date: {DateTime.Now:yyyy-MM-dd HH:mm}");
        txtContent.AppendLine(new string('-', 40));
        txtContent.AppendLine();
        
        foreach (var m in messages)
        {
            var sender = m.IsFromMe ? "Me" : contactName;
            txtContent.AppendLine($"[{m.DisplayTime}] {sender}: {m.Text}");
        }
        await File.WriteAllTextAsync(path, txtContent.ToString());
    }

    private void GeneratePdfBinary(string path, string contactName, List<MessageItem> messages)
    {
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(10).FontFamily(Fonts.SegoeUI));

                page.Header().Row(row =>
                {
                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text($"Chat Transcript: {contactName}").FontSize(20).SemiBold().FontColor(Colors.Blue.Medium);
                        col.Item().Text($"Exported on {DateTime.Now:yyyy-MM-dd HH:mm}").FontSize(10).Italic().FontColor(Colors.Grey.Medium);
                    });
                });

                page.Content().PaddingVertical(10).Column(column =>
                {
                    foreach (var m in messages)
                    {
                        column.Item().PaddingBottom(5).Row(row =>
                        {
                            if (m.IsFromMe)
                            {
                                row.RelativeItem(); // Push to right
                                row.RelativeItem().Background(Colors.Blue.Lighten5).Padding(5).Column(bubble =>
                                {
                                    bubble.Item().Text(m.Text).FontSize(11);
                                    bubble.Item().AlignRight().Text(m.DisplayTime).FontSize(8).FontColor(Colors.Grey.Medium);
                                });
                            }
                            else
                            {
                                row.RelativeItem().Background(Colors.Grey.Lighten4).Padding(5).Column(bubble =>
                                {
                                    bubble.Item().Text(m.Text).FontSize(11);
                                    bubble.Item().AlignRight().Text(m.DisplayTime).FontSize(8).FontColor(Colors.Grey.Medium);
                                });
                                row.RelativeItem(); // Push to left
                            }
                        });
                    }
                });

                page.Footer().AlignCenter().Text(x =>
                {
                    x.Span("Page ");
                    x.CurrentPageNumber();
                });
            });
        })
        .GeneratePdf(path);
    }
    
    public void OpenExportFolder(string path)
    {
        if (Directory.Exists(path))
        {
            try { Process.Start("explorer.exe", path); } catch { }
        }
    }
    
    private static string GetSafeFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Where(c => !invalid.Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "Chat_Export" : safe.Trim();
    }
}
