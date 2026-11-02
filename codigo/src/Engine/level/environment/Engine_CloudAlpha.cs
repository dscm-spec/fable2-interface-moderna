using System;

namespace Refii.Level;

/// <summary>
/// The cloud density map's alpha as the sky samples it on the CPU
/// (<see cref="Engine_SkyEnvironmentNode"/>'s sun visibility). No engine state, so it is tested
/// without the engine.
/// </summary>
public static class Engine_CloudAlpha
{
    /// <summary>
    /// The alpha bytes of the first <paramref name="texels"/> RGBA8 texels (mip 0 leads the
    /// data); a short buffer leaves the rest 0, as the loop it came from did.
    /// </summary>
    public static byte[] Channel(byte[] rgba, int texels)
    {
        var alpha = new byte[Math.Max(texels, 0)];
        for (int i = 0, a = 3; i < alpha.Length && a < rgba.Length; i++, a += 4)
        {
            alpha[i] = rgba[a];
        }

        return alpha;
    }

    /// <summary>How many positions differ, counting a length difference as differing texels.</summary>
    public static int Differences(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        int differ = Math.Abs(a.Length - b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                differ++;
            }
        }

        return differ;
    }
}
