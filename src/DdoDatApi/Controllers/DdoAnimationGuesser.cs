using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DdoDatApi.Controllers;

/// <summary>
/// Best-effort motion classifier for decoded DDO animation clips.
/// This intentionally returns simple user-facing labels rather than claiming
/// to recover Turbine's original animation names.
/// </summary>
public static class DdoAnimationGuesser
{
    public static string Guess(DdoHavokAnimationDecoder.DecodeResult decoded)
    {
        if (decoded == null || !decoded.Success) return "Unknown";
        var frames = decoded.Frames;
        if (frames == null || frames.Count < 2) return "Static Pose";

        var tracks = frames.SelectMany(f => f.Transforms).Select(t => t.Track).Distinct().OrderBy(x => x).ToArray();
        if (tracks.Length == 0) return "Unknown";
        int rootTrack = tracks.Contains(0) ? 0 : tracks[0];
        float duration = decoded.Duration > 0 ? decoded.Duration : Math.Max(1f / 30f, (frames.Count - 1) / 30f);

        var root = frames.Select(f => f.Transforms.FirstOrDefault(t => t.Track == rootTrack)).Where(t => t != null).ToArray();
        float rootDisp = root.Length >= 2 ? Dist(root[^1]!.Translation, root[0]!.Translation) : 0;
        float rootVertical = root.Length >= 2 ? Math.Abs(root[^1]!.Translation[2] - root[0]!.Translation[2]) : 0;
        float rootRot = root.Length >= 2 ? QuatAngle(root[0]!.Rotation, root[^1]!.Rotation) : 0;

        var meanPos = new List<float>();
        var meanRot = new List<float>();
        var peakPos = new List<float>();
        var peakRot = new List<float>();
        int moving = 0, energetic = 0;
        var f0 = frames[0];

        foreach (int tr in tracks)
        {
            var t0 = f0.Transforms.FirstOrDefault(t => t.Track == tr);
            if (t0 == null) continue;
            var pd = new List<float>();
            var rd = new List<float>();
            foreach (var f in frames)
            {
                var t = f.Transforms.FirstOrDefault(x => x.Track == tr);
                if (t == null) continue;
                pd.Add(Dist(t.Translation, t0.Translation));
                rd.Add(QuatAngle(t.Rotation, t0.Rotation));
            }
            float mp = pd.Count > 0 ? pd.Average() : 0;
            float mr = rd.Count > 0 ? rd.Average() : 0;
            meanPos.Add(mp); meanRot.Add(mr);
            peakPos.Add(pd.Count > 0 ? pd.Max() : 0);
            peakRot.Add(rd.Count > 0 ? rd.Max() : 0);
            if (mp > 0.015f || mr > Deg(5)) moving++;
            if (mp > 0.08f || mr > Deg(25)) energetic++;
        }

        float avgPos = meanPos.Count > 0 ? meanPos.Average() : 0;
        float avgRot = meanRot.Count > 0 ? meanRot.Average() : 0;
        float p90Rot = Percentile(peakRot, 0.90f);
        float movingRatio = moving / (float)Math.Max(1, tracks.Length);
        float energeticRatio = energetic / (float)Math.Max(1, tracks.Length);

        var rootSpeeds = new List<float>();
        float rootRangeZ = 0;
        if (root.Length >= 2)
        {
            float dt = duration / Math.Max(1, root.Length - 1);
            for (int i = 1; i < root.Length; i++)
                rootSpeeds.Add(Dist(root[i]!.Translation, root[i - 1]!.Translation) / Math.Max(dt, 1e-6f));
            var zs = root.Select(t => t!.Translation[2]).ToArray();
            rootRangeZ = zs.Max() - zs.Min();
        }
        float rootSpeed = rootSpeeds.Count > 0 ? rootSpeeds.Average() : 0;
        float burstRatio = rootSpeeds.Count > 0 ? rootSpeeds.Max() / Math.Max(rootSpeed, 1e-6f) : 0;

        var endPos = new List<float>();
        var endRot = new List<float>();
        var fend = frames[^1];
        foreach (int tr in tracks)
        {
            var a = f0.Transforms.FirstOrDefault(t => t.Track == tr);
            var b = fend.Transforms.FirstOrDefault(t => t.Track == tr);
            if (a == null || b == null) continue;
            endPos.Add(Dist(a.Translation, b.Translation));
            endRot.Add(QuatAngle(a.Rotation, b.Rotation));
        }
        bool loopLike = endPos.Count > 0 && endRot.Count > 0 && endPos.Average() < 0.03f && endRot.Average() < Deg(8);

        if (avgPos < 0.01f && avgRot < Deg(3) && rootDisp < 0.02f) return "Idle";
        if (loopLike && movingRatio < 0.45f && rootDisp < 0.08f) return "Idle / Emote";
        if (loopLike && rootDisp > 0.12f && movingRatio > 0.35f)
            return rootSpeed > 1.2f || energeticRatio > 0.45f ? "Run" : "Walk";
        if (rootRot > Deg(35) && rootDisp < 0.25f) return "Turn / Pivot";
        if (rootVertical > 0.20f || rootRangeZ > 0.35f) return "Jump / Landing";
        if (duration < 1.2f && burstRatio > 2.7f && movingRatio > 0.25f) return "Hit Reaction";
        if (duration < 2.5f && energeticRatio > 0.25f && p90Rot > Deg(35)) return "Attack";
        if (duration > 2.5f && !loopLike && movingRatio > 0.45f)
            return rootVertical > 0.12f || rootRot > Deg(30) ? "Death / Knockdown" : "Special Action";
        if (loopLike && movingRatio > 0.35f) return "Cyclic / Swim / Flap";
        return "Action";
    }

    static float Dist(float[] a, float[] b)
    {
        if (a == null || b == null || a.Length < 3 || b.Length < 3) return 0;
        float x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2];
        return MathF.Sqrt(x * x + y * y + z * z);
    }

    static float QuatAngle(float[] a, float[] b)
    {
        if (a == null || b == null || a.Length < 4 || b.Length < 4) return 0;
        var qa = Quaternion.Normalize(new Quaternion(a[0], a[1], a[2], a[3]));
        var qb = Quaternion.Normalize(new Quaternion(b[0], b[1], b[2], b[3]));
        float dot = Math.Clamp(MathF.Abs(Quaternion.Dot(qa, qb)), -1f, 1f);
        return 2f * MathF.Acos(dot);
    }

    static float Percentile(List<float> values, float p)
    {
        if (values.Count == 0) return 0;
        var a = values.OrderBy(x => x).ToArray();
        if (a.Length == 1) return a[0];
        float x = (a.Length - 1) * p;
        int lo = (int)MathF.Floor(x), hi = (int)MathF.Ceiling(x);
        if (lo == hi) return a[lo];
        float t = x - lo;
        return a[lo] * (1 - t) + a[hi] * t;
    }

    static float Deg(float degrees) => degrees * MathF.PI / 180f;
}
