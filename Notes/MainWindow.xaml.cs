using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Notes.Controls;
using Notes.Models;
using Notes.Pages;
using Notes.Services;
using Notes.ViewModels;
using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;

namespace Notes;

public sealed partial class MainWindow : Window
{

    public static Phi3View Phi3View;
    public static SearchView SearchView;
    public static MainWindow Instance;
    public ViewModel VM;
    private readonly AudioRecordingService _audioRecordingService;
    private bool _isRecording = false;
    private readonly DispatcherTimer _recordingTimer;
    private DateTime _recordingStartTime;

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

        // Initialize recording timer
        _recordingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _recordingTimer.Tick += RecordingTimer_Tick;
    }

    public async Task SelectNoteById(int id, int? attachmentId = null, string? attachmentText = null)
    {
        NoteViewModel? note = VM.Notes.Where(n => n.Note.Id == id).FirstOrDefault();
        if (note != null)
        {
            navView.SelectedItem = note;

            if (attachmentId.HasValue)
            {
                AttachmentViewModel? attachmentViewModel = note.Attachments.Where(a => a.Attachment.Id == attachmentId).FirstOrDefault();
                if (attachmentViewModel == null)
                {
                    AppDataContext context = await AppDataContext.GetCurrentAsync();
                    Attachment? attachment = context.Attachments.Where(a => a.Id == attachmentId.Value).FirstOrDefault();
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

    public async Task SeekToTimestampInAttachment(int attachmentId, string timestamp)
    {
        Debug.WriteLine($"[MainWindow] Seeking to timestamp {timestamp} in attachment {attachmentId}");

        try
        {
            // Find the attachment
            AppDataContext context = await AppDataContext.GetCurrentAsync();
            Attachment? attachment = context.Attachments.Where(a => a.Id == attachmentId).FirstOrDefault();
            if (attachment == null)
            {
                Debug.WriteLine($"[MainWindow] ERROR: Attachment {attachmentId} not found");
                return;
            }

            // Find the note containing this attachment
            Note? note = context.Notes.Where(n => n.Id == attachment.NoteId).FirstOrDefault();
            if (note == null)
            {
                Debug.WriteLine($"[MainWindow] ERROR: Note for attachment {attachmentId} not found");
                return;
            }

            // Navigate to the note and open the attachment
            await SelectNoteById(note.Id, attachmentId);

            // Parse the timestamp
            TimeSpan timeSpan = NoteViewModel.ParseTimestamp(timestamp);
            Debug.WriteLine($"[MainWindow] Parsed timestamp: {timeSpan}");

            // Set the media player position in the attachment view
            if (attachmentView != null && attachmentView.AttachmentVM?.Attachment?.Id == attachmentId)
            {
                // Access the media player through the attachment view and seek to the timestamp
                attachmentView.SeekToTimestamp(timeSpan);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] ERROR: Failed to seek to timestamp: {ex.Message}");
            Debug.WriteLine($"[MainWindow] Exception details: {ex}");
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
        NoteViewModel note = await VM.CreateNewNote();
        navView.SelectedItem = note;
    }

    private void navView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
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

            // Reset UI state on error
            UpdateRecordingUI(false);

            // Show error message to user
            ContentDialog dialog = new()
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
        StorageFile recordingFile = await _audioRecordingService.StartRecordingAsync();

        if (recordingFile == null)
        {
            Debug.WriteLine("[MainWindow] Failed to start recording");
            throw new InvalidOperationException("Failed to start recording.");
        }

        _isRecording = true;
        _recordingStartTime = DateTime.Now;
        _recordingTimer.Start();
        UpdateRecordingUI(true);
        Debug.WriteLine($"[MainWindow] Recording started successfully: {recordingFile.Path}");
    }

    private async Task StopRecording()
    {
        Debug.WriteLine("[MainWindow] Stopping recording...");

        _recordingTimer.Stop();
        StorageFile recordingFile = await _audioRecordingService.StopRecordingAsync();

        if (recordingFile == null)
        {
            Debug.WriteLine("[MainWindow] Failed to stop recording");
            throw new InvalidOperationException("Failed to stop recording.");
        }

        _isRecording = false;
        UpdateRecordingUI(false);
        Debug.WriteLine($"[MainWindow] Recording stopped successfully: {recordingFile.Path}");

        // Add the recorded file as an attachment to the current note
        await AddRecordingToCurrentNote(recordingFile);
    }

    private void RecordingTimer_Tick(object sender, object e)
    {
        if (_isRecording)
        {
            TimeSpan duration = DateTime.Now - _recordingStartTime;
            string durationText = $"{duration.Minutes:D2}:{duration.Seconds:D2}";

            // Update tooltip with recording duration
            ToolTipService.SetToolTip(RecordButton, $"Stop Recording ({durationText})");
        }
    }

    private void UpdateRecordingUI(bool isRecording)
    {
        try
        {
            if (RecordButton != null)
            {
                RecordButton.IsChecked = isRecording;

                // Update tooltip
                ToolTipService.SetToolTip(RecordButton, isRecording ? "Stop Recording (00:00)" : "Record Voice Note");

                // Show/hide recording indicator
                if (RecordingIndicator != null)
                {
                    RecordingIndicator.Visibility = isRecording ? Visibility.Visible : Visibility.Collapsed;
                }

                // Change microphone icon color/style when recording
                if (MicrophoneIcon != null)
                {
                    MicrophoneIcon.Foreground = isRecording
                        ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red)
                        : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] ERROR: Failed to update recording UI: {ex.Message}");
        }
    }

    private async Task AddRecordingToCurrentNote(StorageFile recordingFile)
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
                NoteViewModel newNote = await VM.CreateNewNote();
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
        ContentDialog dialog = new()
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

internal class MenuItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate NoteTemplate { get; set; }
    public DataTemplate DefaultTemplate { get; set; }
    protected override DataTemplate SelectTemplateCore(object item)
    {
        return item is NoteViewModel ? NoteTemplate : DefaultTemplate;
    }
}
