namespace DdoDatApi.Models;

public class DatIdRange
{
    public uint Minimum { get; set; }

    public string MinInHex => $"0x{Minimum:X8}";

    public uint Maximum { get; set; }

    public string MaxInHex => $"0x{Maximum:X8}";

    /// <summary>
    /// Matches the Enum value ToString from <see cref="VoK.Sdk.Ddo.ObjectType"/>
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Comes off the DescriptionAttribute of the enum value
    /// </summary>
    public string Description { get; set; }

    public bool IsInRange(uint id)
    {
        return (Minimum <= id) && (Maximum >= id);
    }
}
