using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Notes.AI.TextRecognition
{
    internal class ImageText
    {
        public List<RecognizedTextLine> Lines { get; set; } = new();
        public double ImageAngle { get; set; }

        public static ImageText GetFromRecognizedText(object? recognizedText)
        {
            ImageText attachmentRecognizedText = new();

            if (recognizedText == null)
            {
                return attachmentRecognizedText;
            }

            // Stub implementation - Microsoft.Windows.Vision is not available
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
}
