using Microsoft.AspNetCore.Mvc;
using Myria.Server.Realm.Services;

namespace Myria.Server.Realm.Controllers
{
    [ApiController]
    [Route("api/status")]
    public class StatusController(CharacterPresenceService presence) : ControllerBase
    {
        [HttpGet]
        public IActionResult Get() => Ok(new { playerCount = presence.GetTotalCount() });
    }
}
