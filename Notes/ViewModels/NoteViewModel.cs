using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Notes.AI;
using Notes.AI.Embeddings;
using Notes.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Notes.ViewModels
{
    public partial class NoteViewModel : ObservableObject
    {
        public readonly Note Note;

        [ObservableProperty]
        private ObservableCollection<AttachmentViewModel> attachments = new();

        [ObservableProperty]
        private ObservableCollection<string> todos = new();

        [ObservableProperty]
        private bool todosLoading = false;

        private DispatcherTimer _saveTimer;
        private bool _contentLoaded = false;

        public DispatcherQueue DispatcherQueue { get; set; }

        public NoteViewModel(Note note)
        {
            Note = note;
            _saveTimer = new DispatcherTimer();
            _saveTimer.Interval = TimeSpan.FromSeconds(5);
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
            var folder = await Utils.GetLocalFolderAsync();
            var file = await folder.GetFileAsync(Note.Filename);

            await file.RenameAsync(value.Trim() + Utils.FileExtension, NameCollisionOption.GenerateUniqueName);
            Note.Filename = file.Name;
            await AppDataContext.SaveCurrentAsync();
        }

        private async Task SaveContentAsync()
        {
            var folder = await Utils.GetLocalFolderAsync();
            var file = await folder.GetFileAsync(Note.Filename);
            await FileIO.WriteTextAsync(file, Content);
        }

        public async Task LoadContentAsync()
        {
            if (_contentLoaded)
            {
                return;
            }

            _contentLoaded = true;

            var folder = await Utils.GetLocalFolderAsync();
            var file = await folder.GetFileAsync(Note.Filename);
            content = await FileIO.ReadTextAsync(file);

            var context = await AppDataContext.GetCurrentAsync();
            var attachments = context.Attachments.Where(a => a.NoteId == Note.Id).ToList();
            foreach (var attachment in attachments)
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

            var attachmentsFolder = await Utils.GetAttachmentsFolderAsync();
            bool shouldCopyFile = true;

            var attachment = new Attachment()
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

            var context = await AppDataContext.GetCurrentAsync();
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

            var attachment = attachmentViewModel.Attachment;
            Note.Attachments.Remove(attachment);

            var attachmentsFolder = await Utils.GetAttachmentsFolderAsync();
            var file = await attachmentsFolder.GetFileAsync(attachment.Filename);
            await file.DeleteAsync();

            if (attachment.IsProcessed && !string.IsNullOrEmpty(attachment.FilenameForText))
            {
                var attachmentsTranscriptFolder = await Utils.GetAttachmentsTranscriptsFolderAsync();
                var transcriptFile = await attachmentsTranscriptFolder.GetFileAsync(attachment.FilenameForText);
                await transcriptFile.DeleteAsync();
            }

            var context = await AppDataContext.GetCurrentAsync();
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
                var todos = await App.ChatClient.GetTodoItemsFromText(Content);
                if (todos != null && todos.Count > 0)
                {
                    DispatcherQueue.TryEnqueue(() => Todos = new ObservableCollection<string>(todos));
                }
            }

            DispatcherQueue.TryEnqueue(() => TodosLoading = false);
        }

        public async Task SummarizeAudioAttachmentAsync(AttachmentViewModel attachmentViewModel)
        {
            if (App.ChatClient == null || !attachmentViewModel.Attachment.IsProcessed || 
                string.IsNullOrEmpty(attachmentViewModel.Attachment.FilenameForText))
            {
                return;
            }

            try
            {
                Debug.WriteLine($"[NoteViewModel] Starting audio summarization for: {attachmentViewModel.Attachment.Filename}");

                // Read the transcript file
                var transcriptsFolder = await Utils.GetAttachmentsTranscriptsFolderAsync();
                var transcriptFile = await transcriptsFolder.GetFileAsync(attachmentViewModel.Attachment.FilenameForText);
                string transcriptText = await FileIO.ReadTextAsync(transcriptFile);

                Debug.WriteLine($"[NoteViewModel] Transcript loaded, length: {transcriptText.Length} characters");

                // Check if transcript is too large and needs chunking
                const int MAX_CHUNK_SIZE = 8000; // Conservative limit for AI models
                string summaryText = "\n\n## Summary\n";

                if (transcriptText.Length <= MAX_CHUNK_SIZE)
                {
                    // Process normally for small transcripts
                    CancellationTokenSource cts = new CancellationTokenSource();
                    await foreach (var partialResult in App.ChatClient.SummarizeAudioTranscriptAsync(transcriptText, cts.Token))
                    {
                        summaryText += partialResult;
                    }
                }
                else
                {
                    // Process in chunks for large transcripts
                    Debug.WriteLine($"[NoteViewModel] Large transcript detected ({transcriptText.Length} chars), processing in chunks");
                    summaryText = await ProcessLargeTranscriptSummary(transcriptText, MAX_CHUNK_SIZE);
                }

                // Add the summary to the end of the note content
                Content = Content + summaryText + "\n";
                Debug.WriteLine($"[NoteViewModel] Summary added to note content");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NoteViewModel] ERROR: Audio summarization failed: {ex.Message}");
                throw;
            }
        }

        public async Task AddTopicsAndTimestampsAsync(AttachmentViewModel attachmentViewModel)
        {
            if (App.ChatClient == null || !attachmentViewModel.Attachment.IsProcessed || 
                string.IsNullOrEmpty(attachmentViewModel.Attachment.FilenameForText))
            {
                return;
            }

            try
            {
                Debug.WriteLine($"[NoteViewModel] Starting topics and timestamps extraction for: {attachmentViewModel.Attachment.Filename}");

                // Read the transcript file
                var transcriptsFolder = await Utils.GetAttachmentsTranscriptsFolderAsync();
                var transcriptFile = await transcriptsFolder.GetFileAsync(attachmentViewModel.Attachment.FilenameForText);
                string transcriptText = await FileIO.ReadTextAsync(transcriptFile);

                Debug.WriteLine($"[NoteViewModel] Transcript loaded, length: {transcriptText.Length} characters");

                // Check if transcript is too large and needs chunking
                const int MAX_CHUNK_SIZE = 8000; // Conservative limit for AI models
                string topicsText = "\n\n## Topics\n";

                if (transcriptText.Length <= MAX_CHUNK_SIZE)
                {
                    // Process normally for small transcripts
                    CancellationTokenSource cts = new CancellationTokenSource();
                    await foreach (var partialResult in App.ChatClient.ExtractTopicsAndTimestampsAsync(transcriptText, cts.Token))
                    {
                        topicsText += partialResult;
                    }
                }
                else
                {
                    // Process in chunks for large transcripts
                    Debug.WriteLine($"[NoteViewModel] Large transcript detected ({transcriptText.Length} chars), processing in chunks");
                    topicsText = await ProcessLargeTranscriptTopics(transcriptText, MAX_CHUNK_SIZE, attachmentViewModel.Attachment.Id);
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

        private async Task<string> ProcessLargeTranscriptSummary(string transcriptText, int maxChunkSize)
        {
            Debug.WriteLine($"[NoteViewModel] Processing large transcript summary in chunks");
            
            var chunks = SplitTranscriptIntoChunks(transcriptText, maxChunkSize);
            var chunkSummaries = new List<string>();

            Debug.WriteLine($"[NoteViewModel] Split transcript into {chunks.Count} chunks");

            // Process each chunk
            for (int i = 0; i < chunks.Count; i++)
            {
                Debug.WriteLine($"[NoteViewModel] Processing summary chunk {i + 1}/{chunks.Count}");
                
                try
                {
                    string chunkSummary = "";
                    CancellationTokenSource cts = new CancellationTokenSource();
                    
                    await foreach (var partialResult in App.ChatClient.SummarizeAudioTranscriptAsync(chunks[i], cts.Token))
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
                    var result = "\n\n## Summary\n";
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
                        CancellationTokenSource cts = new CancellationTokenSource();
                        
                        await foreach (var partialResult in App.ChatClient.SummarizeAudioTranscriptAsync(combinedSummaries, cts.Token))
                        {
                            finalSummary += partialResult;
                        }
                        
                        return "\n\n## Summary\n" + finalSummary + "\n";
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[NoteViewModel] ERROR: Failed to create final summary: {ex.Message}");
                        // Fall back to individual summaries
                        var result = "\n\n## Summary\n";
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
            
            var chunks = SplitTranscriptIntoChunks(transcriptText, maxChunkSize);
            var allTopics = new List<string>();

            Debug.WriteLine($"[NoteViewModel] Split transcript into {chunks.Count} chunks");

            // Process each chunk
            for (int i = 0; i < chunks.Count; i++)
            {
                Debug.WriteLine($"[NoteViewModel] Processing topics chunk {i + 1}/{chunks.Count}");
                
                try
                {
                    string chunkTopics = "";
                    CancellationTokenSource cts = new CancellationTokenSource();
                    
                    await foreach (var partialResult in App.ChatClient.ExtractTopicsAndTimestampsAsync(chunks[i], cts.Token))
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
            var chunks = new List<string>();
            
            // Try to split on timestamp boundaries first
            var timestampPattern = @"<\|[\d.]+\|>";
            var matches = Regex.Matches(transcriptText, timestampPattern);
            
            if (matches.Count > 1)
            {
                // Split based on timestamps to preserve context
                var currentChunk = "";
                var currentPosition = 0;
                
                foreach (Match match in matches)
                {
                    var nextSegment = transcriptText.Substring(currentPosition, match.Index - currentPosition + match.Length);
                    
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
                    currentChunk += transcriptText.Substring(currentPosition);
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
            var folder = await Utils.GetLocalFolderAsync();
            var file = await folder.GetFileAsync(Note.Filename);

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
            var parts = timestamp.Split(':');
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
            var attachmentsFolder = await Utils.GetAttachmentsFolderAsync();
            var file = await attachmentsFolder.CreateFileAsync(Guid.NewGuid().ToString() + ".png", CreationCollisionOption.GenerateUniqueName);
            using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
            }

            await AddAttachmentAsync(file);
        }
    }
}
