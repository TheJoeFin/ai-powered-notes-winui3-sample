using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Notes.AI;
using Notes.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Notes.Controls;

public sealed partial class Phi3View : UserControl
{
    private CancellationTokenSource _cts;
    public ObservableCollection<SearchResult> Sources { get; } = [];

    public Phi3View()
    {
        this.InitializeComponent();
    }

    public async Task ShowAndSummarize(string text)
    {
        if (App.ChatClient == null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        userPromptText.Text = $"Summarize \n \"{text[..Math.Min(1000, text.Length)]}...\"";
        aIResponseText.Text = "...";
        userQuestionRoot.Visibility = Visibility.Visible;
        aIAnswerRoot.Visibility = Visibility.Visible;
        sourcesText.Visibility = Visibility.Collapsed;
        Sources.Clear();

        this.Visibility = Visibility.Visible;

        await Task.Run(async () =>
        {
            bool firstPartial = true;

            await foreach (string partialResult in App.ChatClient.SummarizeTextAsync(text, token))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (firstPartial)
                    {
                        aIResponseText.Text = string.Empty;
                        firstPartial = false;
                    }

                    aIResponseText.Text += partialResult;
                });

            }
        });
    }

    public async Task ShowForRag()
    {
        this.textBox.Text = string.Empty;
        userQuestionRoot.Visibility = Visibility.Collapsed;
        aIAnswerRoot.Visibility = Visibility.Collapsed;
        textBox.IsEnabled = true;
        textBoxRoot.Visibility = Visibility.Visible;
        Sources.Clear();
        this.Visibility = Visibility.Visible;
    }

    internal async Task FixAndCleanUp(string text)
    {
        if (App.ChatClient == null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        userPromptText.Text = $"Fix this text \n \"{text[..Math.Min(1000, text.Length)]}...\"";
        aIResponseText.Text = "...";
        userQuestionRoot.Visibility = Visibility.Visible;
        aIAnswerRoot.Visibility = Visibility.Visible;
        Sources.Clear();

        this.Visibility = Visibility.Visible;

        await Task.Run(async () =>
        {
            bool firstPartial = true;

            await foreach (string partialResult in App.ChatClient.FixAndCleanUpTextAsync(text, token))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (firstPartial)
                    {
                        aIResponseText.Text = string.Empty;
                        firstPartial = false;
                    }

                    aIResponseText.Text += partialResult;
                });

            }
        });
    }

    private void Button_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    public void Hide()
    {
        this.Visibility = Visibility.Collapsed;
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }

        textBoxRoot.Visibility = Visibility.Collapsed;
    }

    private void StopResponding_Clicked(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
    }

    private void BackgroundTapped(object sender, TappedRoutedEventArgs e)
    {
        // hide the search view only when the backround was tapped but not any of the content inside
        if (e.OriginalSource == Root)
            this.Hide();
    }

    private async void TextBox_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        TextBox? textBox = sender as TextBox;
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (textBox.Text.Length > 0)
            {
                HandleRagQuestion(textBox.Text);

            }
        }
    }

    private async Task HandleRagQuestion(string question)
    {
        if (App.ChatClient == null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        textBox.Text = string.Empty;
        textBox.IsEnabled = false;

        userPromptText.Text = question;
        userQuestionRoot.Visibility = Visibility.Visible;
        aIAnswerRoot.Visibility = Visibility.Visible;
        stopRespondingButton.Visibility = Visibility.Collapsed;
        sourcesText.Visibility = Visibility.Collapsed;
        aIResponseText.Text = "...";
        Sources.Clear();

        List<SearchResult> foundSources = null;

        await Task.Run(async () =>
        {
            bool firstPartial = true;

            foundSources = await Utils.SearchAsync(question, top: 1);

            string information = string.Join(" ", foundSources.Select(chunk => chunk.Content).ToList());
            string response = string.Empty;

            await foreach (string partialResult in App.ChatClient.AskForContentAsync(information, question, _cts.Token))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                response += partialResult;

                if (response.Contains("\n\n"))
                {
                    _cts.Cancel();
                    break;
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (firstPartial)
                    {
                        stopRespondingButton.Visibility = Visibility.Visible;
                        aIResponseText.Text = string.Empty;
                        firstPartial = false;
                    }

                    aIResponseText.Text = response.Trim();
                });
            }
        });

        if (foundSources != null && foundSources.Count > 0)
        {
            sourcesText.Visibility = Visibility.Visible;
            foreach (SearchResult result in foundSources)
            {
                if (Sources.Where(r => r.SourceId == result.SourceId).Count() == 0)
                {
                    Sources.Add(result);
                }
            }
        }

        stopRespondingButton.Visibility = Visibility.Collapsed;
        textBox.IsEnabled = true;
    }

    private async void ListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        AppDataContext context = await AppDataContext.GetCurrentAsync();

        SearchResult? item = e.ClickedItem as SearchResult;
        if (item.ContentType == ContentType.Note)
        {
            MainWindow.Instance.SelectNoteById(item.SourceId);
        }
        else
        {
            Attachment? attachment = context.Attachments.Where(a => a.Id == item.SourceId).FirstOrDefault();
            if (attachment != null)
            {
                Note? note = context.Notes.Where(n => n.Id == attachment.NoteId).FirstOrDefault();
                MainWindow.Instance.SelectNoteById(note.Id, attachment.Id, item.MostRelevantSentence);
            }
        }

        this.Hide();
    }

}
