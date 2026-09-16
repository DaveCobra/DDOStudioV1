using DdoDatApi.Models;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Net;

namespace DdoDatApi.Controllers;

[ApiController]
[Route("[controller]")]
public class RawDatController : ControllerBase
{
    [HttpGet("{dat}/{id}")]
    [ProducesResponseType<FileContentResult>((int)HttpStatusCode.OK)]
    [ProducesResponseType((int)HttpStatusCode.BadRequest)]
    [ProducesResponseType((int)HttpStatusCode.NotFound)]
    public IActionResult Get(DatFileType dat, string id)
    {
        if (!id.IsValid(out var datId, out var error))
            return error;

        var contents = (dat switch
        {
            DatFileType.Anim => DatSource.AnimDat,
            DatFileType.Cell1 => DatSource.Cell1,
            DatFileType.Cell2 => DatSource.Cell2,
            DatFileType.Cell3 => DatSource.Cell3,
            DatFileType.Cell4 => DatSource.Cell4,
            DatFileType.GameLogic => DatSource.GameLogicDat,
            DatFileType.General => DatSource.GeneralDat,
            DatFileType.Highres => DatSource.Highres,
            DatFileType.LocalEnglish => DatSource.LocalEnglishDat,
            DatFileType.Map1 => DatSource.Map1,
            DatFileType.Map2 => DatSource.Map2,
            DatFileType.Map3 => DatSource.Map3,
            DatFileType.Map4 => DatSource.Map4,
            DatFileType.Mesh => DatSource.Mesh,
            DatFileType.Sound => DatSource.SoundDat,
            DatFileType.Surface => DatSource.SurfaceDat,
            _ => null

        })?.GetFileContents(datId);

        if (contents == null) return new NotFoundResult();
        return File(contents, "application/octet-stream");
    }

    /// <summary>
    /// Fetches the ID ranges of common DAT object types. Generally, a given object type only has 1 home. Exceptions are Map/Cell being split by region, and graphics/images spanning surface, highres, and local_english.
    /// </summary>
    [HttpGet("IdRanges")]
    [ProducesResponseType<List<DatIdRange>>((int)HttpStatusCode.OK)]
    public IActionResult IdRanges()
    {
        return new OkObjectResult(DatSource.IdRanges);
    }
}
