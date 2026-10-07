using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace MateEngineQoL.Tests
{
    public class AsyncEnumerableExtensionsTests
    {
        static async IAsyncEnumerable<string> Chunks([EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var s in new[] { "Hel", "lo", "!" })
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return s;
            }
        }

        [Test]
        public void ToListAsync_CollectsAllChunksInOrder()
        {
            var result = Chunks().ToListAsync().GetAwaiter().GetResult();
            Assert.AreEqual(new[] { "Hel", "lo", "!" }, result);
        }

        [Test]
        public void ToListAsync_HonoursCancellation()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.That(() => Chunks().ToListAsync(cts.Token).GetAwaiter().GetResult(),
                Throws.InstanceOf<System.OperationCanceledException>());
        }
    }
}
