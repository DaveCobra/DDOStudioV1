using DdoDatApi.Models;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using VoK.Sdk;
using VoK.Sdk.Common;
using VoK.Sdk.Dat;
using VoK.Sdk.Ddo;
using VoK.Sdk.Exporters;
using VoK.Sdk.Properties;

namespace DdoDatApi;

public static class DatSource
{
    // having problems finding your DDO path automatically? set it here!
    private const string _ddoInstallPath = null;

    private static IDatFile _gameLogic;
    private static IDatFile _general;
    private static IDatFile _sound;
    private static IDatFile _localEnglish;
    private static IDatFile _surface;
    private static IDatFile _anim;
    private static IDatFile _cell1;
    private static IDatFile _cell2;
    private static IDatFile _cell3;
    private static IDatFile _cell4;
    private static IDatFile _highres;
    private static IDatFile _map1;
    private static IDatFile _map2;
    private static IDatFile _map3;
    private static IDatFile _map4;
    private static IDatFile _mesh;
    private static IPropertyMaster _propertyMaster;
    private static IImageExporter _imageExporter;
    private static ISoundExporter _soundExporter;
    private static FileVersionInfo _clientFileInfo;

/// <summary>Resolve DDO from the GUI-provided environment variable, then registry, then common install path.</summary>
    public static string GetDdoMainInstallPath()
    {
        var env = Environment.GetEnvironmentVariable("DDO_INSTALL_PATH");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;

        const string keyPath = "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\bc8a6440-918f-11dd-ad8b-0800200c9a66_is1";
        try
        {
            using var regKey = Registry.LocalMachine.OpenSubKey(keyPath);
            var reg = regKey?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrWhiteSpace(reg) && Directory.Exists(reg)) return reg;
        }
        catch { }

        var common = @"C:\Program Files (x86)\StandingStoneGames\Dungeons & Dragons Online";
        return Directory.Exists(common) ? common : null;
    }

    public static void Load()
    {
        var ddoPath = string.IsNullOrWhiteSpace(_ddoInstallPath) ? GetDdoMainInstallPath() : _ddoInstallPath;
        if (string.IsNullOrEmpty(ddoPath))
            throw new ArgumentException("Unable to locate DDO installation. Set the path in DatSource.cs");

        _gameLogic = DatFactory.LoadDatFile(Path.Combine(ddoPath, "client_gamelogic.dat"));
        _general = DatFactory.LoadDatFile(Path.Combine(ddoPath, "client_general.dat"));
        _sound = DatFactory.LoadDatFile(Path.Combine(ddoPath, "client_sound.dat"));
        _localEnglish = DatFactory.LoadDatFile(Path.Combine(ddoPath, "client_local_English.dat"));
        _surface = DatFactory.LoadDatFile(Path.Combine(ddoPath, "client_surface.dat"));
        _anim = DatFactory.LoadDatFile(Path.Combine(ddoPath, "client_anim.dat"));
        _cell1 = DatFactory.LoadDatFile(Path.Combine(ddoPath, "client_cell_1.dat"));
        _cell2 = DatFactory.LoadDatFile(Path.Combine(ddoPath, "client_cell_2.dat"));
        _cell3 = DatFactory.LoadDatFile(Path.Combine(ddoPath, "client_cell_3.dat"));
        _cell4 = DatFactory.LoadDatFile(Path.Combine(ddoPath, "client_cell_4.dat"));
        _highres = DatFactory.LoadDatFile(Path.Combine(ddoPath, "client_highres.dat"));
        _map1 = DatFactory.LoadDatFile(Path.Combine(ddoPath, "client_map_1.dat"));
        _map2 = DatFactory.LoadDatFile(Path.Combine(ddoPath, "client_map_2.dat"));
        _map3 = DatFactory.LoadDatFile(Path.Combine(ddoPath, "client_map_3.dat"));
        _map4 = DatFactory.LoadDatFile(Path.Combine(ddoPath, "client_map_4.dat"));
        _mesh = DatFactory.LoadDatFile(Path.Combine(ddoPath, "client_mesh.dat"));

        _propertyMaster = PropertyMasterFactory.GetPropertyMaster(GameId.DDO, ddoPath);
        _imageExporter = ExporterFactory.GetImageExporter(ddoPath);
        _soundExporter = ExporterFactory.GetSoundExporter(ddoPath);
        _clientFileInfo = FileVersionInfo.GetVersionInfo(Path.Combine(ddoPath, "dndclient.exe"));
    }

    public static IDatFile GameLogicDat => _gameLogic;
    public static IDatFile GeneralDat => _general;
    public static IDatFile SoundDat => _sound;
    public static IDatFile LocalEnglishDat => _localEnglish;
    public static IDatFile SurfaceDat => _surface;
    public static IDatFile AnimDat => _anim;
    public static IDatFile Cell1 => _cell1;
    public static IDatFile Cell2 => _cell2;
    public static IDatFile Cell3 => _cell3;
    public static IDatFile Cell4 => _cell4;
    public static IDatFile Highres => _highres;
    public static IDatFile Map1 => _map1;
    public static IDatFile Map2 => _map2;
    public static IDatFile Map3 => _map3;
    public static IDatFile Map4 => _map4;
    public static IDatFile Mesh => _mesh;
    public static IPropertyMaster PropertyMaster => _propertyMaster;
    public static IImageExporter ImageExporter => _imageExporter;
    public static ISoundExporter SoundExporter => _soundExporter;
    public static FileVersionInfo ClientFileInfo => _clientFileInfo;
    public static List<DatIdRange> IdRanges { get; } = GetIdRanges();

    private static List<DatIdRange> GetIdRanges()
    {
        var results = new List<DatIdRange>();
        var enumType = typeof(ObjectType);
        var vals = Enum.GetValues(enumType);
        foreach (Enum objType in vals)
        {
            var descAttr = objType.GetAttributeOfType<DescriptionAttribute>();
            if (descAttr == null || descAttr.Description == "Invalid") continue;

            var rangeAttr = objType.GetAttributeOfType<IdRangeAttribute>();
            if (rangeAttr == null) continue;

            results.Add(new DatIdRange() { Name =objType.ToString(), Description = descAttr.Description, Minimum = rangeAttr.MinValue, Maximum = rangeAttr.MaxValue });
        }

        return results.OrderBy(k => k.Minimum).ToList();
    }
}
