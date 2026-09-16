using DdoDatApi.Caching;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Net;

namespace DdoDatApi.Controllers;

[ApiController]
[Route("[controller]")]
public class RecipeController : ControllerBase {

    /// <summary>
    /// Gets all recipes that use the given item as an ingredient.
    /// </summary>
    /// <param name="id">The item Weenie ID. Hex with 0x prefix or plain integer.</param>
    [HttpGet("ForItem/{id}")]
    [ProducesResponseType<List<RecipeData>>((int)HttpStatusCode.OK)]
    [ProducesResponseType((int)HttpStatusCode.BadRequest)]
    [ProducesResponseType((int)HttpStatusCode.FailedDependency)]
    public IActionResult ForItem([FromRoute] string id) {
        if (!id.IsValid(out var itemId, out var error))
            return error;

        if (DatCache.RecipeMap.Count < 1)
            return new StatusCodeResult((int)HttpStatusCode.FailedDependency);

        if (!DatCache.RecipeMap.TryGetValue(itemId, out var recipes))
            recipes = new List<RecipeData>();

        return new OkObjectResult(recipes);
    }
}
