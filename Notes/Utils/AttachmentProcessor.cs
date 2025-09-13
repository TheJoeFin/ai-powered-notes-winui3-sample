using Notes.AI.Embeddings;
using Notes.AI.TextRecognition;
using Notes.AI.VoiceRecognition;
using Notes.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Notes
{
    public static class AttachmentProcessor
    {
        private static List<Attachment> _toBeProcessed = new();
        private static bool _isProcessing = false;

        public static EventHandler<AttachmentProcessedEventArgs> AttachmentProcessed;

        public async static Task AddAttachment(Attachment attachment)
        {
            Debug.WriteLine("===== [AttachmentProcessor] AddAttachment ENTRY =====");
            Debug.WriteLine($"[AttachmentProcessor] URGENT: Adding attachment for processing");
            Debug.WriteLine($"[AttachmentProcessor] Filename: {attachment?.Filename ?? "NULL"}");
            Debug.WriteLine($"[AttachmentProcessor] Type: {attachment?.Type ?? NoteAttachmentType.Document}");
            Debug.WriteLine($"[AttachmentProcessor] ID: {attachment?.Id ?? -1}");
            Debug.WriteLine($"[AttachmentProcessor] IsProcessed: {attachment?.IsProcessed ?? false}");
            Debug.WriteLine($"[AttachmentProcessor] Current queue size: {_toBeProcessed?.Count ?? -1}");
            Debug.WriteLine($"[AttachmentProcessor] Currently processing: {_isProcessing}");

            if (attachment == null)
            {
                Debug.WriteLine("[AttachmentProcessor] ERROR: Attachment is null!");
                return;
            }

            _toBeProcessed.Add(attachment);
            Debug.WriteLine($"[AttachmentProcessor] Added to queue. New queue size: {_toBeProcessed.Count}");

            if (!_isProcessing)
            {
                try
                {
                    Debug.WriteLine("[AttachmentProcessor] STARTING PROCESSING PIPELINE");
                    _isProcessing = true;
                    await Process();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AttachmentProcessor] CRITICAL ERROR: Error processing attachment: {ex.Message}");
                    Debug.WriteLine($"[AttachmentProcessor] Exception details: {ex}");
                    Debug.WriteLine($"[AttachmentProcessor] Stack trace: {ex.StackTrace}");
                }
                finally
                {
                    _isProcessing = false;
                    Debug.WriteLine("[AttachmentProcessor] Processing pipeline completed");
                }
            }
            else
            {
                Debug.WriteLine("[AttachmentProcessor] Already processing, attachment queued");
            }

            Debug.WriteLine("===== [AttachmentProcessor] AddAttachment EXIT =====");
        }

        private static async Task Process()
        {
            Debug.WriteLine("===== [AttachmentProcessor] Process() ENTRY =====");
            Debug.WriteLine($"[AttachmentProcessor] URGENT: Processing queue with {_toBeProcessed?.Count ?? -1} items");

            if (_toBeProcessed == null)
            {
                Debug.WriteLine("[AttachmentProcessor] CRITICAL ERROR: _toBeProcessed is null!");
                return;
            }

            while (_toBeProcessed.Count > 0)
            {
                var attachment = _toBeProcessed[0];
                _toBeProcessed.RemoveAt(0);

                Debug.WriteLine($"[AttachmentProcessor] ===== PROCESSING ATTACHMENT =====");
                Debug.WriteLine($"[AttachmentProcessor] Filename: {attachment?.Filename ?? "NULL"}");
                Debug.WriteLine($"[AttachmentProcessor] Type: {attachment?.Type ?? NoteAttachmentType.Document}");
                Debug.WriteLine($"[AttachmentProcessor] IsProcessed: {attachment?.IsProcessed ?? false}");

                if (attachment.IsProcessed)
                {
                    Debug.WriteLine($"[AttachmentProcessor] Skipping already processed attachment: {attachment.Filename}");
                    continue;
                }

                try
                {
                    if (attachment.Type == NoteAttachmentType.Image)
                    {
                        Debug.WriteLine($"[AttachmentProcessor] CALLING ProcessImage for: {attachment.Filename}");
                        await ProcessImage(attachment);
                    }
                    else if (attachment.Type == NoteAttachmentType.Audio || attachment.Type == NoteAttachmentType.Video)
                    {
                        Debug.WriteLine($"[AttachmentProcessor] CALLING ProcessAudio for: {attachment.Filename}");
                        await ProcessAudio(attachment);
                    }
                    else
                    {
                        Debug.WriteLine($"[AttachmentProcessor] Unknown attachment type: {attachment.Type} for file: {attachment.Filename}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AttachmentProcessor] CRITICAL ERROR: Failed to process attachment {attachment.Filename}: {ex.Message}");
                    Debug.WriteLine($"[AttachmentProcessor] Exception details: {ex}");
                    Debug.WriteLine($"[AttachmentProcessor] Stack trace: {ex.StackTrace}");
                }
            }

            Debug.WriteLine("===== [AttachmentProcessor] Process() EXIT =====");
        }

        private static async Task ProcessImage(Models.Attachment attachment, EventHandler<float>? progress = null)
        {
            Debug.WriteLine($"[AttachmentProcessor] Starting image processing for: {attachment.Filename}");

            try
            {
                // get softwarebitmap from file
                var attachmentsFolder = await Utils.GetAttachmentsFolderAsync();
                var file = await attachmentsFolder.GetFileAsync(attachment.Filename);
                Debug.WriteLine($"[AttachmentProcessor] Image file loaded: {file.Path}");

                using (IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read))
                {
                    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                    SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync();
                    Debug.WriteLine($"[AttachmentProcessor] Image decoded successfully");

                    var recognizedText = await TextRecognition.GetTextFromImage(softwareBitmap);
                    if (recognizedText == null)
                    {
                        Debug.WriteLine($"[AttachmentProcessor] No text recognized in image: {attachment.Filename}");
                        attachment.IsProcessed = true;
                        InvokeAttachmentProcessedComplete(attachment);
                        return;
                    }

                    Debug.WriteLine($"[AttachmentProcessor] Text recognition completed for: {attachment.Filename}");
                    var joinedText = string.Join("\n", recognizedText.Lines.Select(l => l.Text));
                    var serializedText = JsonSerializer.Serialize(recognizedText);

                    var filename = await SaveTextToFileAsync(serializedText, file.DisplayName + ".txt");
                    attachment.FilenameForText = filename;
                    Debug.WriteLine($"[AttachmentProcessor] Text saved to: {filename}");

                    await SemanticIndex.Instance.AddOrReplaceContent(joinedText, attachment.Id, "attachment", (o, p) =>
                    {
                        if (progress != null)
                        {
                            progress.Invoke("Indexing image", 0.5f + (p / 2));
                        }
                    });

                    attachment.IsProcessed = true;
                    InvokeAttachmentProcessedComplete(attachment);
                    Debug.WriteLine($"[AttachmentProcessor] Image processing completed for: {attachment.Filename}");

                    var context = await AppDataContext.GetCurrentAsync();
                    context.Update(attachment);
                    await context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AttachmentProcessor] ERROR: Image processing failed for {attachment.Filename}: {ex.Message}");
                Debug.WriteLine($"[AttachmentProcessor] Exception details: {ex}");
                throw;
            }
        }

        private static async Task ProcessAudio(Attachment attachment)
        {
            Debug.WriteLine($"[AttachmentProcessor] Starting audio processing for: {attachment.Filename}");

            await Task.Run(async () =>
            {
                try
                {
                    var attachmentsFolder = await Utils.GetAttachmentsFolderAsync();
                    var file = await attachmentsFolder.GetFileAsync(attachment.Filename);
                    Debug.WriteLine($"[AttachmentProcessor] Audio file loaded: {file.Path}");
                    Debug.WriteLine($"[AttachmentProcessor] File size: {(await file.GetBasicPropertiesAsync()).Size} bytes");

                    Debug.WriteLine("[AttachmentProcessor] Starting Whisper transcription...");
                    var transcribedChunks = await Whisper.TranscribeAsync(file, (o, p) =>
                    {
                        Debug.WriteLine($"[AttachmentProcessor] Transcription progress: {p * 100:F1}%");
                        if (AttachmentProcessed != null)
                        {
                            AttachmentProcessed.Invoke(null, new AttachmentProcessedEventArgs
                            {
                                AttachmentId = attachment.Id,
                                Progress = p / 2,
                                ProcessingStep = "Transcribing audio"
                            });
                        }
                    });

                    Debug.WriteLine($"[AttachmentProcessor] Transcription completed. Chunks: {transcribedChunks?.Count ?? 0}");

                    if (transcribedChunks != null && transcribedChunks.Count > 0)
                    {
                        var textToSave = string.Join("\n", transcribedChunks.Select(t => $@"<|{t.Start:0.00}|>{t.Text}<|{t.End:0.00}|>"));
                        Debug.WriteLine($"[AttachmentProcessor] Generated transcription text length: {textToSave.Length} characters");

                        var filename = await SaveTextToFileAsync(textToSave, file.DisplayName + ".txt");
                        attachment.FilenameForText = filename;
                        Debug.WriteLine($"[AttachmentProcessor] Transcription saved to: {filename}");

                        var textToIndex = string.Join(" ", transcribedChunks.Select(t => t.Text));
                        Debug.WriteLine($"[AttachmentProcessor] Starting semantic indexing...");

                        try
                        {
                            await SemanticIndex.Instance.AddOrReplaceContent(textToIndex, attachment.Id, "attachment", (o, p) =>
                            {
                                Debug.WriteLine($"[AttachmentProcessor] Indexing progress: {p * 100:F1}%");
                                if (AttachmentProcessed != null)
                                {
                                    AttachmentProcessed.Invoke(null, new AttachmentProcessedEventArgs
                                    {
                                        AttachmentId = attachment.Id,
                                        Progress = 0.5f + p / 2,
                                        ProcessingStep = "Indexing audio transcript"
                                    });
                                }
                            });

                            Debug.WriteLine("[AttachmentProcessor] Semantic indexing completed");
                        }
                        catch (Exception indexingEx)
                        {
                            Debug.WriteLine($"[AttachmentProcessor] WARNING: Semantic indexing failed, but transcription will continue: {indexingEx.Message}");
                            Debug.WriteLine($"[AttachmentProcessor] Indexing exception details: {indexingEx}");
                            // Continue processing even if indexing fails - the transcription is still valuable
                        }
                    }
                    else
                    {
                        Debug.WriteLine("[AttachmentProcessor] WARNING: No transcription chunks generated");
                    }

                    attachment.IsProcessed = true;
                    InvokeAttachmentProcessedComplete(attachment);
                    Debug.WriteLine($"[AttachmentProcessor] Audio processing completed for: {attachment.Filename}");

                    var context = await AppDataContext.GetCurrentAsync();
                    context.Update(attachment);
                    await context.SaveChangesAsync();
                    Debug.WriteLine("[AttachmentProcessor] Database updated");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AttachmentProcessor] ERROR: Audio processing failed for {attachment.Filename}: {ex.Message}");
                    Debug.WriteLine($"[AttachmentProcessor] Exception details: {ex}");
                    Debug.WriteLine($"[AttachmentProcessor] Stack trace: {ex.StackTrace}");
                    throw;
                }
            });
        }

        private async static Task<string> SaveTextToFileAsync(string text, string filename)
        {
            Debug.WriteLine($"[AttachmentProcessor] Saving text to file: {filename}");

            try
            {
                var stateAttachmentsFolder = await Utils.GetAttachmentsTranscriptsFolderAsync();
                var file = await stateAttachmentsFolder.CreateFileAsync(filename, CreationCollisionOption.GenerateUniqueName);
                await FileIO.WriteTextAsync(file, text);
                Debug.WriteLine($"[AttachmentProcessor] Text file saved: {file.Path}");
                return file.Name;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AttachmentProcessor] ERROR: Failed to save text file {filename}: {ex.Message}");
                throw;
            }
        }

        private static void InvokeAttachmentProcessedComplete(Attachment attachment)
        {
            Debug.WriteLine($"[AttachmentProcessor] Attachment processing completed: {attachment.Filename}");

            if (AttachmentProcessed != null)
            {
                AttachmentProcessed.Invoke(null, new AttachmentProcessedEventArgs
                {
                    AttachmentId = attachment.Id,
                    Progress = 1,
                    ProcessingStep = "Complete"
                });
            }
        }
    }

    public class AttachmentProcessedEventArgs
    {
        public int AttachmentId { get; set; }
        public float Progress { get; set; }
        public string? ProcessingStep { get; set; }
    }
}
