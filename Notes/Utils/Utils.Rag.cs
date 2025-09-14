using Notes.AI;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Notes;

internal partial class Utils
{
    public static async IAsyncEnumerable<string> Rag(string question, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (App.ChatClient == null)
        {
            yield return string.Empty;
        }
        else
        {
            List<SearchResult> searchResults = await SearchAsync(question, top: 2);

            string content = string.Join(" ", searchResults.Select(c => c.Content).ToList());

            string systemMessage = "You are a helpful assistant answering questions about this content";

            await foreach (string token in App.ChatClient.InferStreaming($"{systemMessage}: {content}", question, ct))
            {
                yield return token;
            }
        }
    }
}
