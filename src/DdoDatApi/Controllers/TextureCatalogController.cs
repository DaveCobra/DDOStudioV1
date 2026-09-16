using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace DdoDatApi.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class TextureCatalogController : ControllerBase
{
    static readonly object Gate = new();
    static List<TextureEntry>? _catalog;

    public sealed record TextureEntry(string Dat, uint Id);
    public sealed record TextureMeta(string Dat, uint Id, int Width, int Height, string Format, int PayloadBytes, bool Decodable);

    [HttpGet("ids")]
    public IActionResult Ids([FromQuery] bool refresh = false)
    {
        try
        {
            lock (Gate)
            {
                if (_catalog == null || refresh)
                    _catalog = BuildCatalog();
                return Ok(new { count = _catalog.Count, entries = _catalog });
            }
        }
        catch (Exception ex)
        {
            return Problem($"Texture catalog discovery failed: {ex.Message}");
        }
    }

    [HttpGet("{dat}/{id}/meta")]
    public IActionResult Meta(string dat, string id)
    {
        if (!TryId(id, out var did)) return BadRequest("Invalid texture surface ID.");
        var bytes = ReadSurface(dat, did);
        if (bytes == null) return NotFound();
        try
        {
            var p = ParseSurface(bytes);
            return Ok(new TextureMeta(dat, did, p.width, p.height, p.fourcc, p.payload.Length,
                p.fourcc is "DXT1" or "DXT3" or "DXT5"));
        }
        catch (Exception ex) { return BadRequest(ex.Message); }
    }

    [HttpGet("{dat}/{id}/png")]
    public IActionResult Png(string dat, string id)
    {
        if (!TryId(id, out var did)) return BadRequest("Invalid texture surface ID.");
        var bytes = ReadSurface(dat, did);
        if (bytes == null) return NotFound();
        try
        {
            var p = ParseSurface(bytes);
            byte[] rgba = p.fourcc switch
            {
                "DXT1" => DecodeBc1(p.width, p.height, p.payload),
                "DXT3" => DecodeBc2(p.width, p.height, p.payload),
                "DXT5" => DecodeBc3(p.width, p.height, p.payload),
                _ => throw new InvalidOperationException($"Unsupported RenderSurface format {p.fourcc}")
            };
            return File(EncodePng(p.width, p.height, rgba), "image/png", $"surface_{did:X8}.png");
        }
        catch (Exception ex) { return BadRequest(ex.Message); }
    }

    static bool TryId(string s, out uint id)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out id);
    }

    static byte[]? ReadSurface(string dat, uint id) => dat.ToLowerInvariant() switch
    {
        "highres" => DatSource.Highres?.GetFileContents(id),
        "general" => DatSource.GeneralDat?.GetFileContents(id),
        "surface" => DatSource.SurfaceDat?.GetFileContents(id),
        _ => null
    };

    static List<TextureEntry> BuildCatalog()
    {
        var result = new Dictionary<(string, uint), TextureEntry>();
        Discover("Highres", DatSource.Highres, result);
        Discover("General", DatSource.GeneralDat, result);
        Discover("Surface", DatSource.SurfaceDat, result);
        return result.Values.OrderBy(x => x.Id).ThenBy(x => x.Dat).ToList();
    }

    // VoK's IDatFile interface intentionally exposes only lookup. The concrete DAT reader
    // retains its file index internally, so discover it generically without depending on
    // private field names that can change between SDK builds.
    static void Discover(string datName, object? root, Dictionary<(string, uint), TextureEntry> dst)
    {
        if (root == null) return;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Walk(root, datName, dst, visited, 0);
    }

    static void Walk(object? value, string datName, Dictionary<(string, uint), TextureEntry> dst,
        HashSet<object> visited, int depth)
    {
        if (value == null || depth > 5) return;
        var t = value.GetType();
        if (t.IsPrimitive || value is string || value is byte[] || value is Type) return;
        if (!t.IsValueType && !visited.Add(value)) return;

        if (TryNumeric(value, out var direct)) AddIfSurface(datName, direct, dst);

        if (value is IDictionary dict)
        {
            int n = 0;
            foreach (DictionaryEntry e in dict)
            {
                if (++n > 2_000_000) break;
                if (TryNumeric(e.Key, out var k)) AddIfSurface(datName, k, dst);
                Walk(e.Value, datName, dst, visited, depth + 1);
            }
            return;
        }

        if (value is IEnumerable seq)
        {
            int n = 0;
            foreach (var item in seq)
            {
                if (++n > 2_000_000) break;
                if (TryNumeric(item, out var k)) AddIfSurface(datName, k, dst);
                else if (item != null)
                {
                    foreach (var name in new[] { "Id", "ID", "Did", "DID", "FileId", "FileID", "Key" })
                    {
                        var mv = GetMember(item, name);
                        if (TryNumeric(mv, out var mid)) AddIfSurface(datName, mid, dst);
                    }
                    if (depth < 3) Walk(item, datName, dst, visited, depth + 1);
                }
            }
            return;
        }

        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            try { Walk(f.GetValue(value), datName, dst, visited, depth + 1); } catch { }
        }
        foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (p.GetIndexParameters().Length != 0 || !p.CanRead) continue;
            try { Walk(p.GetValue(value), datName, dst, visited, depth + 1); } catch { }
        }
    }

    static object? GetMember(object o, string name)
    {
        var t = o.GetType();
        try { return t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o)
            ?? t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o); }
        catch { return null; }
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

    static void AddIfSurface(string dat, uint id, Dictionary<(string, uint), TextureEntry> dst)
    {
        if ((id & 0xFF000000u) != 0x41000000u) return;
        dst[(dat, id)] = new TextureEntry(dat, id);
    }

    static (int width, int height, string fourcc, byte[] payload) ParseSurface(byte[] data)
    {
        if (data.Length < 24) throw new Exception("RenderSurface header is truncated.");
        int width = checked((int)BitConverter.ToUInt32(data, 8));
        int height = checked((int)BitConverter.ToUInt32(data, 12));
        string fourcc = Encoding.ASCII.GetString(data, 16, 4);
        int size = checked((int)BitConverter.ToUInt32(data, 20));
        if (width <= 0 || height <= 0 || size < 0 || 24L + size > data.Length)
            throw new Exception("Invalid RenderSurface dimensions/payload.");
        return (width, height, fourcc, data.AsSpan(24, size).ToArray());
    }

    static void Color565(ushort c, out byte r, out byte g, out byte b)
    {
        int rr = (c >> 11) & 31, gg = (c >> 5) & 63, bb = c & 31;
        r = (byte)((rr * 255 + 15) / 31); g = (byte)((gg * 255 + 31) / 63); b = (byte)((bb * 255 + 15) / 31);
    }

    static void DecodeColorBlock(ReadOnlySpan<byte> src, Span<byte> rgba, int width, int height, int bx, int by, ReadOnlySpan<byte> alpha)
    {
        ushort c0 = (ushort)(src[0] | (src[1] << 8)), c1 = (ushort)(src[2] | (src[3] << 8));
        Color565(c0, out byte r0, out byte g0, out byte b0); Color565(c1, out byte r1, out byte g1, out byte b1);
        Span<byte> pal = stackalloc byte[16];
        pal[0]=r0; pal[1]=g0; pal[2]=b0; pal[3]=255; pal[4]=r1; pal[5]=g1; pal[6]=b1; pal[7]=255;
        pal[8]=(byte)((2*r0+r1)/3); pal[9]=(byte)((2*g0+g1)/3); pal[10]=(byte)((2*b0+b1)/3); pal[11]=255;
        pal[12]=(byte)((r0+2*r1)/3); pal[13]=(byte)((g0+2*g1)/3); pal[14]=(byte)((b0+2*b1)/3); pal[15]=255;
        uint bits = BitConverter.ToUInt32(src.Slice(4,4));
        for (int py=0;py<4;py++) for(int px=0;px<4;px++)
        {
            int x=bx*4+px,y=by*4+py,p=py*4+px; if(x>=width||y>=height) continue;
            int ci=(int)((bits>>(2*p))&3), d=(y*width+x)*4, s=ci*4;
            rgba[d]=pal[s]; rgba[d+1]=pal[s+1]; rgba[d+2]=pal[s+2]; rgba[d+3]=alpha[p];
        }
    }

    static byte[] DecodeBc1(int width,int height,byte[] src)
    {
        byte[] rgba=new byte[width*height*4]; int blocksX=(width+3)/4,blocksY=(height+3)/4,o=0;
        for(int by=0;by<blocksY;by++) for(int bx=0;bx<blocksX;bx++)
        {
            if(o+8>src.Length) throw new Exception("DXT1 payload truncated.");
            ushort c0=(ushort)(src[o]|(src[o+1]<<8)),c1=(ushort)(src[o+2]|(src[o+3]<<8));
            Color565(c0,out byte r0,out byte g0,out byte b0); Color565(c1,out byte r1,out byte g1,out byte b1);
            Span<byte> pal=stackalloc byte[16];
            pal[0]=r0;pal[1]=g0;pal[2]=b0;pal[3]=255;pal[4]=r1;pal[5]=g1;pal[6]=b1;pal[7]=255;
            if(c0>c1){pal[8]=(byte)((2*r0+r1)/3);pal[9]=(byte)((2*g0+g1)/3);pal[10]=(byte)((2*b0+b1)/3);pal[11]=255;pal[12]=(byte)((r0+2*r1)/3);pal[13]=(byte)((g0+2*g1)/3);pal[14]=(byte)((b0+2*b1)/3);pal[15]=255;}
            else{pal[8]=(byte)((r0+r1)/2);pal[9]=(byte)((g0+g1)/2);pal[10]=(byte)((b0+b1)/2);pal[11]=255;pal[12]=pal[13]=pal[14]=pal[15]=0;}
            uint bits=BitConverter.ToUInt32(src,o+4);
            for(int py=0;py<4;py++)for(int px=0;px<4;px++){int x=bx*4+px,y=by*4+py,q=py*4+px;if(x>=width||y>=height)continue;int ci=(int)((bits>>(2*q))&3),d=(y*width+x)*4,s=ci*4;rgba[d]=pal[s];rgba[d+1]=pal[s+1];rgba[d+2]=pal[s+2];rgba[d+3]=pal[s+3];}
            o+=8;
        }
        return rgba;
    }

    static byte[] DecodeBc2(int width,int height,byte[] src)
    {
        byte[] rgba=new byte[width*height*4]; int bxN=(width+3)/4,byN=(height+3)/4,o=0; Span<byte> alpha=stackalloc byte[16];
        for(int by=0;by<byN;by++)for(int bx=0;bx<bxN;bx++){if(o+16>src.Length)throw new Exception("DXT3 payload truncated.");ulong abits=BitConverter.ToUInt64(src,o);for(int i=0;i<16;i++)alpha[i]=(byte)(((abits>>(4*i))&0xF)*17);DecodeColorBlock(src.AsSpan(o+8,8),rgba,width,height,bx,by,alpha);o+=16;} return rgba;
    }

    static byte[] DecodeBc3(int width,int height,byte[] src)
    {
        byte[] rgba=new byte[width*height*4];int bxN=(width+3)/4,byN=(height+3)/4,o=0;Span<byte> alpha=stackalloc byte[16];Span<byte> ap=stackalloc byte[8];
        for(int by=0;by<byN;by++)for(int bx=0;bx<bxN;bx++){if(o+16>src.Length)throw new Exception("DXT5 payload truncated.");byte a0=src[o],a1=src[o+1];ap[0]=a0;ap[1]=a1;if(a0>a1){ap[2]=(byte)((6*a0+a1)/7);ap[3]=(byte)((5*a0+2*a1)/7);ap[4]=(byte)((4*a0+3*a1)/7);ap[5]=(byte)((3*a0+4*a1)/7);ap[6]=(byte)((2*a0+5*a1)/7);ap[7]=(byte)((a0+6*a1)/7);}else{ap[2]=(byte)((4*a0+a1)/5);ap[3]=(byte)((3*a0+2*a1)/5);ap[4]=(byte)((2*a0+3*a1)/5);ap[5]=(byte)((a0+4*a1)/5);ap[6]=0;ap[7]=255;}ulong idx=0;for(int i=0;i<6;i++)idx|=((ulong)src[o+2+i])<<(8*i);for(int i=0;i<16;i++)alpha[i]=ap[(int)((idx>>(3*i))&7)];DecodeColorBlock(src.AsSpan(o+8,8),rgba,width,height,bx,by,alpha);o+=16;}return rgba;
    }

    static uint[] CrcTable = BuildCrcTable();
    static uint[] BuildCrcTable(){var t=new uint[256];for(uint n=0;n<256;n++){uint c=n;for(int k=0;k<8;k++)c=(c&1)!=0?0xEDB88320u^(c>>1):c>>1;t[n]=c;}return t;}
    static uint Crc32(ReadOnlySpan<byte>a,ReadOnlySpan<byte>b){uint c=0xFFFFFFFFu;foreach(byte x in a)c=CrcTable[(c^x)&255]^(c>>8);foreach(byte x in b)c=CrcTable[(c^x)&255]^(c>>8);return c^0xFFFFFFFFu;}
    static void WriteBe(BinaryWriter bw,uint v)=>bw.Write(new[]{(byte)(v>>24),(byte)(v>>16),(byte)(v>>8),(byte)v});
    static void PngChunk(BinaryWriter bw,string type,byte[] data){byte[] tn=Encoding.ASCII.GetBytes(type);WriteBe(bw,(uint)data.Length);bw.Write(tn);bw.Write(data);WriteBe(bw,Crc32(tn,data));}
    static byte[] EncodePng(int width,int height,byte[] rgba)
    {
        using var raw=new MemoryStream();for(int y=0;y<height;y++){raw.WriteByte(0);raw.Write(rgba,y*width*4,width*4);}byte[] compressed;using(var cm=new MemoryStream()){using(var z=new ZLibStream(cm,CompressionLevel.Optimal,true))z.Write(raw.ToArray());compressed=cm.ToArray();}using var ms=new MemoryStream();using var bw=new BinaryWriter(ms);bw.Write(new byte[]{137,80,78,71,13,10,26,10});using(var ih=new MemoryStream()){using var ibw=new BinaryWriter(ih);WriteBe(ibw,(uint)width);WriteBe(ibw,(uint)height);ibw.Write((byte)8);ibw.Write((byte)6);ibw.Write((byte)0);ibw.Write((byte)0);ibw.Write((byte)0);PngChunk(bw,"IHDR",ih.ToArray());}PngChunk(bw,"IDAT",compressed);PngChunk(bw,"IEND",Array.Empty<byte>());return ms.ToArray();
    }
}
