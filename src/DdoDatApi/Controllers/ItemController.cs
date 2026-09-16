using DdoDatApi.Caching;
using DdoDatApi.Models;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using VoK.Sdk.Ddo.Enums;
using VoK.Sdk.Enums;
using VoK.Sdk.Properties;

namespace DdoDatApi.Controllers;

/// <summary>
/// Returns HTML pages displaying item data from DbProperties.
/// </summary>
[Route("[controller]")]
public class ItemController : Controller
{
    private const uint SetBonusMasterId = 0x7902F3F9;

    /// <summary>
    /// Returns an HTML page showing the item's stats on the left and a DDO tooltip mock on the right.
    /// </summary>
    /// <param name="id">DbProperties ID (hex with 0x prefix, or decimal). 0x70* IDs are converted to 0x79*.</param>
    [HttpGet("id/{id}")]
    public IActionResult GetById(string id)
    {
        if (!id.IsValid(out var datId, out var error))
            return error;

        if (datId >= 0x70000000 && datId < 0x71000000)
            datId += 0x09000000;

        if (datId < 0x78000000 || datId > 0x79FFFFFF)
            return new BadRequestObjectResult("ID not in valid range");

        var props = DatSource.PropertyMaster.GetPropertyCollection(datId);
        if (props == null) return NotFound();

        return View(BuildViewModel(datId, props));
    }

    /// <summary>
    /// Returns the same decoded item metadata as the HTML item page, but as JSON for Asset Studio.
    /// </summary>
    [HttpGet("data/{id}")]
    [Produces("application/json")]
    public IActionResult GetData(string id)
    {
        if (!id.IsValid(out var datId, out var error))
            return error;

        if (datId >= 0x70000000 && datId < 0x71000000)
            datId += 0x09000000;

        if (datId < 0x78000000 || datId > 0x79FFFFFF)
            return new BadRequestObjectResult("ID not in valid range");

        var props = DatSource.PropertyMaster.GetPropertyCollection(datId);
        if (props == null) return NotFound();

        return new OkObjectResult(BuildViewModel(datId, props));
    }

    // ── View model builder ────────────────────────────────────────────────

    private static ItemViewModel BuildViewModel(uint itemId, IPropertyCollection props)
    {
        var weenieType = props.GetWeenieType();
        var theme = GetTheme(props);
        var resolved = ResolveEffects(props);
        var augSlots = GetAugmentSlots(props, weenieType);
        augSlots.AddRange(resolved.AugmentSlots);
        var setBonus1 = GetSetBonusInfo(props, (uint)DdoProperty.Item_SetBonus_1);
        var setBonus2 = GetSetBonusInfo(props, (uint)DdoProperty.Item_SetBonus_2);

        // Prefer Usage_MinLevel from raw props; fall back to Treasure_BaseLevel set by effects
        var minLevel = props.GetInt32PropertyValue((uint)DdoProperty.Usage_MinLevel);
        if (minLevel == null || minLevel == 0) {
            var baseLevel = resolved.EffectCtx.GetValue((uint)DdoProperty.Treasure_BaseLevel);
            if (baseLevel is > 0) minLevel = (int)baseLevel.Value;
        }
        if (minLevel == null || minLevel == 0)
            minLevel = resolved.MinLevel;

        var enhBonus = resolved.EffectCtx.GetValue((uint)DdoProperty.Combat_EnhancementBonus) ?? 0f;

        // Material is computed once; used by both the table row and the durability block in the view.
        string material = weenieType != WeenieTypes.Augment ? GetMaterialName(props) : null;

        int? durability = null;
        int? durabilityHardness = null;
        if (weenieType != WeenieTypes.Augment) {
            var durBase = props.GetInt32PropertyValue((uint)DdoProperty.MaxDurability_Base);
            if (durBase != null) {
                var durBonus = resolved.EffectCtx.GetValue((uint)DdoProperty.MaxDurability_Effect);
                durability = durBase.Value + (int)(durBonus ?? 0f);
                var h = resolved.EffectCtx.GetValue((uint)DdoProperty.Durability_Hardness);
                if (h is > 0) durabilityHardness = (int)h.Value;
            }
        }

        var baseValueF = resolved.EffectCtx.GetValue((uint)DdoProperty.Item_Value);
        int? baseValue = baseValueF is > 0 ? (int)(baseValueF.Value / 1000f) : null;

        var enc = props.GetInt32PropertyValue((uint)DdoProperty.Inventory_Encumbrance);
        var weight = enc != null ? $"{enc.Value / 100.0:0.00} lbs" : null;

        var binding = GetBindingText(props, resolved.BoundLine);
        var desc = FormatGameText(props.GetStringInfoProperty((uint)DdoProperty.Item_Description)
                       ?.GetText(DatSource.PropertyMaster, null, null));

        var acceptsSentience = props.GetBytePropertyValue((uint)DdoProperty.AcceptsSentience) == 1;

        var raceExcluded = new List<string>();
        var raceName = GetEnumDisplayName<RaceType>(props, (uint)DdoProperty.Usage_CreatureRaceExcluded_Absolute);
        if (raceName != null) raceExcluded.Add(raceName);

        // Always compute equip slot; the view shows it on the mock panel for all types.
        // AppendTypeFieldsAfterName only adds a "Slot" table row for Jewelry/Clothing.
        var equipSlot = GetEquipSlot(props);
        var compatibleSlotsRaw = GetBitFieldValues(props, (uint)DdoProperty.Inventory_CompatibleSlot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var precludedSlotsRaw = GetBitFieldValues(props, (uint)DdoProperty.Inventory_PrecludedSlot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var compatibleSlots = compatibleSlotsRaw.Where(x => x is not "Equipment" and not "Backpack").Select(MapSlotName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var precludedSlots = precludedSlotsRaw.Where(x => x is not "Equipment" and not "Backpack").Select(MapSlotName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var equipmentKind = GetEquipmentKind(weenieType, compatibleSlotsRaw);

        // Compute all type-specific fields once; used by both the ViewModel (mock panel) and TableRows (text table).
        string typeLine = null;
        string damageLine = null;
        string critRollLine = null;
        string attackMod = null;
        string damageMod = null;
        float bdr = 0f;
        bool isTwoHanded = false;
        int? shieldBonus = null;
        int? maxDexBonus = null;
        int? damageReduction = null;
        int? attackPenalty = null;
        int? armorBonus = null;
        int? armorCheckPenalty = null;
        int? spellFailureChance = null;

        switch (weenieType) {
            case WeenieTypes.Weapon: {
                typeLine = GetEnumDisplayName<WeaponType>(props, (uint)DdoProperty.Combat_WeaponType);
                damageLine = BuildDamageLine(props, enhBonus, resolved.EffectCtx);
                critRollLine = BuildCriticalRollLine(props);
                attackMod = BuildAttackMod(props);
                damageMod = BuildDamageMod(props);
                bdr = BuildBaseDamageRating(props, resolved.EffectCtx);
                isTwoHanded = GetBitFieldValues(props, (uint)DdoProperty.Inventory_PrecludedSlot).Contains("Weapon2");
                break;
            }
            case WeenieTypes.Shield: {
                // Combat_ShieldType is a BitField (e.g. ["Tower"]), not an Enum property.
                var shieldTypeName = GetBitFieldValues(props, (uint)DdoProperty.Combat_ShieldType).FirstOrDefault();
                typeLine = shieldTypeName != null ? $"{shieldTypeName} Shield" : null;
                var rawBonus = props.GetInt32PropertyValue((uint)DdoProperty.Combat_ShieldBonus);
                if (rawBonus != null) shieldBonus = rawBonus.Value + (int)enhBonus;
                var rawMaxDex = props.GetInt32PropertyValue((uint)DdoProperty.Combat_MaxDexBonus);
                if (rawMaxDex is < 99) maxDexBonus = rawMaxDex;
                var rawDr = props.GetInt32PropertyValue((uint)DdoProperty.Combat_BlockingDamageReduction);
                if (rawDr is > 0) damageReduction = rawDr.Value + (int)enhBonus;
                var rawAcp = props.GetInt32PropertyValue((uint)DdoProperty.Combat_SkillCheckPenalty);
                if (rawAcp != null && rawAcp != 0) {
                    var normalized = rawAcp < 0 ? rawAcp.Value : -rawAcp.Value;
                    var adjusted = normalized + 1;
                    if (adjusted < 0) armorCheckPenalty = adjusted;
                }
                var rawSf = props.GetFloatPropertyValue((uint)DdoProperty.Spell_SpellFailureChance);
                if (rawSf is > 0f) spellFailureChance = (int)Math.Round(rawSf.Value * 100f);
                var rawAtk = props.GetInt32PropertyValue((uint)DdoProperty.Combat_ShieldAttackPenalty);
                if (rawAtk is < 0) attackPenalty = rawAtk;
                damageLine = BuildDamageLine(props, enhBonus, resolved.EffectCtx);
                critRollLine = BuildCriticalRollLine(props);
                attackMod = BuildAttackMod(props);
                damageMod = BuildDamageMod(props);
                bdr = BuildBaseDamageRating(props, resolved.EffectCtx);
                break;
            }
            case WeenieTypes.Armor: {
                var armorType = GetEnumDisplayName<ArmorType>(props, (uint)DdoProperty.Combat_ArmorType);
                typeLine = armorType != null ? $"{armorType} Armor" : null;
                var baseAc = props.GetInt32PropertyValue((uint)DdoProperty.Combat_ArmorBonus);
                if (baseAc != null) armorBonus = baseAc.Value + (int)enhBonus;
                var rawMaxDex = props.GetInt32PropertyValue((uint)DdoProperty.Combat_MaxDexBonus);
                if (rawMaxDex is < 99) maxDexBonus = rawMaxDex;
                var rawAcp = props.GetInt32PropertyValue((uint)DdoProperty.Combat_SkillCheckPenalty);
                if (rawAcp != null && rawAcp != 0) {
                    var normalized = rawAcp < 0 ? rawAcp.Value : -rawAcp.Value;
                    var adjusted = normalized + 1;
                    if (adjusted < 0) armorCheckPenalty = adjusted;
                }
                var rawSf = props.GetFloatPropertyValue((uint)DdoProperty.Spell_SpellFailureChance);
                if (rawSf is > 0f) spellFailureChance = (int)Math.Round(rawSf.Value * 100f);
                break;
            }
            case WeenieTypes.Augment:
                typeLine = GetBitFieldValues(props, (uint)DdoProperty.Augment_SlotTypes).FirstOrDefault();
                break;
        }

        string proficiencyName = null;
        if (weenieType == WeenieTypes.Weapon) {
            var wt = props.GetEnumProperty((uint)DdoProperty.Combat_WeaponType);
            if (wt != null) DatCache.WeaponProficiencyNames.TryGetValue(wt.UInt32Value.Value, out proficiencyName);
        } else if (weenieType == WeenieTypes.Shield && typeLine != null) {
            proficiencyName = $"{typeLine} Proficiency";
        } else if (weenieType == WeenieTypes.Armor) {
            var at = GetEnumDisplayName<ArmorType>(props, (uint)DdoProperty.Combat_ArmorType);
            if (at != null) proficiencyName = $"{at} Armor Proficiency";
        }

        var vm = new ItemViewModel {
            Title = props.Name ?? "Item",
            Theme = theme,
            WeenieType = weenieType,
            WeenieTypeName = Enum.GetName(typeof(WeenieType), weenieType) ?? $"0x{weenieType:X8}",
            Effects = resolved.Effects,
            AugmentSlots = augSlots,
            SetBonus1 = setBonus1,
            SetBonus2 = setBonus2,
            Recipes = GetRecipes(itemId),
            ItemIconUrl = $"/Image/Icon/0x{itemId:X8}",
            ItemName = props.Name ?? "Unknown",
            TypeLine = typeLine,
            EquipsToLine = equipSlot,
            EquipmentKind = equipmentKind,
            CompatibleSlots = compatibleSlots,
            CompatibleSlotsRaw = compatibleSlotsRaw,
            PrecludedSlots = precludedSlots,
            PrecludedSlotsRaw = precludedSlotsRaw,
            WeaponTypeName = weenieType == WeenieTypes.Weapon ? typeLine : null,
            ArmorTypeName = weenieType == WeenieTypes.Armor ? typeLine : null,
            CanMainHand = compatibleSlotsRaw.Contains("Weapon1", StringComparer.OrdinalIgnoreCase),
            CanOffHand = compatibleSlotsRaw.Contains("Weapon2", StringComparer.OrdinalIgnoreCase),
            ProficiencyName = proficiencyName,
            AcceptsSentience = acceptsSentience,
            MinLevel = minLevel is > 0 ? minLevel : null,
            Binding = binding,
            IsExclusive = resolved.IsExclusive,
            RaceExcluded = raceExcluded,
            Clickie = resolved.Clickie,
            Description = desc,
            DamageLine = damageLine,
            CriticalRollLine = critRollLine,
            AttackMod = attackMod,
            DamageMod = damageMod,
            BaseDamageRating = bdr,
            IsTwoHanded = isTwoHanded,
            ShieldBonus = shieldBonus,
            DamageReduction = damageReduction,
            AttackPenalty = attackPenalty,
            ArmorBonus = armorBonus,
            MaxDexBonus = maxDexBonus,
            ArmorCheckPenalty = armorCheckPenalty,
            SpellFailureChance = spellFailureChance,
            Durability = durability,
            Material = material,
            DurabilityHardness = durabilityHardness,
            Weight = weight,
            BaseValue = baseValue,
            BoundLine = resolved.BoundLine,
        };

        vm.TableRows = BuildTableRows(vm);
        return vm;
    }

    private static List<TableRow> BuildTableRows(ItemViewModel vm)
    {
        var rows = new List<TableRow>();

        rows.Add(new TableRow { Field = "Name", Value = vm.ItemName });

        AppendTypeFieldsAfterName(vm, rows);

        if (vm.ProficiencyName != null)
            rows.Add(new TableRow { Field = "Proficiency", Value = vm.ProficiencyName });

        foreach (var race in vm.RaceExcluded)
            rows.Add(new TableRow { Field = "Race Absolutely Excluded", Value = race });

        if (vm.MinLevel is > 0)
            rows.Add(new TableRow { Field = "Minimum Level", Value = vm.MinLevel.Value.ToString() });

        if (vm.Binding != null)
            rows.Add(new TableRow { Field = vm.IsAugment ? "Bind Status" : "Binding", Value = vm.Binding });

        // Shield combat stats follow Binding to match in-game examine panel order.
        if (vm.IsShield) {
            if (vm.ShieldBonus != null) rows.Add(new TableRow { Field = "Shield Bonus", Value = $"+{vm.ShieldBonus}" });
            if (vm.MaxDexBonus != null) rows.Add(new TableRow { Field = "Max Dex Bonus", Value = vm.MaxDexBonus.Value.ToString() });
            if (vm.DamageReduction != null) rows.Add(new TableRow { Field = "Damage Reduction (DR)", Value = vm.DamageReduction.Value.ToString() });
            if (vm.ArmorCheckPenalty != null) rows.Add(new TableRow { Field = "Armor Check Penalty", Value = vm.ArmorCheckPenalty.Value.ToString() });
            if (vm.SpellFailureChance != null) rows.Add(new TableRow { Field = "Spell Failure", Value = $"{vm.SpellFailureChance}%" });
            if (vm.AttackPenalty != null) rows.Add(new TableRow { Field = "Attack Penalty", Value = vm.AttackPenalty.Value.ToString() });
            if (vm.DamageLine != null) rows.Add(new TableRow { Field = "Damage", Value = vm.DamageLine });
            if (vm.CriticalRollLine != null) rows.Add(new TableRow { Field = "Critical Roll", Value = vm.CriticalRollLine });
            if (vm.AttackMod != null) rows.Add(new TableRow { Field = "Attack Mod", Value = vm.AttackMod });
            if (vm.DamageMod != null) rows.Add(new TableRow { Field = "Damage Mod", Value = vm.DamageMod });
        }

        if (vm.IsExclusive)
            rows.Add(new TableRow { Field = "Exclusive", Value = "You may only have ONE of this unique item equipped or in inventory at any one time." });

        if (vm.IsWeapon && vm.AcceptsSentience)
            rows.Add(new TableRow { Field = "Accepts Sentience", Value = "Yes" });

        if (vm.Clickie != null) {
            var ci = vm.Clickie;
            var sb = new System.Text.StringBuilder(ci.SpellName ?? "?");
            if (ci.CasterLevel != null) sb.Append($" (Caster Level: {ci.CasterLevel})");
            sb.Append(" \u2014 ");
            if (ci.MaxCharges != null) sb.Append($"{ci.MaxCharges} Charge{(ci.MaxCharges == 1 ? "" : "s")}");
            if (ci.DailyRecharge != null) {
                if (ci.MaxCharges != null) sb.Append(", ");
                sb.Append($"Recharge {ci.DailyRecharge}/day");
            }
            rows.Add(new TableRow { Field = "Clickie", Value = sb.ToString() });
        }

        if (!string.IsNullOrWhiteSpace(vm.Description))
            rows.Add(new TableRow { Field = "Description", Value = vm.Description, IsHtml = true });

        if (!vm.IsAugment) {
            if (vm.Material != null) rows.Add(new TableRow { Field = "Material", Value = vm.Material });
            if (vm.Durability != null) rows.Add(new TableRow { Field = "Durability", Value = vm.Durability.Value.ToString() });
        }

        if (vm.Weight != null)
            rows.Add(new TableRow { Field = "Weight", Value = vm.Weight });

        return rows;
    }

    private static void AppendTypeFieldsAfterName(ItemViewModel vm, List<TableRow> rows)
    {
        switch (vm.WeenieType) {
            case WeenieTypes.Weapon:
                if (vm.TypeLine != null) rows.Add(new TableRow { Field = "Weapon Type", Value = vm.TypeLine });
                if (vm.DamageLine != null) rows.Add(new TableRow { Field = "Damage", Value = vm.DamageLine });
                if (vm.CriticalRollLine != null) rows.Add(new TableRow { Field = "Critical Roll", Value = vm.CriticalRollLine });
                if (vm.AttackMod != null) rows.Add(new TableRow { Field = "Attack Mod", Value = vm.AttackMod });
                if (vm.DamageMod != null) rows.Add(new TableRow { Field = "Damage Mod", Value = vm.DamageMod });
                if (vm.IsTwoHanded) rows.Add(new TableRow { Field = "Handedness", Value = "Two-handed" });
                break;

            case WeenieTypes.Shield:
                // Only the type identifier goes here — combat stats come after Binding (see BuildTableRows).
                if (vm.TypeLine != null) rows.Add(new TableRow { Field = "Shield Type", Value = vm.TypeLine });
                break;

            case WeenieTypes.Armor:
                if (vm.TypeLine != null) rows.Add(new TableRow { Field = "Armor Type", Value = vm.TypeLine });
                if (vm.ArmorBonus != null) rows.Add(new TableRow { Field = "Armor Bonus", Value = $"+{vm.ArmorBonus}" });
                if (vm.MaxDexBonus != null) rows.Add(new TableRow { Field = "Max Dex Bonus", Value = vm.MaxDexBonus.Value.ToString() });
                if (vm.ArmorCheckPenalty != null) rows.Add(new TableRow { Field = "Armor Check Penalty", Value = vm.ArmorCheckPenalty.Value.ToString() });
                if (vm.SpellFailureChance != null) rows.Add(new TableRow { Field = "Spell Failure", Value = $"{vm.SpellFailureChance}%" });
                break;

            case WeenieTypes.Jewelry:
            case WeenieTypes.Clothing:
                if (vm.EquipsToLine != null) rows.Add(new TableRow { Field = "Slot", Value = vm.EquipsToLine });
                break;

            case WeenieTypes.Augment:
                if (vm.TypeLine != null) rows.Add(new TableRow { Field = "Augment Type", Value = vm.TypeLine });
                break;
        }
    }

    // ── Property helpers ──────────────────────────────────────────────────

    private static string GetTheme(IPropertyCollection props)
    {
        var tt = props.GetEnumProperty((uint)DdoProperty.Treasure_Type)?.UInt32Value;
        return tt switch {
            2 => "gold",      // Named
            3 => "platinum",  // Raid
            1 => "silver",    // Random
            _ => "copper"
        };
    }

    private static readonly System.Text.RegularExpressions.Regex _rgbTagRegex =
        new(@"<RGB=(#[0-9a-fA-F]{6})>(.*?)</RGB>", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Converts game markup to HTML: &lt;RGB=#hex&gt;text&lt;/RGB&gt; → colored spans, literal \n → &lt;br/&gt;.
    /// Result is pre-encoded HTML safe to render with Html.Raw.
    /// </summary>
    private static string FormatGameText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Extract RGB regions first, then HTML-encode the plain parts
        var parts = new System.Text.StringBuilder();
        int pos = 0;
        foreach (System.Text.RegularExpressions.Match m in _rgbTagRegex.Matches(text)) {
            // Encode and append text before this match
            var before = text[pos..m.Index];
            parts.Append(WebUtility.HtmlEncode(before));
            // Append the colored span with encoded inner text
            parts.Append($"<span style=\"color:{m.Groups[1].Value}\">{WebUtility.HtmlEncode(m.Groups[2].Value)}</span>");
            pos = m.Index + m.Length;
        }
        parts.Append(WebUtility.HtmlEncode(text[pos..]));

        // Game data contains literal backslash-n, not real newlines
        parts.Replace("\\n", "<br/>");
        return parts.ToString();
    }

    private static readonly System.Text.RegularExpressions.Regex _damageDiceRegex =
        new(@"^(\d+)d(\d+)(?:\+(\d+))?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static float BuildBaseDamageRating(IPropertyCollection props, EffectContext ctx)
    {
        var diceStr = props.GetStringProperty((uint)DdoProperty.DamageValue)?.StringValue;
        if (diceStr == null) return 0f;

        var match = _damageDiceRegex.Match(diceStr);
        if (!match.Success) return 0f;

        var dieCount = int.Parse(match.Groups[1].Value);
        var dieSize  = int.Parse(match.Groups[2].Value);
        var dieBonus = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;

        var scale     = props.GetFloatPropertyValue((uint)DdoProperty.BaseWeaponDamageDiceModifier) ?? 1.0f;
        var enhBonus  = ctx.GetValue((uint)DdoProperty.Combat_EnhancementBonus) ?? 0f;
        var critRange = props.GetInt32PropertyValue((uint)DdoProperty.Combat_CriticalHitRange) ?? 0;
        var critMod   = props.GetInt32PropertyValue((uint)DdoProperty.Combat_CriticalHitMod) ?? 2;

        var dmgNominal = (int)(scale * (dieCount + dieBonus) + enhBonus);
        var dmgMax     = (int)(scale * (dieCount * dieSize + dieBonus) + enhBonus);
        var avgDmg     = (dmgMax + dmgNominal) / 2f;
        var raw        = avgDmg * ((1 - critRange * 0.05f) + critRange * 0.05f * critMod);
        return (float)Math.Round(raw, 2);
    }

    private static string BuildDamageLine(IPropertyCollection props, float enhBonus, EffectContext ctx)
    {
        var diceStr = props.GetStringProperty((uint)DdoProperty.DamageValue)?.StringValue;
        if (diceStr == null) return null;

        var modifier = props.GetFloatPropertyValue((uint)DdoProperty.BaseWeaponDamageDiceModifier) ?? 1.0f;

        var diceDisplay = Math.Abs(modifier - 1.0f) < 0.0001f
            ? diceStr
            : $"{modifier:0.##}[{diceStr}]";

        var damageTypes = GetBitFieldValues(props, (uint)DdoProperty.DamageFlags)
            .Concat(ctx.GetAppendedBitFieldValues((uint)DdoProperty.DamageFlags))
            .Where(f => f != "Undef" && !f.Contains("SpellPoint"))
            .Distinct()
            .ToList();

        var sb = new System.Text.StringBuilder(diceDisplay);
        if (enhBonus >= 1f) sb.Append($" + {(int)enhBonus}");
        if (damageTypes.Count > 0) sb.Append($" {string.Join(", ", damageTypes)}");
        return sb.ToString().Trim();
    }

    private static string BuildCriticalRollLine(IPropertyCollection props)
    {
        var critRange = props.GetInt32PropertyValue((uint)DdoProperty.Combat_CriticalHitRange) ?? 0;
        var critMod   = props.GetInt32PropertyValue((uint)DdoProperty.Combat_CriticalHitMod) ?? 2;
        return $"({critRange * 5}%) {21 - critRange}-20 / x{critMod}";
    }

    private static string AbbrevAbility(string s) =>
        s?.Length >= 3 ? s.Substring(0, 3).ToUpper() : s?.ToUpper() ?? "?";

    private static string BuildAttackMod(IPropertyCollection props)
    {
        var flags = GetBitFieldValues(props, (uint)DdoProperty.Combat_HitAbilityMod_Multiple).ToList();
        return flags.Any() ? string.Join(", ", flags.Select(AbbrevAbility)) : null;
    }

    private static string BuildDamageMod(IPropertyCollection props)
    {
        var flags = GetBitFieldValues(props, (uint)DdoProperty.Combat_DamageAbilityMod_Multiple).ToList();
        return flags.Any() ? string.Join(", ", flags.Select(AbbrevAbility)) : null;
    }

    private static readonly uint[] StaticMutatorIds = [
        0x79029FD6, // Minimum Level
        0x7902B139, // Item Hardness / Durability
        0x7902B13A, // Item Value
    ];

    private static void ApplyStaticMutators(EffectContext ctx)
    {
        foreach (var id in StaticMutatorIds) {
            var mutProps = DatSource.PropertyMaster.GetPropertyCollection(id);
            if (mutProps != null) EffectResolver.ApplyMods(mutProps, ctx);
        }
    }

    private record ResolvedEffects(
        List<EffectInfo> Effects,
        List<string> AugmentSlots,
        ClickieInfo Clickie,
        EffectContext EffectCtx,
        string BoundLine,
        int? MinLevel,
        bool IsExclusive);

    private static ResolvedEffects ResolveEffects(IPropertyCollection props)
    {
        var effectsArray = props.GetArrayProperty((uint)DdoProperty.Effect_OnCreationEffects);
        if (effectsArray == null)
            return new(new(), new(), null, new EffectContext(props), null, null, false);

        var canBeUsed = props.GetBytePropertyValue((uint)DdoProperty.Usage_CanBeUsed) == 1;
        var ctx = new EffectContext(props);

        // Collect all effect entries up front
        var entries = new List<EffectEntry>();
        foreach (var entry in effectsArray.Properties) {
            if (entry.PropertyId != (uint)DdoProperty.Effect_Entry) continue;
            if (entry is not IArrayProperty entryArr) continue;

            var effectProp = entryArr.GetInt32Property((uint)DdoProperty.Effect);
            if (effectProp == null) continue;

            var did = (uint)effectProp.Int32Value;
            if (did == 0) continue;

            var effectId    = EffectResolver.ConvertId(did);
            var effectProps = DatSource.PropertyMaster.GetPropertyCollection(effectId);

            string fallback = null;
            if (effectProps == null) {
                fallback = effectProp.ReferencedObject as string;
                if (string.IsNullOrEmpty(fallback))
                    DatCache.Index.NameLookup.TryGetValue(effectId, out fallback);
            }

            entries.Add(new EffectEntry { EffectId = effectId, Props = effectProps, FallbackName = fallback });
        }

        // Pass 1: apply all mods from all effects into context before resolving any names.
        foreach (var entry in entries) {
            if (entry.Props != null) EffectResolver.ApplyMods(entry.Props, ctx);
        }

        // Named items don't have an effect that writes Treasure_BaseLevel — the real game seeds it
        // externally. Seed it here so the static mutators (Item Value, Min Level) compute correctly.
        if (!(ctx.GetValue((uint)DdoProperty.Treasure_BaseLevel) > 0)) {
            int? seedLevel = props.GetInt32PropertyValue((uint)DdoProperty.Usage_MinLevel);
            if (!(seedLevel > 0)) {
                foreach (var entry in entries) {
                    var name = entry.Props?.Name ?? entry.FallbackName;
                    if (name != null &&
                        name.StartsWith("Required Level:", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(name.AsSpan("Required Level:".Length).Trim(), out var lvl)) {
                        seedLevel = lvl;
                        break;
                    }
                }
            }
            if (seedLevel > 0)
                ctx.ApplyArithmetic(PropertyOp.Set, (uint)DdoProperty.Treasure_BaseLevel, seedLevel.Value);
        }

        // Apply static mutators now that Treasure_BaseLevel is established.
        ApplyStaticMutators(ctx);

        // Pass 2: resolve each effect's display name using the fully-populated context
        var effects = new List<EffectInfo>();
        var augmentSlots = new List<string>();
        string boundLine = null;
        int? minLevel = null;
        bool isExclusive = false;
        ClickieInfo partialClickie = null;
        int? maxCharges = null;
        int? dailyRecharge = null;

        foreach (var entry in entries) {
            EffectInfo resolved = entry.Props != null
                ? EffectResolver.ResolveName(entry.Props, ctx)
                : new EffectInfo { Name = entry.FallbackName };

            if (string.IsNullOrEmpty(resolved.Name)) continue;

            // Extract metadata from non-displayed effects before filtering them out.
            // "Bound to Account" → shown as a field, not in the effects list.
            if (resolved.Name.Equals("Bound to Account", StringComparison.OrdinalIgnoreCase)) {
                boundLine = resolved.Name;
                continue;
            }

            // "Required Level: N" → shown as the Minimum Level field, not as an effect.
            if (resolved.Name.StartsWith("Required Level:", StringComparison.OrdinalIgnoreCase)) {
                if (int.TryParse(resolved.Name.AsSpan("Required Level:".Length).Trim(), out var lvl))
                    minLevel = lvl;
                continue;
            }

            // Absorb clickie effects into the Clickie field rather than the effects list.
            if (canBeUsed) {
                if (resolved.Name.StartsWith("Spell: ", StringComparison.OrdinalIgnoreCase)) {
                    partialClickie = TryGetSpellDetails(entry.EffectId) ?? new ClickieInfo();
                    continue;
                }
                if (resolved.Name.Contains("Max Charges", StringComparison.OrdinalIgnoreCase)) {
                    var idx = resolved.Name.LastIndexOf('=');
                    if (idx >= 0 && int.TryParse(resolved.Name.AsSpan(idx + 1).Trim(), out var n))
                        maxCharges = n;
                    continue;
                }
                if (resolved.Name.StartsWith("Recharge", StringComparison.OrdinalIgnoreCase)) {
                    var parts = resolved.Name.Split(' ');
                    if (parts.Length >= 2 && int.TryParse(parts[1].Split('/')[0], out var d))
                        dailyRecharge = d;
                    continue;
                }
            }

            if (resolved.Name.Equals("Exclusive", StringComparison.OrdinalIgnoreCase)) {
                isExclusive = true;
                continue;
            }

            // Augment slot effects go into AugmentSlots, not Effects.
            // These have IsDisplayedOnClient = 0 but the game always shows them under Augments.
            var trimmedName = resolved.Name.TrimEnd();
            var trimmedLower = trimmedName.ToLowerInvariant();
            if (trimmedLower.EndsWith("augment slot")
                || trimmedLower.StartsWith("lamordia:")
                || trimmedLower.StartsWith("isle of dread")) {
                augmentSlots.Add(trimmedName);
                continue;
            }

            if (entry.Props != null &&
                entry.Props.GetBytePropertyValue((uint)DdoProperty.Effect_IsDisplayedOnClient) != 1)
                continue;

            effects.Add(resolved);
        }

        ClickieInfo clickie = null;
        if (partialClickie != null)
            clickie = partialClickie with { MaxCharges = maxCharges, DailyRecharge = dailyRecharge };

        return new(effects, augmentSlots, clickie, ctx, boundLine, minLevel, isExclusive);
    }

    private static ClickieInfo TryGetSpellDetails(uint effectDid)
    {
        try {
            var ep = DatSource.PropertyMaster.GetPropertyCollection(EffectResolver.ConvertId(effectDid));
            if (ep == null) return null;

            var iconId = ep.GetInt32PropertyValue((uint)DdoProperty.Effect_Icon);
            var iconUrl = iconId is > 0 ? $"/Image/0x{(uint)iconId.Value:X8}" : null;

            var modArray = ep.GetArrayProperty((uint)DdoProperty.Mod_Array);
            if (modArray == null) return new ClickieInfo { SpellIconUrl = iconUrl };

            int? cl = null;
            uint spellEntityId = 0;

            foreach (var mod in modArray.Properties) {
                if (mod is not IArrayProperty modArr) continue;
                var channel = modArr.GetEnumProperty((uint)DdoProperty.Mod_Channel);
                if (channel == null) continue;
                var ch = (EffectChannel)channel.UInt32Value.Value;

                if (ch == EffectChannel.CasterLevel) {
                    var floatCl = modArr.GetFloatPropertyValue((uint)DdoProperty.Spell_CasterLevel);
                    if (floatCl != null) cl = (int)floatCl.Value;
                    else {
                        var intCl = modArr.GetInt32PropertyValue((uint)DdoProperty.Spell_CasterLevel);
                        if (intCl != null) cl = intCl.Value;
                    }
                } else if (ch == EffectChannel.Spell) {
                    var spellRef = modArr.GetInt32PropertyValue((uint)DdoProperty.Spell);
                    if (spellRef != null) spellEntityId = EffectResolver.ConvertId((uint)spellRef.Value);
                }
            }

            if (spellEntityId == 0) return new ClickieInfo { CasterLevel = cl, SpellIconUrl = iconUrl };

            var spellProps = DatSource.PropertyMaster.GetPropertyCollection(spellEntityId);
            var masterRef = spellProps?.GetInt32PropertyValue((uint)DdoProperty.Spell_Master);
            if (masterRef == null) return new ClickieInfo { CasterLevel = cl, SpellIconUrl = iconUrl };

            var master = DatSource.PropertyMaster.GetPropertyCollection(EffectResolver.ConvertId((uint)masterRef.Value));
            if (master == null) return new ClickieInfo { CasterLevel = cl, SpellIconUrl = iconUrl };

            var masterIconId = master.GetInt32PropertyValue((uint)DdoProperty.Action_Action_m_didIcon);
            if (masterIconId is > 0) iconUrl = $"/Image/0x{(uint)masterIconId.Value:X8}";

            var targets = GetBitFieldValues(master, (uint)DdoProperty.Action_Action_m_eTargetType)
                .Where(t => t != "None").ToList();
            var schools = GetBitFieldValues(master, (uint)DdoProperty.Spell_School)
                .Where(s => s != "None").ToList();
            var srByte = master.GetBytePropertyValue((uint)DdoProperty.Spell_CanBeResisted);
            var descText = master.GetStringInfoProperty((uint)DdoProperty.Action_Action_m_siDescription)
                ?.GetText(DatSource.PropertyMaster, null, null);
            var spellName = master.GetStringInfoProperty((uint)DdoProperty.Action_Action_m_siName)
                ?.GetText(DatSource.PropertyMaster, null, null);

            return new ClickieInfo {
                SpellName = spellName,
                CasterLevel = cl,
                SpellIconUrl = iconUrl,
                Target = targets.Count > 0 ? string.Join(", ", targets) : null,
                School = schools.Count > 0 ? string.Join(", ", schools) : null,
                CanBeResisted = srByte != null ? srByte.Value == 1 : null,
                SpellDescription = descText,
            };
        }
        catch { }
        return null;
    }

    private static List<string> GetAugmentSlots(IPropertyCollection props, uint weenieType)
    {
        var arr = props.GetArrayProperty((uint)DdoProperty.Augment_SlotArray);
        if (arr == null) return new List<string>();

        var slots = new List<string>();
        foreach (var entry in arr.Properties) {
            if (entry.PropertyId != (uint)DdoProperty.Augment_SlotEntry) continue;
            if (entry is not IArrayProperty entryArr) continue;

            var nameProp = entryArr.GetStringInfoProperty((uint)DdoProperty.Augment_SlotName);
            if (nameProp == null || nameProp.Key == 0) continue;

            var text = nameProp.GetText(DatSource.PropertyMaster, null, null);
            if (string.IsNullOrEmpty(text)) continue;

            // Lamordia slot names don't embed the item category (unlike IoD which already includes "(Weapon)" etc.).
            // Derive it from the item's WeenieType so the view can render the colored "(Type)" suffix.
            if (text.StartsWith("Lamordia:", StringComparison.OrdinalIgnoreCase) && !text.Contains('(')) {
                var typeSuffix = LamordiaSlotItemType(weenieType);
                if (typeSuffix != null) text = $"{text} ({typeSuffix})";
            }

            slots.Add(text);
        }
        return slots;
    }

    private static string LamordiaSlotItemType(uint weenieType) => weenieType switch {
        WeenieTypes.Weapon  => "Weapon",
        WeenieTypes.Shield  => "Shield",
        WeenieTypes.Armor   => "Armor",
        WeenieTypes.Jewelry or WeenieTypes.Clothing => "Accessory",
        _ => null
    };

    private static string GetBindingText(IPropertyCollection props, string boundLine = null)
    {
        var onAcquire = props.GetBytePropertyValue((uint)DdoProperty.Inventory_IsBoundOnAcquire);
        var onEquip   = props.GetBytePropertyValue((uint)DdoProperty.Inventory_IsBoundOnEquip);
        var toAccount = props.GetBytePropertyValue((uint)DdoProperty.Inventory_BoundToAccount);

        // Account-binding can come from the property OR from the effects pipeline (boundLine)
        var isAccountBound = toAccount == 1 ||
            (boundLine != null && boundLine.IndexOf("Account", StringComparison.OrdinalIgnoreCase) >= 0);

        var scope = isAccountBound ? "Account" : "Character";
        if (onAcquire == 1) return $"Binds to {scope} on Acquire";
        if (onEquip   == 1) return $"Binds to {scope} on Equip";
        return null;
    }

    private static string GetEquipmentKind(uint weenieType, List<string> slots)
    {
        if (weenieType == WeenieTypes.Weapon) return "Weapon";
        if (weenieType == WeenieTypes.Shield) return "Shield";
        if (slots.Contains("Head", StringComparer.OrdinalIgnoreCase)) return "Head Item";
        if (slots.Contains("Chest", StringComparer.OrdinalIgnoreCase)) return "Chest Armor";
        if (slots.Contains("Hands", StringComparer.OrdinalIgnoreCase)) return "Hands";
        if (slots.Contains("Feet", StringComparer.OrdinalIgnoreCase)) return "Feet";
        if (slots.Contains("Arms", StringComparer.OrdinalIgnoreCase)) return "Wrists";
        if (slots.Contains("Cloak", StringComparer.OrdinalIgnoreCase)) return "Cloak";
        if (slots.Contains("Belt", StringComparer.OrdinalIgnoreCase)) return "Waist";
        if (slots.Contains("Neck", StringComparer.OrdinalIgnoreCase)) return "Neck";
        if (slots.Any(x => x.StartsWith("Finger", StringComparison.OrdinalIgnoreCase))) return "Ring";
        if (slots.Contains("Trinket", StringComparer.OrdinalIgnoreCase)) return "Trinket";
        if (weenieType == WeenieTypes.Armor) return "Armor";
        if (weenieType is WeenieTypes.Jewelry or WeenieTypes.Clothing) return "Accessory";
        return "Equippable Item";
    }

    private static string GetEquipSlot(IPropertyCollection props)
    {
        var slots = GetBitFieldValues(props, (uint)DdoProperty.Inventory_CompatibleSlot)
            .Where(f => f != "Equipment" && f != "Backpack")
            .Select(MapSlotName)
            .ToList();
        return slots.Count > 0 ? string.Join(", ", slots) : null;
    }

    private static string MapSlotName(string slot) => slot switch {
        "Weapon1" => "Main Hand",
        "Weapon2" => "Off Hand",
        "Finger1" => "First Finger",
        "Finger2" => "Second Finger",
        "Head"    => "Head",
        "Neck"    => "Neck",
        "Trinket" => "Trinket",
        "Cloak"   => "Cloak",
        "Arms"    => "Wrists",
        "Hands"   => "Hands",
        "Chest"   => "Armor",
        "Legs"    => "Legs",
        "Feet"    => "Feet",
        "Belt"    => "Waist",
        _         => slot
    };

    private static string GetMaterialName(IPropertyCollection props)
    {
        var did = props.GetInt32PropertyValue((uint)DdoProperty.Material);
        if (did == null || did == 0) return null;

        var mp = DatSource.PropertyMaster.GetPropertyCollection((uint)did.Value);
        if (mp == null) return null;

        return mp.GetStringInfoProperty((uint)DdoProperty.Material_Name)
                   ?.GetText(DatSource.PropertyMaster, null, null)
               ?? mp.Name;
    }

    private static readonly uint[] SetBonusDescriptionIds = [
        (uint)DdoProperty.SetBonus_TwoPiece_Description,
        (uint)DdoProperty.SetBonus_ThreePiece_Description,
        (uint)DdoProperty.SetBonus_FourPiece_Description,
        (uint)DdoProperty.SetBonus_FivePiece_Description,
    ];

    private static SetBonusInfo GetSetBonusInfo(IPropertyCollection props, uint propertyId)
    {
        var setBonusProp = props.GetEnumProperty(propertyId);
        if (setBonusProp == null) return null;

        var setId = setBonusProp.UInt32Value;
        try {
            var master = DatSource.PropertyMaster.GetPropertyCollection(SetBonusMasterId);
            var table  = master?.GetArrayProperty((uint)DdoProperty.SetBonus_Table);
            if (table == null) return null;

            foreach (var entry in table.Properties) {
                if (entry.PropertyId != (uint)DdoProperty.SetBonus_Entry) continue;
                if (entry is not IArrayProperty ea) continue;
                if (ea.GetUInt32PropertyValue((uint)DdoProperty.SetBonus_ID) != setId) continue;

                var name = ea.GetStringInfoProperty((uint)DdoProperty.SetBonus_Name)
                              ?.GetText(DatSource.PropertyMaster, null, null);
                if (string.IsNullOrEmpty(name)) return null;

                var descs = new List<string>();
                foreach (var descId in SetBonusDescriptionIds) {
                    var text = ea.GetStringInfoProperty(descId)
                                 ?.GetText(DatSource.PropertyMaster, null, null);
                    if (!string.IsNullOrEmpty(text))
                        descs.Add(text);
                }

                return new SetBonusInfo { SetId = setId.Value, Name = name, Descriptions = descs };
            }
        }
        catch { }
        return null;
    }

    private static string GetEnumDisplayName<TEnum>(IPropertyCollection props, uint propertyId) where TEnum : struct, Enum
    {
        var prop = props.GetEnumProperty(propertyId);
        if (prop == null) return null;
        return Enum.IsDefined(typeof(TEnum), prop.UInt32Value)
            ? Enum.GetName(typeof(TEnum), prop.UInt32Value)
            : null;
    }

    private static IEnumerable<string> GetBitFieldValues(IPropertyCollection props, uint propertyId)
    {
        var prop = props.GetBitFieldProperty(propertyId);
        if (prop == null) return Enumerable.Empty<string>();
        return prop.Values ?? Enumerable.Empty<string>();
    }

    private static List<RecipeGroup> GetRecipes(uint itemId)
    {
        if (!DatCache.RecipeMap.TryGetValue(itemId, out var recipes) || recipes.Count == 0)
            return new List<RecipeGroup>();

        return recipes
            .GroupBy(r => r.DeviceName ?? "Unknown")
            .Select(g => new RecipeGroup {
                DeviceName = g.Key,
                Recipes = g.Select(r => BuildRecipeRow(r, itemId)).Where(r => r != null).ToList()
            })
            .Where(g => g.Recipes.Count > 0)
            .ToList();
    }

    private static RecipeRow BuildRecipeRow(RecipeData recipeData, uint itemId)
    {
        var recipeProps = DatSource.PropertyMaster.GetPropertyCollection(recipeData.RecipeId);
        if (recipeProps == null) return null;

        var cost = new List<string>();
        var slotList = recipeProps.GetArrayProperty((uint)DdoProperty.Recipe_Slot_List);
        if (slotList != null) {
            foreach (var slot in slotList.Properties) {
                if (slot.PropertyId != (uint)DdoProperty.Recipe_Slot_Entry) continue;
                if (slot is not IInt32Property slotRef) continue;

                var slotDefId = EffectResolver.ConvertId((uint)(slotRef.Int32Value ?? 0));
                if (slotDefId == 0) continue;

                var slotDef = DatSource.PropertyMaster.GetPropertyCollection(slotDefId);
                var ingredientId = slotDef?.GetInt32PropertyValue((uint)DdoProperty.Ingredient_Entity);
                if (ingredientId != null) {
                    var normalizedIngId = EffectResolver.ConvertId((uint)ingredientId.Value);
                    if (normalizedIngId == itemId) continue;
                }

                var slotName = slotRef.ReferencedObject as string ?? slotDef?.Name;
                if (!string.IsNullOrEmpty(slotName))
                    cost.Add(slotName);
            }
        }

        var removed = GetMutationRefs(recipeProps, (uint)DdoProperty.Recipe_Result_RemoveMutations);
        var added   = GetMutationRefs(recipeProps, (uint)DdoProperty.Recipe_Result_AddMutations);

        return new RecipeRow {
            Name = recipeData.Name ?? $"0x{recipeData.RecipeId:X8}",
            Cost = cost,
            Removed = removed,
            Added = added,
        };
    }

    private static List<string> GetMutationRefs(IPropertyCollection recipeProps, uint arrayId)
    {
        var arr = recipeProps.GetArrayProperty(arrayId);
        if (arr == null) return new List<string>();

        var names = new List<string>();
        foreach (var entry in arr.Properties) {
            if (entry.PropertyId != (uint)DdoProperty.Recipe_Result_MutationDID) continue;
            if (entry is not IInt32Property ip) continue;

            var name = ip.ReferencedObject as string;
            if (string.IsNullOrEmpty(name)) {
                var did = EffectResolver.ConvertId((uint)(ip.Int32Value ?? 0));
                name = DatSource.PropertyMaster.GetPropertyCollection(did)?.Name;
            }
            if (!string.IsNullOrEmpty(name))
                names.Add(name);
        }
        return names;
    }
}
