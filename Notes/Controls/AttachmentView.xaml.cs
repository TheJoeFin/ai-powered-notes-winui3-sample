using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Notes.AI.TextRecognition;
using Notes.AI.VoiceRecognition;
using Notes.Models;
using Notes.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Storage;
using Windows.UI;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Notes.Controls;

public sealed partial class AttachmentView : UserControl
{
    private readonly CancellationTokenSource _cts;
    private readonly DispatcherQueue _dispatcher;
    private Timer _timer;

    public ObservableCollection<TranscriptionBlock> TranscriptionBlocks { get; set; } = [];
    public AttachmentViewModel AttachmentVM { get; set; }
    public bool AutoScrollEnabled { get; set; } = true;

    private double _currentZoomLevel = 1.0;
    private PdfPageData _currentPdfData;

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
        if (pdfLoadingProgressRing != null)
            pdfLoadingProgressRing.IsActive = false;
        AttachmentImage.Source = null;
        WaveformImage.Source = null;
        pdfPagesContainer?.Children.Clear();
        _currentPdfData = null;
        this.Visibility = Visibility.Collapsed;

        if (AttachmentVM?.Attachment?.Type is NoteAttachmentType.Video or NoteAttachmentType.Audio)
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
        switch (AttachmentVM.Attachment.Type)
        {
            case NoteAttachmentType.Audio:
                ImageGrid.Visibility = Visibility.Collapsed;
                MediaGrid.Visibility = Visibility.Visible;
                if (PdfGrid != null) PdfGrid.Visibility = Visibility.Collapsed;
                RunWaitForTranscriptionTask(attachmentText);
                WaveformImage.Source = await WaveformRenderer.GetWaveformImage(attachmentFile);
                SetMediaPlayerSource(attachmentFile);
                break;
            case NoteAttachmentType.Image:
                ImageGrid.Visibility = Visibility.Visible;
                MediaGrid.Visibility = Visibility.Collapsed;
                if (PdfGrid != null) PdfGrid.Visibility = Visibility.Collapsed;
                AttachmentImage.Source = new BitmapImage(new Uri(attachmentFile.Path));
                await LoadImageText(attachment.Attachment.Filename);
                break;
            case NoteAttachmentType.Video:
                ImageGrid.Visibility = Visibility.Collapsed;
                MediaGrid.Visibility = Visibility.Visible;
                if (PdfGrid != null) PdfGrid.Visibility = Visibility.Collapsed;
                RunWaitForTranscriptionTask(attachmentText);
                SetMediaPlayerSource(attachmentFile);
                break;
            case NoteAttachmentType.PDF:
                ImageGrid.Visibility = Visibility.Collapsed;
                MediaGrid.Visibility = Visibility.Collapsed;
                if (PdfGrid != null) PdfGrid.Visibility = Visibility.Visible;
                await LoadPdfText(attachment.Attachment.Filename, attachmentText);
                break;
        }
    }

    private async Task LoadPdfText(string fileName, string? searchText = null)
    {
        Debug.WriteLine($"[AttachmentView] Loading PDF content for: {fileName}");
        Debug.WriteLine($"[AttachmentView] Attachment IsProcessed: {AttachmentVM.Attachment.IsProcessed}");
        Debug.WriteLine($"[AttachmentView] FilenameForText: {AttachmentVM.Attachment.FilenameForText}");

        try
        {
            if (pdfLoadingProgressRing != null)
                pdfLoadingProgressRing.IsActive = true;

            // Try to load the PDF immediately for viewing, regardless of processing status
            StorageFolder attachmentsFolder = await Utils.GetAttachmentsFolderAsync();
            StorageFile pdfFile = await attachmentsFolder.GetFileAsync(AttachmentVM.Attachment.Filename);
            Debug.WriteLine($"[AttachmentView] PDF file found: {pdfFile.Path}");

            // First, try to extract and display the PDF content immediately
            try
            {
                Debug.WriteLine("[AttachmentView] Attempting immediate PDF display...");
                _currentPdfData = await PdfProcessor.ExtractPageDataFromPdfAsync(pdfFile);

                if (_currentPdfData != null && _currentPdfData.Pages.Any())
                {
                    Debug.WriteLine($"[AttachmentView] Successfully extracted {_currentPdfData.Pages.Count} pages");
                    DisplayPdfPages(_currentPdfData);
                    UpdatePdfInfo(_currentPdfData);

                    if (pdfLoadingProgressRing != null)
                        pdfLoadingProgressRing.IsActive = false;

                    Debug.WriteLine("[AttachmentView] PDF displayed successfully");

                    // If the attachment isn't processed yet, trigger processing in the background
                    if (!AttachmentVM.Attachment.IsProcessed)
                    {
                        Debug.WriteLine("[AttachmentView] PDF not yet processed, triggering background processing...");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await AttachmentProcessor.AddAttachment(AttachmentVM.Attachment);
                            }
                            catch (Exception processingEx)
                            {
                                Debug.WriteLine($"[AttachmentView] Background processing failed: {processingEx.Message}");
                            }
                        });
                    }

                    return; // Success! No need to wait for processing
                }
                else
                {
                    Debug.WriteLine("[AttachmentView] No pages extracted from PDF");
                }
            }
            catch (Exception immediateEx)
            {
                Debug.WriteLine($"[AttachmentView] Immediate PDF display failed: {immediateEx.Message}");
                Debug.WriteLine($"[AttachmentView] Exception details: {immediateEx}");
                // Fall through to the processing wait logic
            }

            // If immediate display failed, wait for processing if needed
            if (!AttachmentVM.Attachment.IsProcessed)
            {
                Debug.WriteLine("[AttachmentView] PDF not yet processed, waiting...");
                await WaitForPdfProcessing();
            }

            if (string.IsNullOrEmpty(AttachmentVM.Attachment.FilenameForText))
            {
                Debug.WriteLine("[AttachmentView] No text file available for PDF");
                if (pdfLoadingProgressRing != null)
                    pdfLoadingProgressRing.IsActive = false;
                ShowPdfError("PDF processing failed - no text extracted. This PDF may be password protected, corrupted, or image-based.");
                return;
            }

            // Load the processed text
            try
            {
                Debug.WriteLine($"[AttachmentView] Loading processed text from: {AttachmentVM.Attachment.FilenameForText}");
                StorageFolder transcriptsFolder = await Utils.GetAttachmentsTranscriptsFolderAsync();
                StorageFile textFile = await transcriptsFolder.GetFileAsync(AttachmentVM.Attachment.FilenameForText);
                string pdfText = await FileIO.ReadTextAsync(textFile);

                Debug.WriteLine($"[AttachmentView] Processed text loaded: {pdfText.Length} characters");
                Debug.WriteLine($"[AttachmentView] First 200 chars: {pdfText[..Math.Min(200, pdfText.Length)]}");

                // Check if it's an error message
                if (pdfText.Contains("PDF Text Extraction Failed") || pdfText.Contains("PDF Processing Failed"))
                {
                    Debug.WriteLine("[AttachmentView] Detected error content in processed text");
                    ShowPdfError(pdfText);
                }
                else
                {
                    Debug.WriteLine("[AttachmentView] Displaying processed text as simple PDF");
                    DisplaySimplePdfText(pdfText);
                    if (pdfTitleBlock != null)
                        pdfTitleBlock.Text = AttachmentVM.Attachment.Filename;
                }
            }
            catch (Exception textEx)
            {
                Debug.WriteLine($"[AttachmentView] Failed to load processed text: {textEx.Message}");
                ShowPdfError($"Failed to load PDF text: {textEx.Message}");
            }

            // If we have search text, try to scroll to it
            if (!string.IsNullOrEmpty(searchText))
            {
                Debug.WriteLine($"[AttachmentView] Searching for text in PDF: {searchText}");
                // TODO: Implement search highlighting
            }

            if (pdfLoadingProgressRing != null)
                pdfLoadingProgressRing.IsActive = false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AttachmentView] ERROR: Failed to load PDF content: {ex.Message}");
            Debug.WriteLine($"[AttachmentView] Exception details: {ex}");
            if (pdfLoadingProgressRing != null)
                pdfLoadingProgressRing.IsActive = false;
            ShowPdfError($"Error loading PDF: {ex.Message}");
        }
    }

    private void DisplayPdfPages(PdfPageData pdfData)
    {
        if (pdfPagesContainer == null) return;

        pdfPagesContainer.Children.Clear();

        foreach (PdfPageInfo page in pdfData.Pages)
        {
            Border pageContainer = CreatePdfPageElement(page);
            pdfPagesContainer.Children.Add(pageContainer);
        }
    }

    private Border CreatePdfPageElement(PdfPageInfo pageInfo)
    {
        // Create a paper-like container for each page
        Border pageContainer = new()
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            MinWidth = 600,
            MaxWidth = 800,
            Margin = new Thickness(0, 0, 0, 20),
            // Add shadow effect
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 8)
        };

        StackPanel pageContent = new()
        {
            Padding = new Thickness(40, 50, 40, 50)
        };

        // Page number header
        TextBlock pageHeader = new()
        {
            Text = $"Page {pageInfo.PageNumber}",
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20)
        };
        pageContent.Children.Add(pageHeader);

        // Use structured content if available, otherwise fall back to formatted text
        if (pageInfo.StructuredContent != null && pageInfo.StructuredContent.Any())
        {
            CreateStructuredContent(pageContent, pageInfo.StructuredContent);
        }
        else
        {
            // Fallback to enhanced formatted text display
            CreateFormattedTextContent(pageContent, pageInfo.FormattedText ?? pageInfo.Text);
        }

        pageContainer.Child = pageContent;
        return pageContainer;
    }

    private void CreateStructuredContent(StackPanel container, List<PdfTextElement> elements)
    {
        foreach (PdfTextElement element in elements)
        {
            TextBlock textBlock = new()
            {
                Text = element.Text,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black),
                SelectionHighlightColor = new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue),
                Margin = GetMarginForElement(element)
            };

            // Apply styling based on content type
            ApplyContentTypeFormatting(textBlock, element);

            container.Children.Add(textBlock);
        }
    }

    private Thickness GetMarginForElement(PdfTextElement element)
    {
        double leftMargin = element.IsIndented ? 20.0 : 0.0;
        Thickness verticalMargin = element.ElementType switch
        {
            PdfContentType.Header => new Thickness(leftMargin, 15, 0, 10),
            PdfContentType.ListItem => new Thickness(leftMargin + 10, 5, 0, 5),
            PdfContentType.Table => new Thickness(leftMargin, 8, 0, 8),
            _ => new Thickness(leftMargin, 3, 0, 3)
        };

        return verticalMargin;
    }

    private void ApplyContentTypeFormatting(TextBlock textBlock, PdfTextElement element)
    {
        switch (element.ElementType)
        {
            case PdfContentType.Header:
                textBlock.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                textBlock.FontSize = 16;
                if (element.IsCentered)
                {
                    textBlock.HorizontalAlignment = HorizontalAlignment.Center;
                }
                break;

            case PdfContentType.ListItem:
                textBlock.FontSize = 14;
                // Create a more structured list appearance
                if (element.Text.StartsWith("•") || element.Text.StartsWith("-") || element.Text.StartsWith("*"))
                {
                    // Already has bullet, just style it
                    textBlock.Margin = new Thickness(20, 3, 0, 3);
                }
                else if (System.Text.RegularExpressions.Regex.IsMatch(element.Text, @"^\d+\.\s"))
                {
                    // Numbered list
                    textBlock.Margin = new Thickness(20, 3, 0, 3);
                }
                break;

            case PdfContentType.Table:
                textBlock.FontFamily = new FontFamily("Consolas");
                textBlock.FontSize = 13;
                // Format table content with proper spacing
                FormatTableContent(textBlock, element.Text);
                break;

            default:
                textBlock.FontSize = 14;
                textBlock.LineHeight = 20;
                break;
        }
    }

    private void FormatTableContent(TextBlock textBlock, string text)
    {
        // Convert tabs to proper spacing for table-like appearance
        string formattedText = text.Replace("\t", "    ");
        textBlock.Text = formattedText;

        // Add subtle background for table rows
        StackPanel? parentContainer = textBlock.Parent as StackPanel;
        if (parentContainer != null)
        {
            Border border = new()
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(10, 0, 0, 0)),
                Padding = new Thickness(8, 4, 8, 4),
                CornerRadius = new CornerRadius(2),
                Child = textBlock
            };

            // We'll need to replace the textblock with the border
            // This is a simplified approach - in practice you'd want to structure this differently
        }
    }

    private void CreateFormattedTextContent(StackPanel container, string formattedText)
    {
        if (string.IsNullOrWhiteSpace(formattedText)) return;

        string[] lines = formattedText.Split('\n');
        List<List<string>> paragraphs = [];
        List<string> currentParagraph = [];

        // Group lines into paragraphs
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (currentParagraph.Any())
                {
                    paragraphs.Add(new List<string>(currentParagraph));
                    currentParagraph.Clear();
                }
            }
            else
            {
                currentParagraph.Add(line);
            }
        }

        // Add remaining paragraph
        if (currentParagraph.Any())
        {
            paragraphs.Add(currentParagraph);
        }

        // Render paragraphs
        foreach (List<string> paragraph in paragraphs)
        {
            if (paragraph.Count == 1)
            {
                // Single line - could be header or special content
                CreateSingleLineElement(container, paragraph[0]);
            }
            else
            {
                // Multi-line paragraph
                CreateParagraphElement(container, paragraph);
            }

            // Add spacing between paragraphs
            container.Children.Add(new Border { Height = 8 });
        }
    }

    private void CreateSingleLineElement(StackPanel container, string line)
    {
        TextBlock textBlock = new()
        {
            Text = line.Replace("\t", "    "),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black),
            SelectionHighlightColor = new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue)
        };

        ApplyAdvancedFormatting(textBlock, line);
        container.Children.Add(textBlock);
    }

    private void CreateParagraphElement(StackPanel container, List<string> lines)
    {
        // Combine lines into a single paragraph, handling indentation
        StringBuilder paragraphText = new();
        bool isFirstLine = true;

        foreach (string line in lines)
        {
            if (!isFirstLine)
            {
                paragraphText.Append(" ");
            }

            // Preserve important spacing but join logical lines
            string trimmedLine = line.Trim();
            if (!string.IsNullOrEmpty(trimmedLine))
            {
                paragraphText.Append(trimmedLine);
            }
            isFirstLine = false;
        }

        TextBlock textBlock = new()
        {
            Text = paragraphText.ToString(),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            LineHeight = 22,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black),
            SelectionHighlightColor = new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue),
            Margin = new Thickness(0, 0, 0, 5)
        };

        // Check if paragraph should be indented
        string firstLine = lines.FirstOrDefault() ?? "";
        if (firstLine.StartsWith("    ") || firstLine.StartsWith("\t"))
        {
            textBlock.Margin = new Thickness(20, 0, 0, 5);
        }

        container.Children.Add(textBlock);
    }

    private void ApplyAdvancedFormatting(TextBlock textBlock, string line)
    {
        string trimmed = line.Trim();
        string original = line;

        // Detect different content types with more sophisticated rules

        // Headers - Various patterns
        if (IsHeaderLine(trimmed))
        {
            textBlock.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            textBlock.FontSize = 16;
            textBlock.Margin = new Thickness(0, 15, 0, 8);

            // Center headers that appear to be titles
            if (trimmed.Length < 50 && !trimmed.Contains(":") &&
                trimmed.All(c => char.IsUpper(c) || char.IsWhiteSpace(c) || char.IsPunctuation(c)))
            {
                textBlock.HorizontalAlignment = HorizontalAlignment.Center;
                textBlock.FontSize = 18;
                textBlock.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            }
        }
        // List items
        else if (IsListItem(trimmed))
        {
            textBlock.Margin = new Thickness(20, 3, 0, 3);

            // Style different list types
            if (trimmed.StartsWith("•"))
            {
                textBlock.Text = "? " + trimmed[1..].Trim();
            }
        }
        // Table/structured data
        else if (IsStructuredData(original))
        {
            textBlock.FontFamily = new FontFamily("Consolas");
            textBlock.FontSize = 12;
            textBlock.Text = original.Replace("\t", "    ");

            // Add background for table-like content
            Border border = new()
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(15, 100, 100, 100)),
                Padding = new Thickness(8, 4, 8, 4),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 2, 0, 2)
            };

            // This would need to be handled differently in the container structure
            textBlock.Margin = new Thickness(8, 4, 8, 4);
        }
        // Contact info or addresses
        else if (IsContactInfo(trimmed))
        {
            textBlock.FontSize = 13;
            textBlock.Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkSlateGray);
        }
        // Indented content
        else if (original.StartsWith("    ") || original.StartsWith("\t"))
        {
            textBlock.Margin = new Thickness(30, 2, 0, 2);
            textBlock.FontSize = 13;
            textBlock.Text = original.Replace("\t", "    ");
        }
        // Normal content with better spacing
        else
        {
            textBlock.Margin = new Thickness(0, 3, 0, 3);
            textBlock.LineHeight = 20;
        }
    }

    private bool IsHeaderLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        return line.Length < 80 && (
            line.EndsWith(":") ||
            line.All(c => char.IsUpper(c) || char.IsWhiteSpace(c) || char.IsPunctuation(c)) ||
            System.Text.RegularExpressions.Regex.IsMatch(line, @"^[A-Z][A-Za-z\s]{5,40}$") ||
            line.Contains("CONFIRMATION") || line.Contains("HOTEL") || line.Contains("SUMMIT")
        );
    }

    private bool IsListItem(string line)
    {
        return line.StartsWith("•") || line.StartsWith("-") || line.StartsWith("*") ||
               line.StartsWith("?") || line.StartsWith("?") ||
               System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d+\.\s") ||
               System.Text.RegularExpressions.Regex.IsMatch(line, @"^[a-zA-Z]\.\s") ||
               System.Text.RegularExpressions.Regex.IsMatch(line, @"^[ivx]+\.\s", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private bool IsStructuredData(string line)
    {
        return line.Contains("\t") && line.Count(c => c == '\t') >= 2;
    }

    private bool IsContactInfo(string line)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(line, @"\b\d{3}[-.]?\d{3}[-.]?\d{4}\b") || // Phone
               System.Text.RegularExpressions.Regex.IsMatch(line, @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b") || // Email
               System.Text.RegularExpressions.Regex.IsMatch(line, @"\b\d+\s+[A-Za-z\s]+(?:Street|St|Avenue|Ave|Boulevard|Blvd|Road|Rd|Drive|Dr)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase); // Address
    }

    private void UpdatePdfInfo(PdfPageData pdfData)
    {
        if (pdfTitleBlock != null)
            pdfTitleBlock.Text = pdfData.FileName;

        if (pdfPageInfoBlock != null)
            pdfPageInfoBlock.Text = $"{pdfData.TotalPages} pages";
    }

    private void ShowPdfError(string message)
    {
        if (pdfPagesContainer == null) return;

        pdfPagesContainer.Children.Clear();

        Border errorContainer = new()
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(30),
            Margin = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 600
        };

        StackPanel contentStack = new()
        {
            Spacing = 15
        };

        // Error icon and title
        StackPanel headerStack = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        FontIcon errorIcon = new()
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Glyph = "\uE7BA", // Error icon
            FontSize = 24,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed)
        };

        TextBlock titleText = new()
        {
            Text = "PDF Processing Issue",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed)
        };

        headerStack.Children.Add(errorIcon);
        headerStack.Children.Add(titleText);

        // Error message
        TextBlock errorText = new()
        {
            Text = message,
            FontSize = 14,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkSlateGray),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsTextSelectionEnabled = true
        };

        // Helpful suggestions
        TextBlock suggestionText = new()
        {
            Text = "?? Suggestions:\n• Try opening the PDF in a dedicated PDF reader\n• Check if the PDF requires a password\n• Verify the file isn't corrupted\n• For scanned documents, OCR processing may be needed",
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            IsTextSelectionEnabled = true
        };

        contentStack.Children.Add(headerStack);
        contentStack.Children.Add(errorText);
        contentStack.Children.Add(suggestionText);

        errorContainer.Child = contentStack;
        pdfPagesContainer.Children.Add(errorContainer);
    }

    private void PdfZoomIn_Click(object sender, RoutedEventArgs e)
    {
        if (pdfScrollViewer != null && _currentZoomLevel < 3.0)
        {
            _currentZoomLevel = Math.Min(3.0, _currentZoomLevel * 1.25);
            pdfScrollViewer.ZoomToFactor((float)_currentZoomLevel);
            UpdateZoomDisplay();
        }
    }

    private void PdfZoomOut_Click(object sender, RoutedEventArgs e)
    {
        if (pdfScrollViewer != null && _currentZoomLevel > 0.5)
        {
            _currentZoomLevel = Math.Max(0.5, _currentZoomLevel / 1.25);
            pdfScrollViewer.ZoomToFactor((float)_currentZoomLevel);
            UpdateZoomDisplay();
        }
    }

    private void PdfScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (pdfScrollViewer != null)
        {
            _currentZoomLevel = pdfScrollViewer.ZoomFactor;
            UpdateZoomDisplay();
        }
    }

    private void UpdateZoomDisplay()
    {
        if (pdfZoomLevel != null)
        {
            pdfZoomLevel.Text = $"{_currentZoomLevel * 100:F0}%";
        }
    }

    private async Task WaitForPdfProcessing()
    {
        Debug.WriteLine("[AttachmentView] Waiting for PDF processing to complete...");

        if (!AttachmentVM.Attachment.IsProcessed && !AttachmentVM.IsProcessing)
        {
            Debug.WriteLine("[AttachmentView] PDF not processed and not processing - triggering processing");
            AttachmentProcessor.AddAttachment(AttachmentVM.Attachment);
        }

        // Wait for processing to complete with timeout
        int maxWaitTime = 300; // 300 * 500ms = 2.5 minutes timeout
        int waitCounter = 0;

        while ((AttachmentVM.IsProcessing || !AttachmentVM.Attachment.IsProcessed) && waitCounter < maxWaitTime)
        {
            Debug.WriteLine($"[AttachmentView] Waiting for PDF processing... IsProcessing: {AttachmentVM.IsProcessing}, IsProcessed: {AttachmentVM.Attachment.IsProcessed} ({waitCounter}/{maxWaitTime})");
            await Task.Delay(500);
            waitCounter++;
        }

        if (waitCounter >= maxWaitTime)
        {
            Debug.WriteLine("[AttachmentView] ERROR: PDF processing timed out after 2.5 minutes");
        }
        else
        {
            Debug.WriteLine("[AttachmentView] PDF processing completed successfully");
        }
    }

    private async Task LoadImageText(string fileName)
    {
        ImageText text = await TextRecognition.GetSavedText(fileName.Split('.')[0] + ".txt");
        if (text == null)
            return;

        foreach (RecognizedTextLine line in text.Lines)
        {
            double height = line.Height;
            double width = line.Width;
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
                        GradientStops =
                        [
                            new GradientStop() { Color = Color.FromArgb(20, 52, 185, 159), Offset = 0.1 },
                            new GradientStop() { Color = Color.FromArgb(20, 50, 181, 173), Offset = 0.5 },
                            new GradientStop() { Color = Color.FromArgb(20, 59, 177, 119), Offset = 0.9 }
                        ]
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

                StorageFolder transcriptsFolder = await Utils.GetAttachmentsTranscriptsFolderAsync();
                StorageFile transcriptFile = await transcriptsFolder.GetFileAsync(AttachmentVM.Attachment.FilenameForText);
                string rawTranscription = File.ReadAllText(transcriptFile.Path);

                Debug.WriteLine($"[AttachmentView] Transcription loaded: {rawTranscription.Length} characters");

                _dispatcher.TryEnqueue(() =>
                {
                    transcriptLoadingProgressRing.IsActive = false;
                    List<WhisperTranscribedChunk> transcripts = WhisperUtils.ProcessTranscriptionWithTimestamps(rawTranscription);

                    Debug.WriteLine($"[AttachmentView] Processed {transcripts.Count} transcription blocks");

                    foreach (WhisperTranscribedChunk t in transcripts)
                    {
                        TranscriptionBlocks.Add(new TranscriptionBlock(t.Text, t.Start, t.End));
                    }

                    if (transcriptionTextToTryToShow != null)
                    {
                        TranscriptionBlock? block = TranscriptionBlocks.Where(t => t.Text.Contains(transcriptionTextToTryToShow)).FirstOrDefault();
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
        else
        {
            _timer?.Dispose();
        }
    }

    private void CheckTimestampAndSelectTranscription(object? state)
    {
        _dispatcher.TryEnqueue(() =>
        {
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
        _timer?.Dispose();
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
        if (AutoScrollEnabled)
        {
            transcriptBlocksListView.ScrollIntoView(block, ScrollIntoViewAlignment.Leading);
        }
    }

    public void SeekToTimestamp(TimeSpan timestamp)
    {
        Debug.WriteLine($"[AttachmentView] Seeking to timestamp: {timestamp}");

        try
        {
            if (AttachmentVM?.Attachment?.Type is NoteAttachmentType.Audio or
                NoteAttachmentType.Video)
            {
                // Set the media player position
                mediaPlayer.MediaPlayer.Position = timestamp;

                // Find and select the corresponding transcription block
                TranscriptionBlock? correspondingBlock = TranscriptionBlocks.FirstOrDefault(block =>
                    block.Start <= timestamp && block.End >= timestamp);

                if (correspondingBlock != null)
                {
                    transcriptBlocksListView.SelectedItem = correspondingBlock;
                    ScrollTranscriptionToItem(correspondingBlock);
                }

                Debug.WriteLine($"[AttachmentView] Successfully seeked to {timestamp}");
            }
            else
            {
                Debug.WriteLine($"[AttachmentView] WARNING: Cannot seek in non-audio/video attachment");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AttachmentView] ERROR: Failed to seek to timestamp: {ex.Message}");
            Debug.WriteLine($"[AttachmentView] Exception details: {ex}");
        }
    }

    private void DisplaySimplePdfText(string pdfText)
    {
        if (pdfPagesContainer == null) return;

        pdfPagesContainer.Children.Clear();

        // Split by page markers and create page-like displays
        string[] pages = pdfText.Split(new string[] { "=== Page " }, StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < pages.Length; i++)
        {
            string pageText = pages[i];
            int pageNumber = i + 1;

            // Clean up the page text
            if (pageText.StartsWith(pageNumber.ToString()))
            {
                string[] lines = pageText.Split('\n');
                if (lines.Length > 1)
                {
                    pageText = string.Join("\n", lines.Skip(1)).Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(pageText)) continue;

            PdfPageInfo pageInfo = new()
            {
                PageNumber = pageNumber,
                FormattedText = pageText.Trim()
            };

            Border pageElement = CreatePdfPageElement(pageInfo);
            pdfPagesContainer.Children.Add(pageElement);
        }
    }
}
