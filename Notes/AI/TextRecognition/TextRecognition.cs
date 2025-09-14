using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Notes.AI.TextRecognition;

internal static class TextRecognition
{
    public static async Task<ImageText?> GetTextFromImage(SoftwareBitmap image)
    {
        try
        {
            Debug.WriteLine("[TextRecognition] Starting text recognition process...");

            if (image == null)
            {
                Debug.WriteLine("[TextRecognition] Input image is null");
                return new ImageText();
            }

            // Use the new Windows AI implementation
            ImageText result = await ImageText.GetFromRecognizedTextAsync(image);

            Debug.WriteLine($"[TextRecognition] Text recognition completed. Found {result.Lines.Count} text lines");
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TextRecognition] Error during text recognition: {ex.Message}");
            Debug.WriteLine($"[TextRecognition] Exception details: {ex}");

            // Return empty ImageText instead of null to maintain compatibility
            return new ImageText();
        }
    }

    public static async Task<ImageText> GetSavedText(string filename)
    {
        try
        {
            Debug.WriteLine($"[TextRecognition] Loading saved text from file: {filename}");

            StorageFolder folder = await Utils.GetAttachmentsTranscriptsFolderAsync();
            StorageFile file = await folder.GetFileAsync(filename);

            string text = await FileIO.ReadTextAsync(file);

            ImageText? lines = JsonSerializer.Deserialize<ImageText>(text);

            Debug.WriteLine($"[TextRecognition] Loaded saved text with {lines?.Lines.Count ?? 0} lines");
            return lines ?? new ImageText();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TextRecognition] Error loading saved text from {filename}: {ex.Message}");
            // If file doesn't exist or can't be read, return empty ImageText
            return new ImageText();
        }
    }
}
