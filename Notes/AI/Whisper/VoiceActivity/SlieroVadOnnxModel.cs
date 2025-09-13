using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Notes.AI.VoiceRecognition.VoiceActivity
{
    public class SlieroVadOnnxModel : IDisposable
    {
        private readonly InferenceSession session;
        private Tensor<float> h;
        private Tensor<float> c;
        private int lastSr = 0;
        private int lastBatchSize = 0;
        private static readonly List<int> SampleRates = new List<int> { 8000, 16000 };

        public SlieroVadOnnxModel()
        {
            Debug.WriteLine("[SlieroVadOnnxModel] Initializing Silero VAD model...");

            try
            {
                var modelPath = $@"{AppDomain.CurrentDomain.BaseDirectory}onnx-models\whisper\silero_vad.onnx";
                Debug.WriteLine($"[SlieroVadOnnxModel] Model path: {modelPath}");

                if (!System.IO.File.Exists(modelPath))
                {
                    Debug.WriteLine($"[SlieroVadOnnxModel] ERROR: Model file not found at: {modelPath}");
                    throw new System.IO.FileNotFoundException($"Silero VAD model file not found at: {modelPath}");
                }

                Debug.WriteLine($"[SlieroVadOnnxModel] Model file exists, size: {new System.IO.FileInfo(modelPath).Length} bytes");

                var options = new SessionOptions();
                options.InterOpNumThreads = 1;
                options.IntraOpNumThreads = 1;
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED;
                Debug.WriteLine("[SlieroVadOnnxModel] SessionOptions configured");

                session = new InferenceSession(modelPath, options);
                Debug.WriteLine("[SlieroVadOnnxModel] InferenceSession created successfully");

                // Log model inputs and outputs
                Debug.WriteLine($"[SlieroVadOnnxModel] Model inputs: {string.Join(", ", session.InputMetadata.Keys)}");
                Debug.WriteLine($"[SlieroVadOnnxModel] Model outputs: {string.Join(", ", session.OutputMetadata.Keys)}");

                ResetStates();
                Debug.WriteLine("[SlieroVadOnnxModel] Model initialized successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SlieroVadOnnxModel] ERROR: Failed to initialize model: {ex.Message}");
                Debug.WriteLine($"[SlieroVadOnnxModel] Exception details: {ex}");
                throw;
            }
        }

        public void ResetStates()
        {
            Debug.WriteLine("[SlieroVadOnnxModel] Resetting model states...");

            try
            {
                h = new DenseTensor<float>(new[] { 2, 1, 64 });
                c = new DenseTensor<float>(new[] { 2, 1, 64 });
                lastSr = 0;
                lastBatchSize = 0;
                Debug.WriteLine("[SlieroVadOnnxModel] Model states reset successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SlieroVadOnnxModel] ERROR: Failed to reset states: {ex.Message}");
                Debug.WriteLine($"[SlieroVadOnnxModel] Exception details: {ex}");
            }
        }

        public void Close()
        {
            Debug.WriteLine("[SlieroVadOnnxModel] Closing model session...");
            session?.Dispose();
            Debug.WriteLine("[SlieroVadOnnxModel] Model session closed");
        }

        public class ValidationResult
        {
            public readonly float[][] X;
            public readonly int Sr;

            public ValidationResult(float[][] x, int sr)
            {
                X = x;
                Sr = sr;
            }
        }

        private ValidationResult ValidateInput(float[][] x, int sr)
        {
            Debug.WriteLine($"[SlieroVadOnnxModel] Validating input - Dimensions: {x?.Length ?? 0}, Sample rate: {sr}");

            try
            {
                if (x.Length == 1)
                {
                    x = [x[0]];
                }
                if (x.Length > 2)
                {
                    Debug.WriteLine($"[SlieroVadOnnxModel] ERROR: Incorrect audio data dimension: {x.Length}");
                    throw new ArgumentException($"Incorrect audio data dimension: {x.Length}");
                }

                if (sr != 16000 && sr % 16000 == 0)
                {
                    Debug.WriteLine($"[SlieroVadOnnxModel] Downsampling from {sr}Hz to 16000Hz");
                    int step = sr / 16000;
                    float[][] reducedX = x.Select(row => row.Where((_, i) => i % step == 0).ToArray()).ToArray();
                    x = reducedX;
                    sr = 16000;
                }

                if (!SampleRates.Contains(sr))
                {
                    Debug.WriteLine($"[SlieroVadOnnxModel] ERROR: Unsupported sample rate: {sr}Hz");
                    throw new ArgumentException($"Only supports sample rates {String.Join(", ", SampleRates)} (or multiples of 16000)");
                }

                if ((float)sr / x[0].Length > 31.25)
                {
                    Debug.WriteLine($"[SlieroVadOnnxModel] ERROR: Input audio is too short - SR: {sr}, Length: {x[0].Length}");
                    throw new ArgumentException("Input audio is too short");
                }

                Debug.WriteLine($"[SlieroVadOnnxModel] Input validation successful - Final SR: {sr}, Length: {x[0].Length}");
                return new ValidationResult(x, sr);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SlieroVadOnnxModel] ERROR: Input validation failed: {ex.Message}");
                throw;
            }
        }

        public float[] Call(float[][] x, int sr)
        {
            Debug.WriteLine($"[SlieroVadOnnxModel] Model call - Input dimensions: {x?.Length ?? 0}, Sample rate: {sr}");

            try
            {
                var result = ValidateInput(x, sr);
                x = result.X;
                sr = result.Sr;

                int batchSize = x.Length;
                int sampleSize = x[0].Length; // Assuming all subarrays have identical length
                Debug.WriteLine($"[SlieroVadOnnxModel] Batch size: {batchSize}, Sample size: {sampleSize}");

                if (lastBatchSize == 0 || lastSr != sr || lastBatchSize != batchSize)
                {
                    Debug.WriteLine($"[SlieroVadOnnxModel] Model state change detected, resetting states (lastSR: {lastSr} -> {sr}, lastBatch: {lastBatchSize} -> {batchSize})");
                    ResetStates();
                }

                // Flatten the jagged array and create the tensor with the correct shape
                var flatArray = x.SelectMany(inner => inner).ToArray();
                var inputTensor = new DenseTensor<float>(flatArray, [batchSize, sampleSize]);

                // Convert sr to a tensor, if the model expects a scalar as a single-element tensor, ensure matching the expected dimensions
                var srTensor = new DenseTensor<long>(new long[] { sr }, [1]);

                // Try different parameter combinations based on what the model actually expects
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input", inputTensor),
                    NamedOnnxValue.CreateFromTensor("sr", srTensor)
                };

                // Add state parameters - try the most common naming conventions
                var inputMetadataKeys = session.InputMetadata.Keys.ToList();
                Debug.WriteLine($"[SlieroVadOnnxModel] Available input parameters: {string.Join(", ", inputMetadataKeys)}");

                // Try to match state parameters with different naming conventions
                if (inputMetadataKeys.Contains("state"))
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor("state", h));
                    Debug.WriteLine("[SlieroVadOnnxModel] Added 'state' parameter");
                }
                else if (inputMetadataKeys.Contains("h"))
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor("h", h));
                    Debug.WriteLine("[SlieroVadOnnxModel] Added 'h' parameter");
                }

                if (inputMetadataKeys.Contains("stateN"))
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor("stateN", c));
                    Debug.WriteLine("[SlieroVadOnnxModel] Added 'stateN' parameter");
                }
                else if (inputMetadataKeys.Contains("c"))
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor("c", c));
                    Debug.WriteLine("[SlieroVadOnnxModel] Added 'c' parameter");
                }

                Debug.WriteLine($"[SlieroVadOnnxModel] Created {inputs.Count} input tensors, running inference...");

                using (var results = session.Run(inputs))
                {
                    var output = results.First().AsEnumerable<float>().ToArray();

                    // Update state tensors from outputs if available
                    if (results.Count > 1)
                    {
                        h = results.ElementAt(1).AsTensor<float>();
                    }
                    if (results.Count > 2)
                    {
                        c = results.ElementAt(2).AsTensor<float>();
                    }

                    lastSr = sr;
                    lastBatchSize = batchSize;

                    Debug.WriteLine($"[SlieroVadOnnxModel] Inference successful, output length: {output?.Length ?? 0}");
                    if (output != null && output.Length > 0)
                    {
                        Debug.WriteLine($"[SlieroVadOnnxModel] VAD probability: {output[0]:F4}");
                    }

                    return output;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SlieroVadOnnxModel] ERROR: Model call failed: {ex.Message}");
                Debug.WriteLine($"[SlieroVadOnnxModel] Exception details: {ex}");
                Debug.WriteLine($"[SlieroVadOnnxModel] Stack trace: {ex.StackTrace}");
                throw new InvalidOperationException("An error occurred while calling the model", ex);
            }
        }

        public static int count = 0;

        public void Dispose()
        {
            Debug.WriteLine("[SlieroVadOnnxModel] Disposing model...");
            session?.Dispose();
            GC.SuppressFinalize(this);
            Debug.WriteLine("[SlieroVadOnnxModel] Model disposed");
        }
    }
}