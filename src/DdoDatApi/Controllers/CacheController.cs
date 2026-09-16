using DdoDatApi.Caching;
using DdoDatApi.Models;
using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Net;

namespace DdoDatApi.Controllers;

[ApiController]
[Route("[controller]")]
public class CacheController(ICacheBuilderService cacheBuilderService) : ControllerBase
{
    /// <summary>
    /// Kicks off a long-running process to rebuild the cache. Should only really be done after patching
    /// the client or after rebuilding the WebApi.
    /// </summary>
    [HttpPost("Rebuild")]
    [ProducesResponseType((int)HttpStatusCode.Accepted, Description = "A cache rebuild has been started in the background.")]
    [ProducesResponseType((int)HttpStatusCode.AlreadyReported, Description = "A cache rebuild is already in progress.")]
    public IActionResult RebuildCache()
    {
        if (cacheBuilderService.IsRunning)
            return new StatusCodeResult((int)HttpStatusCode.AlreadyReported);

        cacheBuilderService.Export();

        return new StatusCodeResult((int)HttpStatusCode.Accepted);
    }

    /// <summary>
    /// Gets basic timestamp / version / size of the Index. Useful for determining if/when the Index needs to be rebuilt.
    /// </summary>
    [HttpGet("Metadata")]
    [ProducesResponseType<IndexMetadata>((int)HttpStatusCode.OK)]
    [ProducesResponseType((int)HttpStatusCode.FailedDependency, Description = "Happens when the index data has not been loaded")]
    public IActionResult IndexMetadata()
    {
        if (DatCache.Index.CompiledOnUtc == null)
            return new StatusCodeResult((int)HttpStatusCode.FailedDependency);

        var fileInfo = new FileInfo(IndexLoader.CachePath);

        var md = new IndexMetadata()
        {
            ClientVersion = DatCache.Index.ClientVersion,
            CompiledOnUtc = DatCache.Index.CompiledOnUtc,
            CompilationDuration = DatCache.Index.LastIndexDuration,
            SizeInBytes = fileInfo.Length
        };
        return new OkObjectResult(md);
    }

    /// <summary>
    /// Allows the consumer to download the entire Index. As of time of writing, this index was ~22MB, and is expected to get larger.
    /// </summary>
    [HttpGet("Download")]
    [ProducesResponseType<FileContentResult>((int)HttpStatusCode.OK)]
    [ProducesResponseType((int)HttpStatusCode.FailedDependency, Description = "Happens when the index data has not been loaded")]
    public IActionResult DownloadIndex()
    {
        if (DatCache.Index.CompiledOnUtc == null)
            return new StatusCodeResult((int)HttpStatusCode.FailedDependency);

        var stream = new FileStream(IndexLoader.CachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(stream, "application/json", "indexcache.json");
    }
}
