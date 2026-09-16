namespace DdoDatApi.Models;

public class NamedCounter
{
    public uint Id { get; set; }

    public string IdHex => $"0x{Id:X8}";

    public string Name { get; set; }

    public int Count { get; set; }
}
