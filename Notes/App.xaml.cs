using Microsoft.Extensions.AI;
using Microsoft.UI.Xaml;
using Notes.AI;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Notes
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        public static IChatClient? ChatClient { get; private set; }
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            m_window = new MainWindow();
            m_window.Activate();

            // Initialize GenAI for NPU acceleration
            try
            {
                Debug.WriteLine("[App] Initializing GenAI for NPU acceleration...");
                GenAIModel.InitializeGenAI();
                Debug.WriteLine("[App] ✅ GenAI initialization completed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[App] ⚠️ GenAI initialization failed: {ex.Message}");
            }

            _ = InitializeIChatClient();
        }

        private async Task InitializeIChatClient()
        {
            Debug.WriteLine("[App] Starting ChatClient initialization...");

            // Try PhiSilica first (local Windows AI with NPU)
            try
            {
                Debug.WriteLine("[App] Attempting to initialize PhiSilica (NPU-accelerated)...");
                ChatClient = await PhiSilicaClient.CreateAsync();
                if (ChatClient != null)
                {
                    Debug.WriteLine("[App] ✅ PhiSilica ChatClient successfully initialized! Using NPU acceleration.");
                    return;
                }
                else
                {
                    Debug.WriteLine("[App] ⚠️ PhiSilica returned null - not available on this system");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[App] ❌ PhiSilica initialization failed: {ex.Message}");
                Debug.WriteLine($"[App] PhiSilica may not be supported on this Windows version or hardware");
            }

            // Try local GenAI model as fallback
            try
            {
                Debug.WriteLine("[App] Attempting to initialize local GenAI model...");
                var modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "onnx-models", "genai-model");
                Debug.WriteLine($"[App] Model path: {modelPath}");
                Debug.WriteLine($"[App] Model path exists: {Directory.Exists(modelPath)}");

                if (Directory.Exists(modelPath))
                {
                    var files = Directory.GetFiles(modelPath);
                    Debug.WriteLine($"[App] Files in model directory: {string.Join(", ", files.Select(Path.GetFileName))}");
                }

                ChatClient = await GenAIModel.CreateAsync(modelPath, new LlmPromptTemplate
                {
                    System = "<|system|>\n{{CONTENT}}<|end|>\n",
                    User = "<|user|>\n{{CONTENT}}<|end|>\n",
                    Assistant = "<|assistant|>\n{{CONTENT}}<|end|>\n",
                    Stop = ["<|system|>", "<|user|>", "<|assistant|>", "<|end|>"]
                });

                if (ChatClient != null)
                {
                    Debug.WriteLine("[App] ✅ Local GenAI model successfully initialized!");
                    return;
                }
                else
                {
                    Debug.WriteLine("[App] ❌ Local GenAI model returned null");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[App] ❌ Local GenAI model initialization failed: {ex.Message}");
                Debug.WriteLine($"[App] Exception details: {ex}");
            }

            // No local AI available
            Debug.WriteLine("[App] ⚠️ No local AI models available - using fast local processing fallbacks");
            Debug.WriteLine("[App] This means summarization will use rule-based processing instead of AI");
            ChatClient = null;
        }

        private Window m_window;
        public Window Window => m_window;

    }
}
