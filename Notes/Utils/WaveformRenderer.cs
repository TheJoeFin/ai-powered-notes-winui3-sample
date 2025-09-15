using Microsoft.UI.Xaml.Media.Imaging;
using NAudio.Wave;
using NAudio.WaveFormRenderer;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Windows.Storage;

namespace Notes;

public static class WaveformRenderer
{
    public enum PeakProvider
    {
        Max,
        RMS,
        Sampling,
        Average
    }

    private static Image Render(StorageFile audioFile, int height, int width, PeakProvider peakProvider)
    {
        WaveFormRendererSettings settings = new StandardWaveFormRendererSettings
        {
            BackgroundColor = Color.Transparent,
            SpacerPixels = 0,
            TopHeight = height,
            BottomHeight = height,
            Width = width,
            TopPeakPen = new Pen(Color.DarkGray),
            BottomPeakPen = new Pen(Color.DarkGray)
        };
        AudioFileReader audioFileReader = new(audioFile.Path);
        IPeakProvider provider = peakProvider switch
        {
            PeakProvider.Max => new MaxPeakProvider(),
            PeakProvider.RMS => new RmsPeakProvider(200),
            PeakProvider.Sampling => new SamplingPeakProvider(1600),
            _ => new AveragePeakProvider(4),
        };
        WaveFormRenderer renderer = new();
        return renderer.Render(audioFileReader, provider, settings);
    }

    public static async System.Threading.Tasks.Task<BitmapImage> GetWaveformImage(StorageFile audioFile)
    {
        StorageFile imageFile;
        StorageFolder attachmentsFolder = await Utils.GetAttachmentsTranscriptsFolderAsync();
        string waveformFileName = Path.ChangeExtension(Path.GetFileName(audioFile.Path) + "-waveform", ".png");
        try
        {
            imageFile = await attachmentsFolder.CreateFileAsync(waveformFileName, CreationCollisionOption.FailIfExists);
            using Stream stream = await imageFile.OpenStreamForWriteAsync();
            Image image = Render(audioFile, 400, 800, PeakProvider.Average);
            image.Save(stream, ImageFormat.Png);
        }
        catch
        {
            imageFile = await attachmentsFolder.GetFileAsync(waveformFileName);
        }


        Uri uri = new(imageFile.Path);
        BitmapImage bi = new(uri);

        return bi;
    }
}
