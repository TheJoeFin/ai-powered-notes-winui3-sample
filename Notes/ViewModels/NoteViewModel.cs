using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Notes.AI;
using Notes.AI.Embeddings;
using Notes.Models;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Notes.ViewModels
{
    public partial class NoteViewModel : ObservableObject
    {
        public readonly Note Note;

        [ObservableProperty]
        private ObservableCollection<AttachmentViewModel> attachments = new();

        [ObservableProperty]
        private ObservableCollection<string> todos = new();

        [ObservableProperty]
        private bool todosLoading = false;

        private DispatcherTimer _saveTimer;
        private bool _contentLoaded = false;

        public DispatcherQueue DispatcherQueue { get; set; }

        public NoteViewModel(Note note)
        {
            Note = note;
            _saveTimer = new DispatcherTimer();
            _saveTimer.Interval = TimeSpan.FromSeconds(5);
            _saveTimer.Tick += SaveTimerTick;
        }

        public string Title
        {
            get => Note.Title;
            set => SetProperty(Note.Title, value, Note, (note, value) =>
            {
                note.Title = value;
                HandleTitleChanged(value);
            });
        }

        public DateTime Modified
        {
            get => Note.Modified;
            set => SetProperty(Note.Modified, value, Note, (note, value) => note.Modified = value);
        }

        [ObservableProperty]
        private string content;

        private async Task HandleTitleChanged(string value)
        {
            var folder = await Utils.GetLocalFolderAsync();
            var file = await folder.GetFileAsync(Note.Filename);

            await file.RenameAsync(value.Trim() + Utils.FileExtension, NameCollisionOption.GenerateUniqueName);
            Note.Filename = file.Name;
            await AppDataContext.SaveCurrentAsync();
        }

        private async Task SaveContentAsync()
        {
            var folder = await Utils.GetLocalFolderAsync();
            var file = await folder.GetFileAsync(Note.Filename);
            await FileIO.WriteTextAsync(file, Content);
        }

        public async Task LoadContentAsync()
        {
            if (_contentLoaded)
            {
                return;
            }

            _contentLoaded = true;

            var folder = await Utils.GetLocalFolderAsync();
            var file = await folder.GetFileAsync(Note.Filename);
            content = await FileIO.ReadTextAsync(file);

            var context = await AppDataContext.GetCurrentAsync();
            var attachments = context.Attachments.Where(a => a.NoteId == Note.Id).ToList();
            foreach (var attachment in attachments)
            {
                Attachments.Add(new AttachmentViewModel(attachment));
            }
        }

        partial void OnContentChanged(string value)
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        public async Task AddAttachmentAsync(StorageFile file)
        {
            Debug.WriteLine("=== [NoteViewModel] AddAttachmentAsync ENTRY ===");
            Debug.WriteLine($"[NoteViewModel] Adding attachment: {file?.Name ?? "NULL FILE"}");
            Debug.WriteLine($"[NoteViewModel] File path: {file?.Path ?? "NULL PATH"}");
            Debug.WriteLine($"[NoteViewModel] File type: {file?.FileType ?? "NULL TYPE"}");

            var attachmentsFolder = await Utils.GetAttachmentsFolderAsync();
            bool shouldCopyFile = true;

            var attachment = new Attachment()
            {
                IsProcessed = false,
                Note = Note
            };

            Debug.WriteLine($"[NoteViewModel] Created attachment object");

            if (new string[] { ".png", ".jpg", ".jpeg" }.Contains(file.FileType))
            {
                attachment.Type = NoteAttachmentType.Image;
                Debug.WriteLine($"[NoteViewModel] Attachment type: Image");
            }
            else if (new string[] { ".mp3", ".wav", ".m4a", ".opus", ".waptt" }.Contains(file.FileType))
            {
                attachment.Type = NoteAttachmentType.Audio;
                Debug.WriteLine($"[NoteViewModel] Attachment type: Audio");

                // Only convert to WAV if it's not already a WAV file, or if it's not already in the attachments folder
                if (file.FileType != ".wav" || !file.Path.StartsWith(attachmentsFolder.Path))
                {
                    Debug.WriteLine($"[NoteViewModel] Converting audio file to WAV: {file.Path}");
                    file = await Utils.SaveAudioFileAsWav(file, attachmentsFolder);
                    shouldCopyFile = false;
                }
                else
                {
                    Debug.WriteLine($"[NoteViewModel] Audio file is already WAV in attachments folder: {file.Path}");
                    shouldCopyFile = false; // File is already in the right place and format
                }
            }
            else if (file.FileType == ".mp4")
            {
                attachment.Type = NoteAttachmentType.Video;
                Debug.WriteLine($"[NoteViewModel] Attachment type: Video");
            }
            else
            {
                attachment.Type = NoteAttachmentType.Document;
                Debug.WriteLine($"[NoteViewModel] Attachment type: Document");
            }

            if (shouldCopyFile && !file.Path.StartsWith(attachmentsFolder.Path))
            {
                Debug.WriteLine($"[NoteViewModel] Copying file to attachments folder");
                file = await file.CopyAsync(attachmentsFolder, file.Name, NameCollisionOption.GenerateUniqueName);
            }

            attachment.Filename = file.Name;
            Debug.WriteLine($"[NoteViewModel] Final attachment filename: {attachment.Filename}");

            Attachments.Add(new AttachmentViewModel(attachment));
            Debug.WriteLine($"[NoteViewModel] Added attachment to UI collection");

            var context = await AppDataContext.GetCurrentAsync();
            await context.Attachments.AddAsync(attachment);
            Debug.WriteLine($"[NoteViewModel] Added attachment to database context");

            await context.SaveChangesAsync();
            Debug.WriteLine($"[NoteViewModel] Saved changes to database");

            Debug.WriteLine($"[NoteViewModel] CALLING AttachmentProcessor.AddAttachment: {attachment.Filename}");
            Debug.WriteLine($"[NoteViewModel] Attachment ID: {attachment.Id}, Type: {attachment.Type}, IsProcessed: {attachment.IsProcessed}");

            AttachmentProcessor.AddAttachment(attachment);

            Debug.WriteLine("=== [NoteViewModel] AddAttachmentAsync EXIT ===");
        }

        public async Task RemoveAttachmentAsync(AttachmentViewModel attachmentViewModel)
        {
            Attachments.Remove(attachmentViewModel);

            var attachment = attachmentViewModel.Attachment;
            Note.Attachments.Remove(attachment);

            var attachmentsFolder = await Utils.GetAttachmentsFolderAsync();
            var file = await attachmentsFolder.GetFileAsync(attachment.Filename);
            await file.DeleteAsync();

            if (attachment.IsProcessed && !string.IsNullOrEmpty(attachment.FilenameForText))
            {
                var attachmentsTranscriptFolder = await Utils.GetAttachmentsTranscriptsFolderAsync();
                var transcriptFile = await attachmentsTranscriptFolder.GetFileAsync(attachment.FilenameForText);
                await transcriptFile.DeleteAsync();
            }

            var context = await AppDataContext.GetCurrentAsync();
            context.Attachments.Remove(attachment);
            context.TextChunks.RemoveRange(context.TextChunks.Where(tc => tc.SourceId == attachment.Id && tc.ContentType == "attachment"));

            await context.SaveChangesAsync();
        }

        public async Task ShowTodos()
        {
            if (App.ChatClient == null)
            {
                return;
            }

            if (!TodosLoading && (Todos == null || Todos.Count == 0))
            {
                DispatcherQueue.TryEnqueue(() => TodosLoading = true);
                var todos = await App.ChatClient.GetTodoItemsFromText(Content);
                if (todos != null && todos.Count > 0)
                {
                    DispatcherQueue.TryEnqueue(() => Todos = new ObservableCollection<string>(todos));
                }
            }

            DispatcherQueue.TryEnqueue(() => TodosLoading = false);
        }

        public async Task AddAttachmentAsync(SoftwareBitmap bitmap)
        {
            // save bitmap to file
            var attachmentsFolder = await Utils.GetAttachmentsFolderAsync();
            var file = await attachmentsFolder.CreateFileAsync(Guid.NewGuid().ToString() + ".png", CreationCollisionOption.GenerateUniqueName);
            using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
            }

            await AddAttachmentAsync(file);
        }

        private void SaveTimerTick(object? sender, object e)
        {
            _saveTimer.Stop();
            SaveContentToFileAndReIndex();
        }

        private async Task SaveContentToFileAndReIndex()
        {
            var folder = await Utils.GetLocalFolderAsync();
            var file = await folder.GetFileAsync(Note.Filename);

            Debug.WriteLine("Saving note " + Note.Title + " to filename " + Note.Filename);
            await FileIO.WriteTextAsync(file, Content);

            await SemanticIndex.Instance.AddOrReplaceContent(Content, Note.Id, "note", (o, p) => Debug.WriteLine($"Indexing note {Note.Title} {p * 100}%"));
        }
    }
}
