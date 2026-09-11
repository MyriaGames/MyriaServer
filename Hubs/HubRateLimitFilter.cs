using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Myria.Server.Realm.Hubs
{
    /// <summary>
    /// Closes the remaining half of TODO.md item 62: <c>Program.cs</c>'s "authenticated" REST
    /// rate-limit policy only ever covered the HTTP endpoint pipeline - ASP.NET Core's built-in
    /// <c>AddRateLimiter</c>/<c>UseRateLimiter</c> has no notion of individual SignalR hub method
    /// invocations over an already-established connection, so every <c>GameHub</c> method (chat,
    /// combat, trade, shop, crafting, ...) remained completely unthrottled. This is a hand-rolled
    /// per-connection fixed-window counter instead, applied globally alongside
    /// <see cref="UnhandledExceptionLoggingFilter"/> via <c>services.AddSignalR(options =>
    /// options.AddFilter&lt;T&gt;())</c> in <c>Program.cs</c> - NOT the seemingly-equivalent
    /// <c>services.AddSingleton&lt;IHubFilter, T&gt;()</c> DI-only pattern some examples show,
    /// which compiles fine but is never actually invoked by SignalR's dispatcher (confirmed
    /// empirically while building this filter - see TODO.md item 62).
    ///
    /// Partitioned by connection id rather than username: <c>[Authorize]</c> on <see cref="GameHub"/>
    /// already ties every connection to one authenticated user, and connection id is what's
    /// available directly on <see cref="HubInvocationContext"/> without an extra claims lookup on
    /// every single call.
    ///
    /// Limit chosen deliberately higher than the REST "authenticated" policy's 60/min: unlike a
    /// REST call (one full character list/detail fetch), a hub invocation is typically a single
    /// small user-triggered action (one attack, one chat line, one craft) - a fast combat session
    /// or a quick flurry of clicks can legitimately fire several of these a second. 100 requests
    /// per 10-second window (≈600/min sustained, bursty) comfortably covers real play while still
    /// meaningfully bounding a spam script that would otherwise have no ceiling at all.
    /// </summary>
    public class HubRateLimitFilter(ILogger<HubRateLimitFilter> logger) : IHubFilter
    {
        private const int PermitLimit = 100;
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

        private sealed class Bucket
        {
            public int Count;
            public DateTime WindowStart;
        }

        private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

        public async ValueTask<object?> InvokeMethodAsync(
            HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
        {
            var connId = invocationContext.Context.ConnectionId;
            var bucket = _buckets.GetOrAdd(connId, _ => new Bucket { WindowStart = DateTime.UtcNow });

            bool limited;
            lock (bucket)
            {
                var now = DateTime.UtcNow;
                if (now - bucket.WindowStart >= Window)
                {
                    bucket.WindowStart = now;
                    bucket.Count = 0;
                }
                bucket.Count++;
                limited = bucket.Count > PermitLimit;
            }

            if (limited)
            {
                logger.LogWarning(
                    "Hub method {Method} rate-limited for connection {Conn} ({Limit}/{WindowSec}s exceeded)",
                    invocationContext.HubMethodName, connId, PermitLimit, Window.TotalSeconds);
                // HubException's message is the one exception type SignalR forwards verbatim to
                // the caller instead of masking it as a generic error - appropriate here since
                // "you're rate limited" is meant to be visible to the client, unlike an internal
                // fault the UnhandledExceptionLoggingFilter above logs but doesn't want to leak.
                throw new HubException("rate_limited");
            }

            return await next(invocationContext);
        }

        // Buckets are cheap (one small object per connection) and self-resetting per window, but
        // without this a server that ran for a very long time with many short-lived connections
        // (reconnects, relogins) would slowly accumulate dead entries forever - removed the moment
        // the connection that owns it actually disconnects instead.
        public Task OnDisconnectedAsync(
            HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
        {
            _buckets.TryRemove(context.Context.ConnectionId, out _);
            return next(context, exception);
        }
    }
}
