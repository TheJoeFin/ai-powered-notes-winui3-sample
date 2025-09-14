using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Notes.AI;

public static class IChatClientExtensions
{
    public static async IAsyncEnumerable<string> InferStreaming(this IChatClient client, string system, string prompt, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (ChatResponseUpdate messagePart in client.GetStreamingResponseAsync(
                [
                    new ChatMessage(ChatRole.System, system),
                    new ChatMessage(ChatRole.User, prompt)
                ],
                null,
                ct))
        {
            yield return messagePart.Text ?? string.Empty;
        }
    }

    public static IAsyncEnumerable<string> SummarizeTextAsync(this IChatClient client, string userText, CancellationToken ct = default)
    {
        return client.InferStreaming("", $"Summarize this text in three to five bullet points:\r {userText}", ct);
    }

    public static IAsyncEnumerable<string> SummarizeImageTextAsync(this IChatClient client, string imageText, CancellationToken ct = default)
    {
        string systemMessage = "Summarize the text extracted from an image in 3-5 clear bullet points. Focus on the main information and key details found in the image.";
        return client.InferStreaming(systemMessage, imageText, ct);
    }

    public static IAsyncEnumerable<string> SummarizeAudioTranscriptAsync(this IChatClient client, string transcriptText, CancellationToken ct = default)
    {
        string systemMessage = "Summarize this audio transcript in 3-5 bullet points. Focus on the key topics and main points discussed.";
        return client.InferStreaming(systemMessage, transcriptText, ct);
    }

    public static IAsyncEnumerable<string> ExtractTopicsAndTimestampsAsync(this IChatClient client, string transcriptText, CancellationToken ct = default)
    {
        string systemMessage = @"You are analyzing an audio transcript with timestamps in the format <|start|>text<|end|>. 
Extract the main topics discussed and provide them with their corresponding timestamps in this exact format:
""Topic Name (MM:SS) - Brief description""

For example:
""Meeting Discussion (2:15) - Project timeline and deliverables""
""Budget Review (5:30) - Q3 expenses and allocation""

Only include significant topics (3-7 topics maximum). Use the timestamp from when each topic starts being discussed.";
        return client.InferStreaming(systemMessage, transcriptText, ct);
    }

    public static IAsyncEnumerable<string> FixAndCleanUpTextAsync(this IChatClient client, string userText, CancellationToken ct = default)
    {
        string systemMessage = "Your job is to fix spelling, and clean up the text from the user. Only respond with the updated text. Do not explain anything.";
        return client.InferStreaming(systemMessage, userText, ct);
    }

    public static IAsyncEnumerable<string> AutocompleteSentenceAsync(this IChatClient client, string sentence, CancellationToken ct = default)
    {
        string systemMessage = "You are an assistant that helps the user complete sentences. Ignore spelling mistakes and just respond with the words that complete the sentence. Do not repeat the begining of the sentence.";
        return client.InferStreaming(systemMessage, sentence, ct);
    }

    public static Task<List<string>> GetTodoItemsFromText(this IChatClient client, string text)
    {
        return Task.Run(async () =>
        {

            string system = "Summarize the user text to 2-3 to-do items. Use the format [\"to-do 1\", \"to-do 2\"]. Respond only in one json array format";
            string response = string.Empty;

            CancellationTokenSource cts = new();
            await foreach (string partialResult in client.InferStreaming(system, text, cts.Token))
            {
                response += partialResult;
                if (partialResult.Contains("]"))
                {
                    cts.Cancel();
                    break;
                }
            }

            List<string> todos = Regex.Matches(response, @"""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value)
                .Where(t => !t.Contains("todo")).ToList();

            return todos;
        });
    }

    public static IAsyncEnumerable<string> AskForContentAsync(this IChatClient client, string content, string question, CancellationToken ct = default)
    {
        string systemMessage = "You are a helpful assistant answering questions about this content";
        return client.InferStreaming($"{systemMessage}: {content}", question, ct);
    }

    public static IAsyncEnumerable<string> SummarizePdfTextAsync(this IChatClient client, string pdfText, CancellationToken ct = default)
    {
        // For PDF summarization, use a simpler prompt that works better with local models
        string systemMessage = "Summarize this PDF document in 3-5 clear bullet points. Focus on the main topics and key information.";
        return client.InferStreaming(systemMessage, pdfText, ct);
    }

    public static async Task<string> SummarizeImageTextLocalAsync(string imageText, CancellationToken ct = default)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        Debug.WriteLine("[LocalSummarization] Starting ultra-fast image text summarization...");

        // Ultra-fast local image text summarization without requiring any AI models
        List<string> lines = imageText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .Where(line => line.Length > 3) // Skip very short lines
            .ToList();

        if (lines.Count == 0)
        {
            stopwatch.Stop();
            Debug.WriteLine($"[LocalSummarization] Image text summarization completed in {stopwatch.ElapsedMilliseconds}ms (no content)");
            return "No significant text content found in the image.";
        }

        StringBuilder summary = new();
        summary.AppendLine("🖼️ **Image Summary (Ultra-Fast Local Analysis):**");
        summary.AppendLine();

        // Extract potential headings (short lines, all caps, or lines that look like titles)
        List<string> headings = lines.Where(line =>
            line.Length < 50 &&
            (line.All(c => char.IsUpper(c) || char.IsWhiteSpace(c) || char.IsPunctuation(c)) ||
             line.EndsWith(":") ||
             line.StartsWith("=") ||
             (char.IsUpper(line[0]) && line.Split(' ').Length <= 5))
        ).Take(3).ToList();

        if (headings.Any())
        {
            summary.AppendLine("**Text Headers/Titles:**");
            foreach (string? heading in headings)
            {
                string cleanHeading = heading.Replace("=", "").Trim();
                if (!string.IsNullOrWhiteSpace(cleanHeading))
                    summary.AppendLine($"• {cleanHeading}");
            }
            summary.AppendLine();
        }

        // Extract key content (longer lines that contain substantial information)
        List<string> keyContent = lines.Where(line =>
            line.Length > 15 &&
            line.Length < 150 &&
            line.Contains(" ") &&
            line.Split(' ').Length > 3 &&
            !IsLikelyMetadata(line)
        ).Take(4).ToList();

        if (keyContent.Any())
        {
            summary.AppendLine("**Key Text Content:**");
            foreach (string? content in keyContent)
            {
                summary.AppendLine($"• {content}");
            }
            summary.AppendLine();
        }

        // Detect and categorize content types
        List<string> contentTypes = [];
        if (lines.Any(line => line.Contains("@") && line.Contains(".")))
            contentTypes.Add("Email addresses");
        if (lines.Any(line => Regex.IsMatch(line, @"\b\d{3}[-.]?\d{3}[-.]?\d{4}\b")))
            contentTypes.Add("Phone numbers");
        if (lines.Any(line => Regex.IsMatch(line, @"\b\d{1,2}/\d{1,2}/\d{2,4}\b")))
            contentTypes.Add("Dates");
        if (lines.Any(line => line.Contains("$") || line.Contains("€") || line.Contains("£")))
            contentTypes.Add("Financial information");
        if (lines.Any(line => Regex.IsMatch(line, @"^\d+\.\s") || line.StartsWith("•") || line.StartsWith("-")))
            contentTypes.Add("Lists or bullet points");

        if (contentTypes.Any())
        {
            summary.AppendLine("**Content Types Detected:**");
            foreach (string type in contentTypes)
            {
                summary.AppendLine($"• {type}");
            }
            summary.AppendLine();
        }

        // Add basic stats
        int wordCount = lines.Sum(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        int lineCount = lines.Count;

        summary.AppendLine("**Text Statistics:**");
        summary.AppendLine($"• Lines of text: {lineCount}");
        summary.AppendLine($"• Approximate word count: {wordCount}");

        stopwatch.Stop();
        Debug.WriteLine($"[LocalSummarization] ⚡ Image text summarization completed in {stopwatch.ElapsedMilliseconds}ms");

        return summary.ToString();
    }

    private static bool IsLikelyMetadata(string line)
    {
        // Skip lines that look like metadata, page numbers, etc.
        string lowerLine = line.ToLower();
        return (lowerLine.Contains("page") && lowerLine.Length < 20) ||
               Regex.IsMatch(line, @"^\d+$") || // Just a number
               lowerLine.Contains("copyright") ||
               lowerLine.Contains("©") ||
               lowerLine.StartsWith("http") ||
               line.Length < 8; // Very short lines are often metadata
    }

    public static async Task<string> SummarizePdfTextLocalAsync(string pdfText, CancellationToken ct = default)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        Debug.WriteLine("[LocalSummarization] Starting ultra-fast PDF summarization...");

        // Ultra-fast local summarization without requiring any AI models
        List<string> lines = pdfText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .Where(line => line.Length > 10) // Skip very short lines
            .ToList();

        if (lines.Count == 0)
        {
            stopwatch.Stop();
            Debug.WriteLine($"[LocalSummarization] PDF summarization completed in {stopwatch.ElapsedMilliseconds}ms (no content)");
            return "No significant content found in the PDF.";
        }

        StringBuilder summary = new();
        summary.AppendLine("📄 **PDF Summary (Ultra-Fast Local Analysis):**");
        summary.AppendLine();

        // Extract potential headers (short lines, all caps, or ending with colon)
        List<string> headers = lines.Where(line =>
            line.Length < 80 &&
            (line.All(c => char.IsUpper(c) || char.IsWhiteSpace(c) || char.IsPunctuation(c)) ||
             line.EndsWith(":") ||
             line.StartsWith("==="))
        ).Take(5).ToList();

        if (headers.Any())
        {
            summary.AppendLine("**Main Sections:**");
            foreach (string? header in headers)
            {
                string cleanHeader = header.Replace("===", "").Replace("Page", "").Trim();
                if (!string.IsNullOrWhiteSpace(cleanHeader))
                    summary.AppendLine($"• {cleanHeader}");
            }
            summary.AppendLine();
        }

        // Extract key sentences (longer lines that might contain important info)
        List<string> keySentences = lines.Where(line =>
            line.Length > 50 &&
            line.Length < 200 &&
            !line.StartsWith("===") &&
            line.Contains(" ") &&
            (line.Contains("important") || line.Contains("key") || line.Contains("main") ||
             line.Split(' ').Length > 8)
        ).Take(3).ToList();

        if (keySentences.Any())
        {
            summary.AppendLine("**Key Points:**");
            foreach (string? sentence in keySentences)
            {
                summary.AppendLine($"• {sentence}");
            }
            summary.AppendLine();
        }

        // Add basic stats
        int pageCount = pdfText.Count(c => pdfText.Contains("=== Page"));
        int wordCount = lines.Sum(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

        summary.AppendLine("**Document Info:**");
        summary.AppendLine($"• Pages: {Math.Max(1, pageCount)}");
        summary.AppendLine($"• Approximate word count: {wordCount}");

        stopwatch.Stop();
        Debug.WriteLine($"[LocalSummarization] ⚡ PDF summarization completed in {stopwatch.ElapsedMilliseconds}ms");

        return summary.ToString();
    }

    public static async Task<string> SummarizeAudioTranscriptLocalAsync(string transcriptText, CancellationToken ct = default)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        Debug.WriteLine("[LocalSummarization] Starting ultra-fast audio summarization...");

        // Ultra-fast local audio transcript summarization
        List<string> lines = transcriptText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .ToList();

        if (lines.Count == 0)
        {
            stopwatch.Stop();
            Debug.WriteLine($"[LocalSummarization] Audio summarization completed in {stopwatch.ElapsedMilliseconds}ms (no content)");
            return "No content found in the transcript.";
        }

        StringBuilder summary = new();
        summary.AppendLine("🎙️ **Audio Summary (Ultra-Fast Local Analysis):**");
        summary.AppendLine();

        // Extract text content from timestamp format <|time|>text<|time|>
        List<string> textContent = [];
        string timestampPattern = @"<\|[\d.]+\|>([^<]+)<\|[\d.]+\|>";
        MatchCollection matches = Regex.Matches(transcriptText, timestampPattern);

        foreach (Match match in matches)
        {
            if (match.Groups.Count > 1)
            {
                string text = match.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(text) && text.Length > 20)
                {
                    textContent.Add(text);
                }
            }
        }

        // If no timestamp format found, use raw lines
        if (textContent.Count == 0)
        {
            textContent = [.. lines.Where(line => line.Length > 20 && !line.Contains("===")).Take(10)];
        }

        // Find key discussion points
        List<string> keyPoints = textContent.Where(text =>
            text.Split(' ').Length > 5 &&
            text.Split(' ').Length < 30 &&
            (text.Contains("discuss") || text.Contains("talk about") || text.Contains("important") ||
             text.Contains("key") || text.Contains("main") || text.Contains("focus"))
        ).Take(4).ToList();

        if (keyPoints.Any())
        {
            summary.AppendLine("**Key Discussion Points:**");
            foreach (string? point in keyPoints)
            {
                summary.AppendLine($"• {point}");
            }
            summary.AppendLine();
        }

        // Extract the most substantial content segments
        List<string> mainContent = textContent
            .OrderByDescending(text => text.Split(' ').Length)
            .Take(3)
            .ToList();

        if (mainContent.Any())
        {
            summary.AppendLine("**Main Content:**");
            foreach (string? content in mainContent)
            {
                string shortened = content.Length > 150 ? content[..147] + "..." : content;
                summary.AppendLine($"• {shortened}");
            }
            summary.AppendLine();
        }

        // Calculate duration from timestamps if available
        List<double> timestamps = Regex.Matches(transcriptText, @"<\|([\d.]+)\|>")
            .Cast<Match>()
            .Select(m => double.TryParse(m.Groups[1].Value, out double time) ? time : 0)
            .Where(t => t > 0)
            .ToList();

        if (timestamps.Any())
        {
            double duration = Math.Max(0, timestamps.Max() - timestamps.Min());
            double durationMinutes = Math.Round(duration / 60, 1);
            summary.AppendLine("**Recording Info:**");
            summary.AppendLine($"• Duration: ~{durationMinutes} minutes");
            summary.AppendLine($"• Text segments: {textContent.Count}");
        }

        stopwatch.Stop();
        Debug.WriteLine($"[LocalSummarization] ⚡ Audio summarization completed in {stopwatch.ElapsedMilliseconds}ms");

        return summary.ToString();
    }

    public static async Task<string> ExtractTopicsLocalAsync(string transcriptText, int attachmentId, CancellationToken ct = default)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        Debug.WriteLine("[LocalSummarization] Starting ultra-fast topic extraction...");

        // Ultra-fast local topic extraction with clickable timestamps
        StringBuilder topics = new();
        topics.AppendLine("🎯 **Topics (Ultra-Fast Local Analysis):**");
        topics.AppendLine();

        // Extract timestamped segments
        string timestampPattern = @"<\|([\d.]+)\|>([^<]+)<\|([\d.]+)\|>";
        MatchCollection matches = Regex.Matches(transcriptText, timestampPattern);

        List<(double startTime, string text)> segments = [];
        foreach (Match match in matches)
        {
            if (match.Groups.Count > 2 &&
                double.TryParse(match.Groups[1].Value, out double startTime))
            {
                string text = match.Groups[2].Value.Trim();
                if (!string.IsNullOrWhiteSpace(text) && text.Length > 30)
                {
                    segments.Add((startTime, text));
                }
            }
        }

        if (segments.Count == 0)
        {
            stopwatch.Stop();
            Debug.WriteLine($"[LocalSummarization] Topic extraction completed in {stopwatch.ElapsedMilliseconds}ms (no timestamped content)");
            topics.AppendLine("• No timestamped topics found");
            return topics.ToString();
        }

        // Group segments into topics based on content similarity and time gaps
        List<List<(double startTime, string text)>> topicGroups = [];
        List<(double startTime, string text)> currentGroup = [];
        double lastTime = 0.0;

        foreach ((double startTime, string text) segment in segments.OrderBy(s => s.startTime))
        {
            // Start new topic if there's a significant time gap (>30 seconds)
            if (currentGroup.Any() && segment.startTime - lastTime > 30)
            {
                if (currentGroup.Count > 0)
                {
                    topicGroups.Add([.. currentGroup]);
                    currentGroup.Clear();
                }
            }

            currentGroup.Add(segment);
            lastTime = segment.startTime;
        }

        // Add the last group
        if (currentGroup.Any())
        {
            topicGroups.Add(currentGroup);
        }

        // Generate topics with clickable timestamps
        for (int i = 0; i < Math.Min(topicGroups.Count, 7); i++)
        {
            List<(double startTime, string text)> group = topicGroups[i];
            double startTime = group.First().startTime;
            string timeStr = FormatTimestamp(startTime);

            // Create a topic title from the first meaningful segment
            string firstText = group.First().text;
            string topicTitle = ExtractTopicTitle(firstText);

            // Make timestamp clickable
            string clickableTimestamp = $"**[({timeStr})](audio://{attachmentId}/{timeStr})**";

            topics.AppendLine($"• {topicTitle} {clickableTimestamp}");
        }

        stopwatch.Stop();
        Debug.WriteLine($"[LocalSummarization] ⚡ Topic extraction completed in {stopwatch.ElapsedMilliseconds}ms");

        return topics.ToString();
    }

    private static string FormatTimestamp(double seconds)
    {
        TimeSpan timeSpan = TimeSpan.FromSeconds(seconds);
        if (timeSpan.Hours > 0)
        {
            return $"{timeSpan.Hours}:{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}";
        }
        else
        {
            return $"{timeSpan.Minutes}:{timeSpan.Seconds:D2}";
        }
    }

    private static string ExtractTopicTitle(string text)
    {
        // Extract a meaningful topic title from the text
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Look for key topic indicators
        string[] keyPhrases = new[] { "discuss", "talk about", "cover", "review", "explain", "focus on" };

        foreach (string? phrase in keyPhrases)
        {
            int index = text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                string afterPhrase = text[(index + phrase.Length)..].Trim();
                IEnumerable<string> topicWords = afterPhrase.Split(' ').Take(4);
                return string.Join(" ", topicWords).Trim();
            }
        }

        // Fallback: use first few meaningful words
        IEnumerable<string> meaningfulWords = words.Where(w => w.Length > 3 &&
            !new[] { "the", "and", "but", "for", "are", "was", "were", "this", "that" }
            .Contains(w.ToLower())).Take(3);

        string title = string.Join(" ", meaningfulWords);
        return string.IsNullOrWhiteSpace(title) ? "Discussion" : title;
    }
}
