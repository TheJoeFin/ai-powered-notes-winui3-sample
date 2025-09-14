using Notes.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;

namespace Notes.ViewModels;

public class ViewModel
{
    public ObservableCollection<NoteViewModel> Notes { get; set; } = [];

    public ViewModel()
    {
        LoadNotes();
    }

    public async Task<NoteViewModel> CreateNewNote()
    {
        string title = "New note";

        StorageFolder folder = await Utils.GetLocalFolderAsync();
        StorageFile file = await folder.CreateFileAsync(title + Utils.FileExtension, CreationCollisionOption.GenerateUniqueName);

        Note note = new()
        {
            Title = title,
            Created = DateTime.Now,
            Modified = DateTime.Now,
            Filename = file.Name
        };

        NoteViewModel noteViewModel = new(note);
        Notes.Insert(0, noteViewModel);
        AppDataContext dataContext = await AppDataContext.GetCurrentAsync();
        dataContext.Notes.Add(note);
        await dataContext.SaveChangesAsync();

        return noteViewModel;
    }

    private async Task LoadNotes()
    {
        AppDataContext dataContext = await AppDataContext.GetCurrentAsync();
        List<Note> savedNotes = dataContext.Notes.Select(note => note).ToList();

        StorageFolder notesFolder = await Utils.GetLocalFolderAsync();
        IReadOnlyList<StorageFile> files = await notesFolder.GetFilesAsync();
        Dictionary<string, StorageFile> filenames = files.ToDictionary(f => f.Name, f => f);

        foreach (Note? note in savedNotes)
        {
            if (filenames.ContainsKey(note.Filename))
            {
                filenames.Remove(note.Filename);
                Notes.Add(new NoteViewModel(note));
            }
            else
            {
                // delete note from db
                dataContext.Notes.Remove(note);
            }
        }

        foreach (KeyValuePair<string, StorageFile> filename in filenames)
        {
            if (filename.Key.EndsWith(Utils.FileExtension))
            {
                StorageFile file = filename.Value;
                Note note = new()
                {
                    Title = file.DisplayName,
                    Created = file.DateCreated.DateTime,
                    Filename = file.Name,
                    Modified = DateTime.Now
                };
                dataContext.Notes.Add(note);
                Notes.Add(new NoteViewModel(note));
            }
        }

        await dataContext.SaveChangesAsync();
    }
}
