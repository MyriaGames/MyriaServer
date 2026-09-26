using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Myria.Server.Realm.Services;

namespace Myria.Server.Realm.Controllers
{
    // Registered globally: every authenticated realm REST call (characters, friends, guilds,
    // blocks, ...) is refused for an account under an operator ban. The realm trusts the auth
    // service's JWT with no call back, so a ban can't be expressed in the token - this is the
    // realm-side gate (the SignalR hub has its own check in OnConnectedAsync). Unauthenticated
    // endpoints (status, the secret-guarded admin API) carry no identity and pass straight through.
    public sealed class RejectBannedAccountFilter(BanService bans) : IAsyncActionFilter
    {
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var username = context.HttpContext.User.Identity is { IsAuthenticated: true } id ? id.Name : null;
            if (username is not null && await bans.GetActiveAccountBanAsync(username) is { } ban)
            {
                context.Result = new ObjectResult(new
                {
                    code = "AccountBanned",
                    message = "This account is banned from this realm" +
                              (Models.Bans.IsPermanent(ban.Until) ? "." : $" until {ban.Until:u}.") +
                              (string.IsNullOrEmpty(ban.Reason) ? "" : $" Reason: {ban.Reason}"),
                    bannedUntil = Models.Bans.IsPermanent(ban.Until) ? (DateTime?)null : ban.Until,
                    reason = ban.Reason
                })
                { StatusCode = StatusCodes.Status403Forbidden };
                return;
            }

            await next();
        }
    }
}
