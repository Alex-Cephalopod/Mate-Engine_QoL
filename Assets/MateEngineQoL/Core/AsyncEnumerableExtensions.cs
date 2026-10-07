using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MateEngineQoL
{
    /// <summary>
    /// Helpers for the IAsyncEnumerable streams that providers return.
    /// </summary>
    public static class AsyncEnumerableExtensions
    {
        public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source, CancellationToken ct = default)
        {
            var list = new List<T>();
            await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
                list.Add(item);
            return list;
        }
    }
}
