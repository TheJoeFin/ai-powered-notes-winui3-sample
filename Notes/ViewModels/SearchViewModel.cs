using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Notes.ViewModels;

public partial class SearchViewModel : ObservableObject
{
    [ObservableProperty]
    public bool showResults = false;
    public ObservableCollection<SearchResult> Results { get; set; } = [];

    private readonly DispatcherTimer _searchTimer = new();
    private string _searchText = string.Empty;

    public SearchViewModel()
    {
        _searchTimer.Interval = TimeSpan.FromMilliseconds(1500);
        _searchTimer.Tick += SearchTimerTick;
    }
    public void HandleTextChanged(string text)
    {
        Debug.WriteLine("reseting timer");
        _searchTimer.Stop();
        _searchTimer.Start();
        _searchText = text;
    }

    public void Reset()
    {
        Results.Clear();
        ShowResults = false;
    }

    private async Task Search()
    {
        Debug.WriteLine("searching");
        Reset();
        if (string.IsNullOrWhiteSpace(_searchText))
        {
            return;
        }

        // TODO: handle cancelation
        List<SearchResult> results = await Utils.SearchAsync(_searchText);

        foreach (SearchResult result in results)
        {
            Results.Add(result);
        }

        ShowResults = true;
    }

    private async void SearchTimerTick(object? sender, object e)
    {
        ShowResults = false;
        _searchTimer.Stop();
        Search();
    }
}
