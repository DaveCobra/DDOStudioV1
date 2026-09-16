using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace DdoDatApi.Controllers;

/// <summary>
/// Native decoder for the older Havok binary-tag animation records used by DDO's Anim DAT.
/// It intentionally does not depend on Havok SDK binaries or external converters.
///
/// Current supported payloads:
///   hkaSplineCompressedAnimation
///   hkaInterleavedUncompressedAnimation
///   spline scalar quantization: 8-bit / 16-bit
///   spline rotation quantization: POLAR32, THREECOMP40, THREECOMP48, UNCOMPRESSED
///   spline degrees 0..3
///
/// DDO wraps the binary tagfile with a tiny record header. The real Havok tagfile is located
/// by its CAB00D1E / D011FACE magic pair, so the decoder does not assume a fixed wrapper size.
/// Large records are scanned through the full binary tagfile rather than only the first 64 KiB.
/// </summary>
public static class DdoHavokAnimationDecoder
{
    public sealed class DecodeResult
    {
        public bool Success { get; set; }
        public string Error { get; set; } = "";
        public int HavokMagicOffset { get; set; }
        public int AnimationObjectOffset { get; set; }
        public string AnimationType { get; set; } = "";
        public float Duration { get; set; }
        public int TransformTrackCount { get; set; }
        public int FloatTrackCount { get; set; }
        public int NumFrames { get; set; }
        public int NumBlocks { get; set; }
        public int MaxFramesPerBlock { get; set; }
        public int MaskAndQuantizationSize { get; set; }
        public float BlockDuration { get; set; }
        public float BlockInverseDuration { get; set; }
        public float FrameDuration { get; set; }
        public int DataStart { get; set; }
        public int DataLength { get; set; }
        public int[] BlockOffsets { get; set; } = Array.Empty<int>();
        public int[] FloatBlockOffsets { get; set; } = Array.Empty<int>();
        public List<TrackSummary> Tracks { get; set; } = new();
        public List<FrameSample> Frames { get; set; } = new();
        public string Note { get; set; } = "";
    }

    public sealed class TrackSummary
    {
        public int Track { get; set; }
        public string Position { get; set; } = "identity";
        public string Rotation { get; set; } = "identity";
        public string Scale { get; set; } = "identity";
        public string PositionQuantization { get; set; } = "";
        public string RotationQuantization { get; set; } = "";
        public string ScaleQuantization { get; set; } = "";
    }

    public sealed class FrameSample
    {
        public int Frame { get; set; }
        public float Time { get; set; }
        public List<TransformSample> Transforms { get; set; } = new();
    }

    public sealed class TransformSample
    {
        public int Track { get; set; }
        public float[] Translation { get; set; } = new float[3];
        public float[] Rotation { get; set; } = new float[4];
        public float[] Scale { get; set; } = new float[3];
    }

    enum ScalarQuantization
    {
        Bits8 = 0,
        Bits16 = 1
    }

    enum RotationQuantization
    {
        Polar32 = 0,
        ThreeComp40 = 1,
        ThreeComp48 = 2,
        ThreeComp24 = 3,
        Straight16 = 4,
        Uncompressed = 5
    }

    [Flags]
    enum ChannelFlags : byte
    {
        StaticX = 1,
        StaticY = 2,
        StaticZ = 4,
        StaticW = 8,
        SplineX = 16,
        SplineY = 32,
        SplineZ = 64,
        SplineW = 128
    }

    sealed class Header
    {
        public int MagicOffset;
        public int ObjectOffset;
        public float Duration;
        public int Tracks;
        public int FloatTracks;
        public int NumFrames;
        public int NumBlocks;
        public int MaxFramesPerBlock;
        public int MaskSize;
        public float BlockDuration;
        public float BlockInverseDuration;
        public float FrameDuration;
        public List<int> BlockOffsets = new();
        public List<int> FloatBlockOffsets = new();
        public int DataStart;
        public int DataLength;
    }

    sealed class Mask
    {
        public ScalarQuantization PosQuant;
        public RotationQuantization RotQuant;
        public ScalarQuantization ScaleQuant;
        public ChannelFlags PosFlags;
        public ChannelFlags RotFlags;
        public ChannelFlags ScaleFlags;
    }

    sealed class ScalarChannel
    {
        public bool Exists;
        public bool Dynamic;
        public float StaticValue;
        public int Degree;
        public List<byte> Knots = new();
        public List<float> Values = new();
    }

    sealed class VectorSpline
    {
        public ScalarChannel X = new();
        public ScalarChannel Y = new();
        public ScalarChannel Z = new();
    }

    sealed class QuaternionSpline
    {
        public bool Exists;
        public bool Dynamic;
        public Quaternion StaticValue = Quaternion.Identity;
        public int Degree;
        public List<byte> Knots = new();
        public List<Quaternion> Values = new();
    }

    sealed class Track
    {
        public Mask Mask = new();
        public Vector3 StaticPosition = Vector3.Zero;
        public Quaternion StaticRotation = Quaternion.Identity;
        public Vector3 StaticScale = Vector3.One;
        public VectorSpline? PositionSpline;
        public QuaternionSpline Rotation = new();
        public VectorSpline? ScaleSpline;
    }

    public static DecodeResult Decode(byte[] bytes, bool includeFrames = true)
    {
        var result = new DecodeResult();
        try
        {
            if (bytes == null || bytes.Length < 32) throw new InvalidDataException("Animation record is too small.");

            // DDO ships more than one Havok animation subtype. Interleaved/uncompressed
            // clips store one full hkQsTransform (12 floats / 48 bytes) per track per frame.
            // Decode those directly before falling back to the spline-compressed path.
            if (ContainsAscii(bytes, "hkaInterleavedUncompressedAnimation"))
                return DecodeInterleaved(bytes, includeFrames);

            var h = FindAndParseHeader(bytes);
            result.HavokMagicOffset = h.MagicOffset;
            result.AnimationObjectOffset = h.ObjectOffset;
            result.AnimationType = "hkaSplineCompressedAnimation";
            result.Duration = h.Duration;
            result.TransformTrackCount = h.Tracks;
            result.FloatTrackCount = h.FloatTracks;
            result.NumFrames = h.NumFrames;
            result.NumBlocks = h.NumBlocks;
            result.MaxFramesPerBlock = h.MaxFramesPerBlock;
            result.MaskAndQuantizationSize = h.MaskSize;
            result.BlockDuration = h.BlockDuration;
            result.BlockInverseDuration = h.BlockInverseDuration;
            result.FrameDuration = h.FrameDuration;
            result.DataStart = h.DataStart;
            result.DataLength = h.DataLength;
            result.BlockOffsets = h.BlockOffsets.ToArray();
            result.FloatBlockOffsets = h.FloatBlockOffsets.ToArray();

            if (h.Tracks <= 0 || h.Tracks > 4096) throw new InvalidDataException($"Implausible transform-track count: {h.Tracks}.");
            if (h.NumBlocks <= 0 || h.NumBlocks > 1024) throw new InvalidDataException($"Implausible block count: {h.NumBlocks}.");
            if (h.DataStart < 0 || h.DataLength <= 0 || h.DataStart + h.DataLength > bytes.Length)
                throw new InvalidDataException("Spline data range points outside the record.");

            var blocks = new List<Track[]>();
            for (int bi = 0; bi < h.NumBlocks; bi++)
            {
                int rel = bi < h.BlockOffsets.Count ? h.BlockOffsets[bi] : 0;
                int blockStart = h.DataStart + rel;
                if (blockStart < h.DataStart || blockStart >= h.DataStart + h.DataLength)
                    throw new InvalidDataException($"Block {bi} starts outside animation data.");
                blocks.Add(ParseBlock(bytes, blockStart, h.Tracks, h.MaskSize));
            }

            var first = blocks[0];
            for (int i = 0; i < first.Length; i++)
            {
                var t = first[i];
                result.Tracks.Add(new TrackSummary
                {
                    Track = i,
                    Position = DescribeVector(t.Mask.PosFlags),
                    Rotation = DescribeRotation(t.Mask.RotFlags),
                    Scale = DescribeVector(t.Mask.ScaleFlags),
                    PositionQuantization = t.Mask.PosQuant.ToString(),
                    RotationQuantization = t.Mask.RotQuant.ToString(),
                    ScaleQuantization = t.Mask.ScaleQuant.ToString()
                });
            }

            if (includeFrames && h.NumFrames > 0)
            {
                for (int frame = 0; frame < h.NumFrames; frame++)
                {
                    int blockIndex = Math.Min(blocks.Count - 1, frame / Math.Max(1, h.MaxFramesPerBlock));
                    float localFrame = frame - blockIndex * Math.Max(1, h.MaxFramesPerBlock);
                    var fs = new FrameSample
                    {
                        Frame = frame,
                        Time = h.FrameDuration > 0 ? frame * h.FrameDuration : (h.NumFrames > 1 ? h.Duration * frame / (h.NumFrames - 1) : 0)
                    };
                    var tracks = blocks[blockIndex];
                    for (int ti = 0; ti < tracks.Length; ti++)
                    {
                        var tr = tracks[ti];
                        Vector3 p = SampleVector(tr.PositionSpline, tr.StaticPosition, Vector3.Zero, localFrame);
                        Quaternion q = SampleQuaternion(tr.Rotation, tr.StaticRotation, localFrame);
                        Vector3 s = SampleVector(tr.ScaleSpline, tr.StaticScale, Vector3.One, localFrame);
                        fs.Transforms.Add(new TransformSample
                        {
                            Track = ti,
                            Translation = new[] { p.X, p.Y, p.Z },
                            Rotation = new[] { q.X, q.Y, q.Z, q.W },
                            Scale = new[] { s.X, s.Y, s.Z }
                        });
                    }
                    result.Frames.Add(fs);
                }
            }

            result.Success = true;
            result.Note = "Native DDO Havok spline decode succeeded, including padded mask/quantization regions. Track indices are decoded, but DDO's track-to-skeleton-joint binding still has to be resolved before a clip can be safely attached to a model.";
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            result.Note = "The record was preserved raw. Decoder support can be extended without losing the source data.";
            return result;
        }
    }


    sealed class InterleavedHeader
    {
        public int MagicOffset;
        public int ObjectOffset;
        public float Duration;
        public int Tracks;
        public int FloatTracks;
        public int NumFrames;
        public int TransformCount;
        public int DataStart;
        public int DataLength;
        public float FrameDuration;
    }

    static DecodeResult DecodeInterleaved(byte[] bytes, bool includeFrames)
    {
        var result = new DecodeResult();
        try
        {
            var h = FindAndParseInterleavedHeader(bytes);
            result.HavokMagicOffset = h.MagicOffset;
            result.AnimationObjectOffset = h.ObjectOffset;
            result.AnimationType = "hkaInterleavedUncompressedAnimation";
            result.Duration = h.Duration;
            result.TransformTrackCount = h.Tracks;
            result.FloatTrackCount = h.FloatTracks;
            result.NumFrames = h.NumFrames;
            result.NumBlocks = 1;
            result.MaxFramesPerBlock = h.NumFrames;
            result.FrameDuration = h.FrameDuration;
            result.DataStart = h.DataStart;
            result.DataLength = h.DataLength;
            result.BlockOffsets = new[] { 0 };

            for (int ti = 0; ti < h.Tracks; ti++)
            {
                result.Tracks.Add(new TrackSummary
                {
                    Track = ti,
                    Position = "interleaved",
                    Rotation = "interleaved",
                    Scale = "interleaved",
                    PositionQuantization = "Uncompressed",
                    RotationQuantization = "Uncompressed",
                    ScaleQuantization = "Uncompressed"
                });
            }

            if (includeFrames)
            {
                int p = h.DataStart;
                for (int frame = 0; frame < h.NumFrames; frame++)
                {
                    var fs = new FrameSample
                    {
                        Frame = frame,
                        Time = h.FrameDuration > 0 ? frame * h.FrameDuration : (h.NumFrames > 1 ? h.Duration * frame / (h.NumFrames - 1) : 0)
                    };

                    for (int ti = 0; ti < h.Tracks; ti++)
                    {
                        Ensure(bytes, p, 48);
                        float tx = BitConverter.ToSingle(bytes, p + 0);
                        float ty = BitConverter.ToSingle(bytes, p + 4);
                        float tz = BitConverter.ToSingle(bytes, p + 8);
                        // translation.w is padding/auxiliary data for hkQsTransform
                        float qx = BitConverter.ToSingle(bytes, p + 16);
                        float qy = BitConverter.ToSingle(bytes, p + 20);
                        float qz = BitConverter.ToSingle(bytes, p + 24);
                        float qw = BitConverter.ToSingle(bytes, p + 28);
                        float sx = BitConverter.ToSingle(bytes, p + 32);
                        float sy = BitConverter.ToSingle(bytes, p + 36);
                        float sz = BitConverter.ToSingle(bytes, p + 40);
                        // scale.w is likewise not needed by Three.js/glTF transforms.
                        p += 48;

                        if (!Finite(tx) || !Finite(ty) || !Finite(tz) ||
                            !Finite(qx) || !Finite(qy) || !Finite(qz) || !Finite(qw) ||
                            !Finite(sx) || !Finite(sy) || !Finite(sz))
                            throw new InvalidDataException($"Interleaved transform contains NaN/Infinity at frame {frame}, track {ti}.");

                        var q = new Quaternion(qx, qy, qz, qw);
                        float qLen = q.Length();
                        if (qLen > 1e-8f) q = Quaternion.Normalize(q);
                        else q = Quaternion.Identity;

                        fs.Transforms.Add(new TransformSample
                        {
                            Track = ti,
                            Translation = new[] { tx, ty, tz },
                            Rotation = new[] { q.X, q.Y, q.Z, q.W },
                            Scale = new[] { sx, sy, sz }
                        });
                    }
                    result.Frames.Add(fs);
                }
            }

            result.Success = true;
            result.Note = $"Native DDO Havok interleaved decode succeeded: {h.NumFrames} frames × {h.Tracks} tracks ({h.TransformCount} hkQsTransform records).";
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            result.Note = "The record was preserved raw. Decoder support can be extended without losing the source data.";
            return result;
        }
    }

    static InterleavedHeader FindAndParseInterleavedHeader(byte[] b)
    {
        byte[] magic = { 0x1E, 0x0D, 0xB0, 0xCA, 0xCE, 0xFA, 0x11, 0xD0 };
        int magicAt = IndexOf(b, magic);
        if (magicAt < 0) throw new InvalidDataException("Havok binary-tag magic was not found.");

        int classAt = IndexOf(b, System.Text.Encoding.ASCII.GetBytes("hkaInterleavedUncompressedAnimation"));
        if (classAt < 0) throw new InvalidDataException("hkaInterleavedUncompressedAnimation class metadata was not found.");

        float wrappedDuration = b.Length >= 4 ? BitConverter.ToSingle(b, 0) : -1;
        int wrappedTracks = b.Length >= 8 ? BitConverter.ToInt32(b, 4) : -1;
        int searchStart = Math.Max(classAt, magicAt + 8);
        int searchEnd = Math.Min(b.Length - 8, classAt + 1024);

        for (int durationAt = searchStart; durationAt <= searchEnd; durationAt++)
        {
            try
            {
                float duration = BitConverter.ToSingle(b, durationAt);
                if (!(duration > 0) || float.IsNaN(duration) || float.IsInfinity(duration)) continue;
                if (wrappedDuration > 0)
                {
                    float tolerance = Math.Max(0.05f, Math.Abs(wrappedDuration) * 0.01f);
                    if (Math.Abs(duration - wrappedDuration) > tolerance) continue;
                }

                // In the binary-tag hkaAnimation enum, interleaved/uncompressed is type 2.
                // Requiring it immediately before duration prevents a random matching float
                // elsewhere in the transform payload from becoming a false header candidate.
                if (durationAt <= 0 || b[durationAt - 1] != 2) continue;

                int p = durationAt + 4;
                int tracks = ReadCValue(b, ref p);
                if (tracks <= 0 || tracks > 4096) continue;
                if (wrappedTracks > 0 && tracks != wrappedTracks) continue;

                int transformCount = ReadCValue(b, ref p);
                if (transformCount <= 0 || transformCount % tracks != 0) continue;
                int frames = transformCount / tracks;
                if (frames <= 0 || frames > 1_000_000) continue;

                int dataLength = checked(transformCount * 48);
                int dataStart = p;
                int dataEnd = checked(dataStart + dataLength);
                if (dataEnd > b.Length) continue;

                // Binary tagfiles may carry a tiny end marker after the raw transform array.
                // Keep the structural check strict enough that we do not swallow another object.
                int trailer = b.Length - dataEnd;
                if (trailer < 0 || trailer > 64) continue;

                // Sanity-check a few hkQsTransform records: all values finite, quaternion plausible.
                int samples = Math.Min(transformCount, 8);
                bool plausible = true;
                for (int i = 0; i < samples; i++)
                {
                    int qoff = dataStart + i * 48 + 16;
                    float qx = BitConverter.ToSingle(b, qoff + 0);
                    float qy = BitConverter.ToSingle(b, qoff + 4);
                    float qz = BitConverter.ToSingle(b, qoff + 8);
                    float qw = BitConverter.ToSingle(b, qoff + 12);
                    if (!Finite(qx) || !Finite(qy) || !Finite(qz) || !Finite(qw)) { plausible = false; break; }
                    float qlen2 = qx*qx + qy*qy + qz*qz + qw*qw;
                    if (qlen2 < 0.25f || qlen2 > 2.25f) { plausible = false; break; }
                }
                if (!plausible) continue;

                return new InterleavedHeader
                {
                    MagicOffset = magicAt,
                    ObjectOffset = classAt,
                    Duration = duration,
                    Tracks = tracks,
                    FloatTracks = 0,
                    NumFrames = frames,
                    TransformCount = transformCount,
                    DataStart = dataStart,
                    DataLength = dataLength,
                    FrameDuration = frames > 1 ? duration / (frames - 1) : 0f
                };
            }
            catch { }
        }

        throw new InvalidDataException($"Could not locate a structurally valid hkaInterleavedUncompressedAnimation payload (wrapper duration={wrappedDuration:0.###}, wrapper tracks={wrappedTracks}).");
    }

    static bool ContainsAscii(byte[] data, string text)
        => IndexOf(data, System.Text.Encoding.ASCII.GetBytes(text)) >= 0;

    static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);

    static Header FindAndParseHeader(byte[] b)
    {
        byte[] magic = { 0x1E, 0x0D, 0xB0, 0xCA, 0xCE, 0xFA, 0x11, 0xD0 };
        int magicAt = IndexOf(b, magic);
        if (magicAt < 0) throw new InvalidDataException("Havok binary-tag magic was not found.");

        float wrappedDuration = b.Length >= 4 ? BitConverter.ToSingle(b, 0) : -1;
        int wrappedTracks = b.Length >= 8 ? BitConverter.ToInt32(b, 4) : -1;
        int scanEnd = b.Length - 24;

        for (int candidate = magicAt + 8; candidate < scanEnd; candidate++)
        {
            try
            {
                int p = candidate;
                _ = ReadCValue(b, ref p); // runtime type id
                if (p + 4 >= b.Length) continue;
                p++; // unknown/type metadata byte
                byte bf0 = b[p++];
                byte bf1 = b[p++];
                byte bf2 = b[p++];
                if ((bf0 & 0x1C) != 0x1C) continue; // type + duration + transform-track count

                if ((bf0 & 0x01) != 0) _ = ReadCValue(b, ref p);
                if ((bf0 & 0x02) != 0) _ = ReadCValue(b, ref p);
                byte animType = b[p++];
                if (animType != 6) continue; // HK_SPLINE_COMPRESSED_ANIMATION in binary-tag encoding
                float duration = ReadSingle(b, ref p);
                int tracks = ReadCValue(b, ref p);
                // The DDO wrapper usually mirrors Havok duration/track count, but not every
                // Animator record is guaranteed to be a 1:1 copy of the inner animation metadata.
                // Treat wrapper values as corroborating evidence, not hard rejection criteria.
                if (tracks <= 0 || tracks > 4096) continue;
                if (!(duration > 0) || float.IsNaN(duration) || float.IsInfinity(duration)) continue;
                // Duration is the most reliable outer-wrapper cross-check. Allow normal float/metadata
                // drift, but use it to reject accidental byte patterns during the broader scan.
                if (wrappedDuration > 0)
                {
                    float durationTolerance = Math.Max(0.05f, Math.Abs(wrappedDuration) * 0.01f);
                    if (Math.Abs(duration - wrappedDuration) > durationTolerance) continue;
                }

                int floatTracks = 0;
                if ((bf0 & 0x20) != 0) floatTracks = ReadCValue(b, ref p);
                if ((bf0 & 0x40) != 0) _ = ReadCValue(b, ref p); // extractedMotion object reference
                if ((bf0 & 0x80) != 0)
                    throw new NotSupportedException("This DDO animation carries serialized annotation-track objects; annotation-object skipping is not implemented yet.");

                var h = new Header { MagicOffset = magicAt, ObjectOffset = candidate, Duration = duration, Tracks = tracks, FloatTracks = floatTracks };
                if ((bf1 & 0x01) != 0) h.NumFrames = ReadCValue(b, ref p);
                if ((bf1 & 0x02) != 0) h.NumBlocks = ReadCValue(b, ref p);
                if ((bf1 & 0x04) != 0) h.MaxFramesPerBlock = ReadCValue(b, ref p);
                if ((bf1 & 0x08) != 0) h.MaskSize = ReadCValue(b, ref p);
                if ((bf1 & 0x10) != 0) h.BlockDuration = ReadSingle(b, ref p);
                if ((bf1 & 0x20) != 0) h.BlockInverseDuration = ReadSingle(b, ref p);
                if ((bf1 & 0x40) != 0) h.FrameDuration = ReadSingle(b, ref p);
                if ((bf1 & 0x80) != 0) h.BlockOffsets = ReadUnsignedArray(b, ref p);
                if ((bf2 & 0x01) != 0) h.FloatBlockOffsets = ReadUnsignedArray(b, ref p);
                if ((bf2 & 0x02) != 0) _ = ReadUnsignedArray(b, ref p); // transformOffsets
                if ((bf2 & 0x04) != 0) _ = ReadUnsignedArray(b, ref p); // floatOffsets
                if ((bf2 & 0x08) == 0) continue;
                h.DataLength = ReadCValue(b, ref p);
                h.DataStart = p;

                if (h.NumFrames <= 0 || h.NumBlocks <= 0 || h.MaxFramesPerBlock <= 0) continue;
                // Havok pads the mask/quantization region. 58 tracks happens to be 232 bytes
                // exactly, but other clips can round this region up (for example 396 -> 400).
                // Float-track mask bytes can also live in this declared region.
                int minimumMaskBytes = checked(h.Tracks * 4);
                if (h.MaskSize <= 0) h.MaskSize = minimumMaskBytes;
                if (h.MaskSize < minimumMaskBytes) continue;
                if (h.MaskSize > h.DataLength) continue;
                if (h.DataLength <= 0 || h.DataStart + h.DataLength > b.Length) continue;
                if (h.BlockOffsets.Count == 0) h.BlockOffsets.Add(0);
                return h;
            }
            catch (NotSupportedException) { throw; }
            catch { }
        }
        throw new InvalidDataException($"Could not locate a structurally valid hkaSplineCompressedAnimation object in the binary tagfile (full-record scan: {Math.Max(0, scanEnd - (magicAt + 8))} bytes after Havok magic; wrapper duration={wrappedDuration:0.###}, wrapper tracks={wrappedTracks}).");
    }

    static Track[] ParseBlock(byte[] b, int blockStart, int trackCount, int maskAndQuantizationSize)
    {
        int p = blockStart;
        var tracks = new Track[trackCount];
        for (int i = 0; i < trackCount; i++)
        {
            if (p + 4 > b.Length) throw new EndOfStreamException("Spline mask table is truncated.");
            byte q = b[p++];
            tracks[i] = new Track
            {
                Mask = new Mask
                {
                    PosQuant = (ScalarQuantization)(q & 3),
                    RotQuant = (RotationQuantization)((q >> 2) & 0x0F),
                    ScaleQuant = (ScalarQuantization)((q >> 6) & 3),
                    PosFlags = (ChannelFlags)b[p++],
                    RotFlags = (ChannelFlags)b[p++],
                    ScaleFlags = (ChannelFlags)b[p++]
                }
            };
        }
        int minimumMaskBytes = checked(trackCount * 4);
        int declaredMaskBytes = maskAndQuantizationSize > 0 ? maskAndQuantizationSize : minimumMaskBytes;
        if (declaredMaskBytes < minimumMaskBytes)
            throw new InvalidDataException($"Mask/quantization region is too small: {declaredMaskBytes} bytes for {trackCount} transform tracks.");
        if (blockStart + declaredMaskBytes > b.Length)
            throw new EndOfStreamException("Mask/quantization region extends past the animation record.");
        // The mask region is explicitly sized by Havok and may contain alignment padding and
        // float-track mask data after the 4-byte transform masks. Spline payload begins after it.
        p = blockStart + declaredMaskBytes;

        for (int i = 0; i < trackCount; i++)
        {
            var t = tracks[i];
            if (HasSplineVector(t.Mask.PosFlags))
                t.PositionSpline = ReadVectorSpline(b, ref p, blockStart, t.Mask.PosFlags, t.Mask.PosQuant, Vector3.Zero);
            else
                t.StaticPosition = ReadStaticVector(b, ref p, t.Mask.PosFlags, Vector3.Zero);
            p = AlignRelative(p, blockStart, 4);

            bool splineRot = HasSplineRotation(t.Mask.RotFlags);
            bool staticRot = HasStaticRotation(t.Mask.RotFlags);
            if (splineRot)
                t.Rotation = ReadQuaternionSpline(b, ref p, blockStart, t.Mask.RotQuant);
            else if (staticRot)
            {
                p = AlignRelative(p, blockStart, RotationAlignment(t.Mask.RotQuant));
                t.StaticRotation = ReadQuaternion(b, ref p, t.Mask.RotQuant);
                t.Rotation = new QuaternionSpline { Exists = true, Dynamic = false, StaticValue = t.StaticRotation };
            }
            p = AlignRelative(p, blockStart, 4);

            if (HasSplineVector(t.Mask.ScaleFlags))
                t.ScaleSpline = ReadVectorSpline(b, ref p, blockStart, t.Mask.ScaleFlags, t.Mask.ScaleQuant, Vector3.One);
            else
                t.StaticScale = ReadStaticVector(b, ref p, t.Mask.ScaleFlags, Vector3.One);
            p = AlignRelative(p, blockStart, 4);
        }
        return tracks;
    }

    static VectorSpline ReadVectorSpline(byte[] b, ref int p, int baseOffset, ChannelFlags flags, ScalarQuantization quant, Vector3 identity)
    {
        int numItems = ReadUInt16(b, ref p);
        int degree = b[p++];
        int knotCount = checked(numItems + degree + 2);
        var knots = new List<byte>(knotCount);
        for (int i = 0; i < knotCount; i++) knots.Add(b[p++]);
        p = AlignRelative(p, baseOffset, 4);

        var s = new VectorSpline();
        ConfigureVectorChannel(b, ref p, s.X, flags, ChannelFlags.StaticX, ChannelFlags.SplineX, identity.X, degree, knots);
        ConfigureVectorChannel(b, ref p, s.Y, flags, ChannelFlags.StaticY, ChannelFlags.SplineY, identity.Y, degree, knots);
        ConfigureVectorChannel(b, ref p, s.Z, flags, ChannelFlags.StaticZ, ChannelFlags.SplineZ, identity.Z, degree, knots);

        float minX = s.X.Dynamic ? s.X.StaticValue : 0, maxX = s.X.Dynamic ? s.X.Values[0] : 0;
        float minY = s.Y.Dynamic ? s.Y.StaticValue : 0, maxY = s.Y.Dynamic ? s.Y.Values[0] : 0;
        float minZ = s.Z.Dynamic ? s.Z.StaticValue : 0, maxZ = s.Z.Dynamic ? s.Z.Values[0] : 0;
        if (s.X.Dynamic) s.X.Values.Clear();
        if (s.Y.Dynamic) s.Y.Values.Clear();
        if (s.Z.Dynamic) s.Z.Values.Clear();

        for (int i = 0; i <= numItems; i++)
        {
            if (s.X.Dynamic) s.X.Values.Add(ReadQuantizedScalar(b, ref p, minX, maxX, quant));
            if (s.Y.Dynamic) s.Y.Values.Add(ReadQuantizedScalar(b, ref p, minY, maxY, quant));
            if (s.Z.Dynamic) s.Z.Values.Add(ReadQuantizedScalar(b, ref p, minZ, maxZ, quant));
        }
        return s;
    }

    // For dynamic vector channels we temporarily store bbox min in StaticValue and bbox max in Values[0]
    // until the interleaved quantized control points are read.
    static void ConfigureVectorChannel(byte[] b, ref int p, ScalarChannel ch, ChannelFlags flags, ChannelFlags staticFlag, ChannelFlags splineFlag, float identity, int degree, List<byte> knots)
    {
        ch.Degree = degree;
        ch.Knots = new List<byte>(knots);
        if ((flags & splineFlag) != 0)
        {
            ch.Exists = true;
            ch.Dynamic = true;
            ch.StaticValue = ReadSingle(b, ref p);
            ch.Values.Add(ReadSingle(b, ref p));
        }
        else if ((flags & staticFlag) != 0)
        {
            ch.Exists = true;
            ch.Dynamic = false;
            ch.StaticValue = ReadSingle(b, ref p);
        }
        else
        {
            ch.Exists = false;
            ch.Dynamic = false;
            ch.StaticValue = identity;
        }
    }

    static QuaternionSpline ReadQuaternionSpline(byte[] b, ref int p, int baseOffset, RotationQuantization quant)
    {
        int numItems = ReadUInt16(b, ref p);
        int degree = b[p++];
        int knotCount = checked(numItems + degree + 2);
        var q = new QuaternionSpline { Exists = true, Dynamic = true, Degree = degree };
        for (int i = 0; i < knotCount; i++) q.Knots.Add(b[p++]);
        p = AlignRelative(p, baseOffset, RotationAlignment(quant));
        for (int i = 0; i <= numItems; i++) q.Values.Add(ReadQuaternion(b, ref p, quant));
        return q;
    }

    static Vector3 ReadStaticVector(byte[] b, ref int p, ChannelFlags flags, Vector3 identity)
    {
        float x = (flags & ChannelFlags.StaticX) != 0 ? ReadSingle(b, ref p) : identity.X;
        float y = (flags & ChannelFlags.StaticY) != 0 ? ReadSingle(b, ref p) : identity.Y;
        float z = (flags & ChannelFlags.StaticZ) != 0 ? ReadSingle(b, ref p) : identity.Z;
        return new Vector3(x, y, z);
    }

    static Vector3 SampleVector(VectorSpline? s, Vector3 staticValue, Vector3 identity, float frame)
    {
        if (s == null) return staticValue;
        return new Vector3(
            SampleScalar(s.X, identity.X, frame),
            SampleScalar(s.Y, identity.Y, frame),
            SampleScalar(s.Z, identity.Z, frame));
    }

    static float SampleScalar(ScalarChannel c, float identity, float frame)
    {
        if (!c.Exists) return identity;
        if (!c.Dynamic) return c.StaticValue;
        return EvaluateSpline(c.Degree, frame, c.Knots, c.Values);
    }

    static Quaternion SampleQuaternion(QuaternionSpline q, Quaternion staticValue, float frame)
    {
        if (!q.Exists) return staticValue;
        if (!q.Dynamic) return q.StaticValue;
        if (q.Values.Count == 0) return Quaternion.Identity;
        int span = FindKnotSpan(q.Degree, frame, q.Values.Count, q.Knots);
        float[] n = BasisWeights(span, q.Degree, frame, q.Knots);
        Vector4 sum = Vector4.Zero;
        for (int i = 0; i <= q.Degree; i++)
        {
            Quaternion v = q.Values[span - i];
            sum += new Vector4(v.X, v.Y, v.Z, v.W) * n[i];
        }
        var ret = new Quaternion(sum.X, sum.Y, sum.Z, sum.W);
        float len = ret.Length();
        return len > 1e-8f ? Quaternion.Normalize(ret) : Quaternion.Identity;
    }

    static float EvaluateSpline(int degree, float frame, List<byte> knots, List<float> values)
    {
        if (values.Count == 0) return 0;
        if (values.Count == 1) return values[0];
        int span = FindKnotSpan(degree, frame, values.Count, knots);
        float[] n = BasisWeights(span, degree, frame, knots);
        float ret = 0;
        for (int i = 0; i <= degree; i++) ret += values[span - i] * n[i];
        return ret;
    }

    static int FindKnotSpan(int degree, float value, int controlPointCount, List<byte> knots)
    {
        if (knots.Count <= controlPointCount) return Math.Max(0, controlPointCount - 1);
        float max = knots[controlPointCount];
        if (value >= max) return controlPointCount - 1;
        int low = Math.Min(degree, controlPointCount - 1);
        int high = controlPointCount;
        int mid = (low + high) / 2;
        int guard = 0;
        while ((value < knots[mid] || value >= knots[mid + 1]) && guard++ < 128)
        {
            if (value < knots[mid]) high = mid;
            else low = mid;
            int next = (low + high) / 2;
            if (next == mid && high - low <= 1) break;
            mid = next;
        }
        return Math.Clamp(mid, degree, controlPointCount - 1);
    }

    static float[] BasisWeights(int span, int degree, float frame, List<byte> knots)
    {
        float[] n = new float[Math.Max(5, degree + 1)];
        n[0] = 1;
        for (int i = 1; i <= degree; i++)
        {
            for (int j = i - 1; j >= 0; j--)
            {
                float den = knots[span + i - j] - knots[span - j];
                float a = Math.Abs(den) < 1e-8f ? 0 : (frame - knots[span - j]) / den;
                float tmp = n[j] * a;
                n[j + 1] += n[j] - tmp;
                n[j] = tmp;
            }
        }
        return n;
    }

    static float ReadQuantizedScalar(byte[] b, ref int p, float min, float max, ScalarQuantization q)
    {
        float ratio;
        if (q == ScalarQuantization.Bits8) ratio = b[p++] / 255f;
        else if (q == ScalarQuantization.Bits16) ratio = ReadUInt16(b, ref p) / 65535f;
        else throw new NotSupportedException($"Scalar quantization {q} is not supported.");
        return min + (max - min) * ratio;
    }

    static Quaternion ReadQuaternion(byte[] b, ref int p, RotationQuantization q)
    {
        switch (q)
        {
            case RotationQuantization.ThreeComp40:
            {
                Ensure(b, p, 5);
                ulong v = (ulong)b[p] | ((ulong)b[p + 1] << 8) | ((ulong)b[p + 2] << 16) | ((ulong)b[p + 3] << 24) | ((ulong)b[p + 4] << 32);
                p += 5;
                const int mask = (1 << 12) - 1;
                const int positiveMask = mask >> 1;
                const float fractal = 0.000345436f;
                int a = (int)(v & mask) - positiveMask;
                int bb = (int)((v >> 12) & mask) - positiveMask;
                int c = (int)((v >> 24) & mask) - positiveMask;
                int missing = (int)((v >> 36) & 3);
                bool negative = ((v >> 38) & 1) != 0;
                float[] packed = { a * fractal, bb * fractal, c * fractal };
                float[] outq = new float[4];
                for (int i = 0; i < 4; i++)
                    if (i < missing) outq[i] = packed[i];
                    else if (i > missing) outq[i] = packed[i - 1];
                float rem = 1f - packed[0] * packed[0] - packed[1] * packed[1] - packed[2] * packed[2];
                outq[missing] = rem > 0 ? MathF.Sqrt(rem) : 0;
                if (negative) outq[missing] = -outq[missing];
                return Quaternion.Normalize(new Quaternion(outq[0], outq[1], outq[2], outq[3]));
            }
            case RotationQuantization.ThreeComp48:
            {
                Ensure(b, p, 6);
                ushort rx = BitConverter.ToUInt16(b, p); ushort ry = BitConverter.ToUInt16(b, p + 2); ushort rz = BitConverter.ToUInt16(b, p + 4); p += 6;
                int missing = ((ry >> 14) & 2) | ((rx >> 15) & 1);
                bool negative = (rz >> 15) != 0;
                const int mask = 0x7FFF;
                const float fractal = 0.000043161f;
                float[] packed = { ((rx & mask) - (mask >> 1)) * fractal, ((ry & mask) - (mask >> 1)) * fractal, ((rz & mask) - (mask >> 1)) * fractal };
                float[] outq = new float[4];
                for (int i = 0; i < 4; i++) if (i < missing) outq[i] = packed[i]; else if (i > missing) outq[i] = packed[i - 1];
                float rem = 1f - packed[0] * packed[0] - packed[1] * packed[1] - packed[2] * packed[2];
                outq[missing] = rem > 0 ? MathF.Sqrt(rem) : 0;
                if (negative) outq[missing] = -outq[missing];
                return Quaternion.Normalize(new Quaternion(outq[0], outq[1], outq[2], outq[3]));
            }
            case RotationQuantization.Polar32:
            {
                Ensure(b, p, 4);
                uint v = BitConverter.ToUInt32(b, p); p += 4;
                const uint rMask = (1u << 10) - 1;
                float r = ((v >> 18) & rMask) / (float)rMask;
                r = 1f - r * r;
                float phiTheta = v & 0x3FFFF;
                float phi = MathF.Floor(MathF.Sqrt(phiTheta));
                float theta = 0;
                if (phi > 0)
                {
                    theta = (MathF.PI / 4f) * (phiTheta - phi * phi) / phi;
                    phi = (MathF.PI / 2f / 511f) * phi;
                }
                float mag = MathF.Sqrt(MathF.Max(0, 1f - r * r));
                float x = MathF.Sin(phi) * MathF.Cos(theta) * mag;
                float y = MathF.Sin(phi) * MathF.Sin(theta) * mag;
                float z = MathF.Cos(phi) * mag;
                float w = r;
                if ((v & 0x10000000) != 0) x = -x;
                if ((v & 0x20000000) != 0) y = -y;
                if ((v & 0x40000000) != 0) z = -z;
                if ((v & 0x80000000) != 0) w = -w;
                return Quaternion.Normalize(new Quaternion(x, y, z, w));
            }
            case RotationQuantization.Uncompressed:
            {
                float x = ReadSingle(b, ref p), y = ReadSingle(b, ref p), z = ReadSingle(b, ref p), w = ReadSingle(b, ref p);
                return Quaternion.Normalize(new Quaternion(x, y, z, w));
            }
            default:
                throw new NotSupportedException($"Rotation quantization {q} is not implemented yet.");
        }
    }

    static bool HasSplineVector(ChannelFlags f) => (f & (ChannelFlags.SplineX | ChannelFlags.SplineY | ChannelFlags.SplineZ)) != 0;
    static bool HasSplineRotation(ChannelFlags f) => (f & (ChannelFlags.SplineX | ChannelFlags.SplineY | ChannelFlags.SplineZ | ChannelFlags.SplineW)) != 0;
    static bool HasStaticRotation(ChannelFlags f) => (f & (ChannelFlags.StaticX | ChannelFlags.StaticY | ChannelFlags.StaticZ | ChannelFlags.StaticW)) != 0;

    static string DescribeVector(ChannelFlags f)
    {
        bool spline = HasSplineVector(f);
        bool stat = (f & (ChannelFlags.StaticX | ChannelFlags.StaticY | ChannelFlags.StaticZ)) != 0;
        return spline && stat ? "spline+static" : spline ? "spline" : stat ? "static" : "identity";
    }
    static string DescribeRotation(ChannelFlags f)
    {
        bool spline = HasSplineRotation(f), stat = HasStaticRotation(f);
        return spline && stat ? "spline+static" : spline ? "spline" : stat ? "static" : "identity";
    }

    static List<int> ReadUnsignedArray(byte[] b, ref int p)
    {
        int count = ReadCValue(b, ref p);
        _ = ReadCValue(b, ref p); // element type descriptor (integer)
        if (count < 0 || count > 1_000_000) throw new InvalidDataException("Invalid compact-array length.");
        var list = new List<int>(count);
        for (int i = 0; i < count; i++) list.Add(ReadCValue(b, ref p));
        return list;
    }

    static int ReadCValue(byte[] b, ref int p)
    {
        uint raw = 0;
        int shift = 0;
        for (int i = 0; i < 5; i++)
        {
            Ensure(b, p, 1);
            byte x = b[p++];
            raw |= (uint)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) return checked((int)(raw >> 1));
            shift += 7;
        }
        throw new InvalidDataException("Compact Havok integer exceeds supported length.");
    }

    static float ReadSingle(byte[] b, ref int p)
    {
        Ensure(b, p, 4);
        float v = BitConverter.ToSingle(b, p);
        p += 4;
        return v;
    }

    static ushort ReadUInt16(byte[] b, ref int p)
    {
        Ensure(b, p, 2);
        ushort v = BitConverter.ToUInt16(b, p);
        p += 2;
        return v;
    }

    static int RotationAlignment(RotationQuantization q) => q switch
    {
        RotationQuantization.Polar32 => 4,
        RotationQuantization.ThreeComp40 => 1,
        RotationQuantization.ThreeComp48 => 2,
        RotationQuantization.ThreeComp24 => 1,
        RotationQuantization.Straight16 => 2,
        RotationQuantization.Uncompressed => 4,
        _ => 1
    };

    static int AlignRelative(int absolute, int baseOffset, int alignment)
    {
        if (alignment <= 1) return absolute;
        int rel = absolute - baseOffset;
        int mod = rel % alignment;
        return mod == 0 ? absolute : absolute + (alignment - mod);
    }

    static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++) if (haystack[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    static void Ensure(byte[] b, int p, int count)
    {
        if (p < 0 || count < 0 || p + count > b.Length) throw new EndOfStreamException("Havok animation payload ended unexpectedly.");
    }
}
