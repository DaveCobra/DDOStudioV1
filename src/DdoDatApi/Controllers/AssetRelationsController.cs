using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace DdoDatApi.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class AssetRelationsController : ControllerBase
{
    public sealed class TexturePropertyRef
    {
        public uint TextureId { get; set; }
        public string PropertyName { get; set; } = "";
        public uint PropertyId { get; set; }
    }

    public sealed class RelationIndex
    {
        public int Version { get; set; } = 1;
        public DateTime BuiltUtc { get; set; } = DateTime.UtcNow;
        public Dictionary<uint, List<uint>> TextureSurfaces { get; set; } = new();
        public Dictionary<uint, List<TexturePropertyRef>> ModifierTextures { get; set; } = new();
        public Dictionary<uint, List<uint>> InstanceModifiers { get; set; } = new();
        public Dictionary<uint, List<uint>> MeshMaterials { get; set; } = new();
        public Dictionary<uint, List<uint>> SetupMeshes { get; set; } = new();
    }

    static readonly object Gate = new();
    static RelationIndex? _index;
    static Task? _buildTask;
    static string _stage = "Not built";
    static string _lastError = "";
    static int _done;
    static int _total;

    static readonly Assembly Sdk = typeof(VoK.Sdk.Common.RenderMesh).Assembly;

    [HttpGet("status")]
    public IActionResult Status()
    {
        TryLoadCache();
        lock (Gate)
        {
            bool building = _buildTask != null && !_buildTask.IsCompleted;
            return Ok(new
            {
                ready = _index != null,
                building,
                stage = _stage,
                done = _done,
                total = _total,
                error = _lastError,
                builtUtc = _index?.BuiltUtc,
                counts = _index == null ? null : new
                {
                    textures = _index.TextureSurfaces.Count,
                    modifiers = _index.ModifierTextures.Count,
                    materials = _index.InstanceModifiers.Count,
                    meshes = _index.MeshMaterials.Count,
                    setups = _index.SetupMeshes.Count
                }
            });
        }
    }

    [HttpPost("build")]
    public IActionResult Build([FromQuery] bool force = false)
    {
        lock (Gate)
        {
            if (!force && _index != null) return Ok(new { started = false, ready = true });
            if (_buildTask != null && !_buildTask.IsCompleted) return Ok(new { started = false, building = true });
            _lastError = "";
            _done = 0;
            _total = 0;
            _stage = "Discovering DAT records";
            _buildTask = Task.Run(BuildIndex);
            return Ok(new { started = true });
        }
    }

    [HttpGet("texture/{id}")]
    public IActionResult Texture(string id)
    {
        if (!TryId(id, out var requestedId)) return BadRequest("Invalid texture/surface ID.");
        var idx = ReadyIndex();
        if (idx == null) return StatusCode(409, "Relationship index has not been built yet.");

        // The Texture Browser displays 0x41 RenderSurface IDs, while materials point to
        // 0x40 RenderTexture IDs. Accept either and bridge the two automatically.
        uint[] textureIds;
        if ((requestedId >> 24) == 0x41)
            textureIds = idx.TextureSurfaces.Where(kv => kv.Value.Contains(requestedId)).Select(kv => kv.Key).Distinct().ToArray();
        else
            textureIds = new[] { requestedId };

        var textureSet = textureIds.ToHashSet();
        var modifierHits = idx.ModifierTextures
            .Where(kv => kv.Value.Any(x => textureSet.Contains(x.TextureId)))
            .Select(kv => new
            {
                modifierId = kv.Key,
                properties = kv.Value.Where(x => textureSet.Contains(x.TextureId)).ToArray()
            }).ToList();

        var modifierIds = modifierHits.Select(x => x.modifierId).ToHashSet();
        var materialIds = idx.InstanceModifiers.Where(kv => kv.Value.Any(modifierIds.Contains)).Select(kv => kv.Key).Distinct().ToArray();
        var materialSet = materialIds.ToHashSet();
        var meshIds = idx.MeshMaterials.Where(kv => kv.Value.Any(materialSet.Contains)).Select(kv => kv.Key).Distinct().ToArray();
        var meshSet = meshIds.ToHashSet();
        var setupIds = idx.SetupMeshes.Where(kv => kv.Value.Any(meshSet.Contains)).Select(kv => kv.Key).Distinct().ToArray();

        var surfaces = textureIds.SelectMany(tid => idx.TextureSurfaces.TryGetValue(tid, out var ss) ? ss : Enumerable.Empty<uint>()).Distinct().ToArray();
        return Ok(new
        {
            requestedId,
            textureIds,
            surfaces,
            modifiers = modifierHits,
            materials = materialIds,
            meshes = meshIds,
            setups = setupIds
        });
    }

    [HttpGet("material/{id}")]
    public IActionResult Material(string id)
    {
        if (!TryId(id, out var materialId)) return BadRequest("Invalid material ID.");
        var idx = ReadyIndex();
        if (idx == null) return StatusCode(409, "Relationship index has not been built yet.");
        if (!idx.InstanceModifiers.TryGetValue(materialId, out var modifiers)) return NotFound();

        var props = modifiers.SelectMany(mid => idx.ModifierTextures.TryGetValue(mid, out var ps)
            ? ps.Select(p => (object)new { modifierId = mid, p.TextureId, p.PropertyName, p.PropertyId, surfaces = SurfaceIds(idx, p.TextureId) })
            : Enumerable.Empty<object>()).ToArray();
        var meshes = idx.MeshMaterials.Where(kv => kv.Value.Contains(materialId)).Select(kv => kv.Key).Distinct().ToArray();
        var meshSet = meshes.ToHashSet();
        var setups = idx.SetupMeshes.Where(kv => kv.Value.Any(meshSet.Contains)).Select(kv => kv.Key).Distinct().ToArray();
        return Ok(new { materialId, modifiers, properties = props, meshes, setups });
    }

    [HttpGet("setup/{id}")]
    public IActionResult Setup(string id)
    {
        if (!TryId(id, out var setupId)) return BadRequest("Invalid setup ID.");
        var idx = ReadyIndex();
        if (idx == null) return StatusCode(409, "Relationship index has not been built yet.");
        if (!idx.SetupMeshes.TryGetValue(setupId, out var meshes)) return NotFound();

        var materialIds = meshes.SelectMany(mid => idx.MeshMaterials.TryGetValue(mid, out var ms) ? ms : Enumerable.Empty<uint>()).Distinct().ToArray();
        var materials = new List<object>();
        foreach (var materialId in materialIds)
        {
            idx.InstanceModifiers.TryGetValue(materialId, out var mods);
            mods ??= new List<uint>();
            var props = mods.SelectMany(mod => idx.ModifierTextures.TryGetValue(mod, out var ps)
                ? ps.Select(p => (object)new { modifierId = mod, p.TextureId, p.PropertyName, p.PropertyId, surfaces = SurfaceIds(idx, p.TextureId) })
                : Enumerable.Empty<object>()).ToArray();
            materials.Add(new { materialId, modifiers = mods, properties = props });
        }
        return Ok(new { setupId, meshes, materials });
    }


    static uint[] SurfaceIds(RelationIndex idx, uint textureId)
        => idx.TextureSurfaces.TryGetValue(textureId, out var surfaces)
            ? surfaces.Distinct().ToArray()
            : Array.Empty<uint>();

    static RelationIndex? ReadyIndex()
    {
        TryLoadCache();
        lock (Gate) return _index;
    }

    static void BuildIndex()
    {
        try
        {
            SetStage("Discovering General DAT IDs", 0, 0);
            var generalIds = DiscoverIds(DatSource.GeneralDat, new byte[] { 0x04, 0x30, 0x31, 0x40 });
            SetStage("Discovering Mesh DAT IDs", 0, 0);
            var meshIds = DiscoverIds(DatSource.Mesh, new byte[] { 0x06 });

            var textureIds = generalIds.Where(x => (x >> 24) == 0x40).OrderBy(x => x).ToArray();
            var modifierIds = generalIds.Where(x => (x >> 24) == 0x30).OrderBy(x => x).ToArray();
            var instanceIds = generalIds.Where(x => (x >> 24) == 0x31).OrderBy(x => x).ToArray();
            var setupIds = generalIds.Where(x => (x >> 24) == 0x04).OrderBy(x => x).ToArray();
            var renderMeshIds = meshIds.Where(x => (x >> 24) == 0x06).OrderBy(x => x).ToArray();

            var idx = new RelationIndex { BuiltUtc = DateTime.UtcNow };
            int total = textureIds.Length + modifierIds.Length + instanceIds.Length + renderMeshIds.Length + setupIds.Length;
            int done = 0;

            SetStage("Reading RenderTextures", done, total);
            foreach (var id in textureIds)
            {
                try
                {
                    var o = ParseSdk("VoK.Sdk.Common.RenderTexture", DatSource.GeneralDat?.GetFileContents(id));
                    var s = Items(Get(o, "SurfaceDids")).Select(U).Where(x => x != 0).Distinct().ToList();
                    if (s.Count > 0) idx.TextureSurfaces[id] = s;
                }
                catch { }
                Update(++done, total);
            }

            SetStage("Reading MaterialModifiers", done, total);
            foreach (var id in modifierIds)
            {
                try
                {
                    var o = ParseSdk("VoK.Sdk.Common.MaterialModifier", DatSource.GeneralDat?.GetFileContents(id));
                    var props = new List<TexturePropertyRef>();
                    foreach (var p in Items(Get(o, "MaterialProperties")))
                    {
                        uint tid = U(Get(p, "TextureDid"));
                        if (tid == 0) continue;
                        props.Add(new TexturePropertyRef
                        {
                            TextureId = tid,
                            PropertyName = Convert.ToString(Get(p, "MaterialPropertyName"), CultureInfo.InvariantCulture) ?? "",
                            PropertyId = U(Get(p, "MaterialPropertyId"))
                        });
                    }
                    if (props.Count > 0) idx.ModifierTextures[id] = props;
                }
                catch { }
                Update(++done, total);
            }

            SetStage("Reading MaterialInstances", done, total);
            foreach (var id in instanceIds)
            {
                try
                {
                    var o = ParseSdk("VoK.Sdk.Common.MaterialInstance", DatSource.GeneralDat?.GetFileContents(id));
                    var mods = Items(Get(o, "Modifiers")).Select(U).Where(x => x != 0).Distinct().ToList();
                    if (mods.Count > 0) idx.InstanceModifiers[id] = mods;
                }
                catch { }
                Update(++done, total);
            }

            SetStage("Reading RenderMeshes", done, total);
            foreach (var id in renderMeshIds)
            {
                try
                {
                    var o = ParseSdk("VoK.Sdk.Common.RenderMesh", DatSource.Mesh?.GetFileContents(id));
                    var mats = Items(Get(o, "MaterialDids")).Select(U).Where(x => x != 0).Distinct().ToList();
                    if (mats.Count > 0) idx.MeshMaterials[id] = mats;
                }
                catch { }
                Update(++done, total);
            }

            SetStage("Reading Setups", done, total);
            foreach (var id in setupIds)
            {
                try
                {
                    var o = ParseSdk("VoK.Sdk.Common.Setup", DatSource.GeneralDat?.GetFileContents(id));
                    var meshes = Items(Get(o, "RenderMeshIds")).Select(U).Where(x => x != 0).Distinct().ToList();
                    if (meshes.Count > 0) idx.SetupMeshes[id] = meshes;
                }
                catch { }
                Update(++done, total);
            }

            SaveCache(idx);
            lock (Gate)
            {
                _index = idx;
                _stage = "Ready";
                _done = total;
                _total = total;
                _lastError = "";
            }
        }
        catch (Exception ex)
        {
            lock (Gate)
            {
                _stage = "Failed";
                _lastError = ex.ToString();
            }
        }
    }

    static void SetStage(string stage, int done, int total)
    {
        lock (Gate) { _stage = stage; _done = done; _total = total; }
    }

    static void Update(int done, int total)
    {
        lock (Gate) { _done = done; _total = total; }
    }

    static object ParseSdk(string typeName, byte[]? data)
    {
        if (data == null) throw new FileNotFoundException(typeName);
        var t = Sdk.GetType(typeName) ?? throw new Exception($"SDK type missing: {typeName}");
        using var br = new BinaryReader(new MemoryStream(data));
        return Construct(t, br);
    }

    static object Construct(Type t, params object?[] args)
    {
        foreach (var c in t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var p = c.GetParameters();
            if (p.Length != args.Length) continue;
            bool ok = true;
            for (int i = 0; i < p.Length; i++)
            {
                if (args[i] == null) continue;
                if (!p[i].ParameterType.IsAssignableFrom(args[i]!.GetType())) { ok = false; break; }
            }
            if (ok) return c.Invoke(args);
        }
        throw new MissingMethodException($"No usable constructor for {t.FullName}");
    }

    static object? Get(object? o, string name)
    {
        if (o == null) return null;
        var t = o.GetType();
        try
        {
            return t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o)
                ?? t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o);
        }
        catch { return null; }
    }

    static IEnumerable<object> Items(object? o)
    {
        if (o is not IEnumerable e) yield break;
        foreach (var x in e) if (x != null) yield return x;
    }

    static uint U(object? o)
    {
        if (o == null) return 0;
        try { return Convert.ToUInt32(o, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    static HashSet<uint> DiscoverIds(object? root, byte[] prefixes)
    {
        var dst = new HashSet<uint>();
        if (root == null) return dst;
        var prefixSet = prefixes.ToHashSet();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Walk(root, dst, prefixSet, visited, 0);
        return dst;
    }

    static void Walk(object? value, HashSet<uint> dst, HashSet<byte> prefixes, HashSet<object> visited, int depth)
    {
        if (value == null || depth > 5) return;
        var t = value.GetType();
        if (t.IsPrimitive || value is string || value is byte[] || value is Type) return;
        if (!t.IsValueType && !visited.Add(value)) return;

        if (TryNumeric(value, out var direct)) AddIfWanted(direct, dst, prefixes);

        if (value is IDictionary dict)
        {
            int n = 0;
            foreach (DictionaryEntry e in dict)
            {
                if (++n > 2_000_000) break;
                if (TryNumeric(e.Key, out var k)) AddIfWanted(k, dst, prefixes);
                Walk(e.Value, dst, prefixes, visited, depth + 1);
            }
            return;
        }

        if (value is IEnumerable seq)
        {
            int n = 0;
            foreach (var item in seq)
            {
                if (++n > 2_000_000) break;
                if (TryNumeric(item, out var k)) AddIfWanted(k, dst, prefixes);
                else if (item != null)
                {
                    foreach (var name in new[] { "Id", "ID", "Did", "DID", "FileId", "FileID", "Key" })
                    {
                        var mv = Get(item, name);
                        if (TryNumeric(mv, out var mid)) AddIfWanted(mid, dst, prefixes);
                    }
                    if (depth < 3) Walk(item, dst, prefixes, visited, depth + 1);
                }
            }
            return;
        }

        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            try { Walk(f.GetValue(value), dst, prefixes, visited, depth + 1); } catch { }
        foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (p.GetIndexParameters().Length != 0 || !p.CanRead) continue;
            try { Walk(p.GetValue(value), dst, prefixes, visited, depth + 1); } catch { }
        }
    }

    static void AddIfWanted(uint id, HashSet<uint> dst, HashSet<byte> prefixes)
    {
        if (prefixes.Contains((byte)(id >> 24))) dst.Add(id);
    }

    static bool TryNumeric(object? o, out uint value)
    {
        value = 0;
        try
        {
            if (o == null) return false;
            if (o is uint u) { value = u; return true; }
            if (o is int i && i >= 0) { value = (uint)i; return true; }
            if (o is long l && l >= 0 && l <= uint.MaxValue) { value = (uint)l; return true; }
            if (o is ulong ul && ul <= uint.MaxValue) { value = (uint)ul; return true; }
            if (o is ushort us) { value = us; return true; }
            return false;
        }
        catch { return false; }
    }

    static bool TryId(string s, out uint id)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
    }

    static string CachePath()
    {
        var configured = Environment.GetEnvironmentVariable("DDO_ASSET_STUDIO_DATA");
        var root = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDO Asset Studio");
        var dir = Path.Combine(root, "BackendCache");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "asset-relations-v1.json");
    }

    static void SaveCache(RelationIndex idx)
    {
        var path = CachePath();
        var tmp = path + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(idx));
        System.IO.File.Move(tmp, path, true);
    }

    static void TryLoadCache()
    {
        lock (Gate)
        {
            if (_index != null) return;
            try
            {
                var path = CachePath();
                if (!System.IO.File.Exists(path)) return;
                var loaded = JsonSerializer.Deserialize<RelationIndex>(System.IO.File.ReadAllText(path));
                if (loaded == null || loaded.Version != 1) return;
                _index = loaded;
                _stage = "Ready (cached)";
                _lastError = "";
            }
            catch { }
        }
    }
}
