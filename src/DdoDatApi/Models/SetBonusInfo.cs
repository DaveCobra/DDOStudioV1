using System.Collections.Generic;

namespace DdoDatApi.Models;

public record SetBonusInfo
{
    public uint SetId { get; init; }
    public string Name { get; init; }
    public List<string> Descriptions { get; init; }
}
