namespace DdoDatApi.Models;

public record ClickieInfo
{
    public string SpellName { get; init; }
    public string SpellIconUrl { get; init; }
    public string Target { get; init; }
    public string School { get; init; }
    public bool? CanBeResisted { get; init; }
    public string SpellDescription { get; init; }
    public int? CasterLevel { get; init; }
    public int? MaxCharges { get; init; }
    public int? DailyRecharge { get; init; }
}
