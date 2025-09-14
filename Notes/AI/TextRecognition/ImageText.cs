using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Notes.AI.TextRecognition;

internal class ImageText
{
    public List<RecognizedTextLine> Lines { get; set; } = [];
    public double ImageAngle { get; set; }

    public static async Task<ImageText> GetFromRecognizedTextAsync(SoftwareBitmap softwareBitmap)
    {
        ImageText result = new();

        if (softwareBitmap == null)
        {
            return result;
        }

        try
        {
            Debug.WriteLine("[ImageText] Starting Windows OCR text recognition...");

            // Check if OCR is available for the current language
            IReadOnlyList<Language> languages = OcrEngine.AvailableRecognizerLanguages;
            if (languages.Count == 0)
            {
                Debug.WriteLine("[ImageText] No OCR languages available");
                return result;
            }

            // Create OCR engine with the first available language (or English if available)
            OcrEngine ocrEngine = null;
            foreach (Language? language in languages)
            {
                if (language.LanguageTag is "en-US" or "en")
                {
                    ocrEngine = OcrEngine.TryCreateFromLanguage(language);
                    break;
                }
            }

            // If English not found, use the first available language
            if (ocrEngine == null && languages.Count > 0)
            {
                ocrEngine = OcrEngine.TryCreateFromLanguage(languages[0]);
            }

            if (ocrEngine == null)
            {
                Debug.WriteLine("[ImageText] Failed to create OCR engine");
                return result;
            }

            Debug.WriteLine($"[ImageText] OCR engine created for language: {ocrEngine.RecognizerLanguage.DisplayName}");

            // Ensure the bitmap is in the correct format for OCR
            SoftwareBitmap convertedBitmap = softwareBitmap;
            if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
                softwareBitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
            {
                convertedBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            }

            Debug.WriteLine("[ImageText] Performing OCR text recognition...");

            // Perform OCR
            OcrResult ocrResult = await ocrEngine.RecognizeAsync(convertedBitmap);

            Debug.WriteLine($"[ImageText] OCR completed. Found {ocrResult.Lines.Count} text lines");

            // Convert to our format
            foreach (OcrLine? line in ocrResult.Lines)
            {
                // Calculate line bounding box from word bounding boxes
                if (line.Words.Count > 0)
                {
                    double minX = line.Words.Min(w => w.BoundingRect.X);
                    double minY = line.Words.Min(w => w.BoundingRect.Y);
                    double maxX = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
                    double maxY = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);

                    RecognizedTextLine recognizedLine = new()
                    {
                        Text = line.Text,
                        X = minX,
                        Y = minY,
                        Width = maxX - minX,
                        Height = maxY - minY
                    };
                    result.Lines.Add(recognizedLine);
                    Debug.WriteLine($"[ImageText] Recognized text: '{line.Text}' at ({minX}, {minY}) size ({maxX - minX}x{maxY - minY})");
                }
                else
                {
                    // Fallback if no words are found but line has text
                    RecognizedTextLine recognizedLine = new()
                    {
                        Text = line.Text,
                        X = 0,
                        Y = 0,
                        Width = 0,
                        Height = 0
                    };
                    result.Lines.Add(recognizedLine);
                    Debug.WriteLine($"[ImageText] Recognized text (no position): '{line.Text}'");
                }
            }

            // OCR doesn't provide image angle, so we'll keep it as 0
            result.ImageAngle = ocrResult.TextAngle ?? 0.0;

            Debug.WriteLine($"[ImageText] OCR recognition successful. Total lines: {result.Lines.Count}, Angle: {result.ImageAngle}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ImageText] Error during OCR recognition: {ex.Message}");
            Debug.WriteLine($"[ImageText] Exception details: {ex}");
            // Return empty result instead of throwing
        }

        return result;
    }

    public static ImageText GetFromRecognizedText(object? recognizedText)
    {
        ImageText attachmentRecognizedText = new();

        if (recognizedText == null)
        {
            return attachmentRecognizedText;
        }

        // Legacy support - if someone passes a recognized text object
        try
        {
            if (recognizedText is OcrResult ocrResult)
            {
                foreach (OcrLine? line in ocrResult.Lines)
                {
                    // Calculate line bounding box from word bounding boxes
                    if (line.Words.Count > 0)
                    {
                        double minX = line.Words.Min(w => w.BoundingRect.X);
                        double minY = line.Words.Min(w => w.BoundingRect.Y);
                        double maxX = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
                        double maxY = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);

                        RecognizedTextLine recognizedLine = new()
                        {
                            Text = line.Text,
                            X = minX,
                            Y = minY,
                            Width = maxX - minX,
                            Height = maxY - minY
                        };
                        attachmentRecognizedText.Lines.Add(recognizedLine);
                    }
                    else
                    {
                        // Fallback if no words are found but line has text
                        RecognizedTextLine recognizedLine = new()
                        {
                            Text = line.Text,
                            X = 0,
                            Y = 0,
                            Width = 0,
                            Height = 0
                        };
                        attachmentRecognizedText.Lines.Add(recognizedLine);
                    }
                }
                attachmentRecognizedText.ImageAngle = ocrResult.TextAngle ?? 0.0;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ImageText] Error converting recognized text: {ex.Message}");
        }

        return attachmentRecognizedText;
    }
}

internal class RecognizedTextLine
{
    public string Text { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}
