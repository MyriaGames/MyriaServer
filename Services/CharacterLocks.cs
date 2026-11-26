using System.Collections.Concurrent;

namespace Myria.Server.ClassicRealm.Services
{
    // One lock per character name, for every hub method that changes a live character's inventory or
    // gold. SignalR runs one call at a time per connection, but other connections reach the same
    // Character object too (a trade partner's ConfirmTrade, a shop buyer paying the seller), on other
    // threads. Without a lock a purchase could check the gold, await the database, and charge gold a
    // trade had moved away meanwhile; the result was ignored and the item delivered anyway (security
    // review 2026-11-26, A06 F10). Several names are always taken in a fixed (sorted) order, so two
    // calls locking the same pair can't deadlock. Never nest acquisitions in one call chain.
    public sealed class CharacterLocks
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

        public async Task<IDisposable> AcquireAsync(params string?[] names)
        {
            var ordered = names
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var held = new List<SemaphoreSlim>(ordered.Count);
            try
            {
                foreach (var name in ordered)
                {
                    var gate = _locks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
                    await gate.WaitAsync();
                    held.Add(gate);
                }
            }
            catch
            {
                foreach (var g in held) g.Release();
                throw;
            }
            return new Releaser(held);
        }

        private sealed class Releaser(List<SemaphoreSlim> held) : IDisposable
        {
            private int _done;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) != 0) return;
                for (int i = held.Count - 1; i >= 0; i--) held[i].Release();
            }
        }
    }
}
