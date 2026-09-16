using System.Collections.Generic;
using VoK.Sdk.Properties;

namespace DdoDatApi.Caching;

public class DatCache
{
    // ── Weapon proficiency feat IDs ───────────────────────────────────────
    public const uint SimpleProficiencyId  = 2030043450; // 0x7900013A
    public const uint MartialProficiencyId = 2030043362; // 0x790000E2
    public const uint ExoticProficiencyId  = 2030043465; // 0x790000E9

    /// <summary>Cached DbProperties for Simple Weapon Proficiency.</summary>
    public static IPropertyCollection SimpleProficiency { get; set; }

    /// <summary>Cached DbProperties for Martial Weapon Proficiency.</summary>
    public static IPropertyCollection MartialProficiency { get; set; }

    /// <summary>Cached DbProperties for Exotic Weapon Proficiency.</summary>
    public static IPropertyCollection ExoticProficiency { get; set; }

    /// <summary>Maps WeaponType enum uint value to its required base proficiency display name.</summary>
    public static Dictionary<uint, string> WeaponProficiencyNames { get; set; } = new();

    public static IndexData Index { get; set; } = new();

    /// <summary>
    /// Maps treasure table IDs to the item Weenie IDs they can produce.
    /// </summary>
    public static Dictionary<uint, List<uint>> TreasureMap { get; set; } = new();

    /// <summary>
    /// Maps item Weenie IDs to all recipes that use that item as an ingredient.
    /// </summary>
    public static Dictionary<uint, List<RecipeData>> RecipeMap { get; set; } = new();
}
