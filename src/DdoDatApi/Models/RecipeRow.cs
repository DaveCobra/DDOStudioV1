using System.Collections.Generic;

namespace DdoDatApi.Models;

public record RecipeRow
{
    public string Name { get; init; }
    public List<string> Cost { get; init; }
    public List<string> Removed { get; init; }
    public List<string> Added { get; init; }
}
