using System.Collections.Generic;

namespace DdoDatApi.Models;

public record RecipeGroup
{
    public string DeviceName { get; init; }
    public List<RecipeRow> Recipes { get; init; }
}
