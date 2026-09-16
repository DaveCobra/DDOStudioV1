using Microsoft.AspNetCore.Mvc;
using System.Net;
using VoK.Sdk.Properties;

namespace DdoDatApi.Controllers;

[ApiController]
[Route("[controller]")]
public class StringsController : Controller {

    /// <summary>
    /// Looks up the string table entry specified in client_localEnglish.dat.
    /// </summary>
    /// <param name="table">string table data id. Should be in the range 0x25000000-0x26FFFFFF. Hex allowed, should prefix with 0x. Otherwise parsed as an integer.</param>
    /// <param name="key">string key data id. Hex allowed, should prefix with 0x. Otherwise parsed as an integer.</param>
    /// <returns>IStringEntry object fetched from the dats</returns>
    [HttpGet("{table}/{key}")]
    [ProducesResponseType<IStringEntry>(200)]
    [ProducesResponseType((int)HttpStatusCode.BadRequest, Description = "parse error on IDs")]
    [ProducesResponseType((int)HttpStatusCode.NotFound, Description = "could not find values in dat file")]
    public IActionResult Get([FromRoute] string table, [FromRoute] string key) {
        if (!key.IsValid(out var keyId, out var error))
            return error;
        if (!table.IsValid(out var tableId, out error))
            return error;


        var info = DatSource.PropertyMaster.GetStringEntry(keyId, tableId);
        if (info == null)
            return NotFound();

        return new OkObjectResult(info);
    }
}
