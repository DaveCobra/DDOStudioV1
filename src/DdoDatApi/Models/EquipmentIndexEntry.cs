using System.Collections.Generic;

namespace DdoDatApi.Models;

/// <summary>
/// Compact equipment metadata persisted in the search index.  It is intentionally
/// independent of render resolution so Character Studio can classify/filter items
/// before an expensive model-chain walk is needed.
/// </summary>
public sealed class EquipmentIndexEntry
{
    public uint Id { get; set; }
    public string Name { get; set; } = "";
    public uint WeenieType { get; set; }
    public string WeenieTypeName { get; set; } = "Unknown";
    public string EquipmentKind { get; set; } = "Equippable Item";
    public List<string> CompatibleSlots { get; set; } = new();
    public List<string> CompatibleSlotsRaw { get; set; } = new();
    public List<string> PrecludedSlots { get; set; } = new();
    public List<string> PrecludedSlotsRaw { get; set; } = new();
    public string PrimarySlot { get; set; } = "";
    public string WeaponType { get; set; } = "";
    public string ArmorType { get; set; } = "";
    public bool CanMainHand { get; set; }
    public bool CanOffHand { get; set; }
    public bool IsTwoHanded { get; set; }
}
