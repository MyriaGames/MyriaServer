using Microsoft.AspNetCore.Mvc;
using Myria.Server.ClassicRealm.Services;

namespace Myria.Server.ClassicRealm.Controllers
{
    [ApiController]
    [Route("api/status")]
    public class StatusController(CharacterPresenceService presence) : ControllerBase
    {
        [HttpGet]
        public IActionResult Get() => Ok(new { playerCount = presence.GetTotalCount() });
    }
}
