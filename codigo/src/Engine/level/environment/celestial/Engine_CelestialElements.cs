using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Level;

public static class Engine_CelestialElements
{
    // Shader parameter names as StringNames: a string literal converts to a fresh
    // finalizable StringName on every call.
    // Static paths: a string literal here is a new finalizable NodePath on every call.
    private static readonly NodePath Path_Moon = "Moon";
    private static readonly NodePath Path_MoonGlare = "MoonGlare";
    private static readonly NodePath Path_Stars = "Stars";
    private static readonly NodePath Path_SunBeams = "SunBeams";
    private static readonly NodePath Path_SunDisc = "SunDisc";
    private static readonly NodePath Path_SunGlare = "SunGlare";
    private static readonly StringName Param_element_colour = "element_colour";
    private static readonly StringName Param_element_dir = "element_dir";
    private static readonly StringName Param_element_distance = "element_distance";
    private static readonly StringName Param_element_facing_power = "element_facing_power";
    private static readonly StringName Param_element_half = "element_half";
    private static readonly StringName Param_element_mode = "element_mode";
    private static readonly StringName Param_element_premultiply = "element_premultiply";
    private static readonly StringName Param_element_tex = "element_tex";
    private static readonly StringName Param_occlusion_distance = "occlusion_distance";
    private static readonly StringName Param_occlusion_half = "occlusion_half";
    private static readonly StringName Param_star_brightness = "star_brightness";
    private static readonly StringName Param_star_phase_rate = "star_phase_rate";
    private static readonly StringName Param_star_point_size = "star_point_size";
    private static readonly StringName Param_star_radius = "star_radius";

    public const string NodeName = "Celestial";

    public const string MixShaderPath =
        "res://src/Engine/level/environment/celestial/celestial.gdshader";

    public const string AdditiveShaderPath =
        "res://src/Engine/level/environment/celestial/celestial_additive.gdshader";

    public const string OverlayShaderPath =
        "res://src/Engine/level/environment/celestial/celestial_overlay.gdshader";

    public const string StarsShaderPath =
        "res://src/Engine/level/environment/celestial/stars.gdshader";

    public const float MoonBillboardDistance = 4000.0f;

    public const float MoonSizeScale = 300.0f;

    public const float MoonPhaseUStep = 0.125f;

    /// <summary>The glow-layer moon (`0x82279410`): the moon texture at <c>moonDir × 4000</c>,
    /// half-size <c>MOON_SIZE × 400</c> (constant at 0x822799f8 / 0x82279a5c), PS binding 144
    /// whose body is <c>texel.rgb × 0.2 × texel.a</c> — a third larger than the disc and a fifth
    /// as bright, and the only moon the bright pass sees.</summary>
    public const float MoonBloomSizeScale = 400.0f;
    public const float MoonBloomShaderScale = 0.2f;

    public const float MoonGlareDistance = 1000.0f;

    public const float MoonGlareSizeScale = 300.0f;

    public const float SunDiscDistance = 1900.0f;

    public const float SunDiscSizeScale = 300.0f;

    /// <summary>Blob 0121 sunbeams: `texel.rgb * (1/6) * g_SunColour.rgb` — the attenuation is
    /// the shader's own literal, carried here because our shader does not have it.</summary>
    public const float SunBeamsColourScale = 1.0f / 6.0f;

    public const float SunBeamsDistance = 1900.0f;

    public const float SunBeamsSizeScale = 600.0f;

    public const float SunGlareDistance = 1999.0f;

    public const float SunGlareSizeScale = 2000.0f;

    public const float IntensityGate = 0.000099999997f;

    public const int StarCount = 512;

    public const float StarRadius = 2500.0f;

    public const float StarPointSize = 1.5f;

    public const float StarPhaseRate = 2.5f;


    /// <summary>
    /// The moon bloom's CPU-SIDE scale: `flt_833210CC = 0x3CCCCCCD = 0.025`, read out of the
    /// image. `Render_LitDecalDraw_INFERRED` @0x821D57E0 builds pixel-shader constant 244 as
    /// `0.025 * MOON_GLARE_INTENSITY * fade^2` (0x821d5e3c loads the literal, 0x821d5e78/7c do
    /// the two multiplies, 0x821d5e40 selects index 244), alpha 0.
    ///
    /// NOT 0.2. That value came from blob 0120 "moonbloom"'s own internal literal
    /// (`texel.rgb * 0.2 * texel.a`), but the binding table points the moon glare at pixel
    /// shader 0x8C — the SHARED glare shader the sun uses — so 0120 may not be the one that
    /// runs. What is certain is the constant the CPU writes, and that is this.
    /// </summary>
    public const float MoonGlareCpuScale = 0.025f;

    public const int DefaultMoonPhase = 4;

    public const float CullExtent = 100000.0f;

    private static readonly AtmosphereState _frameAtmosphere = new();

    private static float[]? _elementCache;
    private static SkyTheme? _elementCacheFor;

    /// <summary>
    /// The theme's sixteen element parameters, cached against the theme they came from.
    /// </summary>
    /// <remarks>
    /// Called two or three times a frame — the celestial update, EnsureElements, and the sun
    /// sources. A theme is a class and changes only when the region re-evaluates, so reference
    /// identity is the key.
    /// </remarks>
    private static readonly float[] _liveElement = new float[16];

    /// <summary>
    /// Under the classic renderer with the guest's theme block up: the same sixteen values read
    /// from it (MOON_* 65..70, SUN_DISC_* 92..94, SUN_BEAMS_* 95..97, SUN_GLARE_* 100..101).
    /// </summary>
    private static float[]? LiveElementParams()
    {
        if (!Refii.Models.Engine_ClassicMaterial.Active || !Refii.Models.Engine_ClassicTheme.Live)
        {
            return null;
        }
        int[] index = LiveElementIndex;
        for (int i = 0; i < index.Length; i++)
        {
            _liveElement[i] = Refii.Models.Engine_ClassicTheme.Param(index[i]);
        }
        _liveElement[8] = Refii.Models.Engine_ClassicTheme.Param(93, 0);
        _liveElement[9] = Refii.Models.Engine_ClassicTheme.Param(93, 1);
        _liveElement[10] = Refii.Models.Engine_ClassicTheme.Param(93, 2);
        _liveElement[11] = Refii.Models.Engine_ClassicTheme.Param(95);
        _liveElement[12] = Refii.Models.Engine_ClassicTheme.Param(96);
        _liveElement[13] = Refii.Models.Engine_ClassicTheme.Param(97);
        _liveElement[14] = Refii.Models.Engine_ClassicTheme.Param(100);
        _liveElement[15] = Refii.Models.Engine_ClassicTheme.Param(101);
        return _liveElement;
    }

    public static float[] ElementParams(SkyTheme theme)
    {
        if (LiveElementParams() is { } live)
        {
            return live;
        }
        if (_elementCache is { } cached && ReferenceEquals(_elementCacheFor, theme))
        {
            return cached;
        }

        float[] built = BuildElementParams(theme);
        _elementCache = built;
        _elementCacheFor = theme;
        return built;
    }

    private static float[] BuildElementParams(SkyTheme theme) => new[]
    {
        theme.MoonIntensity,
        theme.MoonSize,
        theme.MoonGlareIntensity,
        theme.MoonGlareSize,
        theme.MoonTransparency,
        theme.StarBrightness,
        theme.SunDiscSize,
        theme.SunDiscIntensity,
        theme.SunDiscColour[0],
        theme.SunDiscColour[1],
        theme.SunDiscColour[2],
        theme.SunBeamsWidth,
        theme.SunBeamsHeight,
        theme.SunBeamsIntensity,
        theme.SunGlareIntensity,
        theme.SunGlareSize,
    };

    public static Node3D? Build(
        SkyTheme theme,
        SkyFrame frame,
        Func<string, byte[]?> read,
        int moonPhase = DefaultMoonPhase)
    {
        if (!theme.HasAny)
        {
            return null;
        }

        Shader? mixShader = GD.Load<Shader>(MixShaderPath);
        Shader? additiveShader = GD.Load<Shader>(AdditiveShaderPath);
        if (mixShader is null || additiveShader is null)
        {
            GD.Print($"[celestial] shader missing at {MixShaderPath} / {AdditiveShaderPath}");
            return null;
        }

        AtmosphereState state = Engine_SkyAtmosphere.Compute(
            Engine_SkyAtmosphere.FromFrame(frame, theme));
        float[] sunTint = state.SunElementColour;

        Vector3 sun = ToGodot(frame.SunDirection);
        Vector3 moon = ToGodot(frame.MoonDirection);
        Stated(ref sun, ref moon, ref sunTint);

        var container = new Node3D { Name = NodeName };
        EnsureElements(container, theme, read, moonPhase);
        Update(container, theme, frame, read, moonPhase);

        if (container.GetChildCount() == 0)
        {
            container.QueueFree();
            return null;
        }

        var names = new List<string>();
        foreach (Node child in container.GetChildren())
        {
            names.Add(child.Name.ToString());
        }

        GD.Print(
            $"[celestial] {string.Join(", ", names)}; " +
            $"sun=({sun.X:0.00},{sun.Y:0.00},{sun.Z:0.00}) " +
            $"moon=({moon.X:0.00},{moon.Y:0.00},{moon.Z:0.00}) " +
            $"tint=({sunTint[0]:0.00},{sunTint[1]:0.00},{sunTint[2]:0.00}) " +
            $"phase={Math.Clamp(moonPhase, 0, 7)}");

        return container;
    }

    public const string InitializedMeta = "celestial_elements_initialized";
    private static readonly StringName InitializedMetaName = InitializedMeta;

    public static void EnsureElements(
        Node3D container,
        SkyTheme theme,
        Func<string, byte[]?> read,
        int moonPhase = DefaultMoonPhase)
    {
        if (container.HasMeta(InitializedMetaName))
        {
            return;
        }

        container.SetMeta(InitializedMetaName, true);

        Shader? mixShader = GD.Load<Shader>(MixShaderPath);
        Shader? additiveShader = GD.Load<Shader>(AdditiveShaderPath);
        if (mixShader is null || additiveShader is null)
        {
            return;
        }

        float[] element = ElementParams(theme);
        int priority = container.GetChildCount();

        if (theme.HasSunBeamsTexture &&
            container.GetNodeOrNull<MeshInstance3D>(Path_SunBeams) is null)
        {
            Texture2D? beams = Load(theme.SunBeamsTextureHash, read, "sun beams");
            if (beams is not null)
            {
                container.AddChild(Billboard(
                    "SunBeams", additiveShader, beams, Vector3.Up, SunBeamsDistance,
                    element[11] * SunBeamsSizeScale, element[12] * SunBeamsSizeScale,
                    Vector3.Zero, 1f, 0f, 0f, 1f, priority++));
            }
        }

        if (theme.HasSunDiscTexture &&
            container.GetNodeOrNull<MeshInstance3D>(Path_SunDisc) is null)
        {
            Texture2D? disc = Load(theme.SunDiscTextureHash, read, "sun disc");
            if (disc is not null)
            {
                MeshInstance3D discNode = Billboard(
                    "SunDisc", mixShader, disc, Vector3.Up, SunDiscDistance,
                    element[6] * SunDiscSizeScale, element[6] * SunDiscSizeScale,
                    Vector3.Zero, 1f, 0f, 0f, 1f, priority++, 1);
                // The title's disc (`0x821D8658`): PS SUNDISC, additive ONE/ONE, alpha tested,
                // no depth write — its colour times c20.w like every classic surface.
                Classic(discNode, "PSHADER_SUNDISC", "g_SunDiscTexture", disc,
                    "unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled",
                    "#define XR_WRITE_ALPHA(a) ALPHA = 1.0", 0.5f / 255f);
                container.AddChild(discNode);
            }
        }

        Shader overlayShader = GD.Load<Shader>(OverlayShaderPath) ?? additiveShader;

        if (theme.HasSunGlareTexture &&
            container.GetNodeOrNull<MeshInstance3D>(Path_SunGlare) is null)
        {
            Texture2D? glare = Load(theme.SunGlareTextureHash, read, "sun glare");
            if (glare is not null)
            {
                container.AddChild(Billboard(
                    "SunGlare", overlayShader, glare, Vector3.Up, SunGlareDistance,
                    element[15] * SunGlareSizeScale, element[15] * SunGlareSizeScale,
                    Vector3.Zero, 1f, 2f, 0f, 1f, priority++));
            }
        }

        int phase = Math.Clamp(moonPhase, 0, 7);
        float u0 = phase * MoonPhaseUStep;
        float u1 = (phase + 1) * MoonPhaseUStep;

        if (theme.HasMoonTexture &&
            container.GetNodeOrNull<MeshInstance3D>(Path_Moon) is null)
        {
            Texture2D? moonDisc = Load(theme.MoonTextureHash, read, "moon");
            if (moonDisc is not null)
            {
                MeshInstance3D moonNode = Billboard(
                    "Moon", mixShader, moonDisc, Vector3.Up, MoonBillboardDistance,
                    element[1] * MoonSizeScale, element[1] * MoonSizeScale,
                    Vector3.Zero, element[4], 0f, u0, u1, priority++, 2);
                // The title's moon (`0x821D3B80`): PS MOONDISC, SRCALPHA / INVSRCALPHA, alpha
                // ref 1, no depth write.
                Classic(moonNode, "PSHADER_MOONDISC", "g_MoonTexture", moonDisc,
                    "unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled",
                    string.Empty, 1.5f / 255f);
                container.AddChild(moonNode);
            }
        }

        // THE MOON GLARE IS A SCENE DRAW, like the sun's: main-scene list 14 (slot 18, after
        // the world, Z off, ONE/ONE), `0x821D57E0`, theme texture slot 7 at moonDir × 1000,
        // half-size MOON_GLARE_SIZE × 300, constant 244 = 0.025 × MOON_GLARE_INTENSITY × fade².
        // Published to the glow layer instead, it went through the bloom's screen blend with
        // no exposure or tone curve on it and showed as a white ball across a quarter of the
        // sky (Bower Lake, 2026-10-25). What the glow layer holds of the moon is MOONBLOOM,
        // published from Update.
        if (theme.HasMoonGlareTexture &&
            container.GetNodeOrNull<MeshInstance3D>(Path_MoonGlare) is null)
        {
            Texture2D? moonGlare = Load(theme.MoonGlareTextureHash, read, "moon glare");
            if (moonGlare is not null)
            {
                container.AddChild(Billboard(
                    "MoonGlare", overlayShader, moonGlare, Vector3.Up, MoonGlareDistance,
                    element[3] * MoonGlareSizeScale, element[3] * MoonGlareSizeScale,
                    Vector3.Zero, 1f, 0f, 0f, 1f, priority++));
            }
        }

        if (container.GetNodeOrNull<MeshInstance3D>(Path_Stars) is null)
        {
            MeshInstance3D? stars = Stars(element[5], priority++);
            if (stars is not null)
            {
                container.AddChild(stars);
            }
        }
    }

    public static void Update(
        Node3D container,
        SkyTheme theme,
        SkyFrame frame,
        Func<string, byte[]?>? read = null,
        int moonPhase = DefaultMoonPhase)
    {
        EnsureElements(
            container, theme,
            read ?? Refii.Models.Engine_BnkMeshLoader.ReadBytes, moonPhase);

        float[] element = ElementParams(theme);

        // Reused: `AtmosphereState` holds thirteen arrays and this runs every frame. Every field
        // is overwritten by the compute, so nothing carries over.
        AtmosphereState state = _frameAtmosphere;
        Engine_SkyAtmosphere.Compute(Engine_SkyAtmosphere.FromFrame(frame, theme), state);
        float[] sunTint = state.SunElementColour;

        Vector3 sun = ToGodot(frame.SunDirection);
        Vector3 moon = ToGodot(frame.MoonDirection);
        Stated(ref sun, ref moon, ref sunTint);
        bool sunActive = frame.SkyParams[0] >= IntensityGate;

        if (DebugEnv)
        {
            GD.Print(
                $"[celestial-dbg] sun=({sun.X:0.00},{sun.Y:0.00},{sun.Z:0.00}) " +
                $"moon=({moon.X:0.00},{moon.Y:0.00},{moon.Z:0.00}) " +
                $"tint=({sunTint[0]:0.00},{sunTint[1]:0.00},{sunTint[2]:0.00}) " +
                $"discCol=({element[8]:0.00},{element[9]:0.00},{element[10]:0.00}) " +
                $"discInt={element[7]:0.000} moonInt={element[0]:0.000} " +
                $"moonTrans={element[4]:0.000} moonGlareInt={element[2]:0.000} " +
                $"sunActive={sunActive}");
        }

        Shader? overlay = null;

        // By index: GetChildren() is a Godot Array, a finalizable handle per frame.
        for (int i = 0, count = container.GetChildCount(); i < count; i++)
        {
            Node child = container.GetChild(i);
            if (child is not MeshInstance3D mesh ||
                mesh.MaterialOverride is not ShaderMaterial material)
            {
                continue;
            }

            string elementName = Refii.Core.Engine_NodeNames.Of(child);
            if (elementName is "SunGlare" or "MoonGlare" &&
                material.Shader?.ResourcePath != OverlayShaderPath)
            {
                overlay ??= GD.Load<Shader>(OverlayShaderPath);
                if (overlay is not null)
                {
                    material.Shader = overlay;
                }
            }

            switch (elementName)
            {
                case "SunBeams":
                    material.SetShaderParameter(Param_element_dir, sun);
                    material.SetShaderParameter(Param_element_half, new Vector2(
                        element[11] * SunBeamsSizeScale,
                        element[12] * SunBeamsSizeScale));
                    float beams = element[13] * SunBeamsColourScale;
                    material.SetShaderParameter(Param_element_colour, new Vector4(
                        sunTint[0] * beams,
                        sunTint[1] * beams,
                        sunTint[2] * beams, 1f));
                    // ALWAYS WHEN LIT — the billboard is the scene's sun, and the post-FX
                    // rays are a radial blur OF THE SCENE, so this is their source. Standing it
                    // down while the chain ran left the rays with nothing real to streak and
                    // the chain synthesized a source instead, which drew through cave walls.
                    mesh.Visible = sunActive && element[13] >= IntensityGate;
                    break;
                case "SunDisc":
                    material.SetShaderParameter(Param_element_dir, sun);
                    material.SetShaderParameter(Param_element_half, new Vector2(
                        element[6] * SunDiscSizeScale,
                        element[6] * SunDiscSizeScale));
                    // THE ATMOSPHERE IS A FACTOR OF THE DISC. The guest's pixel constant for
                    // the disc is `sky[144] * SUN_DISC_COLOUR * SUN_DISC_INTENSITY`
                    // (`Render_QueryGatedEffectDraw_INFERRED_2`, 0x821D8658), and sky[144] is the
                    // sun colour after `sub_82263570`'s Rayleigh/Mie attenuation by the sun's
                    // zenith angle — the same tint the beams and glare take here. Without it
                    // the disc is the authored colour at full intensity whenever it is above
                    // the horizon: the huge flat white ball in the night sky.
                    var discColour = new Vector4(
                        sunTint[0] * element[8] * element[7],
                        sunTint[1] * element[9] * element[7],
                        sunTint[2] * element[10] * element[7], 1f);
                    material.SetShaderParameter(Param_element_colour, discColour);
                    ClassicColour(material, discColour);
                    FollowSlot(material, Refii.Models.Engine_ClassicTheme.SlotSunDisc, PsSunDiscTextureName);
                    mesh.Visible = sunActive && element[7] >= IntensityGate;
                    break;
                case "SunGlare":
                    material.SetShaderParameter(Param_element_dir, sun);
                    material.SetShaderParameter(Param_element_half, new Vector2(
                        element[15] * SunGlareSizeScale,
                        element[15] * SunGlareSizeScale));
                    material.SetShaderParameter(Param_element_colour, new Vector4(
                        sunTint[0] * element[14],
                        sunTint[1] * element[14],
                        sunTint[2] * element[14], 1f));
                    material.SetShaderParameter(Param_occlusion_half,
                        element[6] * SunDiscSizeScale);
                    material.SetShaderParameter(Param_occlusion_distance, SunDiscDistance);
                    mesh.Visible = sunActive && element[14] >= IntensityGate;
                    break;
                case "Moon":
                    // The guest's own phase (sky M+20) under the classic renderer.
                    if (Refii.Models.Engine_ClassicMaterial.Active && Refii.Models.Engine_ClassicTheme.Live)
                    {
                        int livePhase = Math.Clamp(Refii.Models.Engine_ClassicTheme.MoonPhase, 0, 7);
                        if (!mesh.HasMeta(MoonPhaseMeta) || mesh.GetMeta(MoonPhaseMeta).AsInt32() != livePhase)
                        {
                            mesh.Mesh = QuadMesh("Moon", livePhase * MoonPhaseUStep, (livePhase + 1) * MoonPhaseUStep);
                            mesh.SetMeta(MoonPhaseMeta, livePhase);
                        }
                    }
                    material.SetShaderParameter(Param_element_dir, moon);
                    material.SetShaderParameter(Param_element_half, new Vector2(
                        element[1] * MoonSizeScale,
                        element[1] * MoonSizeScale));
                    var moonColour = new Vector4(
                        0.5f * element[0],
                        0.5f * element[0],
                        0.5f * element[0],
                        element[4]);
                    material.SetShaderParameter(Param_element_colour, moonColour);
                    ClassicColour(material, moonColour);
                    FollowSlot(material, Refii.Models.Engine_ClassicTheme.SlotMoon, PsMoonTextureName);
                    mesh.Visible = element[0] >= IntensityGate;
                    break;
                case "MoonGlare":
                    material.SetShaderParameter(Param_element_dir, moon);
                    material.SetShaderParameter(Param_element_half, new Vector2(
                        element[3] * MoonGlareSizeScale,
                        element[3] * MoonGlareSizeScale));
                    // THE SCALE WAS 8x TOO BIG, WHICH IS WHAT BLEW THE MOON OUT — and the
                    // intensity term is real, so do not drop it.
                    //
                    // The guest builds this as `0.025 * MOON_GLARE_INTENSITY * fade^2`
                    // (0x821D57E0, constant 244). We had `0.2 * MOON_GLARE_INTENSITY`, and the
                    // authored night values are TENS, not ~1 — from `environmentthemes.gdb`,
                    // MoonGlareIntensity is 20 at Bower Lake midnight and 30/40 at Bowerstone
                    // Market against 1 in daylight. So we emitted 4.0-8.0 where the guest emits
                    // 0.5, on a bloom that legitimately spans ~44 degrees (MoonGlareSize 1.3 x
                    // 300 at distance 1000): a white ball filling the screen.
                    //
                    // `fade^2` is the moon's own occlusion query, `clamp(pixels / (MoonSize^2 *
                    // 6000))` squared (0x82279a94..0x82279ac4, divisor flt_82000C28 = 6000).
                    // Our shader's `vis * vis` already IS that term, so it is not applied here.
                    material.SetShaderParameter(Param_element_premultiply, 1.0f);
                    float glareScale = MoonGlareCpuScale * element[2];
                    material.SetShaderParameter(Param_element_colour, new Vector4(
                        glareScale, glareScale, glareScale, 1f));
                    material.SetShaderParameter(Param_occlusion_half,
                        element[1] * MoonSizeScale);
                    material.SetShaderParameter(Param_occlusion_distance,
                        MoonBillboardDistance);
                    mesh.Visible =
                        element[0] >= IntensityGate && element[2] >= IntensityGate;
                    break;
                case "Stars":
                    material.SetShaderParameter(Param_star_brightness, element[5]);
                    mesh.Visible = element[5] >= IntensityGate;
                    break;
            }
        }

        // Under classic the title's own draws take the sun's beams, disc and glare, the moon's
        // glare and bloom and the stars (Engine_ClassicSkyElements); the moon disc above is
        // already on its shader.
        if (Engine_ClassicSkyElements.Active)
        {
            foreach (NodePath hostDrawn in HostDrawnPaths)
            {
                if (container.GetNodeOrNull<MeshInstance3D>(hostDrawn) is { } node)
                {
                    node.Visible = false;
                }
            }
            Refii.Fx.Engine_PostFxHalos.PublishSky("SunDisc", default);
            Refii.Fx.Engine_PostFxHalos.PublishSky("MoonBloom", default);
            Engine_ClassicSkyElements.Update(container, container.GetViewport()?.GetCamera3D());
            return;
        }

        PublishGlowLayer(
            container, theme, element, sun, moon, sunTint, sunActive, moonPhase,
            read ?? Refii.Models.Engine_BnkMeshLoader.ReadBytes);
    }

    private static readonly Dictionary<uint, Texture2D?> GlareTextures = new();

    /// <summary>
    /// What the guest's glow layer holds of the sky (`sub_82277380`, after the light halos),
    /// published as sky halos so the bright pass sums them after its threshold exactly as the
    /// title's does — the sun's bloom and the moon's faint one, independent of the scene's
    /// bloom and its dial:
    /// <list type="bullet">
    /// <item>SUN DISC (`0x821D8658`): slot 3 at sunDir × 1900, half-size SUN_DISC_SIZE × 300,
    ///   constant 245 = sunColour × SUN_DISC_COLOUR × SUN_DISC_INTENSITY — the same numbers the
    ///   scene disc takes, so the bloom is the disc's colour. Gate SUN_INTENSITY ≥ 1e-4.</item>
    /// <item>MOON (`0x82279410`): slot 6 at moonDir × 4000, half-size MOON_SIZE × 400, the
    ///   current phase's eighth of the texture, PS 144 = texel × 0.2 × alpha. Gate
    ///   MOON_INTENSITY ≥ 1e-4.</item>
    /// </list>
    /// Each guest draw is its own occlusion query, so neither takes a visibility factor; the
    /// halo shader's per-pixel depth test is the query.
    /// </summary>
    private static void PublishGlowLayer(
        Node3D container, SkyTheme theme, float[] element, Vector3 sun, Vector3 moon, float[] sunTint,
        bool sunActive, int moonPhase, Func<string, byte[]?> read)
    {
        bool any = false;

        Texture2D? disc = theme.HasSunDiscTexture ? CachedTexture(theme.SunDiscTextureHash, read, "sun disc") : null;
        bool sunOn = disc is not null && sunActive && element[7] >= IntensityGate && sun.LengthSquared() > 1e-12f;
        Refii.Fx.Engine_PostFxHalos.PublishSky("SunDisc", new Refii.Fx.Engine_PostFxHalos.SkyHalo(
            sun, SunDiscDistance, element[6] * SunDiscSizeScale,
            new Vector3(
                sunTint[0] * element[8] * element[7],
                sunTint[1] * element[9] * element[7],
                sunTint[2] * element[10] * element[7]),
            disc, sunOn));
        any |= sunOn;

        Texture2D? moonTex = theme.HasMoonTexture ? CachedTexture(theme.MoonTextureHash, read, "moon") : null;
        bool moonOn = moonTex is not null && element[0] >= IntensityGate && moon.LengthSquared() > 1e-12f;
        int phase = Math.Clamp(moonPhase, 0, 7);
        Refii.Fx.Engine_PostFxHalos.PublishSky("MoonBloom", new Refii.Fx.Engine_PostFxHalos.SkyHalo(
            moon, MoonBillboardDistance, element[1] * MoonBloomSizeScale,
            new Vector3(MoonBloomShaderScale, MoonBloomShaderScale, MoonBloomShaderScale),
            moonTex, moonOn, phase * MoonPhaseUStep, (phase + 1) * MoonPhaseUStep));
        any |= moonOn;

        if (any)
        {
            Refii.Fx.Engine_PostFxHalos.Ensure(container);
        }
    }

    private static Texture2D? CachedTexture(uint hash, Func<string, byte[]?> read, string label)
    {
        if (!GlareTextures.TryGetValue(hash, out Texture2D? texture))
        {
            texture = Load(hash, read, label);
            GlareTextures[hash] = texture;
        }

        return texture;
    }

    /// <summary>
    /// The direction for a celestial element, in WORLD space.
    ///
    /// The elements are billboards placed by `celestial.gdshader`, whose vertex stage does
    /// `VIEW_MATRIX * vec4(element_dir, 0)` — world to view. It never touches MODEL_MATRIX, so
    /// the region root's Scale(-1, 1, 1) does NOT apply to this direction and it must not be
    /// pre-compensated for it. Feeding region-local here mirrored the disc away from the
    /// post-FX glow and drew two suns.
    /// </summary>
    private static Vector3 ToGodot(float[] direction) =>
        Refii.Level.Engine_SkyFrame.ToGodot(direction);

    private static readonly StringName SlotHashMeta = "xenos_slot_hash";

    /// <summary>
    /// Under classic, a classic disc's texture follows the theme's live slot (`sub_821D0BA0`, the
    /// heaviest of the slot's textures), as the guest binds it each draw. Rebound only when the
    /// hash moves; a hash that does not resolve keeps what is bound.
    /// </summary>
    private static readonly StringName PsSunDiscTextureName = "ps_g_SunDiscTexture";
    private static readonly StringName PsMoonTextureName = "ps_g_MoonTexture";
    private static readonly NodePath[] HostDrawnPaths = { Path_SunBeams, Path_SunDisc, Path_SunGlare, Path_MoonGlare, Path_Stars };
    private static readonly bool DebugEnv = OS.GetEnvironment("REFII_CELESTIAL_DEBUG").Length != 0;
    private static readonly int[] LiveElementIndex = { 65, 66, 67, 68, 69, 70, 92, 94 };

    private static void FollowSlot(ShaderMaterial material, int slot, StringName parameter)
    {
        if (!Refii.Models.Engine_ClassicMaterial.Active || !Refii.Models.Engine_ClassicTheme.TexturesLive ||
            !material.HasMeta(ClassicColourMeta))
        {
            return;
        }
        uint hash = Refii.Models.Engine_ClassicTheme.TopHash(slot);
        if (hash == 0u || (material.HasMeta(SlotHashMeta) && (uint)material.GetMeta(SlotHashMeta).AsInt64() == hash))
        {
            return;
        }
        material.SetMeta(SlotHashMeta, (long)hash);
        if (Engine_EnvironmentTextures.Load(hash, Refii.Models.Engine_BnkMeshLoader.ReadBytes, out _) is { } texture)
        {
            material.SetShaderParameter(parameter, texture);
        }
    }

    private static readonly float[] _statedSunColour = new float[3];

    /// <summary>
    /// Under classic, the guest's own sun and moon: `sub_82263570` writes the directions to
    /// sky+160 and sky+208 and the attenuated sun colour to sky+144 every frame, and its disc,
    /// beams and glare constants are built from those. Recomputing them from the theme would be
    /// a second derivation of what the guest already states.
    /// </summary>
    private static void Stated(ref Vector3 sun, ref Vector3 moon, ref float[] sunTint)
    {
        if (!Refii.Models.Engine_ClassicMaterial.Active || !Refii.Models.Engine_ClassicTheme.Live)
        {
            return;
        }

        sun = ToGodot(Refii.Models.Engine_ClassicTheme.SunDirection);
        moon = ToGodot(Refii.Models.Engine_ClassicTheme.MoonDirection);
        Vector3 colour = Refii.Models.Engine_ClassicTheme.SunColour;
        _statedSunColour[0] = colour.X;
        _statedSunColour[1] = colour.Y;
        _statedSunColour[2] = colour.Z;
        sunTint = _statedSunColour;
    }

    /// <summary>A stated sky direction: a guest world vector (Z-up), so (-x, z, y) — not the host
    /// sky frame's Y-up layout (Engine_ClassicSkyElements.ToGodot).</summary>
    private static Vector3 ToGodot(Vector3 guest) => new(-guest.X, guest.Z, guest.Y);

    private static Vector3 Scaled(float[] rgb, float scale) =>
        new(rgb[0] * scale, rgb[1] * scale, rgb[2] * scale);

    private static readonly HashSet<uint> _loggedHashes = new();

    private static readonly StringName MoonPhaseMeta = "xenos_moon_phase";

    private const string ClassicVertex = "res://src/Engine/level/environment/celestial/classic_celestial_vertex.gdshaderinc";

    /// <summary>Marks an element on the title's pixel shader; its colour constant is named in the meta.</summary>
    private static readonly StringName ClassicColourMeta = "xenos_element_colour";

    /// <summary>
    /// Under the classic renderer, the element on the title's own pixel shader, keeping the
    /// modern placement uniforms (the guest's billboard) so Update drives both.
    /// </summary>
    private static void Classic(
        MeshInstance3D node, string pixelEntry, string textureName, Texture2D texture,
        string renderMode, string defines, float alphaThreshold)
    {
        if (!Refii.Models.Engine_ClassicMaterial.Active || node.MaterialOverride is not ShaderMaterial modern)
        {
            return;
        }
        Shader? shader = Refii.Models.Engine_ClassicShaders.Compose(ClassicVertex, pixelEntry, renderMode, 0, defines);
        if (shader is null)
        {
            return;
        }
        var material = Refii.Models.Engine_MatCopy.Copy(modern, "celestial");
        material.Shader = shader;
        material.SetShaderParameter(PsName + textureName, texture);
        material.SetShaderParameter(XrSpecConstantsName, 2u);
        material.SetShaderParameter(XrAlphaThresholdName, alphaThreshold);
        material.SetMeta(ClassicColourMeta, pixelEntry == "PSHADER_MOONDISC" ? "ps_g_MoonColour" : "ps_g_SunDiscColour");
        node.MaterialOverride = material;
    }

    /// <summary>The element colour to the title's own constant as well, when the element is on its shader.</summary>
    /// <summary>Each classic element material's colour constant name, read from its meta once.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ShaderMaterial, StringName> ClassicColourNames = new();

    private static void ClassicColour(ShaderMaterial material, Vector4 colour)
    {
        if (!ClassicColourNames.TryGetValue(material, out StringName? name))
        {
            if (!material.HasMeta(ClassicColourMeta))
            {
                return;
            }
            // Read once: GetMeta's string Variant and the StringName from it were two
            // finalizable handles per element per frame.
            using Variant stated = material.GetMeta(ClassicColourMeta);
            name = new StringName(stated.AsString());
            ClassicColourNames.Add(material, name);
        }
        material.SetShaderParameter(name, colour);
    }

    private static Texture2D? Load(uint hash, Func<string, byte[]?> read, string label)
    {
        Texture2D? texture = Engine_EnvironmentTextures.Load(hash, read, out string path);
        if (texture is null && _loggedHashes.Add(hash))
        {
            GD.Print(path.Length == 0
                ? $"[celestial] {label} texture 0x{hash:X8} unresolved"
                : $"[celestial] {label} texture '{path}' did not decode");
        }

        return texture;
    }

    private static MeshInstance3D Billboard(
        string name,
        Shader shader,
        Texture2D texture,
        Vector3 direction,
        float distance,
        float halfWidth,
        float halfHeight,
        Vector3 colour,
        float alpha,
        float facingPower,
        float u0,
        float u1,
        int priority,
        int mode = 0)
    {
        var material = new ShaderMaterial { Shader = shader, RenderPriority = priority };
        material.SetShaderParameter(Param_element_tex, texture);
        material.SetShaderParameter(Param_element_dir, direction);
        material.SetShaderParameter(Param_element_distance, distance);
        material.SetShaderParameter(Param_element_half, new Vector2(halfWidth, halfHeight));
        material.SetShaderParameter(Param_element_colour,
            new Vector4(colour.X, colour.Y, colour.Z, alpha));
        material.SetShaderParameter(Param_element_facing_power, facingPower);
        material.SetShaderParameter(Param_element_mode, mode);

        return new MeshInstance3D
        {
            Layers = Refii.Level.Engine_ReflectionLayer.ReflectedLayers,
            Name = name,
            Mesh = QuadMesh(name, u0, u1),
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 16384f,
        };
    }

    public static ArrayMesh QuadMesh(string name, float u0, float u1)
    {
        var corners = new Vector3[]
        {
            new(-1f, 1f, 0f),
            new(1f, 1f, 0f),
            new(-1f, -1f, 0f),
            new(1f, -1f, 0f),
        };

        var quadUvs = new Vector2[]
        {
            new(u0, 0f), new(u1, 0f), new(u0, 1f), new(u1, 1f),
        };

        var order = new[] { 0, 1, 2, 2, 1, 3 };

        var vertices = new Vector3[6];
        var uvs = new Vector2[6];
        for (int v = 0; v < 6; v++)
        {
            vertices[v] = corners[order[v]];
            uvs[v] = quadUvs[order[v]];
        }

        return BuildMesh(name, vertices, uvs);
    }

    private static MeshInstance3D? Stars(float brightness, int priority)
    {
        Shader? shader = GD.Load<Shader>(StarsShaderPath);
        if (shader is null)
        {
            GD.Print($"[celestial] shader missing at {StarsShaderPath}");
            return null;
        }

        var offsets = new Vector2[]
        {
            new(-1f, 1f), new(1f, 1f), new(-1f, -1f),
            new(-1f, -1f), new(1f, 1f), new(1f, -1f),
        };

        var vertices = new Vector3[StarCount * 6];
        var uvs = new Vector2[StarCount * 6];
        for (int star = 0; star < StarCount; star++)
        {
            for (int corner = 0; corner < 6; corner++)
            {
                int v = (star * 6) + corner;
                vertices[v] = new Vector3(star, 0f, 0f);
                uvs[v] = offsets[corner];
            }
        }

        var material = new ShaderMaterial { Shader = shader, RenderPriority = priority };
        material.SetShaderParameter(Param_star_brightness, brightness);
        material.SetShaderParameter(Param_star_radius, StarRadius);
        material.SetShaderParameter(Param_star_point_size, StarPointSize);
        material.SetShaderParameter(Param_star_phase_rate, StarPhaseRate);

        return new MeshInstance3D
        {
            Layers = Refii.Level.Engine_ReflectionLayer.ReflectedLayers,
            Name = "Stars",
            Mesh = BuildMesh("Stars", vertices, uvs),
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 16384f,
        };
    }

    private static ArrayMesh BuildMesh(string name, Vector3[] vertices, Vector2[] uvs)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetName(0, name);
        mesh.CustomAabb = new Aabb(
            new Vector3(-CullExtent, -CullExtent, -CullExtent),
            new Vector3(CullExtent * 2f, CullExtent * 2f, CullExtent * 2f));
        return mesh;
    }

    // Made once: a string literal converts to a new StringName on every call.
    private static readonly StringName PsName = "ps_";
    private static readonly StringName XrSpecConstantsName = "xr_spec_constants";
    private static readonly StringName XrAlphaThresholdName = "xr_alpha_threshold";
}
