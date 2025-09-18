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
using SkiaSharp;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Rendering.Skia;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;

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
    // Expose strictTables mode publicly (optional param preserves back-compat)
    public static async Task<string> ExtractTextFromPdfAsync(StorageFile pdfFile, bool strictTables = false)
    {
        Debug.WriteLine($"[PdfProcessor] Starting advanced PDF text extraction for: {pdfFile.Name}");

        try
        {
            byte[] pdfBytes = await ReadFileAsBytesAsync(pdfFile);

            using PdfDocument document = PdfDocument.Open(pdfBytes, SkiaRenderingParsingOptions.Instance);
            document.AddSkiaPageFactory();
            StringBuilder textBuilder = new();

            for (int pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
            {
                Debug.WriteLine($"[PdfProcessor] Processing page {pageNumber}/{document.NumberOfPages} with advanced layout analysis");

                string pageText = ExtractTextFromPageWithOcr(document, pageNumber, strictTables);

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

    public static async Task<PdfPageData> ExtractPageDataFromPdfAsync(StorageFile pdfFile, bool strictTables = false)
    {
        Debug.WriteLine($"[PdfProcessor] Starting enhanced PDF extraction with advanced layout analysis for: {pdfFile.Name}");

        try
        {
            byte[] pdfBytes = await ReadFileAsBytesAsync(pdfFile);

            using PdfDocument document = PdfDocument.Open(pdfBytes, SkiaRenderingParsingOptions.Instance);
            document.AddSkiaPageFactory();
            List<PdfPageInfo> pages = [];

            const float renderScale = 2.0f;
            RGBColor backgroundColor = RGBColor.White;

            for (int pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
            {
                Debug.WriteLine($"[PdfProcessor] Processing page {pageNumber}/{document.NumberOfPages} with advanced layout analysis");

                Page page = document.GetPage(pageNumber);
                PdfRectangle cropBounds = GetEffectiveCropBounds(page);

                PdfPageInfo pageInfo = new()
                {
                    PageNumber = pageNumber,
                    Text = page.Text,
                    Width = (double)page.Width,
                    Height = (double)page.Height,
                    FormattedText = ExtractTextFromPageWithOcr(document, pageNumber, strictTables),
                    StructuredContent = ExtractAdvancedStructuredContentFromPage(page)
                };

                pageInfo.CropLeft = cropBounds.Left;
                pageInfo.CropBottom = cropBounds.Bottom;
                pageInfo.CropWidth = cropBounds.Width;
                pageInfo.CropHeight = cropBounds.Height;
                pageInfo.Rotation = page.Rotation.Value;

                try
                {
                    using SKBitmap bitmap = document.GetPageAsSKBitmap(pageNumber, renderScale, backgroundColor);
                    if (bitmap != null)
                    {
                        pageInfo.ImageBytes = EncodeBitmapToPng(bitmap);
                        pageInfo.ImagePixelWidth = bitmap.Width;
                        pageInfo.ImagePixelHeight = bitmap.Height;
                        pageInfo.RenderScaleX = pageInfo.CropWidth > 0 ? bitmap.Width / pageInfo.CropWidth : 0;
                        pageInfo.RenderScaleY = pageInfo.CropHeight > 0 ? bitmap.Height / pageInfo.CropHeight : 0;
                    }
                }
                catch (Exception renderEx)
                {
                    Debug.WriteLine($"[PdfProcessor] WARNING: Failed to render page {pageNumber}: {renderEx.Message}");
                }

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
                        Height = 800,
                        CropLeft = 0,
                        CropBottom = 0,
                        CropWidth = 600,
                        CropHeight = 800,
                        RenderScaleX = 1,
                        RenderScaleY = 1,
                        Rotation = 0
                    }
                ],
                TotalPages = 1
            };
        }
    }

    private static string ExtractTextFromPageWithOcr(PdfDocument document, int pageNumber, bool strictTables)
    {
        Page page = document.GetPage(pageNumber);

        // Try advanced extraction first
        string advanced = ExtractAdvancedTextFromPage(page, strictTables);
        if (!string.IsNullOrWhiteSpace(advanced))
        {
            return advanced;
        }

        // If page has no letters/words, fall back to OCR by rendering the page
        try
        {
            using SKBitmap bmp = document.GetPageAsSKBitmap(pageNumber, 2.0f, RGBColor.White);
            if (bmp == null)
            {
                return string.Empty;
            }

            SoftwareBitmap softwareBitmap = ConvertSkBitmapToSoftwareBitmap(bmp);
            if (softwareBitmap == null)
            {
                return string.Empty;
            }

            var imageText = AI.TextRecognition.TextRecognition.GetTextFromImage(softwareBitmap).GetAwaiter().GetResult();
            if (imageText?.Lines == null || imageText.Lines.Count == 0)
            {
                return string.Empty;
            }

            // Order by Y then X to form readable lines
            var lines = imageText.Lines
                .OrderBy(l => Math.Round(l.Y / 4) * 4)
                .ThenBy(l => l.X)
                .Select(l => l.Text?.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t));

            string ocrText = string.Join("\n", lines);
            Debug.WriteLine($"[PdfProcessor] OCR fallback extracted {ocrText.Length} chars on page {pageNumber}");
            return ocrText;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PdfProcessor] OCR fallback failed on page {pageNumber}: {ex.Message}");
            return string.Empty;
        }
    }

    private static SoftwareBitmap ConvertSkBitmapToSoftwareBitmap(SKBitmap bitmap)
    {
        using SKData data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
        using InMemoryRandomAccessStream stream = new();

        // Write PNG bytes into the WinRT stream
        byte[] bytes = data.ToArray();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
        }
        stream.Seek(0);

        BitmapDecoder decoder = BitmapDecoder.CreateAsync(stream).GetAwaiter().GetResult();
        SoftwareBitmap sb = decoder.GetSoftwareBitmapAsync().GetAwaiter().GetResult();
        if (sb.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || sb.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
        {
            sb = SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }
        return sb;
    }

    private static byte[] EncodeBitmapToPng(SKBitmap bitmap)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static PdfRectangle GetEffectiveCropBounds(Page page)
    {
        PdfRectangle? cropBounds = page.CropBox?.Bounds;

        if (cropBounds == null || cropBounds.Value.Width <= 0 || cropBounds.Value.Height <= 0)
        {
            cropBounds = page.MediaBox?.Bounds;
        }

        if (cropBounds == null || cropBounds.Value.Width <= 0 || cropBounds.Value.Height <= 0)
        {
            cropBounds = new PdfRectangle(0, 0, page.Width, page.Height);
        }

        return cropBounds.Value;
    }

    private static IReadOnlyList<Letter> GetCleanLetters(Page page)
    {
        IReadOnlyList<Letter> letters = page.Letters;
        if (letters == null || letters.Count == 0)
        {
            return letters ?? Array.Empty<Letter>();
        }

        // Compute median height to filter tiny chart labels or huge watermarks
        double[] heights = letters.Select(l => l.GlyphRectangle.Height).Where(h => h > 0).OrderBy(h => h).ToArray();
        double medianHeight = heights.Length > 0 ? heights[heights.Length / 2] : 0;
        double minHeight = medianHeight > 0 ? Math.Max(2, medianHeight * 0.35) : 0; // too small => noise
        double maxHeight = medianHeight > 0 ? medianHeight * 3.0 : double.MaxValue;  // too large => watermark-like

        // Deduplicate same-character letters drawn multiple times at the same spot (fill + stroke, shadows, etc.)
        // We round the position to a small grid and keep only one occurrence per grid cell and character
        const double grid = 0.25; // quarter-point grid is usually enough to merge duplicates
        HashSet<string> seen = new();
        List<Letter> filtered = new(letters.Count);
        foreach (Letter l in letters)
        {
            double h = l.GlyphRectangle.Height;
            if (h <= minHeight || h > maxHeight)
            {
                continue; // filter out extreme sizes
            }

            // Build a key based on rounded position and character
            double cx = Math.Round((l.GlyphRectangle.Left + l.GlyphRectangle.Right) / 2 / grid) * grid;
            double cy = Math.Round((l.GlyphRectangle.Bottom + l.GlyphRectangle.Top) / 2 / grid) * grid;
            string key = $"{l.Value}:{cx:F2}:{cy:F2}";
            if (seen.Add(key))
            {
                filtered.Add(l);
            }
        }

        // If filtering removed too much (e.g., image-based), fall back to original
        if (filtered.Count < letters.Count * 0.25)
        {
            return letters;
        }

        return filtered;
    }

    private static IEnumerable<Word> ExtractWordsClean(Page page)
    {
        IReadOnlyList<Letter> cleanLetters = GetCleanLetters(page);
        if (cleanLetters == null || cleanLetters.Count == 0)
        {
            return Array.Empty<Word>();
        }

        NearestNeighbourWordExtractor wordExtractor = NearestNeighbourWordExtractor.Instance;
        IEnumerable<Word> words = wordExtractor.GetWords(cleanLetters);
        if (!words.Any())
        {
            return Array.Empty<Word>();
        }

        // De-duplicate words with almost identical bounding boxes and text
        var groups = words.GroupBy(w => new
        {
            Text = w.Text.Trim(),
            X = Math.Round(w.BoundingBox.Left, 1),
            Y = Math.Round(w.BoundingBox.Bottom, 1),
            W = Math.Round(w.BoundingBox.Width, 1),
            H = Math.Round(w.BoundingBox.Height, 1)
        });
        List<Word> deduped = new();
        foreach (var g in groups)
        {
            Word keep = g.OrderBy(w => w.Letters.Count()).First();
            deduped.Add(keep);
        }
        return deduped;
    }

    private static string ExtractAdvancedTextFromPage(Page page, bool strictTables = false)
    {
        try
        {
            Debug.WriteLine($"[PdfProcessor] Starting advanced text extraction for page {page.Number}");

            // Use cleaned letters to avoid duplicated and noisy glyphs
            IEnumerable<Word> words = ExtractWordsClean(page);

            if (!words.Any())
            {
                Debug.WriteLine($"[PdfProcessor] No words extracted from page {page.Number}");
                return string.Empty; // Let caller decide OCR fallback
            }

            Debug.WriteLine($"[PdfProcessor] Extracted {words.Count()} words from page {page.Number}");

            DocstrumBoundingBoxes pageSegmenter = DocstrumBoundingBoxes.Instance;
            IReadOnlyList<TextBlock> textBlocks = pageSegmenter.GetBlocks(words);

            if (!textBlocks.Any())
            {
                Debug.WriteLine($"[PdfProcessor] No text blocks found on page {page.Number}, using fallback word ordering");
                return ExtractTextFromWordsWithFallback(words);
            }

            Debug.WriteLine($"[PdfProcessor] Found {textBlocks.Count()} text blocks on page {page.Number}");

            UnsupervisedReadingOrderDetector readingOrderDetector = UnsupervisedReadingOrderDetector.Instance;
            IEnumerable<TextBlock> orderedTextBlocks = readingOrderDetector.Get(textBlocks);

            Debug.WriteLine($"[PdfProcessor] Applied reading order to {orderedTextBlocks.Count()} text blocks");

            StringBuilder result = new();

            foreach (TextBlock? block in orderedTextBlocks.OrderBy(b => b.ReadingOrder))
            {
                string blockText = strictTables && LooksLikeTable(block)
                    ? ExtractTableText(block)
                    : NormalizeWhitespace(ExtractTextFromBlock(block));

                if (!string.IsNullOrWhiteSpace(blockText))
                {
                    BlockType blockType = strictTables && LooksLikeTable(block) ? BlockType.Table : DetermineBlockType(block);

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
                            result.AppendLine(blockText); // already formatted with columns
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
            return string.Empty; // Let caller decide OCR fallback
        }
    }

    private static bool LooksLikeTable(TextBlock block)
    {
        // Heuristic: multiple lines, at least some lines with 3+ words and sizable horizontal gaps
        if (block.TextLines.Count() < 2) return false;
        int linesWithManyWords = block.TextLines.Count(l => l.Words.Count() >= 3);
        if (linesWithManyWords == 0) return false;
        // detect repeated big gaps within lines
        foreach (var line in block.TextLines)
        {
            var w = line.Words.OrderBy(x => x.BoundingBox.Left).ToList();
            if (w.Count < 2) continue;
            var gaps = new List<double>();
            for (int i = 1; i < w.Count; i++)
            {
                gaps.Add(w[i].BoundingBox.Left - w[i - 1].BoundingBox.Right);
            }
            double medianGap = gaps.OrderBy(g => g).ElementAt(gaps.Count / 2);
            if (gaps.Count(g => g > medianGap * 1.6) >= 1)
                return true;
        }
        return false;
    }

    private static string ExtractTableText(TextBlock block)
    {
        // Format each line, inserting tabs when gaps are significantly larger than the typical gap
        StringBuilder sb = new();
        foreach (var line in block.TextLines)
        {
            var words = line.Words.OrderBy(w => w.BoundingBox.Left).ToList();
            if (words.Count == 0) continue;
            var gaps = new List<double>();
            for (int i = 1; i < words.Count; i++)
            {
                gaps.Add(words[i].BoundingBox.Left - words[i - 1].BoundingBox.Right);
            }
            double medianGap = gaps.Count > 0 ? gaps.OrderBy(g => g).ElementAt(gaps.Count / 2) : 0;
            double tabThreshold = medianGap > 0 ? medianGap * 1.6 : 15; // px heuristic

            Word prev = null;
            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];
                if (i > 0)
                {
                    double gap = w.BoundingBox.Left - prev.BoundingBox.Right;
                    sb.Append(gap > tabThreshold ? "\t" : " ");
                }
                sb.Append(w.Text);
                prev = w;
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string NormalizeWhitespace(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;
        string collapsed = System.Text.RegularExpressions.Regex.Replace(input, " {3,}", "  ");
        collapsed = System.Text.RegularExpressions.Regex.Replace(collapsed, "\n{2,}", "\n");
        return collapsed.Trim();
    }

    private static string ExtractTextFromWordsWithFallback(IEnumerable<Word> words)
    {
        IOrderedEnumerable<Word> sortedWords = words.OrderBy(w => w.BoundingBox.Top).ThenBy(w => w.BoundingBox.Left);
        StringBuilder result = new();

        Word? previousWord = null;
        foreach (Word? word in sortedWords)
        {
            if (previousWord != null)
            {
                if (Math.Abs(word.BoundingBox.Top - previousWord.BoundingBox.Top) > 5)
                {
                    result.AppendLine();
                }
                else if (word.BoundingBox.Left - previousWord.BoundingBox.Right > 10)
                {
                    result.Append("    ");
                }
                else
                {
                    result.Append(" ");
                }
            }

            result.Append(word.Text);
            previousWord = word;
        }

        return NormalizeWhitespace(result.ToString());
    }

    private static string ExtractTextFromBlock(TextBlock block)
    {
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
                if (Math.Abs(word.BoundingBox.Top - previousWord.BoundingBox.Top) > 3)
                {
                    result.Append(" ");
                }
                else if (word.BoundingBox.Left - previousWord.BoundingBox.Right > 15)
                {
                    result.Append("  ");
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

    private static BlockType DetermineBlockType(TextBlock block)
    {
        string text = ExtractTextFromBlock(block).Trim();
        PdfRectangle boundingBox = block.BoundingBox;

        if (string.IsNullOrWhiteSpace(text))
            return BlockType.Normal;

        if (text.Length < 100 &&
            (text.All(c => char.IsUpper(c) || char.IsWhiteSpace(c) || char.IsPunctuation(c)) ||
             text.EndsWith(":") ||
             block.TextLines.Count() == 1))
        {
            return BlockType.Header;
        }

        if (text.StartsWith("•") || text.StartsWith("-") || text.StartsWith("*") ||
            System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d+\.\s") ||
            System.Text.RegularExpressions.Regex.IsMatch(text, @"^[a-zA-Z]\.\s"))
        {
            return BlockType.List;
        }

        if (block.TextLines.Count() > 1 &&
            block.TextLines.Any(line => line.Words.Count() > 3) &&
            text.Contains("  "))
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

    private static PdfTextElement CreateAdvancedTextElement(TextBlock block)
    {
        string text = ExtractTextFromBlock(block);
        if (string.IsNullOrWhiteSpace(text)) return null;

        List<Letter> letters = block.TextLines
            .SelectMany(line => line.Words)
            .SelectMany(word => word.Letters)
            .ToList();

        if (!letters.Any())
        {
            return null;
        }

        double minLeft = letters.Min(l => l.GlyphRectangle.Left);
        double maxRight = letters.Max(l => l.GlyphRectangle.Right);
        double minBottom = letters.Min(l => l.GlyphRectangle.Bottom);
        double maxTop = letters.Max(l => l.GlyphRectangle.Top);

        double width = maxRight - minLeft;
        double height = maxTop - minBottom;

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        BlockType blockType = DetermineBlockType(block);

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
            Left = minLeft,
            Top = maxTop,
            Width = width,
            Height = height,
            FontName = "Unknown",
            ElementType = contentType,
            IsIndented = minLeft > 50,
            IsCentered = Math.Abs(minLeft - (612 - maxRight)) < 50
        };
    }

    private static List<PdfTextElement> ExtractAdvancedStructuredContentFromPage(Page page)
    {
        List<PdfTextElement> elements = [];

        try
        {
            IEnumerable<Word> words = ExtractWordsClean(page);
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
    public byte[]? ImageBytes { get; set; }
    public int? ImagePixelWidth { get; set; }
    public int? ImagePixelHeight { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double CropLeft { get; set; }
    public double CropBottom { get; set; }
    public double CropWidth { get; set; }
    public double CropHeight { get; set; }
    public double RenderScaleX { get; set; }
    public double RenderScaleY { get; set; }
    public int Rotation { get; set; }
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

