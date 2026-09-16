namespace DdoDatApi.Models;

public record TableRow
{
    public string Field { get; init; }
    public string Value { get; init; }
    public bool IsHtml { get; init; }
}
