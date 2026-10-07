using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MateEngineQoL.AI.Http
{
    /// <summary>
    /// Minimal Server-Sent Events reader: yields the data of each event (multi-line data joined with '\n').
    /// Lines longer than <c>maxLineChars</c> abort the stream, so a misbehaving server can't exhaust memory.
    /// </summary>
    public static class SseReader
    {
        public static async IAsyncEnumerable<string> ReadDataAsync(Stream stream, int maxLineChars,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            // Stream reads in this profile don't all take a token, so cancellation closes the stream instead.
            // The reader is created first: registering on an already-cancelled token disposes immediately.
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            using (ct.Register(stream.Dispose))
            {
                var data = new StringBuilder();
                bool hasData = false;
                var line = new StringBuilder();
                var buffer = new char[4096];

                while (true)
                {
                    int read;
                    try
                    {
                        read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    }
                    catch (System.ObjectDisposedException) when (ct.IsCancellationRequested)
                    {
                        ct.ThrowIfCancellationRequested();
                        throw;
                    }
                    catch (IOException) when (ct.IsCancellationRequested)
                    {
                        ct.ThrowIfCancellationRequested();
                        throw;
                    }

                    if (read == 0) break;

                    for (int i = 0; i < read; i++)
                    {
                        char c = buffer[i];
                        if (c != '\n')
                        {
                            if (c != '\r') line.Append(c);
                            if (line.Length > maxLineChars)
                                throw new ProviderException("The server sent an oversized stream line; stopped reading.");
                            continue;
                        }

                        string text = line.ToString();
                        line.Clear();

                        if (text.Length == 0)
                        {
                            if (hasData)
                            {
                                yield return data.ToString();
                                data.Clear();
                                hasData = false;
                            }
                            continue;
                        }

                        if (text.StartsWith(":")) continue; // comment / keep-alive
                        if (!text.StartsWith("data:")) continue; // event:, id:, retry: are not needed here

                        string value = text.Substring(5);
                        if (value.StartsWith(" ")) value = value.Substring(1);
                        if (hasData) data.Append('\n');
                        data.Append(value);
                        hasData = true;
                    }
                }

                if (line.Length > 0 && line.ToString().StartsWith("data:"))
                {
                    string value = line.ToString().Substring(5).TrimStart(' ');
                    if (hasData) data.Append('\n');
                    data.Append(value);
                    hasData = true;
                }
                if (hasData) yield return data.ToString();
            }
        }
    }
}
