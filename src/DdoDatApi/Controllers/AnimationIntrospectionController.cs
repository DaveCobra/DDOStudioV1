using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using VoK.Sdk;

namespace DdoDatApi.Controllers;

/// <summary>
/// 1.4.1.13 focused SDK introspector. Reads the requested animation directly from client_anim.dat,
/// reads known WStates directly from client_gamelogic.dat, inventories relevant VoK SDK parser/factory
/// methods, and safely attempts constructors/factories that can be supplied from the real records.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class AnimationIntrospectionController : ControllerBase
{
    [HttpGet("inspect/{animationId}")]
    public IActionResult Inspect(string animationId = "0x05000051", string? wstateIds = null, int maxDepth = 6)
    {
        if (!TryId(animationId, out var animation)) return BadRequest("Invalid animation id.");
        maxDepth = Math.Clamp(maxDepth, 2, 8);

        var requestedWStates = ParseIds(wstateIds).ToList();
        if (requestedWStates.Count == 0)
        {
            requestedWStates.AddRange(new uint[]
            {
                0x70000000, 0x70000040, 0x70000041, 0x70000042,
                0x70003F80, 0x70020100, 0x70039236
            });
        }

        var sdkAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("VoK.Sdk", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        var allSdkTypes = sdkAssemblies.SelectMany(SafeTypes).Where(t => t.FullName != null).ToArray();
        var sdkTypes = allSdkTypes
            .Where(t => t.Name.Contains("Anim", StringComparison.OrdinalIgnoreCase) ||
                        t.Name.Contains("State", StringComparison.OrdinalIgnoreCase) ||
                        t.Name.Contains("Script", StringComparison.OrdinalIgnoreCase) ||
                        t.Name.Contains("Skeleton", StringComparison.OrdinalIgnoreCase) ||
                        t.Name.Contains("Havok", StringComparison.OrdinalIgnoreCase) ||
                        t.Name.Contains("Chunk", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.FullName).ToArray();

        // 1.4.1.13: direct strongly-typed IDatFile call. The reflection helper in 1.4.1.13
        // could not see an interface method on the runtime DAT implementation and returned null.
        var animationBytes = SafeGetAnim(animation);

        var animationTypeCandidates = sdkTypes
            .Where(t => !t.IsAbstract && !t.IsInterface &&
                (t.Name.Equals("Animator", StringComparison.OrdinalIgnoreCase) ||
                 t.Name.Contains("Animation", StringComparison.OrdinalIgnoreCase) ||
                 t.Name.Contains("Animator", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var animationCtorAttempts = TryParseMany(animationTypeCandidates, animation, animationBytes, maxDepth, 64);
        var animationFactoryAttempts = TryFactories(allSdkTypes, animationTypeCandidates, animation, animationBytes, maxDepth, 96);

        var wstates = new List<object>();
        foreach (var id in requestedWStates.Distinct())
        {
            var bytes = SafeGetGameLogic(id);
            if (bytes == null)
            {
                wstates.Add(new { id = $"0x{id:X8}", found = false, error = "Record was not returned by client_gamelogic.dat." });
                continue;
            }

            var stateTypes = allSdkTypes.Where(t => !t.IsAbstract && !t.IsInterface &&
                (t.Name.Contains("WState", StringComparison.OrdinalIgnoreCase) ||
                 t.Name.Equals("State", StringComparison.OrdinalIgnoreCase) ||
                 t.Name.EndsWith("State", StringComparison.OrdinalIgnoreCase))).ToArray();
            var ctorAttempts = TryParseMany(stateTypes, id, bytes, maxDepth, 48);
            var factoryAttempts = TryFactories(allSdkTypes, stateTypes, id, bytes, maxDepth, 64);
            wstates.Add(new
            {
                id = $"0x{id:X8}",
                found = true,
                byteLength = bytes.Length,
                firstBytes = Convert.ToHexString(bytes.AsSpan(0, Math.Min(1024, bytes.Length))),
                ascii = Ascii(bytes, 0, Math.Min(2048, bytes.Length)),
                alignedReferences = ExtractRefs(bytes).Take(512).ToArray(),
                sdkConstructorAttempts = ctorAttempts,
                sdkFactoryAttempts = factoryAttempts
            });
        }

        var animatorType = allSdkTypes.FirstOrDefault(t => t.FullName == "VoK.Sdk.Common.Animator");
        var animatorMethods = animatorType == null ? Array.Empty<object>() : DescribeMethods(animatorType).ToArray();
        var animDatMethods = DatSource.AnimDat == null ? Array.Empty<object>() : DescribeMethods(DatSource.AnimDat.GetType()).ToArray();
        var propertyMasterMethods = DatSource.PropertyMaster == null ? Array.Empty<object>() : DescribeMethods(DatSource.PropertyMaster.GetType()).ToArray();

        return Ok(new
        {
            version = "1.4.1.17",
            installPath = DatSource.GetDdoMainInstallPath(),
            animation = $"0x{animation:X8}",
            animationRecord = animationBytes == null ? null : new
            {
                byteLength = animationBytes.Length,
                firstBytes = Convert.ToHexString(animationBytes.AsSpan(0, Math.Min(2048, animationBytes.Length))),
                ascii = Ascii(animationBytes, 0, Math.Min(4096, animationBytes.Length)),
                alignedReferences = ExtractRefs(animationBytes).Take(1024).ToArray()
            },
            sdkTypeCatalog = sdkTypes.Select(DescribeType).ToArray(),
            animatorMethods,
            animDatRuntimeType = DatSource.AnimDat?.GetType().FullName,
            animDatMethods,
            propertyMasterRuntimeType = DatSource.PropertyMaster?.GetType().FullName,
            propertyMasterMethods,
            animationSdkConstructorAttempts = animationCtorAttempts,
            animationSdkFactoryAttempts = animationFactoryAttempts,
            wstates,
            guidance = new
            {
                note = "1.4.1.13 reads Anim/GameLogic records directly and tests both constructors and likely SDK parser/factory methods.",
                next = "Prioritize successful Animator/factory dumps. Compare Animator BoneCount/HavokChunk to the decoded 58-track clip and inspect WState references for state/animation selection metadata."
            }
        });
    }

    static object[] TryParseMany(Type[] types, uint id, byte[]? bytes, int maxDepth, int max)
    {
        if (bytes == null) return Array.Empty<object>();
        var results = new List<object>();
        foreach (var t in types.Take(max))
        {
            var ctors = t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(ConstructorText).ToArray();
            object? parsed = null; string? used = null; string? error = null;
            try
            {
                foreach (var c in t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .OrderByDescending(c => c.GetParameters().Length))
                {
                    if (!TryBuildArgs(c.GetParameters(), id, bytes, out var args, out var owned)) continue;
                    try { parsed = c.Invoke(args); used = ConstructorText(c); break; }
                    catch (TargetInvocationException tie) { error = tie.InnerException?.ToString() ?? tie.ToString(); }
                    catch (Exception ex) { error = ex.ToString(); }
                    finally { foreach (var d in owned) d.Dispose(); }
                }
                if (parsed == null && error == null) error = "No constructor signature could be supplied safely.";
            }
            catch (Exception ex) { error = ex.ToString(); }
            results.Add(new { type = t.FullName, constructors = ctors, success = parsed != null, constructorUsed = used, error, dump = parsed == null ? null : DumpObject(parsed, maxDepth, 128) });
        }
        return results.ToArray();
    }

    static object[] TryFactories(Type[] allTypes, Type[] targetTypes, uint id, byte[]? bytes, int maxDepth, int max)
    {
        if (bytes == null) return Array.Empty<object>();
        var target = new HashSet<Type>(targetTypes);
        var methods = allTypes
            .Where(t => t.FullName != null && (t.Name.Contains("Anim", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("Havok", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("Factory", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("Dat", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("Parser", StringComparison.OrdinalIgnoreCase)))
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Select(m => (type: t, method: m)))
            .Where(x => !x.method.IsGenericMethodDefinition && x.method.ReturnType != typeof(void) &&
                (target.Contains(x.method.ReturnType) || target.Any(tt => tt.IsAssignableFrom(x.method.ReturnType)) || x.method.ReturnType == typeof(object)) &&
                IsLikelyFactoryName(x.method.Name))
            .Take(max).ToArray();

        var results = new List<object>();
        foreach (var x in methods)
        {
            object? parsed = null; string? error = null;
            if (!TryBuildArgs(x.method.GetParameters(), id, bytes, out var args, out var owned))
            {
                results.Add(new { declaringType = x.type.FullName, method = MethodText(x.method), success = false, error = "Parameters not safely supplyable.", dump = (object?)null });
                continue;
            }
            try { parsed = x.method.Invoke(null, args); }
            catch (TargetInvocationException tie) { error = tie.InnerException?.ToString() ?? tie.ToString(); }
            catch (Exception ex) { error = ex.ToString(); }
            finally { foreach (var d in owned) d.Dispose(); }
            results.Add(new { declaringType = x.type.FullName, method = MethodText(x.method), success = parsed != null, error, dump = parsed == null ? null : DumpObject(parsed, maxDepth, 128) });
        }
        return results.ToArray();
    }

    static bool IsLikelyFactoryName(string n) => n.Contains("Parse", StringComparison.OrdinalIgnoreCase) || n.Contains("Read", StringComparison.OrdinalIgnoreCase) || n.Contains("Load", StringComparison.OrdinalIgnoreCase) || n.Contains("Create", StringComparison.OrdinalIgnoreCase) || n.Contains("Get", StringComparison.OrdinalIgnoreCase) || n.Contains("Build", StringComparison.OrdinalIgnoreCase);

    static bool TryBuildArgs(ParameterInfo[] p, uint id, byte[] bytes, out object?[] args, out List<IDisposable> owned)
    {
        args = new object?[p.Length]; owned = new List<IDisposable>();
        for (int i = 0; i < p.Length; i++)
        {
            var pt = p[i].ParameterType;
            if (pt == typeof(BinaryReader)) { var ms = new MemoryStream(bytes, false); var br = new BinaryReader(ms); args[i] = br; owned.Add(br); }
            else if (pt == typeof(Stream)) { var ms = new MemoryStream(bytes, false); args[i] = ms; owned.Add(ms); }
            else if (pt == typeof(byte[])) args[i] = bytes;
            else if (pt == typeof(uint)) args[i] = id;
            else if (pt == typeof(int) && (p[i].Name?.Contains("id", StringComparison.OrdinalIgnoreCase) == true)) args[i] = unchecked((int)id);
            else if (pt.IsInstanceOfType(DatSource.PropertyMaster)) args[i] = DatSource.PropertyMaster;
            else if (pt.IsInstanceOfType(DatSource.AnimDat)) args[i] = DatSource.AnimDat;
            else if (pt.IsInstanceOfType(DatSource.GameLogicDat)) args[i] = DatSource.GameLogicDat;
            else if (p[i].HasDefaultValue) args[i] = p[i].DefaultValue;
            else if (!pt.IsValueType || Nullable.GetUnderlyingType(pt) != null) args[i] = null;
            else { foreach (var d in owned) d.Dispose(); owned.Clear(); return false; }
        }
        return true;
    }

    static object DumpObject(object root, int maxDepth, int maxMembers)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return Dump(root, 0) ?? new { value = "<null>" };
        object? Dump(object? value, int depth)
        {
            if (value == null) return null;
            var t = value.GetType();
            if (value is string || value is char || value is bool || t.IsEnum || t.IsPrimitive || value is decimal) return value.ToString();
            if (value is byte[] ba) return new { type = "byte[]", length = ba.Length, firstBytes = Convert.ToHexString(ba.AsSpan(0, Math.Min(512, ba.Length))) };
            if (depth >= maxDepth) return new { type = t.FullName, value = SafeToString(value), truncated = true };
            if (!t.IsValueType && !visited.Add(value)) return new { type = t.FullName, cycle = true };
            if (value is IDictionary dict)
            {
                var rows = new List<object>(); int n = 0; foreach (DictionaryEntry e in dict) { if (n++ >= 96) break; rows.Add(new { key = Dump(e.Key, depth + 1), value = Dump(e.Value, depth + 1) }); }
                return new { type = t.FullName, count = dict.Count, entries = rows };
            }
            if (value is IEnumerable seq)
            {
                var rows = new List<object>(); int n = 0; foreach (var x in seq) { if (n++ >= 128) break; rows.Add(Dump(x, depth + 1) ?? "<null>"); }
                return new { type = t.FullName, items = rows, truncated = n > 128 };
            }
            var members = new Dictionary<string, object?>();
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) { if (members.Count >= maxMembers) break; try { members["field:" + f.Name] = Dump(f.GetValue(value), depth + 1); } catch (Exception ex) { members["field:" + f.Name] = "<error: " + ex.Message + ">"; } }
            foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) { if (members.Count >= maxMembers) break; if (!p.CanRead || p.GetIndexParameters().Length != 0) continue; try { members["prop:" + p.Name] = Dump(p.GetValue(value), depth + 1); } catch (Exception ex) { members["prop:" + p.Name] = "<error: " + ex.Message + ">"; } }
            return new { type = t.FullName, members };
        }
    }

    static object DescribeType(Type t) => new { name = t.FullName, isAbstract = t.IsAbstract, constructors = t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(ConstructorText).ToArray(), methods = DescribeMethods(t).Take(96).ToArray(), fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(f => $"{f.FieldType.Name} {f.Name}").Take(128).ToArray(), properties = t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(p => p.GetIndexParameters().Length == 0).Select(p => $"{p.PropertyType.Name} {p.Name}").Take(128).ToArray() };
    static IEnumerable<object> DescribeMethods(Type t) => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName).Select(m => (object)new { name = m.Name, isStatic = m.IsStatic, signature = MethodText(m), returnType = m.ReturnType.FullName });
    static string ConstructorText(ConstructorInfo c) => $"{c.DeclaringType?.Name}({string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name + (p.HasDefaultValue ? "=" + (p.DefaultValue ?? "null") : "")))})";
    static string MethodText(MethodInfo m) => $"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name + (p.HasDefaultValue ? "=" + (p.DefaultValue ?? "null") : "")))})";
    static IEnumerable<Type> SafeTypes(Assembly a) { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).Cast<Type>(); } catch { return Array.Empty<Type>(); } }
    static byte[]? SafeGetAnim(uint id) { try { return DatSource.AnimDat?.GetFileContents(id); } catch { return null; } }
    static byte[]? SafeGetGameLogic(uint id) { try { return DatSource.GameLogicDat?.GetFileContents(id); } catch { return null; } }

    static IEnumerable<object> ExtractRefs(byte[] b)
    {
        for (int off = 0; off + 4 <= b.Length; off += 4)
        {
            uint id = BitConverter.ToUInt32(b, off); var r = DatSource.IdRanges.FirstOrDefault(x => id >= x.Minimum && id <= x.Maximum);
            if (r != null) yield return new { offset = off, id = $"0x{id:X8}", type = r.Name };
        }
    }
    static string Ascii(byte[] b, int off, int len) { var chars = new char[len]; for (int i = 0; i < len; i++) { byte x = b[off + i]; chars[i] = x >= 32 && x <= 126 ? (char)x : '.'; } return new string(chars); }
    static string SafeToString(object o) { try { return o.ToString() ?? ""; } catch { return ""; } }
    static IEnumerable<uint> ParseIds(string? s) { if (string.IsNullOrWhiteSpace(s)) yield break; foreach (var part in s.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)) if (TryId(part, out var id)) yield return id; }
    static bool TryId(string s, out uint id) { s = s.Trim(); if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..]; return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id); }
}
