using System;
using System.Collections.Generic;
using Godot;
using Refii.Gdb;
using Refii.Level;
using Refii.Textures;

namespace Refii.Hero;

public sealed class TextureMorphEntry
{
    public uint MorphHash { get; init; }

    public uint Layer { get; init; }

    public float Weight { get; init; }

    public TextureMorphBlend Blend { get; init; }
}

public static class Engine_HeroTextureMorph
{
    /// <summary>Composites kept before the cache is dropped; a dragged slider makes one per value.</summary>
    public const int CompositedLimit = 64;

    private sealed class DecodedTexture
    {
        public int Width { get; set; }

        public int Height { get; set; }

        public bool HasAlpha { get; set; }

        public bool Ok { get; set; }

        public byte[] Rgba { get; set; } = Array.Empty<byte>();
    }

    private static readonly object MorphLock = new();

    private static readonly Dictionary<string, ImageTexture> Composited =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, DecodedTexture> DecodedCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static Func<string, byte[]?>? _resolver;

    private static List<TextureMorphPair>? _pairs;

    public static void SetResolver(Func<string, byte[]?>? resolver)
    {
        lock (MorphLock)
        {
            _resolver = resolver;
            _pairs = null;
            Composited.Clear();
            DecodedCache.Clear();
        }
    }

    public static void Invalidate()
    {
        lock (MorphLock)
        {
            _pairs = null;
            Composited.Clear();
            DecodedCache.Clear();
        }
    }

    public static List<TextureMorphPair> Pairs()
    {
        lock (MorphLock)
        {
            if (_pairs is not null)
            {
                return _pairs;
            }

            if (_resolver is null)
            {
                _pairs = new List<TextureMorphPair>();
                return _pairs;
            }
        }

        var blobs = Refii.Gdb.Engine_GameGdb.LoadSupplementalBlobs(_resolver, includeTu: true);
        List<TextureMorphPair> built = Engine_GdbTextureMorphs.Build(GdbMulti.Build(blobs));
        GD.Print($"[morph] {built.Count} texture morph pair(s), " +
            $"{Engine_TexPathIndex.Count} texture(s) indexed");

        lock (MorphLock)
        {
            _pairs = built;
        }

        return built;
    }

    /// <summary>
    /// The live layers per original texture, in the guest's order: <c>Layer</c> ascending, then
    /// the morph texture (<c>sub_825BE008</c>). Type 1 is always on, type 19 (scars) is not a
    /// stat morph, and a weight at or below 1e-4 is dropped (<c>sub_8220DE88</c>, 0x8220EE6C).
    /// </summary>
    public static Dictionary<uint, List<TextureMorphEntry>> Active(HeroMorphStats stats) =>
        Active(type => Engine_HeroMorphState.Weight(stats, type));

    /// <summary>Composited textures built; run total.</summary>
    public static long CompositesBuilt => System.Threading.Interlocked.Read(ref _compositesBuilt);

    private static long _compositesBuilt;

    /// <summary>
    /// Serialises <c>Duplicate()</c> of the SHARED model materials. Composites build on
    /// several workers at once (GameEntities.QueueCompositeBuild), and Godot's
    /// <c>ShaderMaterial::_get</c>, which <c>Resource::duplicate</c> calls per property, fills a
    /// lazy remap cache on the source material, so concurrent duplicates race (a null deref in
    /// <c>ShaderMaterial::_getv</c>).
    /// </summary>
    private static readonly object DuplicateGate = new();

    /// <summary>
    /// Replaces, on a private copy of each material, every texture the guest stated a composite
    /// for (<c>sub_825B38B0</c>) — whichever slot holds it — and lays the overlays it stated
    /// for the material's diffuse (<c>sub_8222AAC8</c>, command type 14) over diffuse and normal.
    /// </summary>
    public static void ApplyTo(
        ArrayMesh mesh, IReadOnlyList<Refii.Host.HostBridge.GuestTextureComposite> composites)
    {
        if (composites.Count == 0)
        {
            return;
        }

        const int DiffuseSlot = 0;
        const int NormalSlot = 1;
        const int SpecularSlot = 2;

        var byOriginal = new Dictionary<uint, Refii.Host.HostBridge.GuestTextureComposite>();
        var overlays = new Dictionary<uint, Refii.Host.HostBridge.GuestTextureComposite>();

        // WHOLE-ENTITY SWAPS, by our slot: the guest states them as every diffuse (0), specular
        // (1) or normal (2) map, and states no composite beside them (`sub_8220DE88` skips that
        // path, 0x8220eb44).
        var everyMap = new Refii.Host.HostBridge.GuestTextureLayer[]?[3];
        foreach (Refii.Host.HostBridge.GuestTextureComposite composite in composites)
        {
            if (composite.Wildcard)
            {
                int slot = composite.Original switch
                {
                    0u => DiffuseSlot,
                    1u => SpecularSlot,
                    2u => NormalSlot,
                    _ => -1,
                };
                if (slot >= 0)
                {
                    everyMap[slot] = composite.Layers;
                }

                continue;
            }

            (composite.Overlay ? overlays : byOriginal)[composite.Original] = composite;
        }

        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if (mesh.SurfaceGetMaterial(s) is not ShaderMaterial material ||
                !material.HasMeta(Refii.Models.Engine_MdlMaterial.TextureNamesMeta))
            {
                continue;
            }

            string[] names = material.GetMeta(
                Refii.Models.Engine_MdlMaterial.TextureNamesMeta).AsStringArray();
            ShaderMaterial? own = null;

            Refii.Host.HostBridge.GuestTextureLayer[]? over = null;
            if (names.Length > DiffuseSlot && names[DiffuseSlot].Length != 0 &&
                overlays.TryGetValue(
                    Engine_TexPathIndex.PathHash(names[DiffuseSlot]),
                    out Refii.Host.HostBridge.GuestTextureComposite stated))
            {
                over = stated.Layers;
            }

            bool packedSourceChanged = false;
            for (int slot = 0;
                 slot < names.Length && slot < Refii.Models.Engine_MdlMaterial.TextureParameters.Length;
                 slot++)
            {
                if (names[slot].Length == 0)
                {
                    continue;
                }

                uint original = Engine_TexPathIndex.PathHash(names[slot]);
                byOriginal.TryGetValue(
                    original, out Refii.Host.HostBridge.GuestTextureComposite composite);
                if (slot < everyMap.Length && everyMap[slot] is { } swapped)
                {
                    composite = new Refii.Host.HostBridge.GuestTextureComposite(original, swapped);
                }

                Refii.Host.HostBridge.GuestTextureLayer[]? slotOver =
                    slot == DiffuseSlot || slot == NormalSlot ? over : null;

                if (Composite(original, composite.Layers, slotOver, slot == NormalSlot)
                    is not Texture2D composited)
                {
                    continue;
                }

                if (own is null)
                {
                    lock (DuplicateGate)
                    {
                        own = material.HasMeta(Refii.Models.Engine_ClassicMaterial.ClassicMeta)
                            ? Refii.Models.Engine_ClassicMaterial.InstanceCopy(material)
                            : Refii.Models.Engine_MatCopy.Copy(material, "hero");
                    }
                }

                // The slot's own parameter: the classic material names its samplers as the
                // title's shader does, and its glass pass takes the composite too.
                Refii.Models.Engine_ClassicMaterial.SetOnPasses(own,
                    Refii.Models.Engine_ClassicMaterial.TextureParameter(own, slot), composited);
                packedSourceChanged |= slot == NormalSlot || slot == SpecularSlot;
            }

            // THE ORM FOLLOWS THE MAPS IT WAS PACKED FROM. Roughness and metallic come from the
            // specular, occlusion and height from the normal (Engine_TexOrm); a material whose
            // composite replaced either would otherwise draw with the unmorphed pack beside the
            // morphed map — the rule Engine_MdlMaterial already applies to an overridden source.
            if (own is not null && packedSourceChanged && own.GetShaderParameter("has_orm").AsBool())
            {
                ImageTexture? orm = Engine_TexLibrary.BuildOrmFromTextures(
                    own.GetShaderParameter("specular_tex").As<Texture2D>(),
                    own.GetShaderParameter("normal_tex").As<Texture2D>(),
                    own.GetShaderParameter("spec_power_min").AsSingle(),
                    own.GetShaderParameter("spec_power_max").AsSingle(),
                    out bool hasHeight);
                if (orm is not null)
                {
                    own.SetShaderParameter("orm_tex", orm);
                    own.SetShaderParameter("has_height", hasHeight);
                }
            }

            if (own is not null)
            {
                mesh.SurfaceSetMaterial(s, own);
            }
        }
    }

    /// <summary>
    /// The guest's composite: layer 0 opaque, then each layer over it by its blend mode with
    /// the layer's alpha scaled by its weight (<c>sub_82A7EA28</c>); then the overlays, each
    /// blended by its own diffuse's alpha times its weight. On the normal slot an overlay
    /// contributes its bump, masked by that same diffuse alpha. Null when the result is the
    /// original texture itself.
    /// </summary>
    public static Texture2D? Composite(
        uint original,
        Refii.Host.HostBridge.GuestTextureLayer[]? layers,
        Refii.Host.HostBridge.GuestTextureLayer[]? overlays,
        bool normalSlot)
    {
        layers ??= Array.Empty<Refii.Host.HostBridge.GuestTextureLayer>();
        overlays ??= Array.Empty<Refii.Host.HostBridge.GuestTextureLayer>();

        bool layered = layers.Length > 1 ||
            (layers.Length == 1 && (layers[0].Texture != original || layers[0].HasTint));

        bool overlaid = false;
        foreach (Refii.Host.HostBridge.GuestTextureLayer overlay in overlays)
        {
            if (overlay.Weight > Engine_HeroMorph.WeightFloor &&
                (normalSlot ? overlay.Bump != 0 : overlay.Texture != 0))
            {
                overlaid = true;
            }
        }

        if (!layered && !overlaid)
        {
            return null;
        }

        var key = new System.Text.StringBuilder(normalSlot ? "N" : "L");
        key.Append(original.ToString("x8"));
        foreach (Refii.Host.HostBridge.GuestTextureLayer layer in layers)
        {
            AppendKey(key, layer);
        }

        key.Append('+');
        foreach (Refii.Host.HostBridge.GuestTextureLayer overlay in overlays)
        {
            AppendKey(key, overlay);
        }

        string cacheKey = key.ToString();

        lock (MorphLock)
        {
            if (Composited.TryGetValue(cacheKey, out ImageTexture? cached))
            {
                return cached;
            }
        }

        string? basePath = Engine_TexPathIndex.Resolve(layers.Length != 0 ? layers[0].Texture : original);
        DecodedTexture? source = basePath is null ? null : Decode(basePath);
        if (source is null || !source.Ok)
        {
            return null;
        }

        // THE TARGET IS THE LARGEST LAYER, not the base. `sub_82A5BCE8` sizes the composite from
        // 64 x 64 up to the widest and tallest of ALL its layers (texture +148 / +152); a smaller
        // layer is sampled up to it. The Will lines are a larger `ILL_*_will` map over a 32 x 32
        // black `ILL_*` base: composited at the base's size they were soft blobs of glow. The guest
        // then halves past 512 (`dword_8331AB00/04`); that hardware budget is not kept, so no
        // composite comes out softer than its sharpest layer.
        var decoded = new DecodedTexture?[layers.Length];
        int width = Math.Max(MinimumCompositeSize, source.Width);
        int height = Math.Max(MinimumCompositeSize, source.Height);
        for (int i = 1; i < layers.Length; i++)
        {
            string? path = Engine_TexPathIndex.Resolve(layers[i].Texture);
            decoded[i] = path is null ? null : Decode(path);
            if (decoded[i] is { Ok: true } layerTexture)
            {
                width = Math.Max(width, layerTexture.Width);
                height = Math.Max(height, layerTexture.Height);
            }
        }

        byte[] output = Fit(source, width, height).Rgba;
        if (ReferenceEquals(output, source.Rgba))
        {
            output = (byte[])output.Clone();
        }

        if (layers.Length != 0 && layers[0].HasTint)
        {
            var tint = new Colourise(layers[0].Tint, layers[0].Brightness);
            for (int at = 0; at < output.Length; at += 4)
            {
                tint.Apply(output, at, output, at);
            }
        }

        for (int i = 1; i < layers.Length; i++)
        {
            Refii.Host.HostBridge.GuestTextureLayer layer = layers[i];
            if (decoded[i] is { Ok: true } over)
            {
                over = Fit(over, width, height);
                Blend(output, width, height, over, over, layer.Mode, layer.Weight,
                    layer.HasTint ? new Colourise(layer.Tint, layer.Brightness) : null);
            }
        }

        foreach (Refii.Host.HostBridge.GuestTextureLayer overlay in overlays)
        {
            if (overlay.Weight <= Engine_HeroMorph.WeightFloor)
            {
                continue;
            }

            string? maskPath = Engine_TexPathIndex.Resolve(overlay.Texture);
            DecodedTexture? mask = maskPath is null ? null : Decode(maskPath);
            if (mask is null || !mask.Ok)
            {
                continue;
            }

            DecodedTexture? colour = mask;
            if (normalSlot)
            {
                string? bumpPath = Engine_TexPathIndex.Resolve(overlay.Bump);
                colour = bumpPath is null ? null : Decode(bumpPath);
            }

            if (colour is not null && colour.Ok)
            {
                Blend(output, width, height, Fit(colour, width, height), Fit(mask, width, height), 1, overlay.Weight, null);
            }
        }

        System.Threading.Interlocked.Increment(ref _compositesBuilt);
        ImageTexture texture = ToTexture(output, width, height, normalSlot);

        lock (MorphLock)
        {
            if (Composited.Count >= CompositedLimit)
            {
                Composited.Clear();
            }

            Composited[cacheKey] = texture;
        }

        return texture;
    }

    private static void AppendKey(
        System.Text.StringBuilder key, Refii.Host.HostBridge.GuestTextureLayer layer) =>
        key.Append(layer.Texture.ToString("x8")).Append(layer.Mode)
            .Append(BitConverter.SingleToUInt32Bits(layer.Weight).ToString("x8"))
            .Append(layer.HasTint ? layer.Tint.ToString("x8") : "-")
            .Append(BitConverter.SingleToUInt32Bits(layer.Brightness).ToString("x8"))
            .Append(layer.Bump.ToString("x8"));

    /// <summary>The composite's floor: `sub_82A5BCE8` starts its max over the layers from 64 x 64.</summary>
    private const int MinimumCompositeSize = 64;

    /// <summary>
    /// <paramref name="texture"/> at the target's size: itself when it already is, else sampled
    /// bilinearly between texel centres, as the guest's composite pass filters a smaller layer.
    /// </summary>
    private static DecodedTexture Fit(DecodedTexture texture, int width, int height)
    {
        if (texture.Width == width && texture.Height == height)
        {
            return texture;
        }

        var rgba = new byte[width * height * 4];
        int sw = texture.Width;
        int sh = texture.Height;
        byte[] src = texture.Rgba;
        for (int y = 0; y < height; y++)
        {
            float fy = Math.Clamp((((y + 0.5f) * sh) / height) - 0.5f, 0f, sh - 1f);
            int y0 = (int)fy;
            int y1 = Math.Min(y0 + 1, sh - 1);
            float ty = fy - y0;
            for (int x = 0; x < width; x++)
            {
                float fx = Math.Clamp((((x + 0.5f) * sw) / width) - 0.5f, 0f, sw - 1f);
                int x0 = (int)fx;
                int x1 = Math.Min(x0 + 1, sw - 1);
                float tx = fx - x0;
                int a = ((y0 * sw) + x0) * 4;
                int b = ((y0 * sw) + x1) * 4;
                int c = ((y1 * sw) + x0) * 4;
                int d = ((y1 * sw) + x1) * 4;
                int o = ((y * width) + x) * 4;
                for (int k = 0; k < 4; k++)
                {
                    float top = src[a + k] + ((src[b + k] - src[a + k]) * tx);
                    float bottom = src[c + k] + ((src[d + k] - src[c + k]) * tx);
                    rgba[o + k] = (byte)Math.Clamp(top + ((bottom - top) * ty) + 0.5f, 0f, 255f);
                }
            }
        }

        return new DecodedTexture { Width = width, Height = height, HasAlpha = texture.HasAlpha, Ok = true, Rgba = rgba };
    }

    /// <summary>One layer over the target: colour from one texture, coverage from another's alpha.</summary>
    private static void Blend(
        byte[] output, int width, int height, DecodedTexture colour, DecodedTexture coverage,
        int mode, float weight, Colourise? tint)
    {
        var pixel = new byte[4];

        for (int y = 0; y < height; y++)
        {
            int cy = colour.Height == height ? y : Math.Min(colour.Height - 1, y * colour.Height / height);
            int my = coverage.Height == height ? y : Math.Min(coverage.Height - 1, y * coverage.Height / height);

            for (int x = 0; x < width; x++)
            {
                int cx = colour.Width == width ? x : Math.Min(colour.Width - 1, x * colour.Width / width);
                int mx = coverage.Width == width ? x : Math.Min(coverage.Width - 1, x * coverage.Width / width);

                int src = ((cy * colour.Width) + cx) * 4;
                int cover = ((my * coverage.Width) + mx) * 4;
                int dst = ((y * width) + x) * 4;

                float alpha = mode switch
                {
                    0 => 1.0f,
                    3 => weight,
                    _ => (coverage.Rgba[cover + 3] / 255.0f) * weight,
                };

                if (alpha <= 0.0f)
                {
                    continue;
                }

                byte[] from = colour.Rgba;
                int fromAt = src;
                if (tint is not null)
                {
                    tint.Apply(colour.Rgba, src, pixel, 0);
                    from = pixel;
                    fromAt = 0;
                }

                for (int k = 0; k < 3; k++)
                {
                    float value = mode == 2
                        ? output[dst + k] + (from[fromAt + k] * alpha)
                        : (output[dst + k] * (1.0f - alpha)) + (from[fromAt + k] * alpha);

                    output[dst + k] = (byte)Math.Clamp(value + 0.5f, 0.0f, 255.0f);
                }
            }
        }
    }

    /// <summary>
    /// The dye: the layer is reduced to intensity, tone-curved by Brightness, and mapped black
    /// to the dye's hue to white with the pivot at the dye's own luminance. Constants as
    /// <c>sub_82A7E288</c> computes them; the per-pixel arithmetic is the shipped
    /// <c>texture_morph_colourise</c> pixel shader's.
    /// </summary>
    private sealed class Colourise
    {
        private static readonly byte[,] BrightnessRows =
        {
            { 0x00, 0x10, 0x40, 0x90, 0xFF },
            { 0x00, 0x28, 0x60, 0xA8, 0xFF },
            { 0x00, 0x40, 0x80, 0xC0, 0xFF },
            { 0x00, 0x60, 0x9A, 0xCE, 0xFF },
            { 0x00, 0x80, 0xB4, 0xDC, 0xFF },
        };

        private readonly float[] _colour = new float[3];
        private readonly float _colourAlpha;
        private readonly float[] _toIntensity = new float[3];
        private readonly float _pivot;
        private readonly float[] _curve = new float[5];

        public Colourise(uint tint, float brightness)
        {
            int b = (int)((tint >> 24) & 0xFF);
            int g = (int)((tint >> 16) & 0xFF);
            int r = (int)((tint >> 8) & 0xFF);
            int a = (int)(tint & 0xFF);

            int max = Math.Max(r, Math.Max(g, b));
            if (max == 0)
            {
                r = g = b = 1;
                max = 1;
            }

            _colour[0] = r / (float)max;
            _colour[1] = g / (float)max;
            _colour[2] = b / (float)max;
            _colourAlpha = a / 255.0f;

            float wr = MathF.Pow(0.212671f, 0.4545f);
            float wg = MathF.Pow(0.71516f, 0.4545f);
            float wb = MathF.Pow(0.072169f, 0.4545f);
            float sum = wr + wg + wb;
            wr /= sum;
            wg /= sum;
            wb /= sum;

            float scale = max / 255.0f;
            _toIntensity[0] = wr * scale;
            _toIntensity[1] = wg * scale;
            _toIntensity[2] = wb * scale;
            _pivot = (_colour[0] * wr) + (_colour[1] * wg) + (_colour[2] * wb);

            float t = (Math.Clamp(brightness, -1.0f, 1.0f) + 1.0f) * 2.0f;
            int row = Math.Min(4, (int)t);
            int next = Math.Min(4, row + 1);
            float f = t - row;
            for (int i = 0; i < 5; i++)
            {
                _curve[i] = ((BrightnessRows[row, i] * (1.0f - f)) +
                    (BrightnessRows[next, i] * f)) / 255.0f;
            }
        }

        public void Apply(byte[] from, int fromAt, byte[] to, int toAt)
        {
            float tr = from[fromAt] / 255.0f;
            float tg = from[fromAt + 1] / 255.0f;
            float tb = from[fromAt + 2] / 255.0f;

            float t = 4.0f * ((tr * _toIntensity[0]) + (tg * _toIntensity[1]) + (tb * _toIntensity[2]));
            int i = Math.Clamp((int)t, 0, 3);
            float f = Math.Clamp(t - i, 0.0f, 1.0f);
            float curve = _curve[i] + ((_curve[i + 1] - _curve[i]) * f);

            float lo = _pivot > 1e-5f ? MathF.Min(curve / _pivot, 1.0f) : 1.0f;
            float hi = _pivot < 1.0f - 1e-5f
                ? MathF.Max(curve - _pivot, 0.0f) / (1.0f - _pivot)
                : 0.0f;

            float texel0 = tr, texel1 = tg, texel2 = tb;
            for (int k = 0; k < 3; k++)
            {
                float col = lo * _colour[k];
                col += hi * (1.0f - col);
                float texel = k == 0 ? texel0 : k == 1 ? texel1 : texel2;
                float value = texel + ((col - texel) * _colourAlpha);
                to[toAt + k] = (byte)Math.Clamp((value * 255.0f) + 0.5f, 0.0f, 255.0f);
            }

            to[toAt + 3] = from[fromAt + 3];
        }
    }

    public static Dictionary<uint, List<TextureMorphEntry>> Active(Func<uint, float> weightOfType)
    {
        var active = new Dictionary<uint, List<TextureMorphEntry>>();

        foreach (TextureMorphPair pair in Pairs())
        {
            if (pair.MorphType == Engine_HeroMorphState.TypeScar)
            {
                continue;
            }

            float weight = pair.MorphType == Engine_HeroMorphState.TypeAlways
                ? 1.0f
                : weightOfType(pair.MorphType);

            if (weight <= Engine_HeroMorph.WeightFloor)
            {
                continue;
            }

            if (!active.TryGetValue(pair.OriginalTextureHash, out List<TextureMorphEntry>? list))
            {
                list = new List<TextureMorphEntry>();
                active[pair.OriginalTextureHash] = list;
            }

            list.Add(new TextureMorphEntry
            {
                MorphHash = pair.MorphTextureHash,
                Layer = pair.Layer,
                Weight = weight,
                Blend = pair.Blend,
            });
        }

        foreach (List<TextureMorphEntry> list in active.Values)
        {
            list.Sort((a, b) =>
                a.Layer != b.Layer
                    ? a.Layer.CompareTo(b.Layer)
                    : a.MorphHash.CompareTo(b.MorphHash));
        }

        return active;
    }

    public static Texture2D? Resolve(
        string textureName, Dictionary<uint, List<TextureMorphEntry>> active)
    {
        if (textureName.Length == 0 || active.Count == 0)
        {
            return null;
        }

        uint hash = Engine_TexPathIndex.PathHash(textureName);
        if (!active.TryGetValue(hash, out List<TextureMorphEntry>? entries))
        {
            return null;
        }

        string baseName = textureName;
        var layers = new List<(string Path, TextureMorphEntry Entry)>();

        foreach (TextureMorphEntry entry in entries)
        {
            string? target = Engine_TexPathIndex.Resolve(entry.MorphHash);
            if (target is null)
            {
                continue;
            }

            if (entry.Blend == TextureMorphBlend.Swap)
            {
                baseName = target;
                continue;
            }

            layers.Add((target, entry));
        }

        if (layers.Count == 0 &&
            baseName.Equals(textureName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var key = new System.Text.StringBuilder();
        key.Append(Engine_TexPathIndex.PathHash(baseName).ToString("x8"));
        foreach ((string path, TextureMorphEntry entry) in layers)
        {
            key.Append('_')
                .Append(Engine_TexPathIndex.PathHash(path).ToString("x8"))
                .Append(entry.Blend == TextureMorphBlend.Additive ? 'a' : 'o')
                .Append(BitConverter.SingleToUInt32Bits(entry.Weight).ToString("x8"));
        }

        string cacheKey = key.ToString();

        lock (MorphLock)
        {
            if (Composited.TryGetValue(cacheKey, out ImageTexture? cached))
            {
                return cached;
            }
        }

        DecodedTexture source = Decode(baseName);
        if (!source.Ok)
        {
            return null;
        }

        // Sized as `sub_82A5BCE8` sizes a composite: the largest layer, 64 x 64 at least.
        var decodedLayers = new List<(DecodedTexture Texture, TextureMorphEntry Entry)>(layers.Count);
        int width = Math.Max(MinimumCompositeSize, source.Width);
        int height = Math.Max(MinimumCompositeSize, source.Height);
        foreach ((string path, TextureMorphEntry entry) in layers)
        {
            DecodedTexture decodedLayer = Decode(path);
            if (decodedLayer.Ok)
            {
                decodedLayers.Add((decodedLayer, entry));
                width = Math.Max(width, decodedLayer.Width);
                height = Math.Max(height, decodedLayer.Height);
            }
        }

        byte[] output = Fit(source, width, height).Rgba;
        if (ReferenceEquals(output, source.Rgba))
        {
            output = (byte[])output.Clone();
        }

        foreach ((DecodedTexture decodedLayer, TextureMorphEntry entry) in decodedLayers)
        {
            DecodedTexture over = Fit(decodedLayer, width, height);
            bool additive = entry.Blend == TextureMorphBlend.Additive;

            for (int y = 0; y < height; y++)
            {
                int sy = over.Height == height
                    ? y
                    : Math.Min(over.Height - 1, y * over.Height / height);

                for (int x = 0; x < width; x++)
                {
                    int sx = over.Width == width
                        ? x
                        : Math.Min(over.Width - 1, x * over.Width / width);

                    int src = ((sy * over.Width) + sx) * 4;
                    float alpha = (over.Rgba[src + 3] / 255.0f) * entry.Weight;
                    if (alpha <= 0.0f)
                    {
                        continue;
                    }

                    int dst = ((y * width) + x) * 4;
                    for (int k = 0; k < 3; k++)
                    {
                        float value = additive
                            ? output[dst + k] + (over.Rgba[src + k] * alpha)
                            : (output[dst + k] * (1.0f - alpha)) + (over.Rgba[src + k] * alpha);

                        output[dst + k] = (byte)Math.Clamp(value + 0.5f, 0.0f, 255.0f);
                    }
                }
            }
        }

        System.Threading.Interlocked.Increment(ref _compositesBuilt);
        string stem = System.IO.Path.GetFileName(baseName);
        ImageTexture texture = ToTexture(
            output, width, height, stem.StartsWith("norm", StringComparison.OrdinalIgnoreCase));

        lock (MorphLock)
        {
            if (Composited.Count >= CompositedLimit)
            {
                Composited.Clear();
            }

            Composited[cacheKey] = texture;
        }

        return texture;
    }

    /// <summary>
    /// The finished composite as a texture, WITH A FULL MIP CHAIN like every other texture the
    /// material binds. The samplers are mipmapped; a top level alone is minified by
    /// point-picking texels, and on a normal map and a self-illumination map that is a skin
    /// full of sparkling highlights and stray glowing dots. A normal map is renormalised down
    /// the chain and block-compressed the way the library compresses its own (BC5 for a
    /// normal, BC1/BC3 otherwise).
    /// </summary>
    private static ImageTexture ToTexture(byte[] rgba, int width, int height, bool normalMap)
    {
        Image image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba);
        image.GenerateMipmaps(normalMap);
        Engine_TexImage.CompressBlocks(image, normalMap);
        return ImageTexture.CreateFromImage(image);
    }

    private static DecodedTexture Decode(string path)
    {
        string key = path.Replace('\\', '/');

        lock (MorphLock)
        {
            if (DecodedCache.TryGetValue(key, out DecodedTexture? cached))
            {
                return cached;
            }
        }

        var value = new DecodedTexture();
        byte[]? bytes = Refii.Models.Engine_BnkMeshLoader.ReadBytes(path);

        if (bytes is not null)
        {
            try
            {
                Image? image = Engine_TexImage.TryCreateImage(bytes, out bool hasAlpha, out _);

                if (image is not null)
                {
                    value.HasAlpha = hasAlpha;
                    Fill(value, image);
                }
            }
            catch (Exception)
            {
                value.Ok = false;
            }
        }

        lock (MorphLock)
        {
            DecodedCache[key] = value;
        }

        return value;
    }

    private static void Fill(DecodedTexture value, Image image)
    {
        if (image.IsCompressed())
        {
            image.Decompress();
        }

        if (image.GetFormat() != Image.Format.Rgba8)
        {
            image.Convert(Image.Format.Rgba8);
        }

        int width = image.GetWidth();
        int height = image.GetHeight();
        if (width <= 0 || height <= 0)
        {
            return;
        }

        byte[] data = image.GetData();
        int needed = width * height * 4;
        if (data.Length < needed)
        {
            return;
        }

        value.Width = width;
        value.Height = height;
        value.Rgba = data.Length == needed ? data : data[..needed];
        value.Ok = true;
    }
}
