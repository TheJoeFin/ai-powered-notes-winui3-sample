using Notes.AI.Embeddings;
using Notes.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Notes;

internal partial class Utils
{
    public static readonly string FolderName = "MyNotes";
    public static readonly string FileExtension = ".txt";
    public static readonly string StateFolderName = ".notes";
    public static readonly string AttachmentsFolderName = "attachments";

    private static string localFolderPath = string.Empty;

    public static async Task<string> GetLocalFolderPathAsync()
    {
        if (string.IsNullOrWhiteSpace(localFolderPath))
        {
            localFolderPath = (await GetLocalFolderAsync()).Path;
        }

        return localFolderPath;
    }

    public static async Task<StorageFolder> GetLocalFolderAsync()
    {
        return await KnownFolders.DocumentsLibrary.CreateFolderAsync(FolderName, CreationCollisionOption.OpenIfExists);
    }

    public static async Task<StorageFolder> GetStateFolderAsync()
    {
        StorageFolder notesFolder = await GetLocalFolderAsync();
        return await notesFolder.CreateFolderAsync(StateFolderName, CreationCollisionOption.OpenIfExists);
    }

    public static async Task<StorageFolder> GetAttachmentsFolderAsync()
    {
        StorageFolder notesFolder = await GetLocalFolderAsync();
        return await notesFolder.CreateFolderAsync(AttachmentsFolderName, CreationCollisionOption.OpenIfExists);
    }

    public static async Task<StorageFolder> GetAttachmentsTranscriptsFolderAsync()
    {
        StorageFolder notesFolder = await GetStateFolderAsync();
        return await notesFolder.CreateFolderAsync(AttachmentsFolderName, CreationCollisionOption.OpenIfExists);
    }

    public static async Task<List<SearchResult>> SearchAsync(string query, int top = 5)
    {
        List<SearchResult> results = [];
        if (string.IsNullOrWhiteSpace(query))
        {
            return results;
        }

        // TODO: handle cancelation
        List<TextChunk> searchVectors = await SemanticIndex.Instance.Search(query, top);
        AppDataContext context = await AppDataContext.GetCurrentAsync();

        while (searchVectors.Count > 0)
        {
            TextChunk searchVector = searchVectors[0];

            List<TextChunk> sameContent = [.. searchVectors
                .Where(r => r.ContentType == searchVector.ContentType && r.SourceId == searchVector.SourceId)
                .OrderBy(r => r.ChunkIndexInSource)];

            StringBuilder content = new();

            int previousSourceIndex = sameContent.First().ChunkIndexInSource;
            content.Append(sameContent.First().Text);
            searchVectors.Remove(sameContent.First());
            sameContent.RemoveAt(0);

            while (sameContent.Count > 0)
            {
                TextChunk currentContent = sameContent.First();

                if (currentContent.ChunkIndexInSource == previousSourceIndex + 1)
                {
                    content.Append(currentContent.Text3 ?? "");
                }
                else if (currentContent.ChunkIndexInSource == previousSourceIndex + 2)
                {
                    content.Append(currentContent.Text2 ?? "");
                    content.Append(currentContent.Text3 ?? "");
                }
                else
                {
                    content.Append(currentContent.Text);
                }

                previousSourceIndex = currentContent.ChunkIndexInSource;
                searchVectors.Remove(currentContent);
                sameContent.RemoveAt(0);
            }

            SearchResult searchResult = new()
            {
                Content = content.ToString()
            };

            if (searchVector.ContentType == "note")
            {
                Note? note = await context.Notes.FindAsync(searchVector.SourceId);

                searchResult.ContentType = ContentType.Note;
                searchResult.SourceId = note.Id;
                searchResult.Title = note.Title;
            }
            else if (searchVector.ContentType == "attachment")
            {
                Attachment? attachment = await context.Attachments.FindAsync(searchVector.SourceId);

                // Map attachment types to content types
                searchResult.ContentType = attachment.Type switch
                {
                    NoteAttachmentType.PDF => ContentType.PDF,
                    NoteAttachmentType.Image => ContentType.Image,
                    NoteAttachmentType.Audio => ContentType.Audio,
                    NoteAttachmentType.Video => ContentType.Video,
                    _ => ContentType.Document
                };
                searchResult.SourceId = attachment.Id;
                searchResult.Title = attachment.Filename;
            }

            string topSentence = await SubSearchAsync(query, searchResult.Content);
            searchResult.MostRelevantSentence = topSentence;
            results.Add(searchResult);

        }

        return results;
    }

    public static async Task<string> SubSearchAsync(string query, string text)
    {
        string[] sentences = text.Split(new string[] { "." }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        float[][] vectors = await Embeddings.Instance.GetEmbeddingsAsync(sentences);
        float[][] searchVector = await Embeddings.Instance.GetEmbeddingsAsync(new string[] { query });

        int[] ranking = SemanticIndex.CalculateRanking(searchVector[0], [.. vectors]);

        return sentences[ranking[0]];
    }
}

// Advanced PDF processing utilities using PdfPig document layout analysis
public static class PdfProcessor
{
    public static async Task<string> ExtractTextFromPdfAsync(StorageFile pdfFile)
    {
        Debug.WriteLine($"[PdfProcessor] Starting advanced PDF text extraction for: {pdfFile.Name}");

        try
        {
            byte[] pdfBytes = await ReadFileAsBytesAsync(pdfFile);

            using PdfDocument document = PdfDocument.Open(pdfBytes);
            StringBuilder textBuilder = new();

            for (int pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
            {
                Debug.WriteLine($"[PdfProcessor] Processing page {pageNumber}/{document.NumberOfPages} with advanced layout analysis");

                Page page = document.GetPage(pageNumber);
                string pageText = ExtractAdvancedTextFromPage(page);

                if (!string.IsNullOrWhiteSpace(pageText))
                {
                    textBuilder.AppendLine($"=== Page {pageNumber} ===");
                    textBuilder.AppendLine(pageText);
                    textBuilder.AppendLine();
                }
                else
                {
                    Debug.WriteLine($"[PdfProcessor] WARNING: No text found on page {pageNumber}");
                    textBuilder.AppendLine($"=== Page {pageNumber} ===");
                    textBuilder.AppendLine("[No text content found on this page]");
                    textBuilder.AppendLine();
                }
            }

            string extractedText = textBuilder.ToString();
            Debug.WriteLine($"[PdfProcessor] Successfully extracted {extractedText.Length} characters from PDF using advanced analysis");

            // If we got very little text, the PDF might be image-based or protected
            if (extractedText.Length < 50)
            {
                Debug.WriteLine("[PdfProcessor] WARNING: Very little text extracted, PDF might be image-based or protected");
                extractedText += "\n\n[Note: This PDF may contain images or be a scanned document. Text extraction may be limited.]";
            }

            return extractedText;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PdfProcessor] ERROR: Failed to extract text from PDF using advanced analysis: {ex.Message}");
            Debug.WriteLine($"[PdfProcessor] Exception details: {ex}");

            // Return a helpful error message instead of throwing
            return $"PDF Text Extraction Failed\n\nError: {ex.Message}\n\nThis PDF may be:\n- Password protected\n- An image-based/scanned document\n- Corrupted or in an unsupported format\n\nTo access the content, try:\n1. Opening the PDF in a PDF reader\n2. Converting it to a text-based PDF\n3. Using OCR if it's a scanned document";
        }
    }

    public static async Task<PdfPageData> ExtractPageDataFromPdfAsync(StorageFile pdfFile)
    {
        Debug.WriteLine($"[PdfProcessor] Starting enhanced PDF extraction with advanced layout analysis for: {pdfFile.Name}");

        try
        {
            byte[] pdfBytes = await ReadFileAsBytesAsync(pdfFile);

            using PdfDocument document = PdfDocument.Open(pdfBytes);
            List<PdfPageInfo> pages = [];

            for (int pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
            {
                Debug.WriteLine($"[PdfProcessor] Processing page {pageNumber}/{document.NumberOfPages} with advanced layout analysis");

                Page page = document.GetPage(pageNumber);
                PdfPageInfo pageInfo = new()
                {
                    PageNumber = pageNumber,
                    Text = page.Text,
                    Width = (double)page.Width,
                    Height = (double)page.Height,
                    // Extract advanced structured text with proper layout analysis
                    FormattedText = ExtractAdvancedTextFromPage(page),

                    // Create advanced structured content for better display
                    StructuredContent = ExtractAdvancedStructuredContentFromPage(page)
                };

                pages.Add(pageInfo);
            }

            return new PdfPageData
            {
                FileName = pdfFile.Name,
                Pages = pages,
                TotalPages = document.NumberOfPages
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PdfProcessor] ERROR: Failed to extract enhanced PDF data with advanced analysis: {ex.Message}");
            Debug.WriteLine($"[PdfProcessor] Exception details: {ex}");

            // Return a basic error page instead of throwing
            return new PdfPageData
            {
                FileName = pdfFile.Name,
                Pages =
                [
                    new PdfPageInfo
                    {
                        PageNumber = 1,
                        Text = $"PDF Processing Error\n\n{ex.Message}\n\nThis PDF may require special handling.",
                        FormattedText = $"PDF Processing Error\n\n{ex.Message}\n\nThis PDF may require special handling.",
                        Width = 600,
                        Height = 800
                    }
                ],
                TotalPages = 1
            };
        }
    }

    private static string ExtractAdvancedTextFromPage(Page page)
    {
        try
        {
            Debug.WriteLine($"[PdfProcessor] Starting advanced text extraction for page {page.Number}");

            IReadOnlyList<Letter> letters = page.Letters; // Get all letters without preprocessing
            if (!letters.Any())
            {
                Debug.WriteLine($"[PdfProcessor] No letters found on page {page.Number}");
                return page.Text; // Fallback to basic text if no letters found
            }

            Debug.WriteLine($"[PdfProcessor] Found {letters.Count()} letters on page {page.Number}");

            // 1. Extract words using advanced word extractor
            NearestNeighbourWordExtractor wordExtractor = NearestNeighbourWordExtractor.Instance;
            IEnumerable<Word> words = wordExtractor.GetWords(letters);

            if (!words.Any())
            {
                Debug.WriteLine($"[PdfProcessor] No words extracted from page {page.Number}");
                return page.Text; // Fallback to basic text
            }

            Debug.WriteLine($"[PdfProcessor] Extracted {words.Count()} words from page {page.Number}");

            // 2. Segment page into text blocks using advanced page segmentation
            DocstrumBoundingBoxes pageSegmenter = DocstrumBoundingBoxes.Instance;
            IReadOnlyList<TextBlock> textBlocks = pageSegmenter.GetBlocks(words);

            if (!textBlocks.Any())
            {
                Debug.WriteLine($"[PdfProcessor] No text blocks found on page {page.Number}, using fallback word ordering");
                return ExtractTextFromWordsWithFallback(words);
            }

            Debug.WriteLine($"[PdfProcessor] Found {textBlocks.Count()} text blocks on page {page.Number}");

            // 3. Apply reading order detection for proper text flow
            UnsupervisedReadingOrderDetector readingOrderDetector = UnsupervisedReadingOrderDetector.Instance;
            IEnumerable<TextBlock> orderedTextBlocks = readingOrderDetector.Get(textBlocks);

            Debug.WriteLine($"[PdfProcessor] Applied reading order to {orderedTextBlocks.Count()} text blocks");

            // 4. Build formatted text maintaining structure
            StringBuilder result = new();

            foreach (TextBlock? block in orderedTextBlocks.OrderBy(b => b.ReadingOrder))
            {
                string blockText = ExtractTextFromBlock(block);
                if (!string.IsNullOrWhiteSpace(blockText))
                {
                    // Add appropriate spacing based on block positioning
                    BlockType blockType = DetermineBlockType(block);

                    switch (blockType)
                    {
                        case BlockType.Header:
                            result.AppendLine();
                            result.AppendLine(blockText.Trim());
                            result.AppendLine();
                            break;
                        case BlockType.Paragraph:
                            result.AppendLine(blockText.Trim());
                            result.AppendLine();
                            break;
                        case BlockType.List:
                            result.AppendLine($"• {blockText.Trim()}");
                            break;
                        case BlockType.Table:
                            result.AppendLine($"    {blockText.Trim()}");
                            break;
                        default:
                            result.AppendLine(blockText.Trim());
                            break;
                    }
                }
            }

            string finalText = result.ToString().Trim();
            Debug.WriteLine($"[PdfProcessor] Advanced extraction completed, generated {finalText.Length} characters");

            return finalText;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PdfProcessor] Error in advanced text extraction: {ex.Message}");
            Debug.WriteLine($"[PdfProcessor] Falling back to basic text extraction");
            return page.Text; // Fallback to basic text extraction
        }
    }

    private static string ExtractTextFromWordsWithFallback(IEnumerable<Word> words)
    {
        // Fallback method when text block segmentation fails
        IOrderedEnumerable<Word> sortedWords = words.OrderBy(w => w.BoundingBox.Top).ThenBy(w => w.BoundingBox.Left);
        StringBuilder result = new();

        Word? previousWord = null;
        foreach (Word? word in sortedWords)
        {
            if (previousWord != null)
            {
                // Check if we need a line break (significant Y difference)
                if (Math.Abs(word.BoundingBox.Top - previousWord.BoundingBox.Top) > 5)
                {
                    result.AppendLine();
                }
                // Check if we need spacing (significant X gap)
                else if (word.BoundingBox.Left - previousWord.BoundingBox.Right > 10)
                {
                    result.Append("    "); // Tab-like spacing
                }
                else
                {
                    result.Append(" ");
                }
            }

            result.Append(word.Text);
            previousWord = word;
        }

        return result.ToString();
    }

    private static string ExtractTextFromBlock(UglyToad.PdfPig.DocumentLayoutAnalysis.TextBlock block)
    {
        // Extract text from all words in the block, maintaining proper spacing
        IOrderedEnumerable<Word> blockWords = block.TextLines
            .SelectMany(line => line.Words)
            .OrderBy(w => w.BoundingBox.Top)
            .ThenBy(w => w.BoundingBox.Left);

        StringBuilder result = new();
        Word? previousWord = null;

        foreach (Word? word in blockWords)
        {
            if (previousWord != null)
            {
                // Check for line breaks within the block
                if (Math.Abs(word.BoundingBox.Top - previousWord.BoundingBox.Top) > 3)
                {
                    result.Append(" ");
                }
                // Check for significant spacing
                else if (word.BoundingBox.Left - previousWord.BoundingBox.Right > 15)
                {
                    result.Append("  "); // Double space for gaps
                }
                else
                {
                    result.Append(" ");
                }
            }

            result.Append(word.Text);
            previousWord = word;
        }

        return result.ToString();
    }

    private static BlockType DetermineBlockType(UglyToad.PdfPig.DocumentLayoutAnalysis.TextBlock block)
    {
        string text = ExtractTextFromBlock(block).Trim();
        PdfRectangle boundingBox = block.BoundingBox;

        // Determine block type based on content and positioning
        if (string.IsNullOrWhiteSpace(text))
            return BlockType.Normal;

        // Header detection: short text, positioned high, often bold or larger
        if (text.Length < 100 &&
            (text.All(c => char.IsUpper(c) || char.IsWhiteSpace(c) || char.IsPunctuation(c)) ||
             text.EndsWith(":") ||
             block.TextLines.Count() == 1))
        {
            return BlockType.Header;
        }

        // List detection: starts with bullet points or numbers
        if (text.StartsWith("•") || text.StartsWith("-") || text.StartsWith("*") ||
            System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d+\.\s") ||
            System.Text.RegularExpressions.Regex.IsMatch(text, @"^[a-zA-Z]\.\s"))
        {
            return BlockType.List;
        }

        // Table detection: multiple columns or structured spacing
        if (block.TextLines.Count() > 1 &&
            block.TextLines.Any(line => line.Words.Count() > 3) &&
            text.Contains("  ")) // Multiple spaces indicating columns
        {
            return BlockType.Table;
        }

        return BlockType.Paragraph;
    }

    private static async Task<byte[]> ReadFileAsBytesAsync(StorageFile file)
    {
        using IRandomAccessStreamWithContentType stream = await file.OpenReadAsync();
        byte[] bytes = new byte[stream.Size];
        using (DataReader reader = new(stream))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
        }
        return bytes;
    }

    private static PdfTextElement CreateAdvancedTextElement(UglyToad.PdfPig.DocumentLayoutAnalysis.TextBlock block)
    {
        string text = ExtractTextFromBlock(block);
        if (string.IsNullOrWhiteSpace(text)) return null;

        PdfRectangle boundingBox = block.BoundingBox;
        BlockType blockType = DetermineBlockType(block);

        // Convert block type to our content type
        PdfContentType contentType = blockType switch
        {
            BlockType.Header => PdfContentType.Header,
            BlockType.List => PdfContentType.ListItem,
            BlockType.Table => PdfContentType.Table,
            _ => PdfContentType.Normal
        };

        return new PdfTextElement
        {
            Text = text,
            Left = boundingBox.Left,
            Top = boundingBox.Top,
            Width = boundingBox.Width,
            Height = boundingBox.Height,
            FontName = "Unknown", // Would need additional analysis to determine font
            ElementType = contentType,
            IsIndented = boundingBox.Left > 50,
            IsCentered = Math.Abs(boundingBox.Left - (612 - boundingBox.Right)) < 50 // Rough center detection
        };
    }

    private static List<PdfTextElement> ExtractAdvancedStructuredContentFromPage(Page page)
    {
        List<PdfTextElement> elements = [];

        try
        {
            IReadOnlyList<Letter> letters = page.Letters;
            if (!letters.Any()) return elements;

            // Use advanced PdfPig analysis
            NearestNeighbourWordExtractor wordExtractor = NearestNeighbourWordExtractor.Instance;
            IEnumerable<Word> words = wordExtractor.GetWords(letters);

            if (!words.Any()) return elements;

            DocstrumBoundingBoxes pageSegmenter = DocstrumBoundingBoxes.Instance;
            IReadOnlyList<TextBlock> textBlocks = pageSegmenter.GetBlocks(words);

            UnsupervisedReadingOrderDetector readingOrderDetector = UnsupervisedReadingOrderDetector.Instance;
            IEnumerable<TextBlock> orderedTextBlocks = readingOrderDetector.Get(textBlocks);

            foreach (TextBlock? block in orderedTextBlocks)
            {
                PdfTextElement element = CreateAdvancedTextElement(block);
                if (element != null)
                {
                    elements.Add(element);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PdfProcessor] Error in advanced structured content extraction: {ex.Message}");
        }

        return elements;
    }
}

public enum BlockType
{
    Normal,
    Header,
    Paragraph,
    List,
    Table
}

public class PdfPageData
{
    public string FileName { get; set; }
    public List<PdfPageInfo> Pages { get; set; } = [];
    public int TotalPages { get; set; }
}

public class PdfPageInfo
{
    public int PageNumber { get; set; }
    public string Text { get; set; }
    public string FormattedText { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public List<PdfTextElement> StructuredContent { get; set; } = [];
}

public class PdfTextElement
{
    public string Text { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string FontName { get; set; }
    public PdfContentType ElementType { get; set; }
    public bool IsIndented { get; set; }
    public bool IsCentered { get; set; }
}

public enum PdfContentType
{
    Normal,
    Header,
    ListItem,
    Table,
    Footer
}

public record SearchResult
{
    public string Title { get; set; }
    public string? Content { get; set; }
    public string? MostRelevantSentence { get; set; }
    public int SourceId { get; set; }
    public ContentType ContentType { get; set; }

    public static string ContentTypeToGlyph(ContentType type)
    {
        return type switch
        {
            ContentType.Note => "📝",
            ContentType.Image => "🖼️",
            ContentType.Audio => "🎙️",
            ContentType.Video => "🎞️",
            ContentType.Document => "📄",
            ContentType.PDF => "📕"
        };
    }
}

public enum ContentType
{
    Image = 0,
    Audio = 1,
    Video = 2,
    Document = 3,
    Note = 4,
    PDF = 5
}
