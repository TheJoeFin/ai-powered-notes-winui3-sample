using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Notes.AI.VoiceRecognition.VoiceActivity;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Storage;

namespace Notes.AI.VoiceRecognition
{
    public static class Whisper
    {
        private static InferenceSession? _inferenceSession;

        private static InferenceSession InitializeModel()
        {
            Debug.WriteLine("[Whisper] Initializing Whisper model...");

            try
            {
                var modelPath = $@"{AppDomain.CurrentDomain.BaseDirectory}onnx-models\whisper\whisper_medium_int8_cpu_ort_1.18.0.onnx";
                Debug.WriteLine($"[Whisper] Model path: {modelPath}");

                if (!System.IO.File.Exists(modelPath))
                {
                    Debug.WriteLine($"[Whisper] ERROR: Model file not found at: {modelPath}");
                    throw new System.IO.FileNotFoundException($"Whisper model file not found at: {modelPath}");
                }

                Debug.WriteLine($"[Whisper] Model file exists, size: {new System.IO.FileInfo(modelPath).Length} bytes");

                SessionOptions options = new SessionOptions();
                options.RegisterOrtExtensions();
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                options.EnableMemoryPattern = false;
                Debug.WriteLine("[Whisper] SessionOptions configured");

                var session = new InferenceSession(modelPath, options);
                Debug.WriteLine("[Whisper] InferenceSession created successfully");

                // Log model inputs and outputs
                Debug.WriteLine($"[Whisper] Model inputs: {string.Join(", ", session.InputMetadata.Keys)}");
                Debug.WriteLine($"[Whisper] Model outputs: {string.Join(", ", session.OutputMetadata.Keys)}");

                return session;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Whisper] ERROR: Failed to initialize model: {ex.Message}");
                Debug.WriteLine($"[Whisper] Exception details: {ex}");
                throw;
            }
        }

        private static async Task<List<WhisperTranscribedChunk>> TranscribeChunkAsync(float[] pcmAudioData, string inputLanguage, WhisperTaskType taskType, int offsetSeconds = 30)
        {
            Debug.WriteLine($"[Whisper] Starting chunk transcription - Audio length: {pcmAudioData?.Length ?? 0} samples, Language: {inputLanguage}, Offset: {offsetSeconds}s");

            try
            {
                if (_inferenceSession == null)
                {
                    Debug.WriteLine("[Whisper] Inference session not initialized, creating new session...");
                    _inferenceSession = InitializeModel();
                }

                if (pcmAudioData == null || pcmAudioData.Length == 0)
                {
                    Debug.WriteLine("[Whisper] WARNING: Audio data is null or empty");
                    return new List<WhisperTranscribedChunk>();
                }

                Debug.WriteLine("[Whisper] Creating input tensors...");
                var audioTensor = new DenseTensor<float>(pcmAudioData, [1, pcmAudioData.Length]);
                var timestampsEnableTensor = new DenseTensor<int>(new[] { 1 }, [1]);

                int task = (int)taskType;
                int langCode = WhisperUtils.GetLangId(inputLanguage);
                Debug.WriteLine($"[Whisper] Language code: {langCode}, Task: {task}");

                var decoderInputIds = new int[] { 50258, langCode, task };
                var langAndModeTensor = new DenseTensor<int>(decoderInputIds, [1, 3]);

                var inputs = new List<NamedOnnxValue> {
                    NamedOnnxValue.CreateFromTensor("audio_pcm", audioTensor),
                    NamedOnnxValue.CreateFromTensor("min_length", new DenseTensor<int>(new int[] { 0 }, [1])),
                    NamedOnnxValue.CreateFromTensor("max_length", new DenseTensor<int>(new int[] { 448 }, [1])),
                    NamedOnnxValue.CreateFromTensor("num_beams", new DenseTensor<int>(new int[] {1}, [1])),
                    NamedOnnxValue.CreateFromTensor("num_return_sequences", new DenseTensor<int>(new int[] { 1 }, [1])),
                    NamedOnnxValue.CreateFromTensor("length_penalty", new DenseTensor<float>(new float[] { 1.0f }, [1])),
                    NamedOnnxValue.CreateFromTensor("repetition_penalty", new DenseTensor<float>(new float[] { 1.2f }, [1])),
                    NamedOnnxValue.CreateFromTensor("logits_processor", timestampsEnableTensor),
                    NamedOnnxValue.CreateFromTensor("decoder_input_ids", langAndModeTensor)
                };

                Debug.WriteLine($"[Whisper] Created {inputs.Count} input tensors");
                Debug.WriteLine("[Whisper] Running model inference...");

                using var results = _inferenceSession.Run(inputs);
                Debug.WriteLine($"[Whisper] Model inference completed, outputs: {results.Count}");

                string result = results[0].AsTensor<string>().GetValue(0);
                Debug.WriteLine($"[Whisper] Raw transcription result: '{result}'");

                var transcribedChunks = WhisperUtils.ProcessTranscriptionWithTimestamps(result, offsetSeconds);
                Debug.WriteLine($"[Whisper] Processed transcription into {transcribedChunks.Count} chunks");

                return transcribedChunks;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Whisper] ERROR: Chunk transcription failed: {ex.Message}");
                Debug.WriteLine($"[Whisper] Exception details: {ex}");
                Debug.WriteLine($"[Whisper] Stack trace: {ex.StackTrace}");
                // return empty list in case of exception
                return new List<WhisperTranscribedChunk>();
            }
        }

        public async static Task<List<WhisperTranscribedChunk>> TranscribeAsync(StorageFile audioFile, EventHandler<float>? progress = null)
        {
            Debug.WriteLine($"[Whisper] Starting transcription of audio file: {audioFile?.Path ?? "null"}");

            if (audioFile == null)
            {
                Debug.WriteLine("[Whisper] ERROR: Audio file is null");
                return new List<WhisperTranscribedChunk>();
            }

            var transcribedChunks = new List<WhisperTranscribedChunk>();

            try
            {
                var sw = Stopwatch.StartNew();
                Debug.WriteLine("[Whisper] Loading audio bytes...");

                var audioBytes = Utils.LoadAudioBytes(audioFile.Path);

                sw.Stop();
                Debug.WriteLine($"[Whisper] Loading took {sw.ElapsedMilliseconds} ms");
                Debug.WriteLine($"[Whisper] Audio bytes loaded: {audioBytes?.Length ?? 0} bytes");

                if (audioBytes == null || audioBytes.Length == 0)
                {
                    Debug.WriteLine("[Whisper] ERROR: No audio bytes loaded");
                    return transcribedChunks;
                }

                sw.Start();
                Debug.WriteLine("[Whisper] Starting smart chunking...");

                var dynamicChunks = WhisperChunking.SmartChunking(audioBytes);

                sw.Stop();
                Debug.WriteLine($"[Whisper] Chunking took {sw.ElapsedMilliseconds} ms");
                Debug.WriteLine($"[Whisper] Created {dynamicChunks?.Count ?? 0} chunks");

                if (dynamicChunks == null || dynamicChunks.Count == 0)
                {
                    Debug.WriteLine("[Whisper] WARNING: No chunks generated from audio");
                    return transcribedChunks;
                }

                for (var i = 0; i < dynamicChunks.Count; i++)
                {
                    var chunk = dynamicChunks[i];
                    Debug.WriteLine($"[Whisper] Processing chunk {i + 1}/{dynamicChunks.Count}: {chunk.Start:F2}s - {chunk.End:F2}s (duration: {chunk.Length:F2}s)");

                    try
                    {
                        var audioSegment = Utils.ExtractAudioSegment(audioFile.Path, chunk.Start, chunk.End - chunk.Start);
                        Debug.WriteLine($"[Whisper] Extracted audio segment: {audioSegment?.Length ?? 0} samples");

                        if (audioSegment != null && audioSegment.Length > 0)
                        {
                            var transcription = await TranscribeChunkAsync(audioSegment, "en", WhisperTaskType.Transcribe, (int)chunk.Start);

                            if (transcription != null && transcription.Count > 0)
                            {
                                transcribedChunks.AddRange(transcription);
                                Debug.WriteLine($"[Whisper] Added {transcription.Count} transcribed chunks");
                            }
                            else
                            {
                                Debug.WriteLine($"[Whisper] No transcription returned for chunk {i + 1}");
                            }
                        }
                        else
                        {
                            Debug.WriteLine($"[Whisper] WARNING: No audio segment extracted for chunk {i + 1}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Whisper] ERROR: Failed to process chunk {i + 1}: {ex.Message}");
                        Debug.WriteLine($"[Whisper] Exception details: {ex}");
                    }

                    progress?.Invoke(null, (float)i / dynamicChunks.Count);
                }

                Debug.WriteLine($"[Whisper] Transcription completed. Total chunks: {transcribedChunks.Count}");
                return transcribedChunks;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Whisper] ERROR: Transcription failed: {ex.Message}");
                Debug.WriteLine($"[Whisper] Exception details: {ex}");
                Debug.WriteLine($"[Whisper] Stack trace: {ex.StackTrace}");
                return transcribedChunks;
            }
        }
    }

    internal enum WhisperTaskType
    {
        Translate = 50358,
        Transcribe = 50359
    }

    public class WhisperTranscribedChunk
    {
        public string Text { get; set; }
        public double Start { get; set; }
        public double End { get; set; }
        public double Length => End - Start;
    }
}
