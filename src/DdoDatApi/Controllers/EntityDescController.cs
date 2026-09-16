using Microsoft.AspNetCore.Mvc;
using VoK.Sdk.Common;
using VoK.Sdk.Properties;

namespace DdoDatApi.Controllers;

/// <summary>
/// Exports EntityDesc objects from client_gamelogic.dat. EntityDesc is a description of the physics of an entity. Only
/// DbProperties objects that have the capability of being rendered will have a corresponding EntityDesc record.
/// </summary>
[ApiController]
[Route("[controller]")]
public class EntityDescController : ControllerBase
{
    /// <summary>
    /// Gets an EntityDesc object.
    /// </summary>
    /// <param name="id">the Id to get. Hexadecimal is best and should have a "0x" prefix. Also supports integers (without "0x"), if need be. Values must be in the range
    /// of a UINT, from 0x47000000 to 0x47FFFFFF</param>
    /// <returns>The DBProperties object from client_gamelogic.dat</returns>
    [HttpGet("{id}")]
    [ProducesResponseType<EntityDesc>(200)]
    [ProducesResponseType(400)]
    public IActionResult Get(string id)
    {
        if (!id.IsValid(out var datId, out var error))
            return error;

        var ed = EntityDesc.Load(DatSource.GameLogicDat, datId, DatSource.PropertyMaster);
        if (ed == null) return new NotFoundResult();

        return new OkObjectResult(ed);
    }
}
