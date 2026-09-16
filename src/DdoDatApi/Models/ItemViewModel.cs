using System.Collections.Generic;

namespace DdoDatApi.Models;

public class ItemViewModel
{
    public string Title { get; set; }
    public string Theme { get; set; }
    public uint WeenieType { get; set; }
    public string WeenieTypeName { get; set; }

    // Text-side table
    public List<TableRow> TableRows { get; set; } = new();
    public List<EffectInfo> Effects { get; set; } = new();
    public List<string> AugmentSlots { get; set; } = new();
    public SetBonusInfo SetBonus1 { get; set; }
    public SetBonusInfo SetBonus2 { get; set; }
    public List<RecipeGroup> Recipes { get; set; } = new();

    // Mock-side (DDO tooltip panel)
    public string ItemIconUrl { get; set; }
    public string ItemName { get; set; }
    public string TypeLine { get; set; }
    public string EquipsToLine { get; set; }
    public string EquipmentKind { get; set; }
    public List<string> CompatibleSlots { get; set; } = new();
    public List<string> CompatibleSlotsRaw { get; set; } = new();
    public List<string> PrecludedSlots { get; set; } = new();
    public List<string> PrecludedSlotsRaw { get; set; } = new();
    public string WeaponTypeName { get; set; }
    public string ArmorTypeName { get; set; }
    public bool CanMainHand { get; set; }
    public bool CanOffHand { get; set; }
    public string ProficiencyName { get; set; }
    public bool AcceptsSentience { get; set; }
    public int? MinLevel { get; set; }
    public string Binding { get; set; }
    public bool IsExclusive { get; set; }
    public List<string> RaceExcluded { get; set; } = new();
    public ClickieInfo Clickie { get; set; }
    public string Description { get; set; }
    public string DamageLine { get; set; }
    public string CriticalRollLine { get; set; }
    public string AttackMod { get; set; }
    public string DamageMod { get; set; }
    public float BaseDamageRating { get; set; }
    public bool IsTwoHanded { get; set; }
    public int? ShieldBonus { get; set; }
    public int? DamageReduction { get; set; }
    public int? AttackPenalty { get; set; }
    public int? ArmorBonus { get; set; }
    public int? MaxDexBonus { get; set; }
    public int? ArmorCheckPenalty { get; set; }
    public int? SpellFailureChance { get; set; }
    public int? Durability { get; set; }
    public string Material { get; set; }
    public int? DurabilityHardness { get; set; }
    public string Weight { get; set; }
    public int? BaseValue { get; set; }
    public string BoundLine { get; set; }

    public bool IsWeapon => WeenieType == WeenieTypes.Weapon;
    public bool IsShield => WeenieType == WeenieTypes.Shield;
    public bool IsArmor => WeenieType == WeenieTypes.Armor;
    public bool IsAugment => WeenieType == WeenieTypes.Augment;
    public bool IsEquipSlot => WeenieType == WeenieTypes.Jewelry || WeenieType == WeenieTypes.Clothing;
    public bool IsPlatinum => Theme == "platinum";
}
