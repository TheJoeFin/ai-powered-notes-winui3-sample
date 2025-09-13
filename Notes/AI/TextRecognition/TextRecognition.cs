using System;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Notes.AI.TextRecognition
{
    internal static class TextRecognition
    {
        public static async Task<ImageText?> GetTextFromImage(SoftwareBitmap image)
        {
            // Microsoft.Windows.Vision is not available in this environment
            // Return empty ImageText
            await Task.CompletedTask;
            return new ImageText();
        }

        public static async Task<ImageText> GetSavedText(string filename)
        {
            try
            {
                var folder = await Utils.GetAttachmentsTranscriptsFolderAsync();
                var file = await folder.GetFileAsync(filename);

                var text = await FileIO.ReadTextAsync(file);

                var lines = JsonSerializer.Deserialize<ImageText>(text);
                return lines ?? new ImageText();
            }
            catch
            {
                // If file doesn't exist or can't be read, return empty ImageText
                return new ImageText();
            }
        }
    }
}
