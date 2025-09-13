using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Notes.Controls;
using Notes.Pages;
using Notes.Services;
using Notes.ViewModels;
using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Notes
{
    public sealed partial class MainWindow : Window
    {

        public static Phi3View Phi3View;
        public static SearchView SearchView;
        public static MainWindow Instance;
        public ViewModel VM;
        private AudioRecordingService _audioRecordingService;
        private bool _isRecording = false;

        public MainWindow()
        {
            VM = new ViewModel();
            this.InitializeComponent();

            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(TitleBar);

            Instance = this;
            Phi3View = phi3View;
            SearchView = searchView;

            VM.Notes.CollectionChanged += Notes_CollectionChanged;

            // Initialize audio recording service
            _audioRecordingService = new AudioRecordingService();
        }

        public async Task SelectNoteById(int id, int? attachmentId = null, string? attachmentText = null)
        {
            var note = VM.Notes.Where(n => n.Note.Id == id).FirstOrDefault();
            if (note != null)
            {
                navView.SelectedItem = note;

                if (attachmentId.HasValue)
                {
                    var attachmentViewModel = note.Attachments.Where(a => a.Attachment.Id == attachmentId).FirstOrDefault();
                    if (attachmentViewModel == null)
                    {
                        var context = await AppDataContext.GetCurrentAsync();
                        var attachment = context.Attachments.Where(a => a.Id == attachmentId.Value).FirstOrDefault();
                        if (attachment == null)
                        {
                            return;
                        }

                        attachmentViewModel = new AttachmentViewModel(attachment);
                    }

                    OpenAttachmentView(attachmentViewModel, attachmentText);
                }
            }
        }

        private void navView_Loaded(object sender, RoutedEventArgs e)
        {
            if (navView.MenuItems.Count > 0)
                navView.SelectedItem = navView.MenuItems[0];
        }

        private void Notes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (navView.SelectedItem == null && VM.Notes.Count > 0)
                navView.SelectedItem = VM.Notes[0];
        }

        private async void NewButton_Click(object sender, RoutedEventArgs e)
        {
            var note = await VM.CreateNewNote();
            navView.SelectedItem = note;
        }

        private void navView_SelectionChanged(NavigationView sender, Microsoft.UI.Xaml.Controls.NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is NoteViewModel note)
            {
                navFrame.Navigate(typeof(NotesPage), note);
            }
        }

        private void Search_Click(object sender, RoutedEventArgs e)
        {
            searchView.Show();
        }

        private void AskMyNotesClicked(object sender, RoutedEventArgs e)
        {
            phi3View.ShowForRag();
        }

        private async void RecordButton_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("[MainWindow] Record button clicked");

            try
            {
                if (!_isRecording)
                {
                    Debug.WriteLine("[MainWindow] Starting recording...");
                    await StartRecording();
                }
                else
                {
                    Debug.WriteLine("[MainWindow] Stopping recording...");
                    await StopRecording();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainWindow] ERROR: Recording operation failed: {ex.Message}");
                Debug.WriteLine($"[MainWindow] Exception details: {ex}");

                // Show error message to user
                var dialog = new ContentDialog
                {
                    Title = "Recording Error",
                    Content = $"Recording failed: {ex.Message}",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }

        private async Task StartRecording()
        {
            Debug.WriteLine("[MainWindow] Initializing audio recording service...");

            // Initialize the recording service
            bool initialized = await _audioRecordingService.InitializeAsync();
            if (!initialized)
            {
                Debug.WriteLine("[MainWindow] Failed to initialize audio recording service");
                throw new InvalidOperationException("Failed to initialize audio recording service. Please check microphone permissions.");
            }

            Debug.WriteLine("[MainWindow] Starting audio recording...");
            var recordingFile = await _audioRecordingService.StartRecordingAsync();

            if (recordingFile == null)
            {
                Debug.WriteLine("[MainWindow] Failed to start recording");
                throw new InvalidOperationException("Failed to start recording.");
            }

            _isRecording = true;
            Debug.WriteLine($"[MainWindow] Recording started successfully: {recordingFile.Path}");

            // TODO: Update UI to show recording state (change button icon, show recording indicator, etc.)
        }

        private async Task StopRecording()
        {
            Debug.WriteLine("[MainWindow] Stopping recording...");

            var recordingFile = await _audioRecordingService.StopRecordingAsync();

            if (recordingFile == null)
            {
                Debug.WriteLine("[MainWindow] Failed to stop recording");
                throw new InvalidOperationException("Failed to stop recording.");
            }

            _isRecording = false;
            Debug.WriteLine($"[MainWindow] Recording stopped successfully: {recordingFile.Path}");

            // Add the recorded file as an attachment to the current note
            await AddRecordingToCurrentNote(recordingFile);
        }

        private async Task AddRecordingToCurrentNote(Windows.Storage.StorageFile recordingFile)
        {
            Debug.WriteLine($"[MainWindow] Adding recording to current note: {recordingFile.Path}");

            try
            {
                // Get the currently selected note
                if (navView.SelectedItem is NoteViewModel currentNote)
                {
                    Debug.WriteLine($"[MainWindow] Adding recording to note: {currentNote.Title}");
                    await currentNote.AddAttachmentAsync(recordingFile);
                    Debug.WriteLine($"[MainWindow] Recording successfully added to note: {currentNote.Title}");
                }
                else
                {
                    Debug.WriteLine("[MainWindow] No note selected, creating new note for recording");
                    // Create a new note if none is selected
                    var newNote = await VM.CreateNewNote();
                    navView.SelectedItem = newNote;
                    await newNote.AddAttachmentAsync(recordingFile);
                    Debug.WriteLine($"[MainWindow] Recording added to new note: {newNote.Title}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainWindow] ERROR: Failed to add recording to note: {ex.Message}");
                Debug.WriteLine($"[MainWindow] Exception details: {ex}");
                throw;
            }
        }

        private async Task ShowRecordingMessage()
        {
            var dialog = new ContentDialog
            {
                Title = "Recording Feature",
                Content = "Recording feature is temporarily disabled while we fix some issues.",
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }

        public void OpenAttachmentView(AttachmentViewModel attachment, string? attachmentText = null)
        {
            attachmentView.UpdateAttachment(attachment, attachmentText);
            attachmentView.Show();
        }
    }

    class MenuItemTemplateSelector : DataTemplateSelector
    {
        public DataTemplate NoteTemplate { get; set; }
        public DataTemplate DefaultTemplate { get; set; }
        protected override DataTemplate SelectTemplateCore(object item)
        {
            return item is NoteViewModel ? NoteTemplate : DefaultTemplate;
        }
    }
}
