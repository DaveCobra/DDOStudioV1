using DdoDatApi.Caching;
using DdoDatApi.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VoK.Sdk.Ddo.Enums;
using VoK.Sdk.Enums;
using VoK.Sdk.Properties;

namespace DdoDatApi.Controllers;

/// <summary>
/// Mutable property state built up as Effect_OnCreationEffects are applied in order.
/// Arithmetic mods write into the overlay; earlier effects feed values to later equations.
/// </summary>
internal class EffectContext
{
    private readonly Dictionary<uint, float> _overlay = new();
    private readonly Dictionary<uint, List<string>> _bitFieldAppended = new();
    private readonly IPropertyCollection _base;

    public EffectContext(IPropertyCollection baseProps) => _base = baseProps;

    /// <summary>Returns the current float value for a property, checking the overlay first.</summary>
    public float? GetValue(uint propertyId)
    {
        if (_overlay.TryGetValue(propertyId, out var v)) return v;
        var fv = _base.GetFloatPropertyValue(propertyId);
        if (fv != null) return fv.Value;
        var iv = _base.GetInt32PropertyValue(propertyId);
        if (iv != null) return (float)iv.Value;
        var uv = _base.GetUInt32PropertyValue(propertyId);
        if (uv != null) return (float)uv.Value;
        return null;
    }

    public void AppendBitFieldValues(uint propertyId, IEnumerable<string> values)
    {
        if (!_bitFieldAppended.TryGetValue(propertyId, out var list))
            _bitFieldAppended[propertyId] = list = new List<string>();
        list.AddRange(values);
    }

    public IEnumerable<string> GetAppendedBitFieldValues(uint propertyId) =>
        _bitFieldAppended.TryGetValue(propertyId, out var list) ? list : Enumerable.Empty<string>();

    public void ApplyArithmetic(PropertyOp op, uint destId, float operand)
    {
        var current = GetValue(destId) ?? 0f;
        _overlay[destId] = op switch {
            PropertyOp.Set      => operand,
            PropertyOp.Add      => current + operand,
            PropertyOp.Subtract => current - operand,
            PropertyOp.Multiply => current * operand,
            PropertyOp.Divide   => operand != 0 ? current / operand : current,
            _                   => current
        };
    }
}

/// <summary>
/// Resolves Effect_OnCreationEffects entries into human-readable display names by:
///   1. Fetching each effect's Weenie
///   2. Processing Mod_Array entries to build up context state
///   3. Evaluating Effect_Display{1-3}_Equation to obtain replacement values
///   4. Substituting those values into the Effect_Name StringInfo
/// </summary>
internal static class EffectResolver
{
    private static readonly uint[] DisplayEquationIds = [
        (uint)DdoProperty.Effect_Display1_Equation,
        (uint)DdoProperty.Effect_Display2_Equation,
        (uint)DdoProperty.Effect_Display3_Equation,
    ];

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the display name and description for a single effect using an already-complete context.
    /// Does NOT apply mods — call ApplyMods separately (or use the two-pass pattern).
    /// Returns null Name on failure; callers should fall back to the index name.
    /// </summary>
    public static EffectInfo ResolveName(IPropertyCollection effectProps, EffectContext ctx)
    {
        try
        {
            // Creation effect: produce name directly, no mods involved
            var creationEntity = effectProps.GetInt32PropertyValue((uint)DdoProperty.Creation_Entity);
            if (creationEntity is not null and not 0) {
                var entityProps = DatSource.PropertyMaster.GetPropertyCollection(ConvertId((uint)creationEntity.Value));
                var entityName  = entityProps?.Name ?? $"0x{creationEntity.Value:X8}";
                var qty         = effectProps.GetInt32PropertyValue((uint)DdoProperty.Creation_Quantity);
                var creationName = qty is > 1 ? $"Create {entityName} x{qty}" : $"Create {entityName}";
                return new EffectInfo { Name = creationName };
            }

            // Evaluate display equations for {0}, {1}, {2} replacements
            var replacements = new List<string>();
            foreach (var eqId in DisplayEquationIds) {
                var eq  = effectProps.GetArrayProperty(eqId);
                if (eq == null) continue;
                var rep = EvaluateEquation(eq, ctx);
                if (rep != null) replacements.Add(rep);
            }

            var nameProp = effectProps.GetStringInfoProperty((uint)DdoProperty.Effect_Name);
            var resolvedName = nameProp != null ? ResolveStringInfo(nameProp, replacements) : null;

            var descProp = effectProps.GetStringInfoProperty((uint)DdoProperty.Effect_Description);
            var resolvedDesc = descProp != null ? ResolveStringInfo(descProp, replacements) : null;

            return new EffectInfo { Name = resolvedName, Description = resolvedDesc };
        }
        catch { return new EffectInfo(); }
    }

    // ── Mod processing ────────────────────────────────────────────────────

    internal static void ApplyMods(IPropertyCollection effectProps, EffectContext ctx)
    {
        var modArray = effectProps.GetArrayProperty((uint)DdoProperty.Mod_Array);
        if (modArray == null) return;

        foreach (var modProp in modArray.Properties) {
            if (modProp is IArrayProperty mod)
                ProcessMod(mod, ctx);
        }
    }

    private static void ProcessMod(IArrayProperty mod, EffectContext ctx)
    {
        var opProp = mod.GetEnumProperty((uint)DdoProperty.Mod_Op);
        if (opProp == null) return;

        var op = (PropertyOp)opProp.UInt32Value;

        var dest = mod.GetUInt32PropertyValue((uint)DdoProperty.Mod_Destination);
        var src  = mod.GetUInt32PropertyValue((uint)DdoProperty.Mod_Source);
        if (dest == null || src == null) return;

        // Bitwise OR: append flag names from the source bitfield property
        if (op == PropertyOp.Or) {
            var srcBitField = mod.GetBitFieldProperty(src.Value);
            if (srcBitField?.Values?.Count > 0)
                ctx.AppendBitFieldValues(dest.Value, srcBitField.Values);
            return;
        }

        // Only arithmetic ops affect context values relevant to equation display
        if (op is not (PropertyOp.Set or PropertyOp.Add or PropertyOp.Subtract
                       or PropertyOp.Multiply or PropertyOp.Divide))
            return;

        // Base operand: the mod's own property with ID = Mod_Source (may be Float, Int32, or UInt32)
        float baseOperand = mod.GetFloatPropertyValue(src.Value)
                            ?? (float?)mod.GetInt32PropertyValue(src.Value)
                            ?? (float?)mod.GetUInt32PropertyValue(src.Value)
                            ?? 0f;

        // If there's an Equation child it scales/replaces the operand
        var eqArr = mod.GetArrayProperty((uint)DdoProperty.Equation);
        float finalOperand = (eqArr != null && EvaluateEquation(eqArr, ctx) is string eqStr
                              && float.TryParse(eqStr, out var eqVal))
            ? eqVal
            : baseOperand;

        ctx.ApplyArithmetic(op, dest.Value, finalOperand);
    }

    // ── Equation evaluation ───────────────────────────────────────────────

    /// <summary>
    /// Evaluates one display equation. Returns the result as a string, or null if unresolvable.
    /// Numeric results are floored to int. Progression results may be text.
    /// </summary>
    private static string EvaluateEquation(IArrayProperty eq, EffectContext ctx)
    {
        var solution = eq.GetUInt32PropertyValue((uint)DdoProperty.Equation_Solution);
        if (solution == null) return null;

        // ── Value Driver path ─────────────────────────────────────────────
        var valueDriver = eq.GetUInt32PropertyValue((uint)DdoProperty.Equation_ValueDriver);
        if (valueDriver is > 0) {
            var baseBonus  = eq.GetFloatPropertyValue((uint)DdoProperty.Equation_ValueDriverBaseBonus) ?? 0f;
            var multiplier = eq.GetFloatPropertyValue((uint)DdoProperty.Equation_ValueDriverMultiplier) ?? 1f;

            var locProp  = eq.GetEnumProperty((uint)DdoProperty.Equation_ValueDriverLocation);
            var location = locProp != null
                ? (ValueDriverLocation)locProp.UInt32Value
                : ValueDriverLocation.Target;

            float? driverValue;
            if (location == ValueDriverLocation.Source) {
                // Look up the driver property in the accumulated item context
                driverValue = ctx.GetValue(valueDriver.Value);
            } else {
                // Target / default: check the equation's own children first, then fall back to context
                driverValue = eq.GetFloatPropertyValue(valueDriver.Value)
                              ?? (float?)eq.GetInt32PropertyValue(valueDriver.Value)
                              ?? ctx.GetValue(valueDriver.Value);
            }

            if (driverValue == null) return null;
            return ((int)MathF.Floor(baseBonus + driverValue.Value * multiplier)).ToString();
        }

        // ── Level Driver + Progression path ──────────────────────────────
        var levelDriver    = eq.GetUInt32PropertyValue((uint)DdoProperty.Equation_LevelDriver);
        var progressionDid = eq.GetInt32PropertyValue((uint)DdoProperty.Equation_Progression);

        if (levelDriver is > 0 && progressionDid is > 0)
        {
            // Resolve the level index (1-based)
            float? levelVal = eq.GetFloatPropertyValue(levelDriver.Value)
                              ?? (float?)eq.GetInt32PropertyValue(levelDriver.Value)
                              ?? ctx.GetValue(levelDriver.Value);

            if (levelVal == null) return null;

            int idx = (int)levelVal.Value - 1;  // convert to 0-based
            if (idx < 0) return null;

            var progId    = ConvertId((uint)progressionDid.Value);
            var progProps = DatSource.PropertyMaster.GetPropertyCollection(progId);
            var progArray = progProps?.GetArrayProperty((uint)DdoProperty.Progression_Array);
            if (progArray == null || idx >= progArray.Properties.Count) return null;

            var element = progArray.Properties.ElementAtOrDefault(idx);

            // Flat entry: element IS the value directly
            if (element is IStringProperty sp) return sp.StringValue;
            if (element is IStringInfoProperty sip)
                return sip.GetText(DatSource.PropertyMaster, null, null);

            if (element is not IArrayProperty entry) return null;

            // Nested entry: look for named sub-properties
            var levelValue = entry.GetStringProperty((uint)DdoProperty.Progression_Level_Value);
            if (levelValue != null) return levelValue.StringValue;

            // DID value: look up the referenced object's name
            var levelDid = entry.GetInt32PropertyValue((uint)DdoProperty.Progression_Level_DID);
            if (levelDid != null) {
                var nameId = ConvertId((uint)levelDid.Value);
                return DatSource.PropertyMaster.GetPropertyCollection(nameId)?.Name
                       ?? $"0x{nameId:X8}";
            }
        }

        return null;
    }

    // ── StringInfo resolution ─────────────────────────────────────────────

    private static string ResolveStringInfo(IStringInfoProperty prop, List<string> replacements)
    {
        string raw;
        if (prop.IsLiteral) {
            raw = prop.GetText(DatSource.PropertyMaster, null, null);
        } else {
            // GetStringEntry gives the raw string with {0}, {1}, … still intact.
            // GetText(…, null, null) would drop unfilled placeholders, so prefer GetStringEntry.
            var entry = prop.GetStringEntry(DatSource.PropertyMaster);
            raw = entry?.Value ?? prop.GetText(DatSource.PropertyMaster, null, null);
        }

        if (string.IsNullOrEmpty(raw)) return null;

        // Substitute positional placeholders with evaluated equation results
        for (int i = 0; i < replacements.Count; i++)
            raw = raw.Replace("{" + i + "}", replacements[i]);

        // Strip empty parenthetical remnants (e.g. "  ()") left by unsubstituted tokens
        raw = Regex.Replace(raw, @"\s*\(\s*\)\s*", " ").Trim();

        return string.IsNullOrEmpty(raw) ? null : raw;
    }

    // ── Utility ───────────────────────────────────────────────────────────

    internal static uint ConvertId(uint did) =>
        did >= 0x70000000 && did < 0x71000000 ? did + 0x09000000 : did;
}
