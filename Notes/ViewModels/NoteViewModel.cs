using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Notes.AI;
using Notes.AI.Embeddings;
using Notes.AI.TextRecognition;
using Notes.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Notes.ViewModels;

public partial class NoteViewModel : ObservableObject
{
    public readonly Note Note;

    [ObservableProperty]
    private ObservableCollection<AttachmentViewModel> attachments = [];

    [ObservableProperty]
    private ObservableCollection<string> todos = [];

    [ObservableProperty]
    private bool todosLoading = false;

    private readonly DispatcherTimer _saveTimer;
    private bool _contentLoaded = false;

    public DispatcherQueue DispatcherQueue { get; set; }

    public NoteViewModel(Note note)
    {
        Note = note;
        _saveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _saveTimer.Tick += SaveTimerTick;
    }

    public string Title
    {
        get => Note.Title;
        set => SetProperty(Note.Title, value, Note, (note, value) =>
        {
            note.Title = value;
            HandleTitleChanged(value);
        });
    }

    public DateTime Modified
    {
        get => Note.Modified;
        set => SetProperty(Note.Modified, value, Note, (note, value) => note.Modified = value);
    }

    [ObservableProperty]
    private string content;

    private async Task HandleTitleChanged(string value)
    {
        StorageFolder folder = await Utils.GetLocalFolderAsync();
        StorageFile file = await folder.GetFileAsync(Note.Filename);

        await file.RenameAsync(value.Trim() + Utils.FileExtension, NameCollisionOption.GenerateUniqueName);
        Note.Filename = file.Name;
        await AppDataContext.SaveCurrentAsync();
    }

    private async Task SaveContentAsync()
    {
        StorageFolder folder = await Utils.GetLocalFolderAsync();
        StorageFile file = await folder.GetFileAsync(Note.Filename);
        await FileIO.WriteTextAsync(file, Content);
    }

    public async Task LoadContentAsync()
    {
        if (_contentLoaded)
        {
            return;
        }

        _contentLoaded = true;

        StorageFolder folder = await Utils.GetLocalFolderAsync();
        StorageFile file = await folder.GetFileAsync(Note.Filename);
        content = await FileIO.ReadTextAsync(file);

        AppDataContext context = await AppDataContext.GetCurrentAsync();
        List<Attachment> attachments = context.Attachments.Where(a => a.NoteId == Note.Id).ToList();
        foreach (Attachment? attachment in attachments)
        {
            Attachments.Add(new AttachmentViewModel(attachment));
        }
    }

    partial void OnContentChanged(string value)
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public async Task AddAttachmentAsync(StorageFile file)
    {
        Debug.WriteLine("=== [NoteViewModel] AddAttachmentAsync ENTRY ===");
        Debug.WriteLine($"[NoteViewModel] Adding attachment: {file?.Name ?? "NULL FILE"}");
        Debug.WriteLine($"[NoteViewModel] File path: {file?.Path ?? "NULL PATH"}");
        Debug.WriteLine($"[NoteViewModel] File type: {file?.FileType ?? "NULL TYPE"}");

        StorageFolder attachmentsFolder = await Utils.GetAttachmentsFolderAsync();
        bool shouldCopyFile = true;

        Attachment attachment = new()
        {
            IsProcessed = false,
            Note = Note
        };

        Debug.WriteLine($"[NoteViewModel] Created attachment object");

        if (new string[] { ".png", ".jpg", ".jpeg" }.Contains(file.FileType))
        {
            attachment.Type = NoteAttachmentType.Image;
            Debug.WriteLine($"[NoteViewModel] Attachment type: Image");
        }
        else if (new string[] { ".mp3", ".wav", ".m4a", ".opus", ".waptt" }.Contains(file.FileType))
        {
            attachment.Type = NoteAttachmentType.Audio;
            Debug.WriteLine($"[NoteViewModel] Attachment type: Audio");

            // Only convert to WAV if it's not already a WAV file, or if it's not already in the attachments folder
            if (file.FileType != ".wav" || !file.Path.StartsWith(attachmentsFolder.Path))
            {
                Debug.WriteLine($"[NoteViewModel] Converting audio file to WAV: {file.Path}");
                file = await Utils.SaveAudioFileAsWav(file, attachmentsFolder);
                shouldCopyFile = false;
            }
            else
            {
                Debug.WriteLine($"[NoteViewModel] Audio file is already WAV in attachments folder: {file.Path}");
                shouldCopyFile = false; // File is already in the right place and format
            }
        }
        else if (file.FileType == ".mp4")
        {
            attachment.Type = NoteAttachmentType.Video;
            Debug.WriteLine($"[NoteViewModel] Attachment type: Video");
        }
        else if (file.FileType == ".pdf")
        {
            attachment.Type = NoteAttachmentType.PDF;
            Debug.WriteLine($"[NoteViewModel] Attachment type: PDF");
        }
        else
        {
            attachment.Type = NoteAttachmentType.Document;
            Debug.WriteLine($"[NoteViewModel] Attachment type: Document");
        }

        if (shouldCopyFile && !file.Path.StartsWith(attachmentsFolder.Path))
        {
            Debug.WriteLine($"[NoteViewModel] Copying file to attachments folder");
            file = await file.CopyAsync(attachmentsFolder, file.Name, NameCollisionOption.GenerateUniqueName);
        }

        attachment.Filename = file.Name;
        Debug.WriteLine($"[NoteViewModel] Final attachment filename: {attachment.Filename}");

        Attachments.Add(new AttachmentViewModel(attachment));
        Debug.WriteLine($"[NoteViewModel] Added attachment to UI collection");

        AppDataContext context = await AppDataContext.GetCurrentAsync();
        await context.Attachments.AddAsync(attachment);
        Debug.WriteLine($"[NoteViewModel] Added attachment to database context");

        await context.SaveChangesAsync();
        Debug.WriteLine($"[NoteViewModel] Saved changes to database");

        Debug.WriteLine($"[NoteViewModel] CALLING AttachmentProcessor.AddAttachment: {attachment.Filename}");
        Debug.WriteLine($"[NoteViewModel] Attachment ID: {attachment.Id}, Type: {attachment.Type}, IsProcessed: {attachment.IsProcessed}");

        AttachmentProcessor.AddAttachment(attachment);

        Debug.WriteLine("=== [NoteViewModel] AddAttachmentAsync EXIT ===");
    }

    public async Task RemoveAttachmentAsync(AttachmentViewModel attachmentViewModel)
    {
        Attachments.Remove(attachmentViewModel);

        Attachment attachment = attachmentViewModel.Attachment;
        Note.Attachments.Remove(attachment);

        StorageFolder attachmentsFolder = await Utils.GetAttachmentsFolderAsync();
        StorageFile file = await attachmentsFolder.GetFileAsync(attachment.Filename);
        await file.DeleteAsync();

        if (attachment.IsProcessed && !string.IsNullOrEmpty(attachment.FilenameForText))
        {
            StorageFolder attachmentsTranscriptFolder = await Utils.GetAttachmentsTranscriptsFolderAsync();
            StorageFile transcriptFile = await attachmentsTranscriptFolder.GetFileAsync(attachment.FilenameForText);
            await transcriptFile.DeleteAsync();
        }

        AppDataContext context = await AppDataContext.GetCurrentAsync();
        context.Attachments.Remove(attachment);
        context.TextChunks.RemoveRange(context.TextChunks.Where(tc => tc.SourceId == attachment.Id && tc.ContentType == "attachment"));

        await context.SaveChangesAsync();
    }

    public async Task ShowTodos()
    {
        if (App.ChatClient == null)
        {
            return;
        }

        if (!TodosLoading && (Todos == null || Todos.Count == 0))
        {
            DispatcherQueue.TryEnqueue(() => TodosLoading = true);
            List<string> todos = await App.ChatClient.GetTodoItemsFromText(Content);
            if (todos != null && todos.Count > 0)
            {
                DispatcherQueue.TryEnqueue(() => Todos = new ObservableCollection<string>(todos));
            }
        }

        DispatcherQueue.TryEnqueue(() => TodosLoading = false);
    }

    public async Task SummarizeAudioAttachmentAsync(AttachmentViewModel attachmentViewModel)
    {
        if (!attachmentViewModel.Attachment.IsProcessed ||
            string.IsNullOrEmpty(attachmentViewModel.Attachment.FilenameForText))
        {
            return;
        }

        try
        {
            Debug.WriteLine($"[NoteViewModel] Starting audio summarization for: {attachmentViewModel.Attachment.Filename}");

            // Read the transcript file
            StorageFolder transcriptsFolder = await Utils.GetAttachmentsTranscriptsFolderAsync();
            StorageFile transcriptFile = await transcriptsFolder.GetFileAsync(attachmentViewModel.Attachment.FilenameForText);
            string transcriptText = await FileIO.ReadTextAsync(transcriptFile);

            Debug.WriteLine($"[NoteViewModel] Transcript loaded, length: {transcriptText.Length} characters");

            string summaryText;

            // Prioritize fast local processing, use AI only for small transcripts on supported hardware
            if (App.ChatClient != null && transcriptText.Length <= 4000)
            {
                Debug.WriteLine("[NoteViewModel] Using Windows AI for audio summarization (small transcript)");
                // Use Windows AI for small transcripts
                summaryText = "\n\n## Audio Summary (AI Enhanced)\n";
                try
                {
                    CancellationTokenSource cts = new();
                    await foreach (string partialResult in App.ChatClient.SummarizeAudioTranscriptAsync(transcriptText, cts.Token))
                    {
                        summaryText += partialResult;
                    }
                    summaryText += "\n";
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[NoteViewModel] AI summarization failed, falling back to local: {ex.Message}");
                    summaryText = await IChatClientExtensions.SummarizeAudioTranscriptLocalAsync(transcriptText);
                    summaryText += "\n";
                }
            }
            else
            {
                Debug.WriteLine("[NoteViewModel] Using ultra-fast local processing for audio summarization");
                // Use ultra-fast local processing for all other cases
                summaryText = await IChatClientExtensions.SummarizeAudioTranscriptLocalAsync(transcriptText);
                summaryText += "\n";
            }

            // Add the summary to the end of the note content
            Content = Content + summaryText + "\n";
            Debug.WriteLine($"[NoteViewModel] Audio summary added to note content");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NoteViewModel] ERROR: Audio summarization failed: {ex.Message}");
            throw;
        }
    }

    public async Task SummarizeImageAttachmentAsync(AttachmentViewModel attachmentViewModel)
    {
        if (!attachmentViewModel.Attachment.IsProcessed ||
            string.IsNullOrEmpty(attachmentViewModel.Attachment.FilenameForText))
        {
            return;
        }

        try
        {
            Debug.WriteLine($"[NoteViewModel] Starting image summarization for: {attachmentViewModel.Attachment.Filename}");

            // Use the existing TextRecognition API to load the image text
            ImageText imageText = await Notes.AI.TextRecognition.TextRecognition.GetSavedText(attachmentViewModel.Attachment.FilenameForText);

            if (imageText?.Lines == null || imageText.Lines.Count == 0)
            {
                Debug.WriteLine("[NoteViewModel] No text content found in image");
                return;
            }

            // Extract plain text from the ImageText object
            string plainText = string.Join("\n", imageText.Lines.Select(line => line.Text));

            if (string.IsNullOrWhiteSpace(plainText))
            {
                Debug.WriteLine("[NoteViewModel] No meaningful text content found in image");
                return;
            }

            Debug.WriteLine($"[NoteViewModel] Extracted plain text, length: {plainText.Length} characters");

            string summaryText;

            // Prioritize fast local processing, use AI only for small text on supported hardware
            if (App.ChatClient != null && plainText.Length <= 4000)
            {
                Debug.WriteLine("[NoteViewModel] Using Windows AI for image summarization (small text)");
                // Use Windows AI for small text
                summaryText = "\n\n## Image Summary (AI Enhanced)\n";
                try
                {
                    CancellationTokenSource cts = new();
                    await foreach (string partialResult in App.ChatClient.SummarizeImageTextAsync(plainText, cts.Token))
                    {
                        summaryText += partialResult;
                    }
                    summaryText += "\n";
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[NoteViewModel] AI summarization failed, falling back to local: {ex.Message}");
                    summaryText = await IChatClientExtensions.SummarizeImageTextLocalAsync(plainText);
                    summaryText += "\n";
                }
            }
            else
            {
                Debug.WriteLine("[NoteViewModel] Using ultra-fast local processing for image summarization");
                // Use ultra-fast local processing for all other cases
                summaryText = await IChatClientExtensions.SummarizeImageTextLocalAsync(plainText);
                summaryText += "\n";
            }

            // Add the summary to the end of the note content
            Content = Content + summaryText + "\n";
            Debug.WriteLine($"[NoteViewModel] Image summary added to note content");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NoteViewModel] ERROR: Image summarization failed: {ex.Message}");
            throw;
        }
    }

    public async Task AddTopicsAndTimestampsAsync(AttachmentViewModel attachmentViewModel)
    {
        if (!attachmentViewModel.Attachment.IsProcessed ||
            string.IsNullOrEmpty(attachmentViewModel.Attachment.FilenameForText))
        {
            return;
        }

        try
        {
            Debug.WriteLine($"[NoteViewModel] Starting topics and timestamps extraction for: {attachmentViewModel.Attachment.Filename}");

            // Read the transcript file
            StorageFolder transcriptsFolder = await Utils.GetAttachmentsTranscriptsFolderAsync();
            StorageFile transcriptFile = await transcriptsFolder.GetFileAsync(attachmentViewModel.Attachment.FilenameForText);
            string transcriptText = await FileIO.ReadTextAsync(transcriptFile);

            Debug.WriteLine($"[NoteViewModel] Transcript loaded, length: {transcriptText.Length} characters");

            string topicsText;

            // Prioritize fast local processing, use AI only for small transcripts on supported hardware
            if (App.ChatClient != null && transcriptText.Length <= 4000)
            {
                Debug.WriteLine("[NoteViewModel] Using Windows AI for topics extraction (small transcript)");
                // Use Windows AI for small transcripts
                topicsText = "\n\n## Topics (AI Enhanced)\n";
                try
                {
                    CancellationTokenSource cts = new();
                    await foreach (string partialResult in App.ChatClient.ExtractTopicsAndTimestampsAsync(transcriptText, cts.Token))
                    {
                        topicsText += partialResult;
                    }

                    // Make timestamps clickable
                    topicsText = MakeTimestampsClickable(topicsText, attachmentViewModel.Attachment.Id);
                    topicsText += "\n";
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[NoteViewModel] AI topics extraction failed, falling back to local: {ex.Message}");
                    topicsText = await IChatClientExtensions.ExtractTopicsLocalAsync(transcriptText, attachmentViewModel.Attachment.Id);
                    topicsText += "\n";
                }
            }
            else
            {
                Debug.WriteLine("[NoteViewModel] Using ultra-fast local processing for topics extraction");
                // Use ultra-fast local processing for all other cases
                topicsText = await IChatClientExtensions.ExtractTopicsLocalAsync(transcriptText, attachmentViewModel.Attachment.Id);
                topicsText += "\n";
            }

            // Add the topics to the end of the note content
            Content = Content + topicsText + "\n";
            Debug.WriteLine($"[NoteViewModel] Topics and timestamps added to note content");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NoteViewModel] ERROR: Topics and timestamps extraction failed: {ex.Message}");
            throw;
        }
    }

    public async Task SummarizePdfAttachmentAsync(AttachmentViewModel attachmentViewModel)
    {
        if (!attachmentViewModel.Attachment.IsProcessed ||
            string.IsNullOrEmpty(attachmentViewModel.Attachment.FilenameForText))
        {
            return;
        }

        try
        {
            Debug.WriteLine($"[NoteViewModel] Starting PDF summarization for: {attachmentViewModel.Attachment.Filename}");

            // Read the extracted text file
            StorageFolder transcriptsFolder = await Utils.GetAttachmentsTranscriptsFolderAsync();
            StorageFile textFile = await transcriptsFolder.GetFileAsync(attachmentViewModel.Attachment.FilenameForText);
            string pdfText = await FileIO.ReadTextAsync(textFile);

            Debug.WriteLine($"[NoteViewModel] PDF text loaded, length: {pdfText.Length} characters");

            string summaryText;

            // Prioritize fast local processing, use AI only for small PDFs on supported hardware
            if (App.ChatClient != null && pdfText.Length <= 4000)
            {
                Debug.WriteLine("[NoteViewModel] Using Windows AI for PDF summarization (small document)");
                // Use Windows AI for small documents
                summaryText = "\n\n## PDF Summary (AI Enhanced)\n";
                try
                {
                    CancellationTokenSource cts = new();
                    await foreach (string partialResult in App.ChatClient.SummarizePdfTextAsync(pdfText, cts.Token))
                    {
                        summaryText += partialResult;
                    }
                    summaryText += "\n";
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[NoteViewModel] AI PDF summarization failed, falling back to local: {ex.Message}");
                    summaryText = await IChatClientExtensions.SummarizePdfTextLocalAsync(pdfText);
                    summaryText += "\n";
                }
            }
            else
            {
                Debug.WriteLine("[NoteViewModel] Using ultra-fast local processing for PDF summarization");
                // Use ultra-fast local processing for all other cases
                summaryText = await IChatClientExtensions.SummarizePdfTextLocalAsync(pdfText);
                summaryText += "\n";
            }

            // Add the summary to the end of the note content
            Content = Content + summaryText + "\n";
            Debug.WriteLine($"[NoteViewModel] PDF summary added to note content");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NoteViewModel] ERROR: PDF summarization failed: {ex.Message}");
            throw;
        }
    }

    private async Task<string> ProcessLargeTranscriptSummary(string transcriptText, int maxChunkSize)
    {
        Debug.WriteLine($"[NoteViewModel] Processing large transcript summary in chunks");

        List<string> chunks = SplitTranscriptIntoChunks(transcriptText, maxChunkSize);
        List<string> chunkSummaries = [];

        Debug.WriteLine($"[NoteViewModel] Split transcript into {chunks.Count} chunks");

        // Process each chunk
        for (int i = 0; i < chunks.Count; i++)
        {
            Debug.WriteLine($"[NoteViewModel] Processing summary chunk {i + 1}/{chunks.Count}");

            try
            {
                string chunkSummary = "";
                CancellationTokenSource cts = new();

                await foreach (string partialResult in App.ChatClient.SummarizeAudioTranscriptAsync(chunks[i], cts.Token))
                {
                    chunkSummary += partialResult;
                }

                if (!string.IsNullOrWhiteSpace(chunkSummary))
                {
                    chunkSummaries.Add(chunkSummary.Trim());
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NoteViewModel] ERROR: Failed to process chunk {i + 1}: {ex.Message}");
                // Continue with other chunks
            }
        }

        // Combine summaries
        if (chunkSummaries.Count == 0)
        {
            return "\n\n## Summary\n• Unable to generate summary due to processing errors\n";
        }
        else if (chunkSummaries.Count == 1)
        {
            return "\n\n## Summary\n" + chunkSummaries[0] + "\n";
        }
        else
        {
            // If we have multiple chunk summaries, create a consolidated summary
            string combinedSummaries = string.Join("\n\n", chunkSummaries);

            // If the combined summaries are still too long, just present them as sections
            if (combinedSummaries.Length > maxChunkSize)
            {
                string result = "\n\n## Summary\n";
                for (int i = 0; i < chunkSummaries.Count; i++)
                {
                    result += $"### Part {i + 1}\n{chunkSummaries[i]}\n\n";
                }
                return result;
            }
            else
            {
                // Try to create a final consolidated summary
                try
                {
                    string finalSummary = "";
                    CancellationTokenSource cts = new();

                    await foreach (string partialResult in App.ChatClient.SummarizeAudioTranscriptAsync(combinedSummaries, cts.Token))
                    {
                        finalSummary += partialResult;
                    }

                    return "\n\n## Summary\n" + finalSummary + "\n";
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[NoteViewModel] ERROR: Failed to create final summary: {ex.Message}");
                    // Fall back to individual summaries
                    string result = "\n\n## Summary\n";
                    for (int i = 0; i < chunkSummaries.Count; i++)
                    {
                        result += $"### Part {i + 1}\n{chunkSummaries[i]}\n\n";
                    }
                    return result;
                }
            }
        }
    }

    private async Task<string> ProcessLargeTranscriptTopics(string transcriptText, int maxChunkSize, int attachmentId)
    {
        Debug.WriteLine($"[NoteViewModel] Processing large transcript topics in chunks");

        List<string> chunks = SplitTranscriptIntoChunks(transcriptText, maxChunkSize);
        List<string> allTopics = [];

        Debug.WriteLine($"[NoteViewModel] Split transcript into {chunks.Count} chunks");

        // Process each chunk
        for (int i = 0; i < chunks.Count; i++)
        {
            Debug.WriteLine($"[NoteViewModel] Processing topics chunk {i + 1}/{chunks.Count}");

            try
            {
                string chunkTopics = "";
                CancellationTokenSource cts = new();

                await foreach (string partialResult in App.ChatClient.ExtractTopicsAndTimestampsAsync(chunks[i], cts.Token))
                {
                    chunkTopics += partialResult;
                }

                if (!string.IsNullOrWhiteSpace(chunkTopics))
                {
                    allTopics.Add(chunkTopics.Trim());
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NoteViewModel] ERROR: Failed to process chunk {i + 1}: {ex.Message}");
                // Continue with other chunks
            }
        }

        // Combine and format topics
        if (allTopics.Count == 0)
        {
            return "\n\n## Topics\n• Unable to extract topics due to processing errors\n";
        }

        string combinedTopics = "\n\n## Topics\n" + string.Join("\n", allTopics) + "\n";

        // Make timestamps clickable
        return MakeTimestampsClickable(combinedTopics, attachmentId);
    }

    private List<string> SplitTranscriptIntoChunks(string transcriptText, int maxChunkSize)
    {
        List<string> chunks = [];

        // Try to split on timestamp boundaries first
        string timestampPattern = @"<\|[\d.]+\|>";
        MatchCollection matches = Regex.Matches(transcriptText, timestampPattern);

        if (matches.Count > 1)
        {
            // Split based on timestamps to preserve context
            string currentChunk = "";
            int currentPosition = 0;

            foreach (Match match in matches)
            {
                string nextSegment = transcriptText.Substring(currentPosition, match.Index - currentPosition + match.Length);

                if (currentChunk.Length + nextSegment.Length > maxChunkSize && !string.IsNullOrWhiteSpace(currentChunk))
                {
                    chunks.Add(currentChunk.Trim());
                    currentChunk = nextSegment;
                }
                else
                {
                    currentChunk += nextSegment;
                }

                currentPosition = match.Index + match.Length;
            }

            // Add remaining content
            if (currentPosition < transcriptText.Length)
            {
                currentChunk += transcriptText[currentPosition..];
            }

            if (!string.IsNullOrWhiteSpace(currentChunk))
            {
                chunks.Add(currentChunk.Trim());
            }
        }
        else
        {
            // Fall back to simple character-based chunking
            for (int i = 0; i < transcriptText.Length; i += maxChunkSize)
            {
                int chunkSize = Math.Min(maxChunkSize, transcriptText.Length - i);
                chunks.Add(transcriptText.Substring(i, chunkSize));
            }
        }

        Debug.WriteLine($"[NoteViewModel] Created {chunks.Count} chunks with max size {maxChunkSize}");
        return chunks;
    }

    private void SaveTimerTick(object? sender, object e)
    {
        _saveTimer.Stop();
        SaveContentToFileAndReIndex();
    }

    private async Task SaveContentToFileAndReIndex()
    {
        StorageFolder folder = await Utils.GetLocalFolderAsync();
        StorageFile file = await folder.GetFileAsync(Note.Filename);

        Debug.WriteLine("Saving note " + Note.Title + " to filename " + Note.Filename);
        await FileIO.WriteTextAsync(file, Content);

        await SemanticIndex.Instance.AddOrReplaceContent(Content, Note.Id, "note", (o, p) => Debug.WriteLine($"Indexing note {Note.Title} {p * 100}%"));
    }

    private string MakeTimestampsClickable(string topicsText, int attachmentId)
    {
        // Pattern to match timestamps in format (MM:SS) or (H:MM:SS)
        string pattern = @"\((\d{1,2}:\d{2}(?::\d{2})?)\)";

        return Regex.Replace(topicsText, pattern, match =>
        {
            string timestamp = match.Groups[1].Value;
            // Create a clickable link format that includes visual formatting
            // Format: **[(timestamp)](audio://attachmentId/timestamp)**
            return $"**[({timestamp})](audio://{attachmentId}/{timestamp})**";
        });
    }

    public static TimeSpan ParseTimestamp(string timestamp)
    {
        // Parse timestamps in format "MM:SS" or "H:MM:SS"
        string[] parts = timestamp.Split(':');
        if (parts.Length == 2)
        {
            // MM:SS format
            return new TimeSpan(0, int.Parse(parts[0]), int.Parse(parts[1]));
        }
        else if (parts.Length == 3)
        {
            // H:MM:SS format
            return new TimeSpan(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
        }

        return TimeSpan.Zero;
    }

    public async Task AddAttachmentAsync(SoftwareBitmap bitmap)
    {
        // save bitmap to file
        StorageFolder attachmentsFolder = await Utils.GetAttachmentsFolderAsync();
        StorageFile file = await attachmentsFolder.CreateFileAsync(Guid.NewGuid().ToString() + ".png", CreationCollisionOption.GenerateUniqueName);
        using (IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite))
        {
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
        }

        await AddAttachmentAsync(file);
    }
}
