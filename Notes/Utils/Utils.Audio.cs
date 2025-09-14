using NReco.VideoConverter;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;

namespace Notes;

internal partial class Utils
{
    public static byte[] LoadAudioBytes(string file)
    {
        Debug.WriteLine($"[Utils.Audio] Loading audio bytes from: {file}");

        if (string.IsNullOrEmpty(file))
        {
            Debug.WriteLine("[Utils.Audio] ERROR: File path is null or empty");
            return new byte[0];
        }

        if (!File.Exists(file))
        {
            Debug.WriteLine($"[Utils.Audio] ERROR: File does not exist: {file}");
            return new byte[0];
        }

        try
        {
            FileInfo fileInfo = new(file);
            Debug.WriteLine($"[Utils.Audio] Input file size: {fileInfo.Length} bytes");

            string extension = Path.GetExtension(file)[1..];
            Debug.WriteLine($"[Utils.Audio] File extension: {extension}");

            FFMpegConverter ffmpeg = new();
            MemoryStream output = new();

            Debug.WriteLine("[Utils.Audio] Starting audio conversion to PCM...");

            // Convert to PCM
            ffmpeg.ConvertMedia(inputFile: file,
                                inputFormat: null,
                                outputStream: output,
                                //  DE s16le PCM signed 16-bit little-endian
                                outputFormat: "s16le",
                                new ConvertSettings()
                                {
                                    AudioCodec = "pcm_s16le",
                                    AudioSampleRate = 16000,
                                    // Convert to mono
                                    CustomOutputArgs = "-ac 1"
                                });

            byte[] result = output.ToArray();
            Debug.WriteLine($"[Utils.Audio] Conversion completed - Output: {result.Length} bytes");
            Debug.WriteLine($"[Utils.Audio] Audio duration: {result.Length / 2.0 / 16000:F2} seconds");

            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Utils.Audio] ERROR: Failed to load audio bytes from {file}: {ex.Message}");
            Debug.WriteLine($"[Utils.Audio] Exception details: {ex}");
            Debug.WriteLine($"[Utils.Audio] Stack trace: {ex.StackTrace}");
            return new byte[0];
        }
    }

    public static float[] ExtractAudioSegment(string inPath, double startTimeInSeconds, double segmentDurationInSeconds)
    {
        Debug.WriteLine($"[Utils.Audio] Extracting audio segment from: {inPath}");
        Debug.WriteLine($"[Utils.Audio] Start: {startTimeInSeconds:F2}s, Duration: {segmentDurationInSeconds:F2}s");

        if (string.IsNullOrEmpty(inPath))
        {
            Debug.WriteLine("[Utils.Audio] ERROR: Input path is null or empty");
            return new float[0];
        }

        if (!File.Exists(inPath))
        {
            Debug.WriteLine($"[Utils.Audio] ERROR: Input file does not exist: {inPath}");
            return new float[0];
        }

        if (startTimeInSeconds < 0 || segmentDurationInSeconds <= 0)
        {
            Debug.WriteLine($"[Utils.Audio] ERROR: Invalid time parameters - Start: {startTimeInSeconds}, Duration: {segmentDurationInSeconds}");
            return new float[0];
        }

        try
        {
            string extension = Path.GetExtension(inPath)[1..];
            MemoryStream output = new();

            ConvertSettings convertSettings = new()
            {
                Seek = (float?)startTimeInSeconds,
                MaxDuration = (float?)segmentDurationInSeconds,
                //AudioCodec = "pcm_s16le",
                AudioSampleRate = 16000,
                CustomOutputArgs = "-vn -ac 1",
            };

            Debug.WriteLine($"[Utils.Audio] FFmpeg conversion settings - Seek: {startTimeInSeconds:F2}s, Duration: {segmentDurationInSeconds:F2}s");

            FFMpegConverter ffMpegConverter = new();
            ffMpegConverter.ConvertMedia(
                inputFile: inPath,
                inputFormat: null,
                outputStream: output,
                outputFormat: "wav",
                convertSettings);

            byte[] buffer = output.ToArray();
            Debug.WriteLine($"[Utils.Audio] Extracted buffer size: {buffer.Length} bytes");

            if (buffer.Length == 0)
            {
                Debug.WriteLine("[Utils.Audio] WARNING: No data extracted from audio segment");
                return new float[0];
            }

            int bytesPerSample = 2; // Assuming 16-bit depth (2 bytes per sample)

            // Calculate total samples in the buffer
            int totalSamples = buffer.Length / bytesPerSample;
            float[] samples = new float[totalSamples];

            Debug.WriteLine($"[Utils.Audio] Converting {totalSamples} samples to float array...");

            for (int i = 0; i < totalSamples; i++)
            {
                int bufferIndex = i * bytesPerSample;
                short sample = (short)((buffer[bufferIndex + 1] << 8) | buffer[bufferIndex]);
                samples[i] = sample / 32768.0f; // Normalize to range [-1,1] for floating point samples
            }

            Debug.WriteLine($"[Utils.Audio] Audio segment extraction completed - {samples.Length} samples ({samples.Length / 16000.0:F2}s)");
            return samples;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Utils.Audio] ERROR: Error during audio extraction from {inPath}: {ex.Message}");
            Debug.WriteLine($"[Utils.Audio] Exception details: {ex}");
            Debug.WriteLine($"[Utils.Audio] Stack trace: {ex.StackTrace}");
            return new float[0];
        }
    }

    public static async Task<StorageFile> SaveAudioFileAsWav(StorageFile file, StorageFolder folderToSaveTo)
    {
        Debug.WriteLine($"[Utils.Audio] Converting audio file to WAV: {file?.Path ?? "null"}");

        if (file == null)
        {
            Debug.WriteLine("[Utils.Audio] ERROR: Input file is null");
            return null;
        }

        if (folderToSaveTo == null)
        {
            Debug.WriteLine("[Utils.Audio] ERROR: Destination folder is null");
            return null;
        }

        try
        {
            FFMpegConverter ffmpeg = new();
            string newFilePath = $"{folderToSaveTo.Path}\\{file.DisplayName}.wav";
            Debug.WriteLine($"[Utils.Audio] Converting to: {newFilePath}");

            ffmpeg.ConvertMedia(file.Path, newFilePath, "wav");
            Debug.WriteLine($"[Utils.Audio] Conversion completed");

            StorageFile newFile = await StorageFile.GetFileFromPathAsync(newFilePath);
            Debug.WriteLine($"[Utils.Audio] WAV file created: {newFile.Path}");

            return newFile;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Utils.Audio] ERROR: Failed to convert audio file to WAV: {ex.Message}");
            Debug.WriteLine($"[Utils.Audio] Exception details: {ex}");
            throw;
        }
    }
}
