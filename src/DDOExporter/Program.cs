using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoK.Sdk.Dat;
using VoK.Sdk.Exporters;
using VoK.Sdk.Imaging;

static class R
{
    public static object? Get(object? o, string name)
    {
        if (o == null) return null;
        var t = o.GetType();
        return t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o)
            ?? t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o);
    }

    public static IEnumerable<object> Items(object? o)
    {
        if (o is not IEnumerable e) yield break;
        foreach (var x in e) if (x != null) yield return x;
    }

    public static int I(object? o) => o == null ? 0 : Convert.ToInt32(o, CultureInfo.InvariantCulture);
    public static uint U(object? o) => o == null ? 0u : Convert.ToUInt32(o, CultureInfo.InvariantCulture);
    public static float F(object? o) => o == null ? 0f : Convert.ToSingle(o, CultureInfo.InvariantCulture);

    public static Vector3 V3(object? o)
    {
        if (o is Vector3 v) return v;
        return new Vector3(F(Get(o, "X")), F(Get(o, "Y")), F(Get(o, "Z")));
    }

    public static object Construct(Type t, params object?[] args)
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
}

sealed class BinBuilder
{
    readonly MemoryStream ms = new();

    public int Position => checked((int)ms.Position);

    public void Align4()
    {
        while ((ms.Position & 3) != 0) ms.WriteByte(0);
    }

    public (int offset, int length) Add(Action<BinaryWriter> write)
    {
        Align4();
        int off = Position;
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        write(bw);
        bw.Flush();
        return (off, Position - off);
    }

    public byte[] ToArray()
    {
        Align4();
        return ms.ToArray();
    }
}

sealed record JointData(string Name, int Parent, Vector3 Translation, Quaternion Rotation, Vector3 Scale);

/// <summary>
/// Render state a material declares about itself. Every field is nullable because "the material
/// never mentioned it" and "the material said false" are different things, and only the first one
/// should fall back to a default.
///
/// These used to be guessed. doubleSided was hardcoded false and alphaMode came from scanning the
/// decoded texture for a pixel with alpha &lt; 250 - which gets Demise's rune band wrong, because the
/// band declares AlphaTest with AlphaBlendPass off (a cutout) and the scan called it BLEND. The
/// material has said so all along; nothing had read it.
/// </summary>
sealed class MaterialRenderState
{
    public bool? DoubleSided { get; set; }
    public bool? AlphaTest { get; set; }
    public float? AlphaTestRef { get; set; }
    public bool? AlphaBlendPass { get; set; }
    public bool? DepthWrite { get; set; }
    public bool? DepthTest { get; set; }
    public bool? WrapU { get; set; }
    public bool? WrapV { get; set; }

    /// <summary>
    /// glTF alpha mode for what the material declared, or null when it declared nothing and the
    /// caller should keep its own default. AlphaBlendPass means the material is drawn in the
    /// translucent pass; AlphaTest means a cutout, which glTF spells MASK.
    /// </summary>
    public string? GltfAlphaMode =>
        AlphaBlendPass == true ? "BLEND" :
        AlphaTest == true ? "MASK" :
        (AlphaBlendPass.HasValue || AlphaTest.HasValue) ? "OPAQUE" : null;

    public string Describe()
    {
        var parts = new List<string>();
        if (DoubleSided.HasValue) parts.Add($"DoubleSided={DoubleSided}");
        if (AlphaTest.HasValue) parts.Add($"AlphaTest={AlphaTest}");
        if (AlphaTestRef.HasValue) parts.Add($"AlphaTestRef={AlphaTestRef:0.###}");
        if (AlphaBlendPass.HasValue) parts.Add($"AlphaBlendPass={AlphaBlendPass}");
        if (DepthWrite.HasValue) parts.Add($"DepthWrite={DepthWrite}");
        if (DepthTest.HasValue) parts.Add($"DepthTest={DepthTest}");
        if (WrapU.HasValue || WrapV.HasValue) parts.Add($"Wrap={WrapU}/{WrapV}");
        return parts.Count == 0 ? "(nothing declared)" : string.Join(", ", parts);
    }
}

sealed class GltfBuilder
{
    readonly BinBuilder bin = new();
    readonly JsonArray bufferViews = new();
    readonly JsonArray accessors = new();
    readonly JsonArray meshes = new();
    readonly JsonArray nodes = new();
    readonly JsonArray skins = new();
    readonly JsonArray materials = new();
    readonly JsonArray images = new();
    readonly JsonArray textures = new();
    readonly JsonArray samplers = new();
    readonly JsonArray animations = new();

    public JsonArray Nodes => nodes;
    public JsonArray Meshes => meshes;
    public JsonArray Skins => skins;

    int AddBufferView(int offset, int length, int? target = null)
    {
        var j = new JsonObject
        {
            ["buffer"] = 0,
            ["byteOffset"] = offset,
            ["byteLength"] = length
        };
        if (target.HasValue) j["target"] = target.Value;
        bufferViews.Add(j);
        return bufferViews.Count - 1;
    }

    int AddAccessor(int view, int componentType, int count, string type, float[]? min = null, float[]? max = null)
    {
        var j = new JsonObject
        {
            ["bufferView"] = view,
            ["byteOffset"] = 0,
            ["componentType"] = componentType,
            ["count"] = count,
            ["type"] = type
        };
        if (min != null) j["min"] = new JsonArray(min.Select(x => (JsonNode?)x).ToArray());
        if (max != null) j["max"] = new JsonArray(max.Select(x => (JsonNode?)x).ToArray());
        accessors.Add(j);
        return accessors.Count - 1;
    }

    public int AddPositions(IReadOnlyList<Vector3> xs)
    {
        var r = bin.Add(bw => { foreach (var v in xs) { bw.Write(v.X); bw.Write(v.Y); bw.Write(v.Z); } });
        int view = AddBufferView(r.offset, r.length, 34962);
        Vector3 mn = new(float.PositiveInfinity), mx = new(float.NegativeInfinity);
        foreach (var v in xs) { mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); }
        return AddAccessor(view, 5126, xs.Count, "VEC3",
            new[] { mn.X, mn.Y, mn.Z }, new[] { mx.X, mx.Y, mx.Z });
    }

    public int AddNormals(IReadOnlyList<Vector3> xs)
    {
        var r = bin.Add(bw => { foreach (var v in xs) { bw.Write(v.X); bw.Write(v.Y); bw.Write(v.Z); } });
        return AddAccessor(AddBufferView(r.offset, r.length, 34962), 5126, xs.Count, "VEC3");
    }

    public int AddUVs(IReadOnlyList<Vector2> xs)
    {
        var r = bin.Add(bw => { foreach (var v in xs) { bw.Write(v.X); bw.Write(v.Y); } });
        return AddAccessor(AddBufferView(r.offset, r.length, 34962), 5126, xs.Count, "VEC2");
    }

    public int AddJoints(IReadOnlyList<ushort[]> xs)
    {
        var r = bin.Add(bw =>
        {
            foreach (var a in xs)
                for (int i = 0; i < 4; i++) bw.Write(i < a.Length ? a[i] : (ushort)0);
        });
        return AddAccessor(AddBufferView(r.offset, r.length, 34962), 5123, xs.Count, "VEC4");
    }

    public int AddWeights(IReadOnlyList<float[]> xs)
    {
        var r = bin.Add(bw =>
        {
            foreach (var a in xs)
                for (int i = 0; i < 4; i++) bw.Write(i < a.Length ? a[i] : 0f);
        });
        return AddAccessor(AddBufferView(r.offset, r.length, 34962), 5126, xs.Count, "VEC4");
    }

    /// <summary>Per-vertex RGBA as COLOR_0.</summary>
    public int AddColors(IReadOnlyList<float[]> xs)
    {
        var r = bin.Add(bw =>
        {
            foreach (var c in xs)
                for (int i = 0; i < 4; i++) bw.Write(i < c.Length ? c[i] : 1f);
        });
        return AddAccessor(AddBufferView(r.offset, r.length, 34962), 5126, xs.Count, "VEC4");
    }

    public int AddIndices(IReadOnlyList<uint> xs)
    {
        var r = bin.Add(bw => { foreach (var x in xs) bw.Write(x); });
        return AddAccessor(AddBufferView(r.offset, r.length, 34963), 5125, xs.Count, "SCALAR",
            new[] { 0f }, new[] { xs.Count == 0 ? 0f : (float)xs.Max() });
    }

    public int AddMatrices(IReadOnlyList<Matrix4x4> xs)
    {
        var r = bin.Add(bw =>
        {
            foreach (var m in xs)
            {
                // System.Numerics uses row-vector matrices. glTF uses column vectors and
                // column-major serialization. Serializing System.Numerics rows in order
                // produces the column-major bytes of the equivalent transposed glTF matrix.
                bw.Write(m.M11); bw.Write(m.M12); bw.Write(m.M13); bw.Write(m.M14);
                bw.Write(m.M21); bw.Write(m.M22); bw.Write(m.M23); bw.Write(m.M24);
                bw.Write(m.M31); bw.Write(m.M32); bw.Write(m.M33); bw.Write(m.M34);
                bw.Write(m.M41); bw.Write(m.M42); bw.Write(m.M43); bw.Write(m.M44);
            }
        });
        return AddAccessor(AddBufferView(r.offset, r.length), 5126, xs.Count, "MAT4");
    }

    int AddFloatScalars(IReadOnlyList<float> xs)
    {
        var r = bin.Add(bw => { foreach (var x in xs) bw.Write(x); });
        float mn = xs.Count == 0 ? 0 : xs.Min();
        float mx = xs.Count == 0 ? 0 : xs.Max();
        return AddAccessor(AddBufferView(r.offset, r.length), 5126, xs.Count, "SCALAR", new[] { mn }, new[] { mx });
    }

    int AddAnimationVec3(IReadOnlyList<Vector3> xs)
    {
        var r = bin.Add(bw => { foreach (var v in xs) { bw.Write(v.X); bw.Write(v.Y); bw.Write(v.Z); } });
        return AddAccessor(AddBufferView(r.offset, r.length), 5126, xs.Count, "VEC3");
    }

    int AddAnimationQuat(IReadOnlyList<Quaternion> xs)
    {
        var r = bin.Add(bw => { foreach (var q0 in xs) { var q = q0.LengthSquared() > 1e-12f ? Quaternion.Normalize(q0) : Quaternion.Identity; bw.Write(q.X); bw.Write(q.Y); bw.Write(q.Z); bw.Write(q.W); } });
        return AddAccessor(AddBufferView(r.offset, r.length), 5126, xs.Count, "VEC4");
    }

    public void AddAnimationClip(string name, IReadOnlyList<float> times, IReadOnlyList<Vector3[]> translations, IReadOnlyList<Quaternion[]> rotations, IReadOnlyList<Vector3[]> scales)
    {
        if (times.Count == 0 || translations.Count == 0) return;
        int timeAccessor = AddFloatScalars(times);
        var animSamplers = new JsonArray();
        var channels = new JsonArray();

        for (int joint = 0; joint < translations.Count; joint++)
        {
            void AddChannel(int output, string path)
            {
                int samplerIndex = animSamplers.Count;
                animSamplers.Add(new JsonObject
                {
                    ["input"] = timeAccessor,
                    ["output"] = output,
                    ["interpolation"] = "LINEAR"
                });
                channels.Add(new JsonObject
                {
                    ["sampler"] = samplerIndex,
                    ["target"] = new JsonObject { ["node"] = joint, ["path"] = path }
                });
            }

            AddChannel(AddAnimationVec3(translations[joint]), "translation");
            AddChannel(AddAnimationQuat(rotations[joint]), "rotation");
            AddChannel(AddAnimationVec3(scales[joint]), "scale");
        }

        animations.Add(new JsonObject
        {
            ["name"] = name,
            ["samplers"] = animSamplers,
            ["channels"] = channels
        });
    }

    public int AddMaterial(string name)
    {
        materials.Add(new JsonObject
        {
            ["name"] = name,
            ["pbrMetallicRoughness"] = new JsonObject
            {
                ["baseColorFactor"] = new JsonArray(0.75, 0.75, 0.75, 1.0),
                ["metallicFactor"] = 0.0,
                ["roughnessFactor"] = 0.8
            }
        });
        return materials.Count - 1;
    }

    int AddEmbeddedPngTexture(string name, byte[] png)
    {
        var r = bin.Add(bw => bw.Write(png));
        int view = AddBufferView(r.offset, r.length);

        images.Add(new JsonObject
        {
            ["name"] = name + "_Image",
            ["bufferView"] = view,
            ["mimeType"] = "image/png"
        });
        int imageIndex = images.Count - 1;

        if (samplers.Count == 0)
        {
            samplers.Add(new JsonObject
            {
                ["magFilter"] = 9729,
                ["minFilter"] = 9987,
                ["wrapS"] = 10497,
                ["wrapT"] = 10497
            });
        }

        textures.Add(new JsonObject
        {
            ["name"] = name + "_Texture",
            ["sampler"] = 0,
            ["source"] = imageIndex
        });
        return textures.Count - 1;
    }

    public int AddPngMaterial(string name, byte[] png)
        => AddPngMaterial(name, png, null, null);

    public int AddPngMaterial(string name, byte[]? diffusePng, byte[]? normalPng, IReadOnlyDictionary<string, uint>? ddoTextureIds)
        => AddPngMaterial(name, diffusePng, normalPng, null, false, ddoTextureIds);

    public int AddPngMaterial(string name, byte[]? diffusePng, byte[]? normalPng, byte[]? emissivePng, bool alphaBlend, IReadOnlyDictionary<string, uint>? ddoTextureIds)
        => AddPngMaterial(name, diffusePng, normalPng, emissivePng, alphaBlend, false, ddoTextureIds);

    public int AddPngMaterial(string name, byte[]? diffusePng, byte[]? normalPng, byte[]? emissivePng, bool alphaBlend, bool unlitDecal, IReadOnlyDictionary<string, uint>? ddoTextureIds, MaterialRenderState? state = null)
    {
        int diffuseTexture = diffusePng == null ? -1 : AddEmbeddedPngTexture(name + "_Diffuse", diffusePng);
        int normalTexture = normalPng == null ? -1 : AddEmbeddedPngTexture(name + "_Normal", normalPng);
        int emissiveTexture = emissivePng == null ? -1 : AddEmbeddedPngTexture(name + "_Emissive", emissivePng);

        // An unlit decal keeps the texture's alpha for its shape but contributes no lit colour: scene
        // lighting must not touch it. Demise's rune band is one - at the studio preset's ~4.4x light its
        // (252,176,27) clipped straight to flat yellow, losing the whole red-to-yellow gradient, because
        // the client never lights this material either. Its colour comes from the emissive map instead.
        var pbr = new JsonObject
        {
            ["baseColorFactor"] = unlitDecal ? new JsonArray(0.0, 0.0, 0.0, 1.0) : new JsonArray(1.0, 1.0, 1.0, 1.0),
            ["metallicFactor"] = 0.0,
            ["roughnessFactor"] = 0.8
        };
        if (diffuseTexture >= 0)
            pbr["baseColorTexture"] = new JsonObject { ["index"] = diffuseTexture };

        var mat = new JsonObject
        {
            ["name"] = name,
            // Both of these come from the material's own DoubleSided / AlphaTest / AlphaBlendPass
            // properties when it declares them, and only fall back to the old guesses when it does not.
            //
            // The fallbacks are what used to run unconditionally. Single-sided matches the client: every
            // draw in a RenderDoc capture used CullMode = BACK (3), and exporting doubleSided drew the
            // back faces of thin ribbon geometry - Demise's rune band wraps its blade edge, so its far
            // side showed just outside the blade's front silhouette and the runes appeared to run off the
            // model. The alpha fallback scans the decoded texture for real transparency, which is a
            // guess: it cannot tell a cutout from a blend, and it called Demise's band BLEND when the
            // material says AlphaTest with AlphaBlendPass off.
            ["doubleSided"] = state?.DoubleSided ?? false,
            ["alphaMode"] = state?.GltfAlphaMode ?? (alphaBlend ? "BLEND" : "OPAQUE"),
            ["pbrMetallicRoughness"] = pbr
        };
        // glTF's default cutoff is 0.5; the material carries its own reference as a waveform, so use its
        // value at rest rather than the default when it has one.
        if ((string?)mat["alphaMode"] == "MASK" && state?.AlphaTestRef is float cutoff && cutoff > 0f)
            mat["alphaCutoff"] = (double)cutoff;
        if (normalTexture >= 0)
            mat["normalTexture"] = new JsonObject { ["index"] = normalTexture, ["scale"] = 1.0 };
        if (emissiveTexture >= 0)
        {
            mat["emissiveTexture"] = new JsonObject { ["index"] = emissiveTexture };
            mat["emissiveFactor"] = new JsonArray(1.0, 1.0, 1.0);
        }

        var extras = new JsonObject();
        if (ddoTextureIds != null && ddoTextureIds.Count > 0)
        {
            var ddo = new JsonObject();
            foreach (var kv in ddoTextureIds.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                ddo[kv.Key] = $"0x{kv.Value:X8}";
            extras["ddoTextures"] = ddo;
        }
        if (unlitDecal)
        {
            // glTF has no additive blend mode, so flag it for the viewer. Measured against an in-game
            // screenshot: the rune band's brightest pixels are (251,234,114) over metal at (95,92,81).
            // Alpha-blending the decal predicts (216,178,44) - too dark and too saturated - while adding
            // it predicts (255,250,107). The client draws these in its effect pass (AlphaBlendPass=1,
            // DepthWrite=0), which is additive, and that is what makes the runes read as glowing.
            extras["ddoAdditive"] = true;
        }
        if (extras.Count > 0)
            mat["extras"] = extras;

        materials.Add(mat);
        return materials.Count - 1;
    }

    public int AddMesh(string name, JsonArray primitives)
    {
        meshes.Add(new JsonObject { ["name"] = name, ["primitives"] = primitives });
        return meshes.Count - 1;
    }

    public int AddNode(JsonObject node)
    {
        nodes.Add(node);
        return nodes.Count - 1;
    }

    public int AddSkin(JsonObject skin)
    {
        skins.Add(skin);
        return skins.Count - 1;
    }

    public void WriteGlb(string path, IReadOnlyList<int> sceneRoots)
    {
        byte[] binBytes = bin.ToArray();

        // DDO model coordinates are Z-up. glTF is Y-up.
        // Put every original scene root (skeleton + mesh nodes) under one common
        // -90 degree X-axis conversion node. Because both the skinned meshes and
        // their joints share this parent, the skin bind relationship is preserved.
        const float qx = -0.7071067811865476f;
        const float qw =  0.7071067811865476f;
        nodes.Add(new JsonObject
        {
            ["name"] = "DDO_Zup_to_glTF_Yup",
            ["rotation"] = new JsonArray(qx, 0.0f, 0.0f, qw),
            ["children"] = new JsonArray(sceneRoots.Select(x => (JsonNode?)x).ToArray())
        });
        int orientationRoot = nodes.Count - 1;

        var root = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "DDOGlbExporter-1.4.1-MaterialPipeline" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject
            {
                ["name"] = "DDO Scene",
                ["nodes"] = new JsonArray((JsonNode?)orientationRoot)
            }),
            ["nodes"] = nodes,
            ["meshes"] = meshes,
            ["skins"] = skins,
            ["materials"] = materials,
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = binBytes.Length }),
            ["bufferViews"] = bufferViews,
            ["accessors"] = accessors
        };

        if (images.Count > 0) root["images"] = images;
        if (textures.Count > 0) root["textures"] = textures;
        if (samplers.Count > 0) root["samplers"] = samplers;
        if (animations.Count > 0) root["animations"] = animations;

        byte[] json = Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        int jsonPad = (4 - (json.Length & 3)) & 3;
        int binPad = (4 - (binBytes.Length & 3)) & 3;
        int total = 12 + 8 + json.Length + jsonPad + 8 + binBytes.Length + binPad;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        bw.Write(0x46546C67); // glTF
        bw.Write(2);
        bw.Write(total);

        bw.Write(json.Length + jsonPad);
        bw.Write(0x4E4F534A); // JSON
        bw.Write(json);
        for (int i = 0; i < jsonPad; i++) bw.Write((byte)0x20);

        bw.Write(binBytes.Length + binPad);
        bw.Write(0x004E4942); // BIN
        bw.Write(binBytes);
        for (int i = 0; i < binPad; i++) bw.Write((byte)0);
    }
}

class Program
{
    sealed class AppearanceMeshReplacement
    {
        public double Priority { get; init; }
        public uint MeshType { get; init; }
        public uint MeshDid { get; init; }
    }

    sealed class AppearanceMaterialMod
    {
        public double Priority { get; init; }
        public uint TemplateDid { get; init; }
        public uint ModifierDid { get; init; }
        public uint MeshType { get; init; }
        public uint MaterialType { get; init; }
    }

    sealed class AppearanceSetupReplacement
    {
        public double Priority { get; init; }
        public uint SetupDid { get; init; }
    }

    sealed class AppearancePlan
    {
        public string SourcePath { get; init; } = "";
        public int SelectorCount { get; init; }
        public List<AppearanceMeshReplacement> MeshReplacements { get; } = new();
        public List<AppearanceMaterialMod> MaterialMods { get; } = new();
        public List<AppearanceSetupReplacement> SetupReplacements { get; } = new();

        public static AppearancePlan? Load(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("resolvedAppearance", out var resolved) || resolved.ValueKind != JsonValueKind.Object)
                return null;

            var plan = new AppearancePlan
            {
                SourcePath = Path.GetFullPath(path),
                SelectorCount = resolved.TryGetProperty("selectorCount", out var sc) && sc.TryGetInt32(out var n) ? n : 0
            };

            if (resolved.TryGetProperty("meshReplacements", out var meshes) && meshes.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in meshes.EnumerateArray())
                {
                    uint did = JsonHex(e, "meshDid");
                    if (did == 0) continue;
                    plan.MeshReplacements.Add(new AppearanceMeshReplacement
                    {
                        Priority = JsonDouble(e, "priority"),
                        MeshType = JsonHex(e, "meshType"),
                        MeshDid = did
                    });
                }
            }

            if (resolved.TryGetProperty("materialMods", out var mats) && mats.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in mats.EnumerateArray())
                {
                    uint did = JsonHex(e, "modifierDid");
                    if (did == 0) continue;
                    plan.MaterialMods.Add(new AppearanceMaterialMod
                    {
                        Priority = JsonDouble(e, "priority"),
                        TemplateDid = JsonHex(e, "templateDid"),
                        ModifierDid = did,
                        MeshType = JsonHex(e, "meshType"),
                        MaterialType = JsonHex(e, "materialType")
                    });
                }
            }

            if (resolved.TryGetProperty("setupReplacements", out var setups) && setups.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in setups.EnumerateArray())
                {
                    uint did = JsonHex(e, "setupDid");
                    if (did == 0) continue;
                    plan.SetupReplacements.Add(new AppearanceSetupReplacement
                    {
                        Priority = JsonDouble(e, "priority"),
                        SetupDid = did
                    });
                }
            }
            return plan;
        }
    }

    static uint JsonHex(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var n)) return n;
        if (v.ValueKind != JsonValueKind.String) return 0;
        var s = (v.GetString() ?? "").Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n) ? n : 0;
    }

    static double JsonDouble(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return 0;
    }
    static Uri ResolveApiBase()
    {
        var configured = Environment.GetEnvironmentVariable("DDO_STUDIO_API_BASE");
        if (!string.IsNullOrWhiteSpace(configured) && Uri.TryCreate(configured, UriKind.Absolute, out var parsed))
        {
            var value = parsed.ToString();
            if (!value.EndsWith('/')) value += "/";
            return new Uri(value);
        }

        // Standalone/developer fallback. DDO Studio 1.7.2 supplies its private
        // per-instance backend URL through DDO_STUDIO_API_BASE.
        return new Uri("http://127.0.0.1:5138/");
    }

    // Still used for animation decoding only: AnimationCatalog/{id}/decode is backend work (Havok), not a
    // raw dat read. Records and textures no longer go over HTTP.
    static readonly HttpClient Http = new() { BaseAddress = ResolveApiBase() };
    static readonly Assembly Sdk = typeof(VoK.Sdk.Common.RenderMesh).Assembly;

    /// <summary>DDO install folder, supplied by the desktop app via ConfigureExporterEnvironment.</summary>
    static string ResolveDatFolder()
    {
        var path = Environment.GetEnvironmentVariable("DDO_INSTALL_PATH");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(Path.Combine(path, "client_general.dat")))
            throw new Exception("DDO_INSTALL_PATH is not set to a folder containing client_general.dat.");
        return path;
    }

    static readonly Lazy<string> DatFolder = new(ResolveDatFolder);

    /// <summary>
    /// RenderMaterial templates by id. A template is shared by every instance that references it and the
    /// records run to a megabyte, so a Setup with a dozen materials would otherwise re-parse the same
    /// one repeatedly.
    /// </summary>
    static readonly Dictionary<uint, object> TemplateCache = new();
    static readonly Lazy<IDatFile> GeneralDat = new(() => DatFactory.LoadDatFile(Path.Combine(DatFolder.Value, "client_general.dat")));
    static readonly Lazy<IDatFile> MeshDat = new(() => DatFactory.LoadDatFile(Path.Combine(DatFolder.Value, "client_mesh.dat")));

    /// <summary>
    /// Surface lookup, handed to the SDK. It already searches client_surface, client_local_English,
    /// client_highres and the highres aux packs, which is the whole point: an id's dat is the SDK's
    /// business, not ours.
    /// </summary>
    static readonly Lazy<IImageExporter> Images = new(() => ExporterFactory.GetImageExporter(DatFolder.Value));
    static readonly Lazy<VoK.Sdk.Properties.IPropertyMaster> PropMaster =
        new(() => VoK.Sdk.Properties.PropertyMasterFactory.GetPropertyMaster(VoK.Sdk.GameId.DDO, DatFolder.Value));

    /// <summary>
    /// Reads a record straight out of the dat files. The dat argument keeps the old numbering (13 = mesh,
    /// everything else general) purely so call sites did not all have to change; it is no longer an index
    /// into anything.
    /// </summary>
    static Task<byte[]> Raw(int dat, uint id)
    {
        var file = dat == 13 ? MeshDat.Value : GeneralDat.Value;
        if (!file.HasFile(id))
            throw new Exception($"0x{id:X8} is not present in {Path.GetFileName(file.DatFilePath)}");
        return Task.FromResult(file.GetFileContents(id));
    }

    static object ParseSetup(byte[] data)
    {
        var t = Sdk.GetType("VoK.Sdk.Common.Setup") ?? throw new Exception("Setup type missing");
        using var br = new BinaryReader(new MemoryStream(data));
        return R.Construct(t, br, null);
    }

    static object ParseMesh(byte[] data)
    {
        var t = Sdk.GetType("VoK.Sdk.Common.RenderMesh") ?? throw new Exception("RenderMesh type missing");
        using var br = new BinaryReader(new MemoryStream(data));
        return R.Construct(t, br);
    }

    static List<JointData> DecodeJoints(object setup)
    {
        object? havok = R.Get(setup, "HavokSetup");
        if (havok == null) return new List<JointData>();
        var names = R.Items(R.Get(havok, "BoneNames")).Select(x => x.ToString() ?? "").ToList();
        var parentsRaw = R.Items(R.Get(havok, "ParentIndices")).Select(R.I).ToList();
        var records = R.Items(R.Get(havok, "Bones")).ToList();

        int count = Math.Min(names.Count, Math.Min(parentsRaw.Count, records.Count / 2));
        if (count == 0) return new List<JointData>();

        var outJ = new List<JointData>(count);
        for (int i = 0; i < count; i++)
        {
            var a = records[i * 2];
            var b = records[i * 2 + 1];
            Vector3 av1 = R.V3(R.Get(a, "V1"));
            Vector3 av2 = R.V3(R.Get(a, "V2"));
            Vector3 bv1 = R.V3(R.Get(b, "V1"));
            Vector3 bv2 = R.V3(R.Get(b, "V2"));

            var q = new Quaternion(av2.Y, av2.Z, bv1.X, bv1.Y);
            if (q.LengthSquared() < 1e-12f) q = Quaternion.Identity;
            else q = Quaternion.Normalize(q);

            var s = new Vector3(bv1.Z, bv2.X, bv2.Y);
            if (Math.Abs(s.X) < 1e-8f && Math.Abs(s.Y) < 1e-8f && Math.Abs(s.Z) < 1e-8f)
                s = Vector3.One;

            int raw = parentsRaw[i];
            int parent = (raw == 3 || (raw & 1) != 0) ? -1 : raw / 2;
            if (parent == i || parent < -1 || parent >= count) parent = -1;

            string name = string.IsNullOrWhiteSpace(names[i]) ? $"Bone_{i:00}" : names[i];
            outJ.Add(new JointData(name, parent, av1, q, s));
        }
        return outJ;
    }

    static Matrix4x4 LocalMatrix(JointData j) =>
        Matrix4x4.CreateScale(j.Scale) *
        Matrix4x4.CreateFromQuaternion(j.Rotation) *
        Matrix4x4.CreateTranslation(j.Translation);

    static List<Matrix4x4> GlobalBind(IReadOnlyList<JointData> joints)
    {
        var g = Enumerable.Repeat(Matrix4x4.Identity, joints.Count).ToList();
        var done = new bool[joints.Count];

        Matrix4x4 Calc(int i, HashSet<int> stack)
        {
            if (done[i]) return g[i];
            if (!stack.Add(i)) throw new Exception($"Skeleton cycle involving joint {i}");
            var local = LocalMatrix(joints[i]);
            g[i] = joints[i].Parent >= 0 ? local * Calc(joints[i].Parent, stack) : local;
            done[i] = true;
            stack.Remove(i);
            return g[i];
        }

        for (int i = 0; i < joints.Count; i++) Calc(i, new HashSet<int>());
        return g;
    }

    static (ushort[] joints, float[] weights) DecodeSkin(object vertex, object vertexSet, int influences, int jointCount)
    {
        var ib = R.Items(R.Get(vertexSet, "InfluencedBones")).Select(R.I).ToList();
        var parsed = R.Items(R.Get(vertex, "Bones")).ToList();

        var joints = new ushort[4];
        var weights = new float[4];

        if (influences <= 0 || parsed.Count == 0)
        {
            weights[0] = 1;
            return (joints, weights);
        }

        // VoK.Sdk currently parses this stream as repeated [byte][float].
        // DDO actually stores [N index bytes][N float weights].
        // Reconstruct the original 5*N bytes from the SDK's parsed records,
        // then reinterpret them using the actual layout.
        byte[] raw = new byte[parsed.Count * 5];
        for (int i = 0; i < parsed.Count; i++)
        {
            raw[i * 5] = Convert.ToByte(R.Get(parsed[i], "Prefix") ?? 0);
            float f = R.F(R.Get(parsed[i], "FloatVal"));
            BitConverter.GetBytes(f).CopyTo(raw, i * 5 + 1);
        }

        int n = Math.Min(Math.Min(influences, parsed.Count), 4);
        float sum = 0;
        for (int i = 0; i < n; i++)
        {
            // The grouped byte is the actual logical DDO joint index.
            // InfluencedBones is NOT a palette for this stream; mapping through it
            // collapses valid limb joints (e.g. 20/21, 28/29) into unrelated bones.
            int actual = raw[i];
            if (actual < 0 || actual >= jointCount) actual = 0;
            joints[i] = (ushort)actual;

            int woff = influences + i * 4;
            float w = woff + 4 <= raw.Length ? BitConverter.ToSingle(raw, woff) : 0f;
            if (!float.IsFinite(w) || w < 0) w = 0;
            weights[i] = w;
            sum += w;
        }

        if (sum <= 1e-12f)
        {
            weights[0] = 1;
        }
        else if (Math.Abs(sum - 1f) > 1e-5f)
        {
            for (int i = 0; i < 4; i++) weights[i] /= sum;
        }

        return (joints, weights);
    }

    static uint TriField(object tri, string name)
    {
        var v = R.Get(tri, name) ?? R.Get(tri, name.ToUpperInvariant());
        return Convert.ToUInt32(v ?? 0, CultureInfo.InvariantCulture);
    }

    static object ParseSdk(string typeName, byte[] data)
    {
        var t = Sdk.GetType(typeName) ?? throw new Exception($"SDK type missing: {typeName}");
        using var br = new BinaryReader(new MemoryStream(data));
        return R.Construct(t, br);
    }

    static string ScalarMembers(object o)
    {
        try
        {
            var parts = new List<string>();
            var t = o.GetType();
            foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                object? v; try { v = p.GetValue(o); } catch { continue; }
                if (v == null || v is IEnumerable && v is not string) continue;
                if (v.GetType().IsPrimitive || v is string || v is Enum || v is decimal) parts.Add($"{p.Name}={v}");
            }
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object? v; try { v = f.GetValue(o); } catch { continue; }
                if (v == null || v is IEnumerable && v is not string) continue;
                if (v.GetType().IsPrimitive || v is string || v is Enum || v is decimal) parts.Add($"{f.Name}={v}");
            }
            return string.Join(", ", parts.Distinct());
        }
        catch { return ""; }
    }

    sealed class MaterialTextureSet
    {
        public Dictionary<string, uint> ByName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public MaterialRenderState State { get; } = new();
        public uint? FirstTexture { get; set; }
        public uint? Diffuse => Find("DiffuseMap", "BaseColorMap", "ColorMap", "AlbedoMap");
        public uint? Normal => Find("NormalMap", "NormalTexture", "BumpMap");

        uint? Find(params string[] names)
        {
            foreach (var n in names)
                if (ByName.TryGetValue(n, out var id) && id != 0) return id;
            return null;
        }
    }

    /// <summary>
    /// Folds one MaterialModifier's property list into <paramref name="result"/>, last write winning.
    ///
    /// Used for the RenderMaterial template's own properties and for every modifier layered on top of
    /// it, because the client treats them the same way: MaterialProperty::Apply writes a property onto
    /// the material whether it arrived with the template or with an override.
    /// </summary>
    static void ApplyMaterialProperties(object source, MaterialTextureSet result, bool verbose)
    {
        int pi = 0;
        foreach (var prop in R.Items(R.Get(source, "MaterialProperties")))
        {
            uint tid = 0;
            try { tid = R.U(R.Get(prop, "TextureDid")); } catch { }
            string propertyName = "";
            try { propertyName = Convert.ToString(R.Get(prop, "MaterialPropertyName")) ?? ""; } catch { }
            if (verbose)
                Console.WriteLine($"        property {pi++}: TextureDid=0x{tid:X8}; {ScalarMembers(prop)}");

            if (tid != 0)
            {
                result.FirstTexture ??= tid;
                var textureName = string.IsNullOrWhiteSpace(propertyName) ? $"Texture_{tid:X8}" : propertyName;
                // APR modifiers intentionally override earlier/base material values.
                result.ByName[textureName] = tid;
            }

            bool? boolValue = null;
            try { if (R.Get(prop, "BoolId") is object b && b != null) boolValue = R.U(b) != 0; } catch { }

            float? waveBase = null;
            try { if (R.Get(prop, "WaveForm") is object w && w != null) waveBase = Convert.ToSingle(R.Get(w, "Base")); } catch { }

            var state = result.State;
            switch (propertyName)
            {
                case "DoubleSided": state.DoubleSided = boolValue ?? state.DoubleSided; break;
                case "AlphaTest": state.AlphaTest = boolValue ?? state.AlphaTest; break;
                case "AlphaBlendPass": state.AlphaBlendPass = boolValue ?? state.AlphaBlendPass; break;
                case "DepthWrite": state.DepthWrite = boolValue ?? state.DepthWrite; break;
                case "DepthTest": state.DepthTest = boolValue ?? state.DepthTest; break;
                case "DiffuseMapWrapU": state.WrapU = boolValue ?? state.WrapU; break;
                case "DiffuseMapWrapV": state.WrapV = boolValue ?? state.WrapV; break;
                case "AlphaTestRef": state.AlphaTestRef = waveBase ?? state.AlphaTestRef; break;
            }
        }
    }

    static async Task<MaterialTextureSet> ResolveMaterialTextures(uint materialInstanceId, uint meshType, AppearancePlan? appearance)
    {
        var result = new MaterialTextureSet();
        try
        {
            var inst = ParseSdk("VoK.Sdk.Common.MaterialInstance", await Raw(6, materialInstanceId));

            // The RenderMaterial template holds the material's defaults - its textures, its colours and
            // the render state it declares about itself - and the modifiers below only override what they
            // mention. Applying the template first is what the client does; skipping it used to lose any
            // texture no modifier happened to repeat, and left every render-state question to be guessed.
            uint templateDid = 0;
            try { templateDid = R.U(R.Get(inst, "RenderMaterialDid")); } catch { }
            object? template = null;
            if (templateDid != 0)
            {
                try
                {
                    template = TemplateCache.TryGetValue(templateDid, out var cached)
                        ? cached
                        : TemplateCache[templateDid] = VoK.Sdk.Common.RenderMaterial.Load(GeneralDat.Value, templateDid, PropMaster.Value);
                }
                catch (Exception ex) { Console.WriteLine($"    template 0x{templateDid:X8}: {ex.Message}"); }
            }

            if (template != null)
            {
                var templateProps = R.Get(template, "Properties");
                if (templateProps != null)
                    ApplyMaterialProperties(templateProps, result, verbose: false);
            }

            if (Environment.GetEnvironmentVariable("DDO_DUMP_TEMPLATE") == "1")
            {
                uint rmDid = templateDid;
                if (rmDid != 0 && template != null)
                {
                    try
                    {
                        var rm = template;
                        var layers = R.Items(R.Get(rm, "Layers")).ToList();
                        Console.WriteLine($"    TEMPLATE 0x{rmDid:X8}: {layers.Count} layer(s)");
                        int shaders = 0, dxbc = 0, stages = 0, mods = 0;
                        foreach (var ly in layers)
                        {
                            foreach (var sh in R.Items(R.Get(ly, "Shaders")))
                            {
                                foreach (var key in new[] { "Primary", "Secondary", "Tertiary" })
                                {
                                    if (R.Get(sh, key) is byte[] buf && buf.Length >= 4)
                                    {
                                        shaders++;
                                        if (buf[0] == (byte)'D' && buf[1] == (byte)'X' && buf[2] == (byte)'B' && buf[3] == (byte)'C') dxbc++;
                                    }
                                }
                            }
                            stages += R.Items(R.Get(ly, "Stages")).Count();
                            mods += R.Items(R.Get(ly, "Modifiers")).Count();
                        }
                        Console.WriteLine($"      VALIDATE layers={layers.Count} shaderBuffers={shaders} startingWithDXBC={dxbc} stages={stages} modifiers={mods}");
                        foreach (var prop in R.Items(R.Get(R.Get(rm, "Properties"), "MaterialProperties")))
                        {
                            var pname = R.Get(prop, "MaterialPropertyName");
                            var parts = new List<string>();
                            foreach (var f in prop.GetType().GetProperties())
                            {
                                object? val = null;
                                try { val = f.GetValue(prop); } catch { }
                                if (val != null && !(val is System.Collections.IEnumerable && !(val is string)))
                                    parts.Add($"{f.Name}={val}");
                            }
                            Console.WriteLine($"      {pname}: {string.Join(", ", parts)}");
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"    TEMPLATE 0x{rmDid:X8}: {ex.Message}"); }
                }
            }
            uint materialType = R.U(R.Get(inst, "MaterialTypeId"));
            Console.WriteLine($"    material 0x{materialInstanceId:X8}: instance {ScalarMembers(inst)}");

            var modifierQueue = new List<(double priority, uint did, bool apr, AppearanceMaterialMod? operation)>();
            foreach (var modObj in R.Items(R.Get(inst, "Modifiers")))
            {
                uint modId = R.U(modObj);
                if (modId != 0) modifierQueue.Add((double.MinValue, modId, false, null));
            }

            if (appearance != null)
            {
                foreach (var op in appearance.MaterialMods
                    .Where(x => (x.MeshType == 0 || x.MeshType == 1 || x.MeshType == meshType) &&
                                (x.MaterialType == 0 || x.MaterialType == 1 || x.MaterialType == materialType))
                    .OrderBy(x => x.Priority))
                {
                    modifierQueue.Add((op.Priority, op.ModifierDid, true, op));
                }
            }

            foreach (var queued in modifierQueue)
            {
                var mod = ParseSdk("VoK.Sdk.Common.MaterialModifier", await Raw(6, queued.did));
                Console.WriteLine(queued.apr
                    ? $"      APR priority {queued.priority:0.###} modifier 0x{queued.did:X8}: {ScalarMembers(mod)}"
                    : $"      base modifier 0x{queued.did:X8}: {ScalarMembers(mod)}");

                ApplyMaterialProperties(mod, result, verbose: true);

                if (queued.apr)
                    Console.WriteLine($"        applied to meshType=0x{meshType:X8}, materialType=0x{materialType:X8}");
            }

            if (!result.Diffuse.HasValue && result.FirstTexture.HasValue)
            {
                result.ByName["DiffuseMap_Fallback"] = result.FirstTexture.Value;
                Console.WriteLine($"    material 0x{materialInstanceId:X8}: no named DiffuseMap; fallback 0x{result.FirstTexture.Value:X8}");
            }
            else if (result.Diffuse.HasValue)
            {
                Console.WriteLine($"    material 0x{materialInstanceId:X8}: DiffuseMap 0x{result.Diffuse.Value:X8}");
            }

            if (result.Normal.HasValue)
                Console.WriteLine($"    material 0x{materialInstanceId:X8}: NormalMap 0x{result.Normal.Value:X8}");

            if (result.ByName.Count == 0)
                Console.WriteLine($"    material 0x{materialInstanceId:X8}: no texture-bearing material property found");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    material 0x{materialInstanceId:X8}: texture lookup failed ({ex.Message})");
        }
        return result;
    }

    /// <summary>
    /// Resolves a RenderTexture to the first of its surfaces the SDK can decode, returning PNG bytes.
    ///
    /// This used to read the surface bytes itself, probing dat indices 7 and 6 and accepting only DXT1/3/5.
    /// That silently failed for anything stored elsewhere: "Demise, the Beginning of the Fall" keeps its
    /// surfaces in client_surface.dat, every probe threw, and both its materials fell back to the neutral
    /// placeholder with no texture at all. Sireth only worked because its surface happens to sit in
    /// client_highres.dat, one of the two that were hardcoded.
    /// </summary>
    static async Task<(uint surfaceId, byte[] png, string format)?> FetchBestSurface(uint textureId)
    {
        try
        {
            var tex = ParseSdk("VoK.Sdk.Common.RenderTexture", await Raw(6, textureId));
            var surfaces = R.Items(R.Get(tex, "SurfaceDids")).Select(R.U).ToList();
            if (surfaces.Count == 0) return null;

            // Surface lists are highest-res first, so the first one that decodes is the one we want.
            foreach (uint sid in surfaces)
            {
                try
                {
                    var png = Images.Value.GetPngImageBytes(sid);
                    if (png == null || png.Length == 0) continue;

                    var format = "?";
                    try { Images.Value.GetImage(sid, out _, out var f); format = f.ToString(); }
                    catch { }

                    Console.WriteLine($"    texture 0x{textureId:X8}: surface 0x{sid:X8} decoded as {format}");
                    return (sid, png, format);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    texture 0x{textureId:X8}: surface 0x{sid:X8} not decodable ({ex.Message})");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    texture 0x{textureId:X8}: surface lookup failed ({ex.Message})");
        }
        return null;
    }

    static uint[] CrcTable = BuildCrcTable();
    static uint[] BuildCrcTable()
    {
        var t=new uint[256];
        for(uint n=0;n<256;n++)
        {
            uint c=n;
            for(int k=0;k<8;k++) c=(c&1)!=0 ? 0xEDB88320u^(c>>1) : c>>1;
            t[n]=c;
        }
        return t;
    }
    static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint c=0xFFFFFFFFu;
        foreach(byte x in a) c=CrcTable[(c^x)&255]^(c>>8);
        foreach(byte x in b) c=CrcTable[(c^x)&255]^(c>>8);
        return c^0xFFFFFFFFu;
    }
    static void WriteBe(BinaryWriter bw, uint v)
    {
        bw.Write(new[]{(byte)(v>>24),(byte)(v>>16),(byte)(v>>8),(byte)v});
    }
    static void PngChunk(BinaryWriter bw, string type, byte[] data)
    {
        byte[] tn=Encoding.ASCII.GetBytes(type);
        WriteBe(bw,(uint)data.Length); bw.Write(tn); bw.Write(data); WriteBe(bw,Crc32(tn,data));
    }

    static byte[] EncodePng(int width, int height, byte[] rgba)
    {
        using var raw=new MemoryStream();
        for(int y=0;y<height;y++)
        {
            raw.WriteByte(0); // filter None
            raw.Write(rgba, y*width*4, width*4);
        }
        byte[] compressed;
        using(var cm=new MemoryStream())
        {
            using(var z=new ZLibStream(cm, CompressionLevel.Optimal, leaveOpen:true))
                z.Write(raw.ToArray());
            compressed=cm.ToArray();
        }

        using var ms=new MemoryStream();
        using var bw=new BinaryWriter(ms);
        bw.Write(new byte[]{137,80,78,71,13,10,26,10});
        using(var ih=new MemoryStream())
        using(var ibw=new BinaryWriter(ih))
        {
            WriteBe(ibw,(uint)width); WriteBe(ibw,(uint)height);
            ibw.Write((byte)8); ibw.Write((byte)6); ibw.Write((byte)0); ibw.Write((byte)0); ibw.Write((byte)0);
            PngChunk(bw,"IHDR",ih.ToArray());
        }
        PngChunk(bw,"IDAT",compressed);
        PngChunk(bw,"IEND",Array.Empty<byte>());
        return ms.ToArray();
    }

    static async Task<(byte[] png, uint surfaceId, int width, int height, string fourcc)?> LoadTexturePng(uint textureId)
    {
        var surf = await FetchBestSurface(textureId);
        if (!surf.HasValue) return null;
        var (w, h) = PngSize(surf.Value.png);
        return (surf.Value.png, surf.Value.surfaceId, w, h, surf.Value.format);
    }

    static uint? LookupTexture(IReadOnlyDictionary<string, uint>? byName, string key)
    {
        if (byName == null) return null;
        foreach (var kv in byName)
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && kv.Value != 0) return kv.Value;
        return null;
    }

    /// <summary>
    /// RGBA pixels for a texture's best surface.
    ///
    /// Comes straight from the SDK's GetRgbaPixels: plain top-down RGBA at the true dimensions, no file
    /// container to unwrap and no block decoder to maintain here. The exporter used to carry its own BC1/2/3
    /// decoders because the SDK's DxtToDds wrote the DDS dwHeight and dwWidth fields transposed, which
    /// scrambled every non-square texture; that is fixed in the SDK with regression tests, so the local
    /// decoders are gone - one of them (DXT3) was quietly returning a single flat colour.
    ///
    /// Note GetRgbaImageBytes is still not what it sounds like: despite the name it returns BMP *file*
    /// bytes. GetRgbaPixels is the one that returns pixels.
    /// </summary>
    static async Task<(byte[] rgba, int width, int height, uint surfaceId)?> LoadTextureRgba(uint textureId)
    {
        var surf = await FetchBestSurface(textureId);
        if (!surf.HasValue) return null;

        // Pixels come from the SDK: GetRgbaPixels returns plain top-down RGBA at the correct
        // dimensions, so there is no container to unwrap and no decoder to maintain here.
        var px = Images.Value.GetRgbaPixels(surf.Value.surfaceId);
        if (px?.Pixels == null || px.Width <= 0 || px.Height <= 0)
        {
            Console.WriteLine($"    texture 0x{textureId:X8}: surface 0x{surf.Value.surfaceId:X8} produced no RGBA pixels");
            return null;
        }
        return (px.Pixels, px.Width, px.Height, surf.Value.surfaceId);
    }

    /// <summary>
    /// Flattens a two-layer DDO material into the single base-colour texture glTF allows.
    ///
    /// Materials like Demise's blade bands carry DiffuseMap plus DiffuseMap2, and the shader
    /// (Texture_ColorAndOpacity / Texture_ColorAndOpacity2) multiplies them - the alphas included, which is
    /// where the shape lives. The band's DiffuseMap is white RGB with a rune-shaped alpha and DiffuseMap2
    /// supplies the orange, so exporting layer 1 alone gives the solid white strip these used to render as.
    ///
    /// Layer 2 is sampled nearest-neighbour into layer 1's dimensions; they are rarely the same size
    /// (1024x64 against 256x16 here). UTranslate/UTranslate2 scroll the layers in game, so baking freezes
    /// them at t=0 - static runes rather than moving ones, which needs viewer-side animation to fix
    /// properly, the way the weapon aura is handled.
    /// </summary>
    static double StdDev(double[] values)
    {
        double mean = 0;
        foreach (var v in values) mean += v;
        mean /= values.Length;
        double acc = 0;
        foreach (var v in values) acc += (v - mean) * (v - mean);
        return Math.Sqrt(acc / values.Length);
    }

    /// <summary>Length of the repeating run in a 1-D colour ramp, comparing averaged samples.</summary>
    static int RampPeriod(double[] values)
    {
        int n = values.Length;
        for (int p = 1; p < n; p++)
        {
            if (n % p != 0) continue;
            bool ok = true;
            for (int i = p; i < n && ok; i++)
                if (Math.Abs(values[i] - values[i % p]) > 16) ok = false;
            if (ok) return p;
        }
        return n;
    }

    static double[] AxisMeans(byte[] rgba, int w, int h, bool alongU)
    {
        var means = new double[alongU ? w : h];
        int other = alongU ? h : w;
        for (int i = 0; i < means.Length; i++)
        {
            double sum = 0;
            for (int j = 0; j < other; j++)
            {
                int x = alongU ? i : j, y = alongU ? j : i;
                sum += rgba[(y * w + x) * 4 + 1];   // green separates this ramp's red from its yellow
            }
            means[i] = sum / other;
        }
        return means;
    }


    static byte[] CombineLayers(byte[] a, int aw, int ah, byte[] b, int bw, int bh, float rampPhase = 0f, float[][]? vertexColorByU = null)
    {
        // Layer 2 is a colour ramp and it runs ALONG the band, not across it: measured off an in-game
        // screenshot, green along the band reads [113,163,189,155,119] end-to-end - red at both ends,
        // yellow in the middle - while across it only the border lines vary. A plain 1:1 sample sweeps the
        // ramp once over the band's length, which is exactly that. An earlier build mapped it across the
        // width instead; the direction was never the problem, only the ramp being tiled and mis-phased,
        // which the DXT transpose fix in the SDK resolved.
        var outRgba = new byte[aw * ah * 4];
        for (int y = 0; y < ah; y++)
        {
            int by = bh == ah ? y : (int)((long)y * bh / ah);
            for (int x = 0; x < aw; x++)
            {
                // Sample the ramp at this pixel's offset from the band's UV start, wrapped.
                double u = x / (double)aw - rampPhase;
                u -= Math.Floor(u);
                int bx = (int)(u * bw);
                int ai = (y * aw + x) * 4;
                int bi = (Math.Min(by, bh - 1) * bw + Math.Min(bx, bw - 1)) * 4;
                for (int c = 0; c < 4; c++)
                    outRgba[ai + c] = (byte)(a[ai + c] * b[bi + c] / 255);

                // The client multiplies the vertex colour in as well, not just its alpha. The alpha is
                // what fades these bands out at the blade tips (o0.w = tex0.a * tex1.a * v3.w), and the
                // RGB narrows the gradient: its green runs 0.32,0.25,0.32,0.41,0.59,0.50,0.39,0.31 along
                // the band - a much tighter yellow peak than the texture ramp's, which on its own leaves
                // the band too yellow and loses the red the game keeps over most of its length.
                if (vertexColorByU != null)
                {
                    int bin = Math.Clamp((int)((x / (double)aw) * vertexColorByU.Length), 0, vertexColorByU.Length - 1);
                    // Straight out of the client's own shader for this material (RenderMaterial
                    // 0x2B00036A):
                    //
                    //   alpha  = tex0.a * tex1.a * v3.w        (v3.w is the vertex alpha, applied raw)
                    //   colour = 2 * tex0.rgb * tex1.rgb * v3.rgb * <lighting>
                    //
                    // The x2 is the same MODULATE2X the mesh shaders use, and it is where the brightness
                    // comes from - not from an alpha gain, which was an earlier guess that scaled the
                    // whole decal instead of its colour. Multiplying the vertex RGB first keeps the x2
                    // from clipping: green peaks at 194*0.59 = 114, so doubling lands at 229 rather than
                    // saturating, which is what brings the yellow back.
                    var vc = vertexColorByU[bin];
                    for (int c = 0; c < 3; c++)
                        outRgba[ai + c] = (byte)Math.Min(255, outRgba[ai + c] * Math.Clamp(vc[c], 0f, 1f) * 2f);
                    outRgba[ai + 3] = (byte)(outRgba[ai + 3] * Math.Clamp(vc[3], 0f, 1f));
                }


            }
        }
        return outRgba;
    }

    /// <summary>True when a texture carries real transparency, so the material needs alphaMode BLEND.</summary>
    static bool HasTransparency(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] < 250) return true;
        return false;
    }

    /// <summary>Width/height straight from the PNG IHDR, for logging only.</summary>
    static (int width, int height) PngSize(byte[] png)
    {
        if (png.Length < 24) return (0, 0);
        int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return (w, h);
    }

    static JsonArray Arr(params float[] xs) => new(xs.Select(x => (JsonNode?)x).ToArray());

    static async Task<bool> AddAnimationAsync(GltfBuilder gltf, uint animationId, int jointCount)
    {
        using var response = await Http.GetAsync($"AnimationCatalog/0x{animationId:X8}/decode?frames=true");
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Animation 0x{animationId:X8}: decode failed ({(int)response.StatusCode})");
            return false;
        }

        var root = JsonNode.Parse(body) as JsonObject;
        var dec = root?["decoded"] as JsonObject;
        if (dec == null) return false;
        int tracks = dec["transformTrackCount"]?.GetValue<int>() ?? 0;
        var frames = dec["frames"] as JsonArray;
        if (tracks != jointCount || frames == null || frames.Count == 0)
        {
            Console.WriteLine($"Animation 0x{animationId:X8}: track mismatch or no frames ({tracks} tracks, expected {jointCount})");
            return false;
        }

        var times = new List<float>(frames.Count);
        var pos = Enumerable.Range(0, jointCount).Select(_ => new List<Vector3>(frames.Count)).ToArray();
        var rot = Enumerable.Range(0, jointCount).Select(_ => new List<Quaternion>(frames.Count)).ToArray();
        var scl = Enumerable.Range(0, jointCount).Select(_ => new List<Vector3>(frames.Count)).ToArray();

        foreach (var fn in frames)
        {
            if (fn is not JsonObject frame) return false;
            times.Add(frame["time"]?.GetValue<float>() ?? 0f);
            var transforms = frame["transforms"] as JsonArray;
            if (transforms == null || transforms.Count < jointCount) return false;

            for (int i = 0; i < jointCount; i++)
            {
                if (transforms[i] is not JsonObject t) return false;
                int track = t["track"]?.GetValue<int>() ?? i;
                if (track != i) return false;
                var tr = t["translation"] as JsonArray;
                var qr = t["rotation"] as JsonArray;
                var sc = t["scale"] as JsonArray;
                if (tr == null || qr == null || sc == null || tr.Count < 3 || qr.Count < 4 || sc.Count < 3) return false;
                pos[i].Add(new Vector3(tr[0]!.GetValue<float>(), tr[1]!.GetValue<float>(), tr[2]!.GetValue<float>()));
                var q = new Quaternion(qr[0]!.GetValue<float>(), qr[1]!.GetValue<float>(), qr[2]!.GetValue<float>(), qr[3]!.GetValue<float>());
                rot[i].Add(q.LengthSquared() > 1e-12f ? Quaternion.Normalize(q) : Quaternion.Identity);
                scl[i].Add(new Vector3(sc[0]!.GetValue<float>(), sc[1]!.GetValue<float>(), sc[2]!.GetValue<float>()));
            }
        }

        gltf.AddAnimationClip(
            $"DDO_0x{animationId:X8}",
            times,
            pos.Select(x => x.ToArray()).ToArray(),
            rot.Select(x => x.ToArray()).ToArray(),
            scl.Select(x => x.ToArray()).ToArray());
        Console.WriteLine($"Animation 0x{animationId:X8}: embedded {frames.Count} frames across {jointCount} joints as glTF channels.");
        return true;
    }

    static async Task<List<uint>> ComposeMeshIdsAsync(object setup, AppearancePlan? appearance, bool appearanceOnly)
    {
        var baseMeshIds = R.Items(R.Get(setup, "RenderMeshIds")).Select(R.U).Where(x => x != 0).ToList();
        if (appearance == null || appearance.MeshReplacements.Count == 0) return baseMeshIds;

        // Standalone wearables (helmets/armor/cloaks) are encoded as APR replacements
        // against a generic base Setup. For a Library/export preview we want the wearable
        // geometry itself, not the generic body/head used only as an APR target. When the
        // appearance contains real mesh replacements, start with an empty slot set and add
        // only the replacement meshes. Material-only appearances safely keep the base Setup.
        var meshIds = appearanceOnly ? new List<uint>() : baseMeshIds;
        var slots = new List<(uint meshId, uint meshType)>();
        foreach (var id in meshIds)
        {
            uint meshType = 0;
            try
            {
                var mesh = ParseMesh(await Raw(13, id));
                meshType = R.U(R.Get(mesh, "MeshTypeId"));
            }
            catch { }
            slots.Add((id, meshType));
        }

        foreach (var op in appearance.MeshReplacements.OrderBy(x => x.Priority))
        {
            int index = op.MeshType == 0
                ? -1
                : slots.FindIndex(x => x.meshType == op.MeshType);

            if (index >= 0)
            {
                uint old = slots[index].meshId;
                slots[index] = (op.MeshDid, op.MeshType);
                Console.WriteLine($"Appearance priority {op.Priority:0.###}: meshType 0x{op.MeshType:X8} replaced 0x{old:X8} with 0x{op.MeshDid:X8}.");
            }
            else
            {
                uint actualType = op.MeshType;
                if (actualType == 0)
                {
                    try
                    {
                        var replacement = ParseMesh(await Raw(13, op.MeshDid));
                        actualType = R.U(R.Get(replacement, "MeshTypeId"));
                    }
                    catch { }
                }
                slots.Add((op.MeshDid, actualType));
                Console.WriteLine($"Appearance priority {op.Priority:0.###}: meshType 0x{actualType:X8} added 0x{op.MeshDid:X8}.");
            }
        }

        var composed = slots.Select(x => x.meshId).Where(x => x != 0).Distinct().ToList();
        if (appearanceOnly && composed.Count == 0)
        {
            Console.WriteLine("Appearance-only mode produced no replacement meshes; falling back to the base Setup meshes.");
            return baseMeshIds;
        }
        return composed;
    }

    static async Task Main(string[] args)
    {
        uint setupId = args.Length > 0
            ? Convert.ToUInt32(args[0].Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16)
            : 0x0400022C;

        if (setupId == 0)
        {
            Console.Error.WriteLine("This record has no renderable Setup. DDO Studio 1.7.2 filters Setup 0x00000000 records from the Asset Library instead of sending them to the GLB exporter.");
            Environment.ExitCode = 2;
            return;
        }

        string outFile;
        if (args.Length > 1)
        {
            outFile = Path.GetFullPath(args[1]);
        }
        else
        {
            string outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DDO Studio", "Exports");
            outFile = Path.Combine(outDir, $"setup_{setupId:X8}.glb");
        }

        var animationIds = new List<uint>();
        string? appearancePath = null;
        bool appearanceOnly = false;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i].Equals("--animation", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                var rawAnim = args[++i].Replace("0x", "", StringComparison.OrdinalIgnoreCase);
                if (uint.TryParse(rawAnim, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedAnim)) animationIds.Add(parsedAnim);
            }
            else if (args[i].Equals("--animation-list", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                var listPath = args[++i];
                if (File.Exists(listPath))
                {
                    foreach (var line in File.ReadLines(listPath))
                    {
                        var rawAnim = line.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
                        if (uint.TryParse(rawAnim, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedAnim)) animationIds.Add(parsedAnim);
                    }
                }
            }
            else if (args[i].Equals("--appearance", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                appearancePath = Path.GetFullPath(args[++i]);
            }
            else if (args[i].Equals("--appearance-only", StringComparison.OrdinalIgnoreCase))
            {
                appearanceOnly = true;
            }
        }
        animationIds = animationIds.Distinct().ToList();

        AppearancePlan? appearance = AppearancePlan.Load(appearancePath);
        Console.WriteLine($"DDO GLB Exporter 1.7.2 appearance-composition build - Setup 0x{setupId:X8}");
        if (appearanceOnly) Console.WriteLine("Standalone wearable mode: export APR replacement geometry without the generic base Setup meshes.");
        Console.WriteLine($"Dat folder: {DatFolder.Value}");
        Console.WriteLine($"Backend API (animations only): {Http.BaseAddress}");
        if (appearance != null)
        {
            Console.WriteLine($"Appearance composition: {appearance.SourcePath}");
            Console.WriteLine($"Appearance composition decoded: {appearance.SelectorCount} selected part(s), {appearance.MeshReplacements.Count} mesh replacement(s), {appearance.MaterialMods.Count} material modifier(s), {appearance.SetupReplacements.Count} Setup replacement(s).");
            var setupReplacement = appearance.SetupReplacements.OrderBy(x => x.Priority).LastOrDefault();
            if (setupReplacement != null && setupReplacement.SetupDid != 0 && setupReplacement.SetupDid != setupId)
            {
                Console.WriteLine($"Appearance priority {setupReplacement.Priority:0.###}: Setup 0x{setupId:X8} replaced with 0x{setupReplacement.SetupDid:X8}.");
                setupId = setupReplacement.SetupDid;
            }
        }
        Console.WriteLine("Reading Setup...");
        object setup = ParseSetup(await Raw(6, setupId));

        var joints = DecodeJoints(setup);
        bool rigged = joints.Count > 0;
        Console.WriteLine(rigged ? $"Skeleton: {joints.Count} joints" : "Skeleton: none (static asset)");

        var gltf = new GltfBuilder();
        int skinIndex = -1;
        var sceneRoots = new List<int>();

        if (rigged)
        {
            for (int i = 0; i < joints.Count; i++)
                Console.WriteLine($"  {i,2}: parent={joints[i].Parent,3} name={joints[i].Name} T={joints[i].Translation}");

            var global = GlobalBind(joints);
            var invBind = new List<Matrix4x4>();
            foreach (var m in global)
            {
                if (!Matrix4x4.Invert(m, out var inv)) throw new Exception("Non-invertible bind matrix.");
                invBind.Add(inv);
            }

            var children = Enumerable.Range(0, joints.Count).Select(_ => new List<int>()).ToList();
            for (int i = 0; i < joints.Count; i++)
                if (joints[i].Parent >= 0) children[joints[i].Parent].Add(i);

            for (int i = 0; i < joints.Count; i++)
            {
                var j = joints[i];
                var node = new JsonObject
                {
                    ["name"] = j.Name,
                    ["translation"] = Arr(j.Translation.X, j.Translation.Y, j.Translation.Z),
                    ["rotation"] = Arr(j.Rotation.X, j.Rotation.Y, j.Rotation.Z, j.Rotation.W),
                    ["scale"] = Arr(j.Scale.X, j.Scale.Y, j.Scale.Z)
                };
                if (children[i].Count > 0)
                    node["children"] = new JsonArray(children[i].Select(x => (JsonNode?)x).ToArray());
                gltf.AddNode(node);
            }

            int ibmAccessor = gltf.AddMatrices(invBind);
            int rootJoint = Enumerable.Range(0, joints.Count).FirstOrDefault(i => joints[i].Parent < 0);
            skinIndex = gltf.AddSkin(new JsonObject
            {
                ["name"] = $"Skin_{setupId:X8}",
                ["inverseBindMatrices"] = ibmAccessor,
                ["skeleton"] = rootJoint,
                ["joints"] = new JsonArray(Enumerable.Range(0, joints.Count).Select(x => (JsonNode?)x).ToArray())
            });
            foreach (int i in Enumerable.Range(0, joints.Count))
                if (joints[i].Parent < 0) sceneRoots.Add(i);
        }

        int defaultMaterial = gltf.AddMaterial("DDO_Default");
        var materialCache = new Dictionary<(uint materialId, uint meshType),int>();

        var jointUse = new long[joints.Count];
        var jointWeight = new double[joints.Count];
        var meshIds = await ComposeMeshIdsAsync(setup, appearance, appearanceOnly);
        Console.WriteLine(appearance != null
            ? $"Render meshes after appearance composition{(appearanceOnly ? " (wearable only)" : "")}: {meshIds.Count}"
            : $"Render meshes in Setup: {meshIds.Count}");

        foreach (uint meshId in meshIds)
        {
            object mesh;
            try { mesh = ParseMesh(await Raw(13, meshId)); }
            catch (Exception ex)
            {
                Console.WriteLine($"Mesh 0x{meshId:X8}: skipped ({ex.Message})");
                continue;
            }

            uint meshType = R.U(R.Get(mesh, "MeshTypeId"));
            var sets = R.Items(R.Get(mesh, "VertexArray")).ToList();
            int totalVerts = sets.Sum(s => R.Items(R.Get(s, "Vertices")).Count());
            if (totalVerts <= 3)
            {
                Console.WriteLine($"Mesh 0x{meshId:X8}: helper ({totalVerts} verts), skipped");
                continue;
            }

            var indexArrays = R.Items(R.Get(mesh, "Indices")).ToList();
            var meshMaterialIds = R.Items(R.Get(mesh, "MaterialDids")).Select(R.U).ToList();
            Console.WriteLine($"Mesh 0x{meshId:X8} STRUCTURE: meshType=0x{meshType:X8}, vertexSets={sets.Count}, indexGroups={indexArrays.Count}, materials={meshMaterialIds.Count}");
            for (int mi = 0; mi < meshMaterialIds.Count; mi++)
                Console.WriteLine($"  materialSlot {mi}: 0x{meshMaterialIds[mi]:X8}");
            for (int gi = 0; gi < indexArrays.Count; gi++)
            {
                int tc = 0; uint mx = 0;
                foreach (var tri in R.Items(indexArrays[gi]))
                {
                    uint a = TriField(tri, "a1"), b = TriField(tri, "a2"), c = TriField(tri, "a3");
                    mx = Math.Max(mx, Math.Max(a, Math.Max(b, c))); tc++;
                }
                Console.WriteLine($"  indexGroup {gi}: triangles={tc}, maxIndex={mx}");
            }
            var primitives = new JsonArray();

            for (int si = 0; si < sets.Count; si++)
            {
                var set = sets[si];
                var verts = R.Items(R.Get(set, "Vertices")).ToList();
                if (verts.Count == 0) continue;

                uint vf = R.U(R.Get(set, "VertexFormat"));
                int influences = (int)((vf & 0xF0000u) >> 16);

                var pos = new List<Vector3>(verts.Count);
                var nrm = new List<Vector3>(verts.Count);
                var uv = new List<Vector2>(verts.Count);
                var js = new List<ushort[]>(verts.Count);
                var ws = new List<float[]>(verts.Count);
                var vcol = new List<float[]>(verts.Count);
                bool anyVertexColor = false;

                // 1.4.1 Raw-V texture-wrap tests: DDO assets can carry multiple texture-coordinate entries per vertex.
                // Record the complete channel structure before exporting channel 0 so we can determine
                // whether the game shader selects another channel or applies an additional transform.
                var uvCountHistogram = new SortedDictionary<int, int>();
                var uvStats = new List<(float minU, float maxU, float minV, float maxV, int finite, int outside01)>();
                int maxUvChannels = 0;
                foreach (var v in verts)
                {
                    var all = R.Items(R.Get(v, "UVMap")).ToList();
                    maxUvChannels = Math.Max(maxUvChannels, all.Count);
                    uvCountHistogram[all.Count] = uvCountHistogram.TryGetValue(all.Count, out var old) ? old + 1 : 1;
                }
                for (int ch = 0; ch < maxUvChannels; ch++)
                    uvStats.Add((float.PositiveInfinity, float.NegativeInfinity, float.PositiveInfinity, float.NegativeInfinity, 0, 0));

                int vertexNumber = 0;
                foreach (var v in verts)
                {
                    pos.Add(R.V3(R.Get(v, "Position")));

                    var no = R.Get(v, "Normal");
                    nrm.Add(no != null ? Vector3.Normalize(R.V3(no)) : Vector3.UnitZ);

                    if (Environment.GetEnvironmentVariable("DDO_DUMP_VCOL") == "1" && vertexNumber < 6)
                    {
                        var dif = R.Get(v, "Diffuse");
                        var uv0 = R.Items(R.Get(v, "UVMap")).FirstOrDefault();
                        Console.WriteLine($"    VCOL set={si} v{vertexNumber}: Diffuse={(dif == null ? "null" : dif.ToString())} u={(uv0 == null ? 0 : R.F(R.Get(uv0, "U")))}");
                    }
                    // DDO stores a packed 32-bit vertex colour, but the SDK surfaces it as float? - so the
                    // bits have to be read back as an integer. Demise's rune band uses it as an alpha
                    // ramp: opaque through the middle (0xFF......, which reads as NaN) fading to nearly
                    // clear at the tips (0x01......, which reads as a denormal near 2.3e-38). Dropping it
                    // is why the band ran at full strength right off the ends of the blade.
                    var difRaw = R.Get(v, "Diffuse");
                    if (difRaw != null)
                    {
                        uint packed = BitConverter.SingleToUInt32Bits(Convert.ToSingle(difRaw));
                        if (packed != 0)
                        {
                            vcol.Add(new[]
                            {
                                ((packed >> 16) & 0xFF) / 255f,   // R
                                ((packed >> 8) & 0xFF) / 255f,    // G
                                (packed & 0xFF) / 255f,           // B
                                ((packed >> 24) & 0xFF) / 255f    // A
                            });
                            anyVertexColor = true;
                        }
                        else vcol.Add(new[] { 1f, 1f, 1f, 1f });
                    }
                    else vcol.Add(new[] { 1f, 1f, 1f, 1f });

                    var uvs = R.Items(R.Get(v, "UVMap")).ToList();
                    for (int ch = 0; ch < uvs.Count; ch++)
                    {
                        float rawU = R.F(R.Get(uvs[ch], "U"));
                        float rawV = R.F(R.Get(uvs[ch], "V"));
                        if (float.IsFinite(rawU) && float.IsFinite(rawV))
                        {
                            var st = uvStats[ch];
                            st.minU = Math.Min(st.minU, rawU); st.maxU = Math.Max(st.maxU, rawU);
                            st.minV = Math.Min(st.minV, rawV); st.maxV = Math.Max(st.maxV, rawV);
                            st.finite++;
                            if (rawU < 0 || rawU > 1 || rawV < 0 || rawV > 1) st.outside01++;
                            uvStats[ch] = st;
                        }
                    }
                    if (vertexNumber < 12)
                    {
                        string uvSample = uvs.Count == 0 ? "<none>" : string.Join(" | ", uvs.Select((x, ch) => $"uv{ch}=({R.F(R.Get(x, "U")):0.######},{R.F(R.Get(x, "V")):0.######}) [{ScalarMembers(x)}]"));
                        Console.WriteLine($"  UV SAMPLE set={si} vertex={vertexNumber}: pos={R.V3(R.Get(v, "Position"))}; {uvSample}");
                    }
                    if (uvs.Count > 0)
                        uv.Add(new Vector2(R.F(R.Get(uvs[0], "U")), R.F(R.Get(uvs[0], "V"))));
                    else
                        uv.Add(Vector2.Zero);
                    vertexNumber++;

                    var sw = rigged ? DecodeSkin(v, set, influences, joints.Count) : (new ushort[4], new float[] { 1, 0, 0, 0 });
                    js.Add(sw.Item1);
                    ws.Add(sw.Item2);
                    for (int k = 0; rigged && k < 4; k++)
                    {
                        int ji = sw.Item1[k];
                        float ww = sw.Item2[k];
                        if (ww > 0 && ji >= 0 && ji < joints.Count)
                        {
                            jointUse[ji]++;
                            jointWeight[ji] += ww;
                        }
                    }
                }

                Console.WriteLine($"  UV DIAGNOSTIC set {si}: VertexFormat=0x{vf:X8}; influences={influences}; maxUvChannels={maxUvChannels}; UV-count histogram: {string.Join(", ", uvCountHistogram.Select(kv => $"{kv.Key}=>{kv.Value}"))}");
                for (int ch = 0; ch < uvStats.Count; ch++)
                {
                    var st = uvStats[ch];
                    Console.WriteLine($"    uv{ch}: finite={st.finite}/{verts.Count}, U=[{st.minU:0.######},{st.maxU:0.######}], V=[{st.minV:0.######},{st.maxV:0.######}], outside01={st.outside01}");
                }
                if (verts.Count > 0)
                    Console.WriteLine($"    first vertex scalars: {ScalarMembers(verts[0])}");

                var idx = new List<uint>();
                if (si < indexArrays.Count)
                {
                    foreach (var tri in R.Items(indexArrays[si]))
                    {
                        uint a = TriField(tri, "a1"), b = TriField(tri, "a2"), c = TriField(tri, "a3");
                        if (a < verts.Count && b < verts.Count && c < verts.Count)
                        {
                            idx.Add(a); idx.Add(b); idx.Add(c);
                        }
                    }
                }

                if (idx.Count == 0)
                {
                    Console.WriteLine($"  set {si}: no usable indices, skipped");
                    continue;
                }

                int pAcc = gltf.AddPositions(pos);
                int nAcc = gltf.AddNormals(nrm);
                // Vertex alpha as a function of U, so it can be baked into the composited texture.
                // three r128 declares `varying vec3 vColor` and drops the alpha channel of COLOR_0, so
                // exporting it is not enough - but the band's U maps straight onto the texture's U, which
                // makes the fade expressible as a 1-D curve over U.
                float[][]? vertexColorByU = null;
                if (anyVertexColor && uv.Count == vcol.Count && uv.Count > 0)
                {
                    const int bins = 256;
                    var sum = new double[bins, 4];
                    var hits = new int[bins];
                    for (int i = 0; i < uv.Count; i++)
                    {
                        double uu = uv[i].X - Math.Floor(uv[i].X);
                        int bin = Math.Clamp((int)(uu * bins), 0, bins - 1);
                        for (int c = 0; c < 4; c++) sum[bin, c] += vcol[i][c];
                        hits[bin]++;
                    }
                    var table = new float[bins][];
                    for (int i = 0; i < bins; i++)
                        table[i] = hits[i] > 0
                            ? new[] { (float)(sum[i, 0] / hits[i]), (float)(sum[i, 1] / hits[i]), (float)(sum[i, 2] / hits[i]), (float)(sum[i, 3] / hits[i]) }
                            : null;
                    // Interpolate across empty bins rather than copying the nearest neighbour. There are
                    // only 176 vertices spread over 256 bins, so most are empty, and nearest-neighbour
                    // fill smears a tip vertex's near-zero alpha across a wide stretch of U - the band
                    // then fades out far sooner than in game. The client interpolates vertex attributes
                    // linearly across each triangle, so do the same here.
                    var populated = new List<int>();
                    for (int i = 0; i < bins; i++) if (table[i] != null) populated.Add(i);
                    if (populated.Count == 0)
                    {
                        for (int i = 0; i < bins; i++) table[i] = new[] { 1f, 1f, 1f, 1f };
                    }
                    else
                    {
                        for (int i = 0; i < bins; i++)
                        {
                            if (table[i] != null) continue;
                            int prev = -1, next = -1;
                            for (int d = 1; d <= bins; d++)
                            {
                                int a = (i - d + bins) % bins;
                                if (prev < 0 && table[a] != null) prev = a;
                                int b = (i + d) % bins;
                                if (next < 0 && table[b] != null) next = b;
                                if (prev >= 0 && next >= 0) break;
                            }
                            int gapPrev = (i - prev + bins) % bins;
                            int gapNext = (next - i + bins) % bins;
                            float t = gapPrev + gapNext == 0 ? 0f : gapPrev / (float)(gapPrev + gapNext);
                            var lo = table[prev]; var hi = table[next];
                            table[i] = new[]
                            {
                                lo[0] + (hi[0] - lo[0]) * t,
                                lo[1] + (hi[1] - lo[1]) * t,
                                lo[2] + (hi[2] - lo[2]) * t,
                                lo[3] + (hi[3] - lo[3]) * t
                            };
                        }
                    }
                    vertexColorByU = table;
                }

                int uAcc = gltf.AddUVs(uv);
                int cAcc = anyVertexColor ? gltf.AddColors(vcol) : -1;
                if (anyVertexColor)
                    Console.WriteLine($"  set {si}: per-vertex colour present, exported as COLOR_0");
                int jAcc = rigged ? gltf.AddJoints(js) : -1;
                int wAcc = rigged ? gltf.AddWeights(ws) : -1;
                int iAcc = gltf.AddIndices(idx);

                int materialIndex = defaultMaterial;
                if (meshMaterialIds.Count > 0)
                {
                    uint mid = meshMaterialIds[Math.Min(si, meshMaterialIds.Count - 1)];
                    var materialKey = (materialId: mid, meshType);
                    if (!materialCache.TryGetValue(materialKey, out materialIndex))
                    {
                        try
                        {
                            var textureSet = await ResolveMaterialTextures(mid, meshType, appearance);
                            uint? diffuseId = textureSet.Diffuse ?? textureSet.FirstTexture;
                            uint? normalId = textureSet.Normal;

                            (byte[] png, uint surfaceId, int width, int height, string fourcc)? diffuse = null;
                            (byte[] png, uint surfaceId, int width, int height, string fourcc)? normal = null;

                            if (diffuseId.HasValue)
                            {
                                try { diffuse = await LoadTexturePng(diffuseId.Value); }
                                catch (Exception ex) { Console.WriteLine($"  material 0x{mid:X8}: DiffuseMap decode failed ({ex.Message})"); }
                            }
                            if (normalId.HasValue)
                            {
                                try { normal = await LoadTexturePng(normalId.Value); }
                                catch (Exception ex) { Console.WriteLine($"  material 0x{mid:X8}: NormalMap decode failed ({ex.Message})"); }
                            }

                            // Second diffuse layer, emissive/glow, and whether this material is actually
                            // transparent - none of which used to reach the GLB.
                            byte[]? diffusePng = diffuse.HasValue ? diffuse.Value.png : null;
                            byte[]? emissivePng = null;
                            bool alphaBlend = false;
                            bool unlitDecal = false;

                            uint? diffuse2Id = LookupTexture(textureSet.ByName, "DiffuseMap2");
                            if (diffuse2Id.HasValue && diffuseId.HasValue)
                            {
                                try
                                {
                                    var l1 = await LoadTextureRgba(diffuseId.Value);
                                    var l2 = await LoadTextureRgba(diffuse2Id.Value);
                                    if (l1.HasValue && l2.HasValue)
                                    {
                                        // Phase the ramp to the band's real UV start. These UVs do not
                                        // begin at 0 - Demise's band runs U 0.362..1.366, wrapping once -
                                        // so a plain sweep puts the ramp's red end wherever the band
                                        // crosses U=1, about a third of the way along, instead of at its
                                        // tip. Right gradient, wrong place.
                                        float rampPhase = uvStats.Count > 0 && float.IsFinite(uvStats[0].minU)
                                            ? uvStats[0].minU
                                            : 0f;
                                        var merged = CombineLayers(l1.Value.rgba, l1.Value.width, l1.Value.height,
                                                                   l2.Value.rgba, l2.Value.width, l2.Value.height,
                                                                   rampPhase, vertexColorByU);
                                        diffusePng = EncodePng(l1.Value.width, l1.Value.height, merged);
                                        alphaBlend = HasTransparency(merged);
                                        // Self-illuminate a composited decal layer. These bands read as
                                        // glowing runes in game, not as lit metal - the material sits in
                                        // the alpha-blend pass (AlphaBlendPass=1, DepthWrite=0) and owes
                                        // its brightness to the effect pass rather than to scene lighting,
                                        // so without emissive it renders as a dull painted-on stripe.
                                        emissivePng = diffusePng;
                                        unlitDecal = true;
                                        Console.WriteLine($"  material 0x{mid:X8}: DiffuseMap2 0x{diffuse2Id.Value:X8} composited into baseColorTexture ({l1.Value.width}x{l1.Value.height} x {l2.Value.width}x{l2.Value.height}){(alphaBlend ? ", alphaMode BLEND" : "")}");
                                    }
                                }
                                catch (Exception ex) { Console.WriteLine($"  material 0x{mid:X8}: DiffuseMap2 composite failed ({ex.Message})"); }
                            }
                            else if (diffuse.HasValue)
                            {
                                try
                                {
                                    var l1 = await LoadTextureRgba(diffuseId!.Value);
                                    if (l1.HasValue && HasTransparency(l1.Value.rgba))
                                    {
                                        alphaBlend = true;
                                        Console.WriteLine($"  material 0x{mid:X8}: diffuse carries alpha, alphaMode BLEND");
                                    }
                                }
                                catch { }
                            }

                            uint? glowId = LookupTexture(textureSet.ByName, "EmissiveMap") ?? LookupTexture(textureSet.ByName, "GlowMap");
                            if (glowId.HasValue)
                            {
                                try
                                {
                                    var glow = await LoadTexturePng(glowId.Value);
                                    if (glow.HasValue)
                                    {
                                        emissivePng = glow.Value.png;
                                        Console.WriteLine($"  material 0x{mid:X8}: EmissiveMap/GlowMap 0x{glowId.Value:X8} surface 0x{glow.Value.surfaceId:X8} -> GLB emissiveTexture");
                                    }
                                }
                                catch (Exception ex) { Console.WriteLine($"  material 0x{mid:X8}: glow decode failed ({ex.Message})"); }
                            }

                            if (diffuse.HasValue || normal.HasValue || emissivePng != null)
                            {
                                materialIndex = gltf.AddPngMaterial(
                                    $"Material_{mid:X8}",
                                    diffusePng,
                                    normal.HasValue ? normal.Value.png : null,
                                    emissivePng,
                                    alphaBlend,
                                    unlitDecal,
                                    textureSet.ByName,
                                    textureSet.State);

                                if (diffuse.HasValue)
                                    Console.WriteLine($"  material 0x{mid:X8}: DiffuseMap surface 0x{diffuse.Value.surfaceId:X8} {diffuse.Value.width}x{diffuse.Value.height} {diffuse.Value.fourcc} -> GLB baseColorTexture");
                                if (normal.HasValue)
                                    Console.WriteLine($"  material 0x{mid:X8}: NormalMap surface 0x{normal.Value.surfaceId:X8} {normal.Value.width}x{normal.Value.height} {normal.Value.fourcc} -> GLB normalTexture");

                                foreach (var kv in textureSet.ByName.Where(kv =>
                                    !kv.Key.Equals("DiffuseMap", StringComparison.OrdinalIgnoreCase) &&
                                    !kv.Key.Equals("NormalMap", StringComparison.OrdinalIgnoreCase) &&
                                    !kv.Key.Equals("DiffuseMap_Fallback", StringComparison.OrdinalIgnoreCase)))
                                    Console.WriteLine($"  material 0x{mid:X8}: discovered {kv.Key}=0x{kv.Value:X8}; preserved in GLB extras (not mapped to PBR yet)");
                            }
                            else
                            {
                                materialIndex = defaultMaterial;
                                Console.WriteLine($"  material 0x{mid:X8}: no decodable diffuse/normal texture, using neutral material");
                            }
                        }
                        catch(Exception ex)
                        {
                            materialIndex = defaultMaterial;
                            Console.WriteLine($"  material 0x{mid:X8}: material pipeline failed ({ex.Message})");
                        }
                        materialCache[materialKey] = materialIndex;
                    }
                }

                var attrs = new JsonObject
                {
                    ["POSITION"] = pAcc,
                    ["NORMAL"] = nAcc,
                    ["TEXCOORD_0"] = uAcc
                };
                if (cAcc >= 0) attrs["COLOR_0"] = cAcc;
                if (rigged)
                {
                    attrs["JOINTS_0"] = jAcc;
                    attrs["WEIGHTS_0"] = wAcc;
                }
                primitives.Add(new JsonObject
                {
                    ["attributes"] = attrs,
                    ["indices"] = iAcc,
                    ["material"] = materialIndex,
                    ["mode"] = 4
                });

                Console.WriteLine($"Mesh 0x{meshId:X8} set {si}: {verts.Count} verts, {idx.Count / 3} tris, {influences} influences");
            }

            if (primitives.Count == 0) continue;

            int gm = gltf.AddMesh($"Mesh_{meshId:X8}", primitives);
            var meshNode = new JsonObject
            {
                ["name"] = $"Mesh_{meshId:X8}",
                ["mesh"] = gm
            };
            if (rigged) meshNode["skin"] = skinIndex;
            int node = gltf.AddNode(meshNode);
            sceneRoots.Add(node);
        }

        if (gltf.Meshes.Count == 0) throw new Exception("No visible meshes were exported.");

        Console.WriteLine();
        if (rigged) Console.WriteLine("===== JOINT USAGE AUDIT (direct grouped indices) =====");
        for (int i = 0; rigged && i < joints.Count; i++)
        {
            if (jointUse[i] > 0)
                Console.WriteLine($"Joint {i,2} {joints[i].Name,-8}: refs={jointUse[i],5} totalWeight={jointWeight[i],8:F2}");
        }

        if (rigged && animationIds.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Embedding {animationIds.Count:N0} validated skeleton-family animation(s)…");
            int embedded = 0, skipped = 0;
            foreach (var animationId in animationIds)
            {
                Console.WriteLine($"Embedding validated animation 0x{animationId:X8}…");
                if (await AddAnimationAsync(gltf, animationId, joints.Count)) embedded++;
                else skipped++;
            }
            Console.WriteLine($"Animation bundle complete: {embedded:N0} embedded, {skipped:N0} skipped.");
        }

        gltf.WriteGlb(outFile, sceneRoots);
        Console.WriteLine();
        Console.WriteLine($"GLB written: {outFile}");
        Console.WriteLine(rigged ? "Export complete: rigged/textured GLB." : "Export complete: static/textured GLB.");
    }
}
