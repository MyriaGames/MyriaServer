using Microsoft.AspNetCore.SignalR;

namespace Myria.Server.Realm.Hubs
{
    /// <summary>
    /// Originally added as a scoped, additive safety net for the 2026-09-10 security/robustness
    /// audit's finding on <c>GameHub.cs</c> (see TODO.md item 66): every unhandled exception from
    /// any hub method invocation is logged here with full detail (method name, connection id)
    /// before being rethrown completely unchanged - zero behavior change for callers, just makes
    /// the failure visible in the server log instead of vanishing into whatever SignalR's default
    /// handling does with it. Item 66's actual per-method fix (guarding the specific call sites
    /// where an in-memory effect was applied before an unguarded DB write) has since been done
    /// directly in <c>GameHub.cs</c> (the 8 <c>RookieService</c> bonus call sites) - this filter
    /// stays in place regardless as general-purpose visibility for any *other* unhandled hub
    /// exception, not just that one bug class.
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
