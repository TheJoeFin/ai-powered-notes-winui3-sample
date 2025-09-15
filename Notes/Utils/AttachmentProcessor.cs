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

namespace Notes;

public static class AttachmentProcessor
{
    private static readonly List<Attachment> _toBeProcessed = [];
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
            Attachment? attachment = _toBeProcessed[0];
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
                else if (attachment.Type is NoteAttachmentType.Audio or NoteAttachmentType.Video)
                {
                    Debug.WriteLine($"[AttachmentProcessor] CALLING ProcessAudio for: {attachment.Filename}");
                    await ProcessAudio(attachment);
                }
                else if (attachment.Type == NoteAttachmentType.PDF)
                {
                    Debug.WriteLine($"[AttachmentProcessor] CALLING ProcessPdf for: {attachment.Filename}");
                    await ProcessPdf(attachment);
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

    private static async Task ProcessImage(Attachment attachment, EventHandler<float>? progress = null)
    {
        Debug.WriteLine($"[AttachmentProcessor] Starting image processing for: {attachment.Filename}");

        try
        {
            // get softwarebitmap from file
            StorageFolder attachmentsFolder = await Utils.GetAttachmentsFolderAsync();
            StorageFile file = await attachmentsFolder.GetFileAsync(attachment.Filename);
            Debug.WriteLine($"[AttachmentProcessor] Image file loaded: {file.Path}");

            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync();
            Debug.WriteLine($"[AttachmentProcessor] Image decoded successfully");

            ImageText? recognizedText = await TextRecognition.GetTextFromImage(softwareBitmap);
            if (recognizedText == null)
            {
                Debug.WriteLine($"[AttachmentProcessor] No text recognized in image: {attachment.Filename}");
                attachment.IsProcessed = true;
                InvokeAttachmentProcessedComplete(attachment);
                return;
            }

            Debug.WriteLine($"[AttachmentProcessor] Text recognition completed for: {attachment.Filename}");
            string joinedText = string.Join("\n", recognizedText.Lines.Select(l => l.Text));
            string serializedText = JsonSerializer.Serialize(recognizedText);

            string filename = await SaveTextToFileAsync(serializedText, file.DisplayName + ".txt");
            attachment.FilenameForText = filename;
            Debug.WriteLine($"[AttachmentProcessor] Text saved to: {filename}");

            await SemanticIndex.Instance.AddOrReplaceContent(joinedText, attachment.Id, "attachment", (o, p) =>
            {
                progress?.Invoke("Indexing image", 0.5f + (p / 2));
            });

            // Use dispatcher for UI thread safety
            if (MainWindow.Instance?.DispatcherQueue != null)
            {
                MainWindow.Instance.DispatcherQueue.TryEnqueue(() =>
                {
                    attachment.IsProcessed = true;
                });
            }
            else
            {
                attachment.IsProcessed = true;
            }

            InvokeAttachmentProcessedComplete(attachment);
            Debug.WriteLine($"[AttachmentProcessor] Image processing completed for: {attachment.Filename}");

            AppDataContext context = await AppDataContext.GetCurrentAsync();
            context.Update(attachment);
            await context.SaveChangesAsync();
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
                StorageFolder attachmentsFolder = await Utils.GetAttachmentsFolderAsync();
                StorageFile file = await attachmentsFolder.GetFileAsync(attachment.Filename);
                Debug.WriteLine($"[AttachmentProcessor] Audio file loaded: {file.Path}");
                Debug.WriteLine($"[AttachmentProcessor] File size: {(await file.GetBasicPropertiesAsync()).Size} bytes");

                Debug.WriteLine("[AttachmentProcessor] Starting Whisper transcription...");
                List<WhisperTranscribedChunk> transcribedChunks = await Whisper.TranscribeAsync(file, (o, p) =>
                {
                    Debug.WriteLine($"[AttachmentProcessor] Transcription progress: {p * 100:F1}%");
                    AttachmentProcessed?.Invoke(null, new AttachmentProcessedEventArgs
                    {
                        AttachmentId = attachment.Id,
                        Progress = p / 2,
                        ProcessingStep = "Transcribing audio"
                    });
                });

                Debug.WriteLine($"[AttachmentProcessor] Transcription completed. Chunks: {transcribedChunks?.Count ?? 0}");

                if (transcribedChunks != null && transcribedChunks.Count > 0)
                {
                    string textToSave = string.Join("\n", transcribedChunks.Select(t => $@"<|{t.Start:0.00}|>{t.Text}<|{t.End:0.00}|>"));
                    Debug.WriteLine($"[AttachmentProcessor] Generated transcription text length: {textToSave.Length} characters");

                    string filename = await SaveTextToFileAsync(textToSave, file.DisplayName + ".txt");
                    attachment.FilenameForText = filename;
                    Debug.WriteLine($"[AttachmentProcessor] Transcription saved to: {filename}");

                    string textToIndex = string.Join(" ", transcribedChunks.Select(t => t.Text));
                    Debug.WriteLine($"[AttachmentProcessor] Starting semantic indexing...");

                    try
                    {
                        await SemanticIndex.Instance.AddOrReplaceContent(textToIndex, attachment.Id, "attachment", (o, p) =>
                        {
                            Debug.WriteLine($"[AttachmentProcessor] Indexing progress: {p * 100:F1}%");
                            if (AttachmentProcessed != null)
                            {
                                // Use dispatcher for thread-safe UI updates
                                if (MainWindow.Instance?.DispatcherQueue != null)
                                {
                                    MainWindow.Instance.DispatcherQueue.TryEnqueue(() =>
                                    {
                                        try
                                        {
                                            AttachmentProcessed.Invoke(null, new AttachmentProcessedEventArgs
                                            {
                                                AttachmentId = attachment.Id,
                                                Progress = 0.5f + (p / 2),
                                                ProcessingStep = "Indexing audio transcript"
                                            });
                                        }
                                        catch (Exception ex)
                                        {
                                            Debug.WriteLine($"[AttachmentProcessor] ERROR: Failed to invoke progress event: {ex.Message}");
                                        }
                                    });
                                }
                                else
                                {
                                    // Fallback for direct invocation
                                    try
                                    {
                                        AttachmentProcessed.Invoke(null, new AttachmentProcessedEventArgs
                                        {
                                            AttachmentId = attachment.Id,
                                            Progress = 0.5f + (p / 2),
                                            ProcessingStep = "Indexing audio transcript"
                                        });
                                    }
                                    catch (Exception ex)
                                    {
                                        Debug.WriteLine($"[AttachmentProcessor] ERROR: Failed to invoke progress event (direct): {ex.Message}");
                                    }
                                }
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

                // Use dispatcher for UI thread safety when marking as processed
                if (MainWindow.Instance?.DispatcherQueue != null)
                {
                    MainWindow.Instance.DispatcherQueue.TryEnqueue(() =>
                    {
                        attachment.IsProcessed = true;
                    });
                }
                else
                {
                    attachment.IsProcessed = true;
                }

                InvokeAttachmentProcessedComplete(attachment);
                Debug.WriteLine($"[AttachmentProcessor] Audio processing completed for: {attachment.Filename}");

                AppDataContext context = await AppDataContext.GetCurrentAsync();
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

    private static async Task ProcessPdf(Attachment attachment)
    {
        Debug.WriteLine($"[AttachmentProcessor] Starting PDF processing for: {attachment.Filename}");

        try
        {
            StorageFolder attachmentsFolder = await Utils.GetAttachmentsFolderAsync();
            StorageFile file = await attachmentsFolder.GetFileAsync(attachment.Filename);
            Debug.WriteLine($"[AttachmentProcessor] PDF file loaded: {file.Path}");

            // Extract text from PDF
            string extractedText = await PdfProcessor.ExtractTextFromPdfAsync(file);

            // Always save the result, even if it's an error message
            // This allows the user to see what went wrong
            Debug.WriteLine($"[AttachmentProcessor] Text extraction completed for: {attachment.Filename}");
            Debug.WriteLine($"[AttachmentProcessor] Extracted text length: {extractedText.Length} characters");

            // Save extracted text to file
            string filename = await SaveTextToFileAsync(extractedText, file.DisplayName + ".txt");
            attachment.FilenameForText = filename;
            Debug.WriteLine($"[AttachmentProcessor] Text saved to: {filename}");

            // Only add to semantic index if we got meaningful content
            // (not just error messages)
            if (!string.IsNullOrWhiteSpace(extractedText) &&
                !extractedText.Contains("PDF Text Extraction Failed") &&
                extractedText.Length > 100)
            {
                try
                {
                    await SemanticIndex.Instance.AddOrReplaceContent(extractedText, attachment.Id, "attachment", (o, p) =>
                    {
                        Debug.WriteLine($"[AttachmentProcessor] PDF indexing progress: {p * 100:F1}%");
                        if (AttachmentProcessed != null)
                        {
                            if (MainWindow.Instance?.DispatcherQueue != null)
                            {
                                MainWindow.Instance.DispatcherQueue.TryEnqueue(() =>
                                {
                                    try
                                    {
                                        AttachmentProcessed.Invoke(null, new AttachmentProcessedEventArgs
                                        {
                                            AttachmentId = attachment.Id,
                                            Progress = 0.5f + (p / 2),
                                            ProcessingStep = "Indexing PDF content"
                                        });
                                    }
                                    catch (Exception ex)
                                    {
                                        Debug.WriteLine($"[AttachmentProcessor] ERROR: Failed to invoke progress event: {ex.Message}");
                                    }
                                });
                            }
                        }
                    });
                    Debug.WriteLine("[AttachmentProcessor] PDF semantic indexing completed");
                }
                catch (Exception indexEx)
                {
                    Debug.WriteLine($"[AttachmentProcessor] WARNING: PDF semantic indexing failed: {indexEx.Message}");
                    // Continue processing even if indexing fails
                }
            }
            else
            {
                Debug.WriteLine("[AttachmentProcessor] Skipping semantic indexing due to insufficient or error content");
            }

            // Use dispatcher for UI thread safety
            if (MainWindow.Instance?.DispatcherQueue != null)
            {
                MainWindow.Instance.DispatcherQueue.TryEnqueue(() =>
                {
                    attachment.IsProcessed = true;
                });
            }
            else
            {
                attachment.IsProcessed = true;
            }

            InvokeAttachmentProcessedComplete(attachment);
            Debug.WriteLine($"[AttachmentProcessor] PDF processing completed for: {attachment.Filename}");

            AppDataContext context = await AppDataContext.GetCurrentAsync();
            context.Update(attachment);
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AttachmentProcessor] ERROR: PDF processing failed for {attachment.Filename}: {ex.Message}");
            Debug.WriteLine($"[AttachmentProcessor] Exception details: {ex}");

            try
            {
                // Even if processing fails, save an error message so the user knows what happened
                string errorText = $"PDF Processing Failed\n\nFile: {attachment.Filename}\nError: {ex.Message}\n\nThe PDF file may be:\n- Password protected\n- Corrupted\n- In an unsupported format\n- An image-based/scanned document";
                string filename = await SaveTextToFileAsync(errorText, attachment.Filename + ".error.txt");
                attachment.FilenameForText = filename;
                attachment.IsProcessed = true;

                InvokeAttachmentProcessedComplete(attachment);

                AppDataContext context = await AppDataContext.GetCurrentAsync();
                context.Update(attachment);
                await context.SaveChangesAsync();

                Debug.WriteLine($"[AttachmentProcessor] Error message saved for failed PDF: {filename}");
            }
            catch (Exception saveEx)
            {
                Debug.WriteLine($"[AttachmentProcessor] CRITICAL: Failed to save error message: {saveEx.Message}");
                throw; // Re-throw if we can't even save an error message
            }
        }
    }

    private async static Task<string> SaveTextToFileAsync(string text, string filename)
    {
        Debug.WriteLine($"[AttachmentProcessor] Saving text to file: {filename}");

        try
        {
            StorageFolder stateAttachmentsFolder = await Utils.GetAttachmentsTranscriptsFolderAsync();
            StorageFile file = await stateAttachmentsFolder.CreateFileAsync(filename, CreationCollisionOption.GenerateUniqueName);
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

        try
        {
            // Use MainWindow dispatcher to safely update UI from background thread
            if (MainWindow.Instance?.DispatcherQueue != null)
            {
                MainWindow.Instance.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        attachment.IsProcessed = true;

                        AttachmentProcessed?.Invoke(null, new AttachmentProcessedEventArgs
                        {
                            AttachmentId = attachment.Id,
                            Progress = 1,
                            ProcessingStep = "Complete"
                        });
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AttachmentProcessor] ERROR: Failed to update UI in completion handler: {ex.Message}");
                    }
                });
            }
            else
            {
                // Fallback: direct update if no dispatcher available
                attachment.IsProcessed = true;

                AttachmentProcessed?.Invoke(null, new AttachmentProcessedEventArgs
                {
                    AttachmentId = attachment.Id,
                    Progress = 1,
                    ProcessingStep = "Complete"
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AttachmentProcessor] ERROR: Exception in completion handler: {ex.Message}");
            Debug.WriteLine($"[AttachmentProcessor] Exception details: {ex}");

            // Still try to mark as processed even if events fail
            try
            {
                attachment.IsProcessed = true;
            }
            catch (Exception innerEx)
            {
                Debug.WriteLine($"[AttachmentProcessor] CRITICAL: Failed to mark attachment as processed: {innerEx.Message}");
            }
        }
    }
}

public class AttachmentProcessedEventArgs
{
    public int AttachmentId { get; set; }
    public float Progress { get; set; }
    public string? ProcessingStep { get; set; }
}
