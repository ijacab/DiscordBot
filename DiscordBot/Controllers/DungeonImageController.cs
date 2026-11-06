using DiscordBot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DiscordBot.Controllers
{
    /// <summary>Serves the dungeon dashboard's latest picture. Discord fetches it through the embed's image URL.</summary>
    [Route("dungeon")]
    public class DungeonImageController : Controller
    {
        private readonly DungeonImageStore _store;

        public DungeonImageController(DungeonImageStore store)
        {
            _store = store;
        }

        [HttpGet("latest.png")]
        public IActionResult Latest()
        {
            byte[] png = _store.Latest();
            if (png == null)
                return NotFound();

            Response.Headers["Cache-Control"] = "no-store";
            return File(png, "image/png");
        }
    }
}
