using Microsoft.AspNetCore.SignalR;

namespace Myria.Server.Realm.Hubs
{
    /// <summary>
    /// Scoped, additive fix for one half of the 2026-09-10 security/robustness audit's finding
    /// on <c>GameHub.cs</c>: outside a couple of methods, hub methods have no try/catch around
    /// their DB-writing service calls, so an exception thrown after an in-memory effect was
    /// already applied (e.g. <c>BuyFromNpcShop</c> granting the item and spending gold *before*
    /// its later <c>RookieService.ApplyPurchaseCommissionAsync</c> call) leaves the server's
    /// authoritative session state and what the client was told diverged - the player is told
    /// "it failed" for an action that server-side actually succeeded (and will persist on next
    /// save). Properly closing that gap needs each affected method individually restructured
    /// (validate/commit the DB write before applying any in-memory effect, or roll the effect
    /// back on failure) - too broad a change to make safely across dozens of methods without the
    /// live testing this pass didn't have time for, so deliberately deferred as a real follow-up
    /// (see TODO.md item 66).
    ///
    /// What this filter DOES do, safely and with zero behavior change for callers: every
    /// unhandled exception from any hub method invocation is logged here with full detail
    /// (method name, exception) before being rethrown completely unchanged - so this class of
    /// failure at least becomes visible in the server log instead of vanishing into whatever
    /// SignalR's own default handling does with it, without altering what the client
    /// experiences at all (the original exception still propagates exactly as it did before this
    /// filter existed).
    /// </summary>
    public class UnhandledExceptionLoggingFilter(ILogger<UnhandledExceptionLoggingFilter> logger) : IHubFilter
    {
        public async ValueTask<object?> InvokeMethodAsync(
            HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
        {
            try
            {
                return await next(invocationContext);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Unhandled exception in hub method {Method} (conn {Conn}) - any in-memory " +
                    "effect already applied before the throw may have diverged from what the " +
                    "caller was told (see TODO.md item 66)",
                    invocationContext.HubMethodName, invocationContext.Context.ConnectionId);
                throw;
            }
        }
    }
}
