using Microsoft.AspNetCore.Mvc;
using System;
using System.Net;

namespace DdoDatApi.Controllers;

[ApiController]
[Route("[controller]")]
public class SoundController : ControllerBase
{
    /// <summary>
    /// Sounds have a 0x2A****** file in client_general that gives more information about the actual sound bit.
    /// </summary>
    /// <param name="id">Id of the sound to fetch. This should match the IdRange for "SoundInfo", which is 0x2A000000 - 0x2AFFFFFF.</param>
    [HttpGet("{id}")]
    [ProducesResponseType<FileContentResult>((int)HttpStatusCode.OK)]
    [ProducesResponseType((int)HttpStatusCode.NotFound)]
    [ProducesResponseType((int)HttpStatusCode.BadRequest, Description = "Malformed Id, or when the SDK throws an Argument exception, which usually means a bad ID was provided.")]
    public IActionResult Index(string id)
    {
        if (!id.IsValid(out var datId, out var error))
            return error;

        try
        {
            var bytes = DatSource.SoundExporter.GetSoundData(datId, out string ext);
            if (bytes == null || bytes.Length < 1) return new NotFoundResult();
            // ext is (so far) only ever ".ogg" or ".wav"
            var contentType = $"audio/{ext?.TrimStart('.') ?? "octet-stream"}";
            return new FileContentResult(bytes, contentType);
        }
        catch (ArgumentException)
        {
            return new StatusCodeResult((int)HttpStatusCode.BadRequest);
        }
    }
}
