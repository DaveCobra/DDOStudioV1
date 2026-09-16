namespace DdoDatApi.Models;

public class SearchResult
{
    /// <summary>
    /// DbProperties object ID.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Name of the DbProperties Object.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Percentage of input words that matched this object's name (0.0 to 1.0).
    /// </summary>
    public float Score { get; set; }
}
