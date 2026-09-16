using DdoDatApi.Caching;
using DdoDatApi.Models;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using VoK.Sdk.Ddo.Enums;
using VoK.Sdk.Properties;

namespace DdoDatApi.Controllers;

/// <summary>
/// Handles loading 0x70* / 0x78* / 0x79* objects from client_gamelogic.dat.
/// </summary>
[ApiController]
[Route("[controller]")]
public class DbPropertiesController : ControllerBase {
    /// <summary>
    /// Gets a DbProperties collection object. If the ID provided starts with 0x70******, it will be modified
    /// to be 0x79******.
    /// </summary>
    /// <param name="id">the Id to get. Hexadecimal is best and should have a "0x" prefix. Also supports integers (without "0x"), if need be. Values must be in the range
    /// of a UINT, from 0x78000000 to 0x79FFFFFF</param>
    /// <returns>The DBProperties object from client_gamelogic.dat</returns>
    [HttpGet("{id}")]
    [ProducesResponseType<IPropertyCollection>((int)HttpStatusCode.OK)]
    [ProducesResponseType((int)HttpStatusCode.NotFound)]
    [ProducesResponseType((int)HttpStatusCode.BadRequest)]
    public IActionResult Get(string id) {
        if (!id.IsValid(out var datId, out var error))
            return error;

        if (datId >= 0x70000000 && datId < 0x71000000)
            datId += 0x09000000;

        if (datId < 0x78000000 || datId > 0x79FFFFFF)
            return new BadRequestObjectResult("ID provided not within the valid range");

        var result = DatSource.PropertyMaster.GetPropertyCollection(datId);
        if (result == null) return new NotFoundResult();

        return new OkObjectResult(result);
    }

    /// <summary>
    /// Looks in the cache for objects of the matching name.
    /// </summary>
    /// <returns>List of IDs</returns>
    [HttpGet("IdsForName")]
    [ProducesResponseType<List<uint>>((int)HttpStatusCode.OK)]
    [ProducesResponseType((int)HttpStatusCode.FailedDependency, Description = "Happens when the index data has not been loaded")]
    public IActionResult GetByName([FromQuery] string name) {
        if (DatCache.Index.Names.Count < 1)
            return new StatusCodeResult((int)HttpStatusCode.FailedDependency);

        if (DatCache.Index.Names.ContainsKey(name))
            return new OkObjectResult(DatCache.Index.Names[name]);

        return new OkObjectResult(new List<uint>());
    }

    /// <summary>
    /// Gets the number of each type of Weenie in the Index
    /// </summary>
    [HttpGet("WeenieTypeCounts")]
    [ProducesResponseType<List<NamedCounter>>((int)HttpStatusCode.OK)]
    [ProducesResponseType((int)HttpStatusCode.FailedDependency, Description = "Happens when the index data has not been loaded")]
    public IActionResult GetWeenieTypeCounts() {
        if (DatCache.Index.Names.Count < 1)
            return new StatusCodeResult((int)HttpStatusCode.FailedDependency);

        var result = DatCache.Index.WeenieTypes.Select(kvp => new NamedCounter() {
            Id = kvp.Key,
            Name = Enum.GetName(typeof(WeenieType), kvp.Key) ?? "Undefined",
            Count = kvp.Value?.Count ?? 0
        })
            .OrderBy(nc => nc.Id)
            .ToList();

        return new OkObjectResult(result);
    }

    /// <summary>
    /// Gets all of the DbPropertyIds of the specified weenie type
    /// </summary>
    /// <param name="weenieType">Indexed weenie type (hex allowed). Mostly corresponds to <see cref="VoK.Sdk.Ddo.Enums.WeenieType"/>, but often has values outside the enumeration.</param>
    [HttpGet("ByWeenieType/{weenieType}")]
    [ProducesResponseType<List<NamedItem>>((int)HttpStatusCode.OK)]
    [ProducesResponseType((int)HttpStatusCode.BadRequest, Description = "usually when the provided weenie type could not be converted.")]
    [ProducesResponseType((int)HttpStatusCode.FailedDependency, Description = "Happens when the index data has not been loaded")]
    [ProducesResponseType((int)HttpStatusCode.NotFound, Description = "WeenieType is not in the index data.")]
    public IActionResult GetByWeenieType([FromRoute] string weenieType) {
        if (!weenieType.IsValid(out var wt, out var error))
            return error;

        if (DatCache.Index.Names.Count < 1)
            return new StatusCodeResult((int)HttpStatusCode.FailedDependency);

        if (!DatCache.Index?.WeenieTypes?.ContainsKey(wt) ?? true)
            return new NotFoundResult();

        var ids = DatCache.Index.WeenieTypes[wt] ?? new List<uint>();
        var result = ids
            .Select(id => new NamedItem() {
                Id = id,
                Name = DatCache.Index.NameLookup.ContainsKey(id) ? DatCache.Index.NameLookup[id] : $"0x{id:X8}"
            })
            .OrderBy(ni => ni.Id)
            .ToList();
        return new OkObjectResult(result);
    }

    /// <summary>
    /// Gets all Enhancement Trees from the index, with names.
    /// </summary>
    [HttpGet("EnhancementTrees")]
    [ProducesResponseType<List<NamedItem>>((int)HttpStatusCode.OK)]
    [ProducesResponseType((int)HttpStatusCode.FailedDependency, Description = "Happens when the index data has not been loaded")]
    public IActionResult GetEnhancementTrees() {
        if (DatCache.Index.EnhancementTrees.Count < 1)
            return new StatusCodeResult((int)HttpStatusCode.FailedDependency);

        var result = DatCache.Index.EnhancementTrees.Select(id => new NamedItem() {
            Id = id,
            Name = DatCache.Index.NameLookup.ContainsKey(id) ? DatCache.Index.NameLookup[id] : $"0x{id:X8}"
        })
            .OrderBy(ni => ni.Name)
            .ToList();

        return new OkObjectResult(result);
    }

    /// <summary>
    /// Gets all Treasure Table IDs from the index.
    /// </summary>
    [HttpGet("TreasureTables")]
    [ProducesResponseType<List<uint>>((int)HttpStatusCode.OK)]
    [ProducesResponseType((int)HttpStatusCode.FailedDependency, Description = "Happens when the index data has not been loaded")]
    public IActionResult GetTreasureTables() {
        if (DatCache.Index.TreasureTables.Count < 1)
            return new StatusCodeResult((int)HttpStatusCode.FailedDependency);

        return new OkObjectResult(DatCache.Index.TreasureTables);
    }

    /// <summary>
    /// Gets all Treasure Table IDs that contain the given Weenie ID.
    /// </summary>
    /// <param name="id">The Weenie ID to search for. Hex with "0x" prefix or plain integer.</param>
    [HttpGet("TreasureTablesFor/{id}")]
    [ProducesResponseType<List<uint>>((int)HttpStatusCode.OK)]
    [ProducesResponseType((int)HttpStatusCode.BadRequest)]
    [ProducesResponseType((int)HttpStatusCode.FailedDependency, Description = "Happens when the treasure map has not been loaded")]
    public IActionResult GetTreasureTablesFor(string id) {
        if (!id.IsValid(out var datId, out var error))
            return error;

        if (datId >= 0x70000000 && datId < 0x71000000)
            datId += 0x09000000;

        if (DatCache.TreasureMap.Count < 1)
            return new StatusCodeResult((int)HttpStatusCode.FailedDependency);

        var tables = DatCache.TreasureMap
            .Where(kvp => kvp.Value.Contains(datId))
            .Select(kvp => kvp.Key)
            .OrderBy(k => k)
            .ToList();

        return new OkObjectResult(tables);
    }

    /// <summary>
    /// Searches the cache for objects whose names match the provided words.
    /// Each word can be a regex pattern or a plain substring. The score represents
    /// the percentage of input words that matched.
    /// </summary>
    [HttpPost("Search")]
    [ProducesResponseType<List<SearchResult>>((int)HttpStatusCode.OK)]
    [ProducesResponseType((int)HttpStatusCode.BadRequest)]
    [ProducesResponseType((int)HttpStatusCode.FailedDependency, Description = "Happens when the index data has not been loaded")]
    public IActionResult Search([FromBody] SearchRequest request) {
        if (request?.Words == null || request.Words.Count < 1)
            return BadRequest("At least one search word is required.");

        if (DatCache.Index.NameLookup.Count < 1)
            return new StatusCodeResult((int)HttpStatusCode.FailedDependency);

        int numResults = request.NumResults ?? 20;

        // Pre-compile regexes for each word
        var regexes = new List<Regex>();
        foreach (var word in request.Words) {
            try {
                regexes.Add(new Regex(word, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)));
            }
            catch (ArgumentException) {
                return BadRequest($"Invalid regex pattern: {word}");
            }
        }

        var results = new List<SearchResult>();

        foreach (var kvp in DatCache.Index.NameLookup) {
            int matchCount = 0;
            for (int i = 0; i < request.Words.Count; i++) {
                if (regexes[i].IsMatch(kvp.Value) || kvp.Value.Contains(request.Words[i], StringComparison.OrdinalIgnoreCase))
                    matchCount++;
            }

            if (matchCount > 0) {
                results.Add(new SearchResult {
                    Id = kvp.Key,
                    Name = kvp.Value,
                    Score = (float)matchCount / request.Words.Count
                });
            }
        }

        var sorted = results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Id)
            .Take(numResults)
            .ToList();

        return new OkObjectResult(sorted);
    }
}
