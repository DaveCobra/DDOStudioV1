using System.Collections.Generic;

namespace DdoDatApi.Models;

public class SearchRequest
{
    /// <summary>
    /// Words to search for. Each word can be a regex pattern or a plain substring.
    /// At least one word is required.
    /// </summary>
    public List<string> Words { get; set; }

    /// <summary>
    /// Maximum number of results to return. Defaults to 20.
    /// </summary>
    public int? NumResults { get; set; }
}
