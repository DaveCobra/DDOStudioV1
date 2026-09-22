using DdoDatApi.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using VoK.Sdk.Ddo;
using VoK.Sdk.Ddo.Enums;
using VoK.Sdk.Enums;
using VoK.Sdk.Properties;

namespace DdoDatApi.Caching;

public class IndexLoader {
    private const int CurrentSchemaVersion = 7;
    // Installed builds live under Program Files, which is intentionally read-only for
    // standard users. Keep every generated cache in LocalAppData instead.
    private static string DataDirectory {
        get {
            var configured = Environment.GetEnvironmentVariable("DDO_ASSET_STUDIO_DATA");
            var root = !string.IsNullOrWhiteSpace(configured)
                ? configured
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDO Asset Studio");
            var path = Path.Combine(root, "BackendCache");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string CachePath => Path.Combine(DataDirectory, "indexcache.json");
    public static string TreasureMapPath => Path.Combine(DataDirectory, "treasuremap.json");
    public static string RecipeMapPath => Path.Combine(DataDirectory, "recipemap.json");

    public static void RefreshCacheFromDats(CancellationToken token) {
        var indexData = new IndexData { SchemaVersion = CurrentSchemaVersion };
        var treasureMap = new Dictionary<uint, List<uint>>();
        var recipeMap = new Dictionary<uint, List<RecipeData>>();
        var glfs = DatSource.GameLogicDat.FileList;
        var dbpRange = DatSource.IdRanges.FirstOrDefault(r => r.Name == ObjectType.DbProperties.ToString());
        var timer = Stopwatch.StartNew();

        Console.WriteLine($"Building Cache from GameLogic dat file (scanning {glfs.Count} dat objects)...");
        var found = 0;
        var iter = 0;
        var updateFrequency = TimeSpan.FromSeconds(30);
        var lastUpdate = DateTime.UtcNow;

#if DEBUG
        updateFrequency = TimeSpan.FromSeconds(2);
#endif
        indexData.ClientVersion = DatSource.ClientFileInfo.FileVersion;
        indexData.CompiledOnUtc = DateTime.UtcNow;

        foreach (var glf in glfs) {
            if (token.IsCancellationRequested) return;

            var id = glf.Id;
            iter++;
            if (id >= 0x70000000 && id < 0x71000000)
                id += 0x09000000;

            if (dbpRange.IsInRange(id)) {
                var dbp = DatSource.PropertyMaster.GetPropertyCollection(id);
                if (dbp == null) continue;
                var wt = dbp.GetWeenieType();

                if (!indexData.WeenieTypes.ContainsKey(wt))
                    indexData.WeenieTypes.Add(wt, new List<uint>());
                if (!indexData.WeenieTypes[wt].Contains(id))
                    indexData.WeenieTypes[wt].Add(id);

                var name = NameGenerator.GetName(DatSource.PropertyMaster, dbp, null);
                if (IsEquippableWeenieType(wt))
                    indexData.Equipment[id] = BuildEquipmentIndexEntry(id, name, wt, dbp);

                var characterTemplate = BuildCharacterTemplateIndexEntry(id, name, dbp);
                if (characterTemplate != null)
                    indexData.CharacterTemplates[id] = characterTemplate;

                // The public model-name index keeps directly renderable standalone equipment
                // that has proven useful as a model asset (weapons and shields), while excluding
                // worn/composed equipment classes that do not reliably render as standalone models.
                bool libraryBrowsable = !IsEquippableWeenieType(wt) || IsLibraryBrowsableEquippableWeenieType(wt);
                if (!string.IsNullOrWhiteSpace(name) && libraryBrowsable) {
                    if (!indexData.NameLookup.ContainsKey(id))
                        indexData.NameLookup.Add(id, name);

                    if (!indexData.Names.ContainsKey(name))
                        indexData.Names.Add(name, new List<uint>());

                    if (!indexData.Names[name].Contains(id))
                        indexData.Names[name].Add(id);
                }

                if (wt == (uint)WeenieType.Spell)
                    indexData.Spells.Add(id);

                var questName = dbp.GetStringInfoProperty((uint)DdoProperty.Quest_Name);
                if (questName != null)
                    indexData.Quests.Add(new NamedItem() { Id = id, Name = questName.Text });

                var personality = dbp.GetEnumProperty((uint)DdoProperty.SentientPersonality);
                if (personality != null)
                    indexData.SentientPersonalities.Add(id);

                var treasureArray = dbp.GetProperty((uint)DdoProperty.Treasure_Array);
                if (treasureArray != null) {
                    indexData.TreasureTables.Add(id);
                    var items = new List<uint>();
                    CollectTreasureItems(dbp.Properties, items, new HashSet<uint> { id });
                    if (items.Count > 0)
                        treasureMap[id] = items;
                }

                var enhTree = dbp.GetProperty((uint)DdoProperty.EnhancementTree_Name);
                if (enhTree != null)
                    indexData.EnhancementTrees.Add(id);

                if (wt == 0x0000004F) {
                    var canBeUsed = dbp.GetBytePropertyValue((uint)DdoProperty.Usage_CanBeUsed) ?? 0;
                    if (canBeUsed > 0)
                        indexData.NPCs.Add(id);
                }

                if (wt == 0x00200081 && !string.IsNullOrWhiteSpace(name) && !IsExcludedDevice(name)) {
                    CollectRecipes(dbp, id, name, recipeMap);
                }

                found++;

                if (DateTime.UtcNow > lastUpdate + updateFrequency) {
                    var progress = 100 * iter / glfs.Count;
                    Console.WriteLine($"Progress: {progress}% ({found} through {iter} of {glfs.Count} objects)");
                    lastUpdate = DateTime.UtcNow;
                }
            }
        }

        Console.WriteLine($"Successfully built indexes over {found} items in {timer.Elapsed}.");

        Console.WriteLine($"Loading other miscellaneous index data...");
        indexData.WellKnown.Add("Feat Directory", (uint)Feat.FeatDirectory);
        indexData.WellKnown.Add("Base Skin Mappings", (uint)Uniquedb.BaseSkinMappings);

        // important to note - unless the SDK is rebuilt/updated, this won't change and get new values
        foreach (var wc in Enum.GetValues(typeof(Weeniecontent)))
            if (!indexData.WellKnown.ContainsKey(wc.ToString()))
                indexData.WellKnown.Add(wc.ToString(), (uint)wc);

        indexData.LastIndexDuration = timer.Elapsed;
        Console.WriteLine($"Miscellaneous stuff done.");

        DatCache.Index = indexData;
        DatCache.TreasureMap = treasureMap;
        DatCache.RecipeMap = recipeMap;

        BuildWeaponProficiencyMap();

        SaveJson(CachePath, indexData, "indexing data");
        SaveJson(TreasureMapPath, treasureMap, "treasure map");
        SaveJson(RecipeMapPath, recipeMap, "recipe map");
    }

    private static void SaveJson(string path, object data, string label) {
        using (StreamWriter sw = File.CreateText(path))
        using (JsonTextWriter writer = new JsonTextWriter(sw)) {
            JsonSerializer serializer = new JsonSerializer();
            writer.Formatting = Formatting.Indented;
            serializer.Serialize(writer, data);
        }

        var fileInfo = new FileInfo(path);
        var kb = fileInfo.Length / 1024;
        var mb = kb / 1024;
        var sizeStr = mb > 0 ? $"{mb}MB" : $"{kb}kb";
        Console.WriteLine($"Saved {label} ({sizeStr}) to {path}");
    }

    private static void CollectTreasureItems(IEnumerable<IProperty> properties, List<uint> items, HashSet<uint> visited) {
        foreach (var prop in properties) {
            if (prop.PropertyId == (uint)DdoProperty.Treasure_Entity && prop is IInt32Property i32) {
                var val = (uint)(i32.Int32Value ?? 0);
                if (val == 0 || !visited.Add(val))
                    continue;

                var child = DatSource.PropertyMaster.GetPropertyCollection(val);
                if (child == null)
                    continue;

                if (child.GetWeenieType() == 0)
                    CollectTreasureItems(child.Properties, items, visited);
                else {
                    var normalized = val >= 0x70000000 && val < 0x71000000 ? val + 0x09000000 : val;
                    if (!items.Contains(normalized))
                        items.Add(normalized);
                }
            }
            else if (prop is IArrayProperty arr) {
                CollectTreasureItems(arr.Properties, items, visited);
            }
        }
    }

    private static readonly string[] ExcludedDevicePrefixes =
    {
        "Ruby of", "Diamond of", "Topaz of", "Sapphire of",
        "Tome of", "Fragmented Tome of", "Upgrade Tome of",
        "Dust of", "+5 Ability", "Augments: Level",
        "Ability Score", "Test "
    };

    private static bool IsExcludedDevice(string name) {
        foreach (var prefix in ExcludedDevicePrefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static void CollectRecipes(IPropertyCollection device, uint deviceId, string deviceName, Dictionary<uint, List<RecipeData>> recipeMap) {
        var recipeList = device.GetArrayProperty((uint)DdoProperty.Device_Recipe_List);
        if (recipeList == null) return;

        foreach (var entry in recipeList.Properties) {
            if (entry.PropertyId != (uint)DdoProperty.Device_Recipe_Entry || entry is not IInt32Property recipeRef)
                continue;

            var recipeId = (uint)(recipeRef.Int32Value ?? 0);
            if (recipeId == 0) continue;

            var recipe = DatSource.PropertyMaster.GetPropertyCollection(recipeId);
            if (recipe == null) continue;

            var recipeName = recipe.GetStringInfoProperty((uint)DdoProperty.Recipe_Name)?.Text;
            var recipeDesc = recipe.GetStringInfoProperty((uint)DdoProperty.Recipe_Description)?.Text;

            var recipeData = new RecipeData {
                RecipeId = recipeId >= 0x70000000 && recipeId < 0x71000000 ? recipeId + 0x09000000 : recipeId,
                Name = recipeName,
                DeviceId = deviceId,
                DeviceName = deviceName
            };

            var slotList = recipe.GetArrayProperty((uint)DdoProperty.Recipe_Slot_List);
            if (slotList == null) continue;

            foreach (var slot in slotList.Properties) {
                if (slot.PropertyId != (uint)DdoProperty.Recipe_Slot_Entry || slot is not IInt32Property slotRef)
                    continue;

                var slotDefId = (uint)(slotRef.Int32Value ?? 0);
                if (slotDefId == 0) continue;

                var slotDef = DatSource.PropertyMaster.GetPropertyCollection(slotDefId);
                if (slotDef == null) continue;

                var ingredientProp = slotDef.GetProperty((uint)DdoProperty.Ingredient_Entity) as IInt32Property;
                if (ingredientProp == null) continue;

                var itemId = (uint)(ingredientProp.Int32Value ?? 0);
                if (itemId == 0) continue;

                var normalizedItemId = itemId >= 0x70000000 && itemId < 0x71000000 ? itemId + 0x09000000 : itemId;

                if (!recipeMap.ContainsKey(normalizedItemId))
                    recipeMap.Add(normalizedItemId, new List<RecipeData>());

                if (!recipeMap[normalizedItemId].Any(r => r.RecipeId == recipeData.RecipeId))
                    recipeMap[normalizedItemId].Add(recipeData);
            }
        }
    }


    private static CharacterTemplateIndexEntry? BuildCharacterTemplateIndexEntry(uint id, string name, IPropertyCollection props)
    {
        // Property IDs are intentionally numeric here. They are stable DDO property IDs and
        // avoid coupling the indexer to SDK enum-name changes.
        const uint CreatureSpecies = 0x10000ABD;
        const uint CharacterGender = 0x10000642;
        const uint PhysObj = 0x00000111;
        const uint AppearanceClassList = 0x000003AF;

        var species = props.GetEnumProperty(CreatureSpecies);
        var gender = props.GetEnumProperty(CharacterGender);
        var phys = props.GetInt32PropertyValue(PhysObj);
        if (species?.UInt32Value == null || species.UInt32Value.Value == 0 || phys == null || phys.Value == 0)
            return null;

        uint raceValue = species.UInt32Value.Value;
        uint genderValue = gender?.UInt32Value ?? 0;
        string race = Enum.IsDefined(typeof(RaceType), raceValue) ? (Enum.GetName(typeof(RaceType), raceValue) ?? $"0x{raceValue:X8}") : $"0x{raceValue:X8}";
        // Iconic heroes often share Creature_Species with their parent race. Preserve the
        // iconic identity when the record itself names the variant so Dressing Room can
        // expose those templates separately instead of collapsing everything to Human/etc.
        string lowerName = (name ?? "").ToLowerInvariant();
        if (lowerName.Contains("bladeforged")) race = "Bladeforged";
        else if (lowerName.Contains("deep gnome")) race = "Deep Gnome";
        else if (lowerName.Contains("purple dragon knight")) race = "Purple Dragon Knight";
        else if (lowerName.Contains("shadar-kai") || lowerName.Contains("shadar kai")) race = "Shadar-kai";
        else if (lowerName.Contains("scourge aasimar")) race = "Scourge Aasimar";
        else if (lowerName.Contains("tiefling scoundrel")) race = "Tiefling Scoundrel";
        else if (lowerName.Contains("wood elf ranger")) race = "Wood Elf Ranger";
        else if (lowerName.Contains("razorclaw shifter")) race = "Razorclaw Shifter";
        else if (lowerName.Contains("tabaxi trailblazer")) race = "Tabaxi Trailblazer";
        else if (lowerName.Contains("eladrin chaosmancer")) race = "Eladrin Chaosmancer";
        else race = NormalizePlayableRaceName(race);
        string genderName = genderValue switch
        {
            0x00001000 => "Male",
            0x00002000 => "Female",
            0 => "Unspecified",
            _ => $"0x{genderValue:X8}"
        };

        bool hasAppearance = props.GetProperty(AppearanceClassList) != null;
        string n = name ?? $"0x{id:X8}";
        int score = 0;
        // Prefer records that look like reusable/base player bodies, but keep every
        // structural candidate available so the user can choose a specific model.
        if (n.Contains("base", StringComparison.OrdinalIgnoreCase)) score += 50;
        if (n.Contains("player", StringComparison.OrdinalIgnoreCase)) score += 40;
        if (n.Contains("character", StringComparison.OrdinalIgnoreCase)) score += 20;
        if (hasAppearance) score += 10;
        if (genderValue != 0) score += 5;

        return new CharacterTemplateIndexEntry
        {
            Id = id,
            Name = n,
            RaceValue = raceValue,
            Race = race,
            GenderValue = genderValue,
            Gender = genderName,
            PhysObj = unchecked((uint)phys.Value),
            HasAppearanceClassList = hasAppearance,
            CandidateScore = score
        };
    }

    private static string NormalizePlayableRaceName(string race)
    {
        string key = new string((race ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return key switch
        {
            "human" => "Human",
            "elf" => "Elf",
            "dwarf" => "Dwarf",
            "halfling" => "Halfling",
            "halforc" => "Half-Orc",
            "halfelf" => "Half-Elf",
            "dragonborn" => "Dragonborn",
            "tiefling" => "Tiefling",
            "gnome" => "Gnome",
            "warforged" => "Warforged",
            "drow" or "drowelf" => "Drow",
            "aasimar" => "Aasimar",
            "woodelf" => "Wood Elf",
            "shifter" => "Shifter",
            "tabaxi" => "Tabaxi",
            "eladrin" => "Eladrin",
            "korobokuru" => "Korobokuru",
            _ => race
        };
    }

    private static bool IsEquippableWeenieType(uint wt) => wt is WeenieTypes.Weapon or WeenieTypes.Shield or WeenieTypes.Armor or WeenieTypes.Jewelry or WeenieTypes.Clothing;
    private static bool IsLibraryBrowsableEquippableWeenieType(uint wt) => wt is WeenieTypes.Weapon or WeenieTypes.Shield;

    private static EquipmentIndexEntry BuildEquipmentIndexEntry(uint id, string name, uint wt, IPropertyCollection props)
    {
        var compatibleRaw = GetBitFieldValues(props, (uint)DdoProperty.Inventory_CompatibleSlot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var precludedRaw = GetBitFieldValues(props, (uint)DdoProperty.Inventory_PrecludedSlot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var compatible = compatibleRaw.Where(x => x is not "Equipment" and not "Backpack").Select(MapSlotName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var precluded = precludedRaw.Where(x => x is not "Equipment" and not "Backpack").Select(MapSlotName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        string weaponType = wt == WeenieTypes.Weapon ? GetEnumDisplayName<WeaponType>(props, (uint)DdoProperty.Combat_WeaponType) ?? "" : "";
        string armorType = wt == WeenieTypes.Armor ? GetEnumDisplayName<ArmorType>(props, (uint)DdoProperty.Combat_ArmorType) ?? "" : "";
        bool canMain = compatibleRaw.Contains("Weapon1", StringComparer.OrdinalIgnoreCase);
        bool canOff = compatibleRaw.Contains("Weapon2", StringComparer.OrdinalIgnoreCase);
        bool twoHanded = wt == WeenieTypes.Weapon && precludedRaw.Contains("Weapon2", StringComparer.OrdinalIgnoreCase);

        return new EquipmentIndexEntry
        {
            Id = id,
            Name = name ?? $"0x{id:X8}",
            WeenieType = wt,
            WeenieTypeName = Enum.GetName(typeof(WeenieType), wt) ?? $"0x{wt:X8}",
            EquipmentKind = ClassifyEquipmentKind(wt, compatibleRaw),
            CompatibleSlots = compatible,
            CompatibleSlotsRaw = compatibleRaw,
            PrecludedSlots = precluded,
            PrecludedSlotsRaw = precludedRaw,
            PrimarySlot = compatible.FirstOrDefault() ?? "",
            WeaponType = weaponType,
            ArmorType = armorType,
            CanMainHand = canMain,
            CanOffHand = canOff,
            IsTwoHanded = twoHanded
        };
    }

    private static string ClassifyEquipmentKind(uint wt, List<string> slots)
    {
        if (wt == WeenieTypes.Weapon) return "Weapon";
        if (wt == WeenieTypes.Shield) return "Shield";
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
        if (wt == WeenieTypes.Armor) return "Armor";
        if (wt is WeenieTypes.Jewelry or WeenieTypes.Clothing) return "Accessory";
        return "Equippable Item";
    }

    private static IEnumerable<string> GetBitFieldValues(IPropertyCollection props, uint propertyId)
    {
        var prop = props.GetBitFieldProperty(propertyId);
        return prop?.Values ?? Enumerable.Empty<string>();
    }

    private static string? GetEnumDisplayName<TEnum>(IPropertyCollection props, uint propertyId) where TEnum : struct, Enum
    {
        var prop = props.GetEnumProperty(propertyId);
        if (prop?.UInt32Value == null) return null;
        return Enum.IsDefined(typeof(TEnum), prop.UInt32Value.Value) ? Enum.GetName(typeof(TEnum), prop.UInt32Value.Value) : null;
    }

    private static string MapSlotName(string slot) => slot switch
    {
        "Weapon1" => "Main Hand",
        "Weapon2" => "Off Hand",
        "Finger1" => "First Finger",
        "Finger2" => "Second Finger",
        "Head" => "Head",
        "Neck" => "Neck",
        "Trinket" => "Trinket",
        "Cloak" => "Cloak",
        "Arms" => "Wrists",
        "Hands" => "Hands",
        "Chest" => "Armor",
        "Legs" => "Legs",
        "Feet" => "Feet",
        "Belt" => "Waist",
        _ => slot
    };

    public static void LoadIndexCache() {
        if (File.Exists(CachePath)) {
            var loaded = LoadJson<IndexData>(CachePath, "indexing data");
            if (loaded != null && loaded.SchemaVersion >= CurrentSchemaVersion && loaded.Equipment != null && loaded.CharacterTemplates != null)
                DatCache.Index = loaded;
            else
            {
                Console.WriteLine("Cached index uses an older schema; DDO Studio will rebuild the model-search index.");
                DatCache.Index = new IndexData();
            }
        }

        if (File.Exists(TreasureMapPath)) {
            DatCache.TreasureMap = LoadJson<Dictionary<uint, List<uint>>>(TreasureMapPath, "treasure map");
        }

        if (File.Exists(RecipeMapPath)) {
            DatCache.RecipeMap = LoadJson<Dictionary<uint, List<RecipeData>>>(RecipeMapPath, "recipe map");
        }

        BuildWeaponProficiencyMap();
    }

    private static void BuildWeaponProficiencyMap()
    {
        DatCache.SimpleProficiency  = DatSource.PropertyMaster.GetPropertyCollection(DatCache.SimpleProficiencyId);
        DatCache.MartialProficiency = DatSource.PropertyMaster.GetPropertyCollection(DatCache.MartialProficiencyId);
        DatCache.ExoticProficiency  = DatSource.PropertyMaster.GetPropertyCollection(DatCache.ExoticProficiencyId);

        var baseNames = new Dictionary<uint, string>();
        if (DatCache.SimpleProficiency?.Name  != null) baseNames[DatCache.SimpleProficiencyId]  = DatCache.SimpleProficiency.Name;
        if (DatCache.MartialProficiency?.Name != null) baseNames[DatCache.MartialProficiencyId] = DatCache.MartialProficiency.Name;
        if (DatCache.ExoticProficiency?.Name  != null) baseNames[DatCache.ExoticProficiencyId]  = DatCache.ExoticProficiency.Name;

        var map = new Dictionary<uint, string>();

        foreach (var (name, ids) in DatCache.Index.Names)
        {
            if (!name.StartsWith("Proficiency: ", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var id in ids)
            {
                var props = DatSource.PropertyMaster.GetPropertyCollection(id);
                if (props == null) continue;

                var weaponTypeProp = props.GetEnumProperty((uint)DdoProperty.Feat_WeaponType);
                if (weaponTypeProp == null || weaponTypeProp.UInt32Value == 0) continue;

                var baseFeatRaw = props.GetInt32PropertyValue((uint)DdoProperty.Feat_BaseFeat);
                if (baseFeatRaw == null || baseFeatRaw == 0) continue;

                var baseFeatId = (uint)baseFeatRaw.Value;
                if (baseFeatId >= 0x70000000 && baseFeatId < 0x71000000)
                    baseFeatId += 0x09000000;

                if (baseNames.TryGetValue(baseFeatId, out var profName))
                    map[weaponTypeProp.UInt32Value.Value] = profName;
            }
        }

        DatCache.WeaponProficiencyNames = map;
        Console.WriteLine($"Built weapon proficiency map ({map.Count} weapon types).");
    }

    private static T LoadJson<T>(string path, string label) {
        var fileInfo = new FileInfo(path);
        var kb = fileInfo.Length / 1024;
        var mb = kb / 1024;
        var sizeStr = mb > 0 ? $"{mb}MB" : $"{kb}kb";

        using (FileStream fileStream = File.Open(path, FileMode.Open))
        using (StreamReader streamReader = new StreamReader(fileStream))
        using (JsonTextReader jsonReader = new JsonTextReader(streamReader)) {
            JsonSerializer serializer = new JsonSerializer();
            var data = serializer.Deserialize<T>(jsonReader);
            Console.WriteLine($"Loaded {label} ({sizeStr}) from {path}");
            return data;
        }
    }
}
