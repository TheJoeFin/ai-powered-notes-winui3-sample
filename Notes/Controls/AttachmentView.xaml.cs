using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Threading;
using Windows.Storage;
using Windows.Media.Core;
using Microsoft.UI.Dispatching;
using Notes.Models;
using Notes.AI.VoiceRecognition;
using Notes.ViewModels;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Notes.AI.TextRecognition;
using System.Diagnostics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Notes.Controls
{
    public sealed partial class AttachmentView : UserControl
    {
        private CancellationTokenSource _cts;
        private DispatcherQueue _dispatcher;
        private Timer _timer;

        public ObservableCollection<TranscriptionBlock> TranscriptionBlocks { get; set; } = new ObservableCollection<Models.TranscriptionBlock>();
        public AttachmentViewModel AttachmentVM { get; set; }
    public bool AutoScrollEnabled { get; set; } = true;

        public AttachmentView()
        {
            this.InitializeComponent();
            this.Visibility = Visibility.Collapsed;
            this._dispatcher = DispatcherQueue.GetForCurrentThread();
        }

        public async Task Show()
        {
            this.Visibility = Visibility.Visible;
        }

        public void Hide()
        {
            TranscriptionBlocks.Clear();
            transcriptLoadingProgressRing.IsActive = false;
            AttachmentImage.Source = null;
            WaveformImage.Source = null;
            this.Visibility = Visibility.Collapsed;

            if (AttachmentVM.Attachment.Type == NoteAttachmentType.Video || AttachmentVM.Attachment.Type == NoteAttachmentType.Audio)
            {
                ResetMediaPlayer();  
            }
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
        }

        private void BackgroundTapped(object sender, TappedRoutedEventArgs e)
        {
            // hide the search view only when the backround was tapped but not any of the content inside
            if (e.OriginalSource == Root)
                this.Hide();
        }

        public async Task UpdateAttachment(AttachmentViewModel attachment, string? attachmentText = null)
        {
            AttachmentImageTextCanvas.Children.Clear();

            AttachmentVM = attachment;
            StorageFolder attachmentsFolder = await Utils.GetAttachmentsFolderAsync();
            StorageFile attachmentFile = await attachmentsFolder.GetFileAsync(attachment.Attachment.Filename);
            switch(AttachmentVM.Attachment.Type)
            {
                case NoteAttachmentType.Audio: 
                    ImageGrid.Visibility = Visibility.Collapsed;
                    MediaGrid.Visibility = Visibility.Visible;
                    RunWaitForTranscriptionTask(attachmentText);
                    WaveformImage.Source = await WaveformRenderer.GetWaveformImage(attachmentFile);
                    SetMediaPlayerSource(attachmentFile);
                    break;
                case NoteAttachmentType.Image:
                    ImageGrid.Visibility = Visibility.Visible;
                    MediaGrid.Visibility = Visibility.Collapsed;
                    AttachmentImage.Source = new BitmapImage(new Uri(attachmentFile.Path));
                    LoadImageText(attachment.Attachment.Filename);
                    break;
                case NoteAttachmentType.Video:
                    ImageGrid.Visibility = Visibility.Collapsed;
                    MediaGrid.Visibility = Visibility.Visible;
                    RunWaitForTranscriptionTask(attachmentText);
                    SetMediaPlayerSource(attachmentFile);
                    break;
            }
        }

        private async Task LoadImageText(string fileName)
        {
            var text = await TextRecognition.GetSavedText(fileName.Split('.')[0] + ".txt");
            foreach (var line in text.Lines)
            {
                var height = line.Height;
                var width = line.Width;
                AttachmentImageTextCanvas.Children.Add(
                    new Border()
                    {
                        Child = new Viewbox()
                        {
                            Child = new TextBlock()
                            {
                                Text = line.Text,
                                FontSize = 16,
                                IsTextSelectionEnabled = true,
                                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                            },
                            Stretch = Stretch.Fill,
                            StretchDirection = StretchDirection.Both,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(4),
                            Height = height,
                            Width = width,
                        },
                        Height = height + 8,
                        Width = width + 8,
                        CornerRadius = new CornerRadius(8),
                        Margin = new Thickness(line.X - 4, line.Y - 4, 0, 0),
                        RenderTransform = new RotateTransform() { Angle = text.ImageAngle },
                        BorderThickness = new Thickness(0),
                        Background = new LinearGradientBrush()
                        {
                            GradientStops = new GradientStopCollection()
                            {
                                new GradientStop() { Color = Color.FromArgb(20, 52, 185, 159), Offset = 0.1 },
                                new GradientStop() { Color = Color.FromArgb(20, 50, 181, 173), Offset = 0.5 },
                                new GradientStop() { Color = Color.FromArgb(20, 59, 177, 119), Offset = 0.9 }
                            }
                        },
                        //BorderBrush = new LinearGradientBrush()
                        //{
                        //    GradientStops = new GradientStopCollection()
                        //    {
                        //        new GradientStop() { Color = Color.FromArgb(255, 147, 89, 248), Offset = 0.1 },
                        //        new GradientStop() { Color = Color.FromArgb(255, 203, 123, 190), Offset = 0.5 },
                        //        new GradientStop() { Color = Color.FromArgb(255, 240, 184, 131), Offset = 0.9 },
                        //    },
                        //},
                    }
                );
            }
        }

        private void SetMediaPlayerSource(StorageFile file)
        {
            mediaPlayer.Source = MediaSource.CreateFromStorageFile(file);
            mediaPlayer.MediaPlayer.CurrentStateChanged += MediaPlayer_CurrentStateChanged;
        }

        private async void RunWaitForTranscriptionTask(string? transcriptionTextToTryToShow = null)
        {
            Debug.WriteLine($"[AttachmentView] RunWaitForTranscriptionTask - Attachment: {AttachmentVM?.Attachment?.Filename}");
            Debug.WriteLine($"[AttachmentView] IsProcessed: {AttachmentVM?.Attachment?.IsProcessed}");
            Debug.WriteLine($"[AttachmentView] FilenameForText: {AttachmentVM?.Attachment?.FilenameForText}");
            Debug.WriteLine($"[AttachmentView] IsProcessing: {AttachmentVM?.IsProcessing}");
            
            transcriptLoadingProgressRing.IsActive = true;
            
            _ = Task.Run(async () =>
            {
                try
                {
                    Debug.WriteLine("[AttachmentView] Starting transcription wait loop...");
                    
                    // Safety check: if marked as processing but no processing is actually happening,
                    // reset and trigger processing
                    if (AttachmentVM.IsProcessing && !AttachmentVM.Attachment.IsProcessed)
                    {
                        Debug.WriteLine("[AttachmentView] Detected potential phantom processing state - waiting 5 seconds to confirm...");
                        await Task.Delay(5000); // Wait 5 seconds to see if real processing is happening
                        
                        // If still stuck after 5 seconds and no AttachmentProcessor logs appeared, 
                        // assume phantom state and reset
                        if (AttachmentVM.IsProcessing && !AttachmentVM.Attachment.IsProcessed)
                        {
                            Debug.WriteLine("[AttachmentView] PHANTOM PROCESSING DETECTED - Resetting and triggering processing");
                            Debug.WriteLine("[AttachmentView] This indicates the attachment processor was never called or failed silently");
                            
                            // Reset the processing state on UI thread
                            _dispatcher.TryEnqueue(() =>
                            {
                                AttachmentVM.IsProcessing = false;
                            });
                            
                            // Wait a moment for the UI update to complete
                            await Task.Delay(100);
                            
                            // Trigger processing manually
                            Debug.WriteLine("[AttachmentView] Manually triggering AttachmentProcessor.AddAttachment");
                            AttachmentProcessor.AddAttachment(AttachmentVM.Attachment);
                        }
                    }
                    
                    // If attachment isn't processed and no processing is happening, trigger processing
                    if (!AttachmentVM.Attachment.IsProcessed && !AttachmentVM.IsProcessing)
                    {
                        Debug.WriteLine("[AttachmentView] Attachment not processed and not processing - triggering processing pipeline");
                        AttachmentProcessor.AddAttachment(AttachmentVM.Attachment);
                    }
                    
                    // Wait for processing to complete with timeout
                    int maxWaitTime = 300; // 300 * 500ms = 2.5 minutes timeout
                    int waitCounter = 0;
                    
                    while ((AttachmentVM.IsProcessing || !AttachmentVM.Attachment.IsProcessed) && waitCounter < maxWaitTime)
                    {
                        Debug.WriteLine($"[AttachmentView] Waiting... IsProcessing: {AttachmentVM.IsProcessing}, IsProcessed: {AttachmentVM.Attachment.IsProcessed} ({waitCounter}/{maxWaitTime})");
                        Thread.Sleep(500);
                        waitCounter++;
                    }
                    
                    if (waitCounter >= maxWaitTime)
                    {
                        Debug.WriteLine("[AttachmentView] ERROR: Transcription timed out after 2.5 minutes");
                        _dispatcher.TryEnqueue(() =>
                        {
                            transcriptLoadingProgressRing.IsActive = false;
                            // Could show timeout error message here
                        });
                        return;
                    }
                    
                    Debug.WriteLine("[AttachmentView] Processing completed, loading transcription file...");
                    
                    if (string.IsNullOrEmpty(AttachmentVM.Attachment.FilenameForText))
                    {
                        Debug.WriteLine("[AttachmentView] ERROR: No transcription file available after processing");
                        _dispatcher.TryEnqueue(() =>
                        {
                            transcriptLoadingProgressRing.IsActive = false;
                            // Could show an error message or retry button here
                        });
                        return;
                    }
                    
                    var transcriptsFolder = await Utils.GetAttachmentsTranscriptsFolderAsync();
                    StorageFile transcriptFile = await transcriptsFolder.GetFileAsync(AttachmentVM.Attachment.FilenameForText);
                    string rawTranscription = File.ReadAllText(transcriptFile.Path);
                    
                    Debug.WriteLine($"[AttachmentView] Transcription loaded: {rawTranscription.Length} characters");
                    
                    _dispatcher.TryEnqueue(() =>
                    {
                        transcriptLoadingProgressRing.IsActive = false;
                        var transcripts = WhisperUtils.ProcessTranscriptionWithTimestamps(rawTranscription);
                        
                        Debug.WriteLine($"[AttachmentView] Processed {transcripts.Count} transcription blocks");
                        
                        foreach (var t in transcripts)
                        {
                            TranscriptionBlocks.Add(new TranscriptionBlock(t.Text, t.Start, t.End));
                        }

                        if (transcriptionTextToTryToShow != null)
                        {
                            var block = TranscriptionBlocks.Where(t => t.Text.Contains(transcriptionTextToTryToShow)).FirstOrDefault();
                            if (block != null)
                            {
                                transcriptBlocksListView.SelectedItem = block;
                                ScrollTranscriptionToItem(block);
                            }
                        }
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AttachmentView] ERROR: Transcription loading failed: {ex.Message}");
                    Debug.WriteLine($"[AttachmentView] Exception details: {ex}");
                    Debug.WriteLine($"[AttachmentView] Stack trace: {ex.StackTrace}");
                    
                    _dispatcher.TryEnqueue(() =>
                    {
                        transcriptLoadingProgressRing.IsActive = false;
                        // Could show error message to user
                    });
                }
            });
        }

        private void MediaPlayer_CurrentStateChanged(Windows.Media.Playback.MediaPlayer sender, object args)
        {
            if (sender.CurrentState.ToString() == "Playing")
            {
                _timer = new Timer(CheckTimestampAndSelectTranscription, null, 0, 250);
            }
            else if(_timer != null)
            {
                _timer.Dispose();
            }
        }

        private void CheckTimestampAndSelectTranscription(object? state)
        {
            _dispatcher.TryEnqueue(() => {
                TimeSpan currentPos = mediaPlayer.MediaPlayer.Position;
                foreach (TranscriptionBlock block in TranscriptionBlocks)
                {
                    if (block.Start < currentPos & block.End > currentPos)
                    {
                        transcriptBlocksListView.SelectionChanged -= TranscriptBlocksListView_SelectionChanged;
                        transcriptBlocksListView.SelectedItem = block;
                        transcriptBlocksListView.SelectionChanged += TranscriptBlocksListView_SelectionChanged;
                        ScrollTranscriptionToItem(block);
                        break;
                    }
                }
            });   
        }

        private void ResetMediaPlayer()
        {
            if(_timer != null)
            {
                _timer.Dispose();
            } 
            mediaPlayer.MediaPlayer.CurrentStateChanged -= MediaPlayer_CurrentStateChanged;
            mediaPlayer.MediaPlayer.Pause();
            mediaPlayer.Source = null;
        }

        private void TranscriptBlocksListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ListView transcriptListView)
            {
                TranscriptionBlock selectedBlock = (TranscriptionBlock)transcriptListView.SelectedItem;
                if (selectedBlock != null)
                {
                    mediaPlayer.MediaPlayer.Position = selectedBlock.Start;
                }
            }
        }

        private void ScrollTranscriptionToItem(TranscriptionBlock block)
        {
            if(AutoScrollEnabled)
            {
                transcriptBlocksListView.ScrollIntoView(block, ScrollIntoViewAlignment.Leading);
            }
        }
    }
}
