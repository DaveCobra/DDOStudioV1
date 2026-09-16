using Microsoft.AspNetCore.Mvc;
using System.Linq;
using System.Net;
using VoK.Sdk.Enums;
using VoK.Sdk.Properties;

namespace DdoDatApi.Controllers;

/// <summary>
/// Provides set bonus information from the set bonus master table (0x7902F3F9).
/// </summary>
[ApiController]
[Route("[controller]")]
public class SetController : ControllerBase {

    private const uint SetBonusMasterId = 0x7902F3F9;
    private static IPropertyCollection _setBonusMaster;

    private static IPropertyCollection SetBonusMaster =>
        _setBonusMaster ??= DatSource.PropertyMaster.GetPropertyCollection(SetBonusMasterId);

    /// <summary>
    /// Gets a SetBonus_Entry by its SetBonus_ID from the set bonus master table.
    /// </summary>
    /// <param name="setId">The SetBonus_ID to look up. Hex with 0x prefix or plain integer.</param>
    /// <returns>The matching SetBonus_Entry property collection.</returns>
    [HttpGet("{setId}")]
    [ProducesResponseType<IPropertyCollection>((int)HttpStatusCode.OK)]
    [ProducesResponseType((int)HttpStatusCode.NotFound)]
    [ProducesResponseType((int)HttpStatusCode.BadRequest)]
    public IActionResult Get([FromRoute] string setId) {
        if (!setId.IsValid(out var id, out var error))
            return error;

        if (SetBonusMaster == null)
            return new StatusCodeResult((int)HttpStatusCode.FailedDependency);

        var table = SetBonusMaster.GetArrayProperty((uint)DdoProperty.SetBonus_Table);
        var setEntries = table.Properties.Where(p => p.PropertyId == (uint)DdoProperty.SetBonus_Entry).Cast<IArrayProperty>();
        foreach (var set in setEntries) {
            var thisSetId = set.GetUInt32PropertyValue((uint)DdoProperty.SetBonus_ID);
            if (thisSetId == id)
                return new OkObjectResult(set);
        }

        return NotFound();
    }
}
