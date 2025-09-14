using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Notes.AI.VoiceRecognition.VoiceActivity;

public class DetectionResult
{
    public string Type { get; set; }
    public double Seconds { get; set; }
}

public class WhisperChunk
{
    public double Start { get; set; }
    public double End { get; set; }

    public WhisperChunk(double start, double end)
    {
        this.Start = start;
        this.End = end;
    }

    public double Length => End - Start;
}

public static class WhisperChunking
{
    private static readonly int SAMPLE_RATE = 16000;
    private static readonly float START_THRESHOLD = 0.25f;
    private static readonly float END_THRESHOLD = 0.25f;
    private static readonly int MIN_SILENCE_DURATION_MS = 1000;
    private static readonly int SPEECH_PAD_MS = 400;
    private static readonly int WINDOW_SIZE_SAMPLES = 3200;

    private static readonly double MAX_CHUNK_S = 29;
    private static readonly double MIN_CHUNK_S = 5;

    public static List<WhisperChunk> SmartChunking(byte[] audioBytes)
    {
        Debug.WriteLine($"[WhisperChunking] Starting smart chunking - Audio bytes: {audioBytes?.Length ?? 0}");

        if (audioBytes == null || audioBytes.Length == 0)
        {
            Debug.WriteLine("[WhisperChunking] ERROR: Audio bytes are null or empty");
            return [];
        }

        float totalSeconds = audioBytes.Length / (SAMPLE_RATE * 2.0f);
        Debug.WriteLine($"[WhisperChunking] Audio duration: {totalSeconds:F2} seconds");

        try
        {
            Debug.WriteLine("[WhisperChunking] Attempting VAD-based chunking...");
            Debug.WriteLine("[WhisperChunking] Initializing VAD detector...");
            SlieroVadDetector vadDetector = new(START_THRESHOLD, END_THRESHOLD, SAMPLE_RATE, MIN_SILENCE_DURATION_MS, SPEECH_PAD_MS);
            Debug.WriteLine("[WhisperChunking] VAD detector initialized successfully");

            int bytesPerSample = 2;
            int bytesPerWindow = WINDOW_SIZE_SAMPLES * bytesPerSample;
            Debug.WriteLine($"[WhisperChunking] Window size: {WINDOW_SIZE_SAMPLES} samples ({bytesPerWindow} bytes)");

            List<DetectionResult> result = [];
            Stopwatch sw = Stopwatch.StartNew();

            int totalWindows = 0;
            for (int offset = 0; offset + bytesPerWindow <= audioBytes.Length; offset += bytesPerWindow)
            {
                totalWindows++;
                byte[] data = new byte[bytesPerWindow];
                Array.Copy(audioBytes, offset, data, 0, bytesPerWindow);

                try
                {
                    Dictionary<string, double> detectResult = vadDetector.Apply(data, true);

                    if (detectResult.Count > 0)
                    {
                        Debug.WriteLine($"[WhisperChunking] Window {totalWindows}: VAD detected {detectResult.Count} events");
                        foreach ((string? key, double value) in detectResult)
                        {
                            result.Add(new DetectionResult { Type = key, Seconds = value });
                            Debug.WriteLine($"[WhisperChunking] VAD event: {key} at {value:F2}s");
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[WhisperChunking] ERROR: Error applying VAD detector at offset {offset}: {e.Message}");
                    Debug.WriteLine($"[WhisperChunking] Exception details: {e}");

                    // If VAD fails, fallback to simple time-based chunking
                    Debug.WriteLine("[WhisperChunking] VAD failed, falling back to time-based chunking");
                    vadDetector?.Close();
                    return CreateTimeBasedChunks(totalSeconds, MAX_CHUNK_S);
                }
            }

            sw.Stop();
            Debug.WriteLine($"[WhisperChunking] VAD detection took {sw.ElapsedMilliseconds} ms");
            Debug.WriteLine($"[WhisperChunking] Processed {totalWindows} windows, detected {result.Count} VAD events");

            vadDetector.Close();
            Debug.WriteLine("[WhisperChunking] VAD detector closed");

            List<WhisperChunk>? stamps = GetTimeStamps(result, totalSeconds, MAX_CHUNK_S, MIN_CHUNK_S);
            Debug.WriteLine($"[WhisperChunking] Generated {stamps?.Count ?? 0} time-based chunks");

            return stamps ?? [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WhisperChunking] ERROR: VAD-based chunking failed: {ex.Message}");
            Debug.WriteLine($"[WhisperChunking] Exception details: {ex}");
            Debug.WriteLine("[WhisperChunking] Falling back to simple time-based chunking");

            // Fallback to simple time-based chunking
            return CreateTimeBasedChunks(totalSeconds, MAX_CHUNK_S);
        }
    }

    private static List<WhisperChunk> CreateTimeBasedChunks(double totalSeconds, double maxChunkLength)
    {
        Debug.WriteLine($"[WhisperChunking] Creating time-based chunks for {totalSeconds:F2}s audio with {maxChunkLength}s max chunk length");

        List<WhisperChunk> chunks = [];

        if (totalSeconds <= maxChunkLength)
        {
            Debug.WriteLine($"[WhisperChunking] Short audio, creating single chunk: 0.00s - {totalSeconds:F2}s");
            chunks.Add(new WhisperChunk(0, totalSeconds));
        }
        else
        {
            double currentStart = 0;
            int chunkNumber = 1;

            while (currentStart < totalSeconds)
            {
                double chunkEnd = Math.Min(currentStart + maxChunkLength, totalSeconds);
                Debug.WriteLine($"[WhisperChunking] Time-based chunk {chunkNumber}: {currentStart:F2}s - {chunkEnd:F2}s");
                chunks.Add(new WhisperChunk(currentStart, chunkEnd));
                currentStart = chunkEnd;
                chunkNumber++;
            }
        }

        Debug.WriteLine($"[WhisperChunking] Created {chunks.Count} time-based chunks");
        return chunks;
    }

    private static List<WhisperChunk> GetTimeStamps(List<DetectionResult> voiceAreas, double totalSeconds, double maxChunkLength, double minChunkLength)
    {
        Debug.WriteLine($"[WhisperChunking] Creating timestamps from {voiceAreas?.Count ?? 0} voice areas, total duration: {totalSeconds:F2}s");

        try
        {
            if (totalSeconds <= maxChunkLength)
            {
                Debug.WriteLine($"[WhisperChunking] Short audio ({totalSeconds:F2}s <= {maxChunkLength}s), creating single chunk");
                return [new WhisperChunk(0, totalSeconds)];
            }

            if (voiceAreas == null)
            {
                Debug.WriteLine("[WhisperChunking] WARNING: Voice areas is null");
                voiceAreas = [];
            }

            voiceAreas = [.. voiceAreas.OrderBy(va => va.Seconds)];
            Debug.WriteLine($"[WhisperChunking] Voice areas sorted by time");

            List<WhisperChunk> chunks = [];

            double nextChunkStart = 0.0;
            int chunkNumber = 1;

            while (nextChunkStart < totalSeconds)
            {
                double idealChunkEnd = nextChunkStart + maxChunkLength;
                double chunkEnd = idealChunkEnd > totalSeconds ? totalSeconds : idealChunkEnd;

                List<DetectionResult> validVoiceAreas = voiceAreas.Where(va => va.Seconds > nextChunkStart && va.Seconds <= chunkEnd).ToList();

                if (validVoiceAreas.Any())
                {
                    chunkEnd = validVoiceAreas.Last().Seconds;
                    Debug.WriteLine($"[WhisperChunking] Chunk {chunkNumber}: {nextChunkStart:F2}s - {chunkEnd:F2}s (adjusted to voice area end)");
                }
                else
                {
                    Debug.WriteLine($"[WhisperChunking] Chunk {chunkNumber}: {nextChunkStart:F2}s - {chunkEnd:F2}s (no voice areas)");
                }

                chunks.Add(new WhisperChunk(nextChunkStart, chunkEnd));
                nextChunkStart = chunkEnd + 0.1;
                chunkNumber++;
            }

            Debug.WriteLine($"[WhisperChunking] Created {chunks.Count} initial chunks");
            List<WhisperChunk>? mergedChunks = MergeSmallChunks(chunks, maxChunkLength, minChunkLength);
            Debug.WriteLine($"[WhisperChunking] After merging: {mergedChunks?.Count ?? 0} final chunks");

            return mergedChunks;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WhisperChunking] ERROR: GetTimeStamps failed: {ex.Message}");
            Debug.WriteLine($"[WhisperChunking] Exception details: {ex}");
            return [new WhisperChunk(0, totalSeconds)];
        }
    }

    private static List<WhisperChunk> MergeSmallChunks(List<WhisperChunk> chunks, double maxChunkLength, double minChunkLength)
    {
        Debug.WriteLine($"[WhisperChunking] Merging small chunks - Input: {chunks?.Count ?? 0} chunks");

        if (chunks == null || chunks.Count == 0)
        {
            Debug.WriteLine("[WhisperChunking] No chunks to merge");
            return chunks ?? [];
        }

        try
        {
            int mergedCount = 0;
            for (int i = 1; i < chunks.Count; i++)
            {
                // Check if current chunk is small and can be merged with previous
                if (chunks[i].Length < minChunkLength)
                {
                    double prevChunkLength = chunks[i - 1].Length;
                    double combinedLength = prevChunkLength + chunks[i].Length;

                    if (combinedLength <= maxChunkLength)
                    {
                        Debug.WriteLine($"[WhisperChunking] Merging chunk {i} (length: {chunks[i].Length:F2}s) with previous (combined: {combinedLength:F2}s)");
                        chunks[i - 1].End = chunks[i].End; // Merge with previous chunk
                        chunks.RemoveAt(i); // Remove current chunk
                        i--; // Adjust index to recheck current position now pointing to next chunk
                        mergedCount++;
                    }
                }
            }

            Debug.WriteLine($"[WhisperChunking] Merged {mergedCount} small chunks, final count: {chunks.Count}");
            return chunks;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WhisperChunking] ERROR: MergeSmallChunks failed: {ex.Message}");
            Debug.WriteLine($"[WhisperChunking] Exception details: {ex}");
            return chunks;
        }
    }
}
