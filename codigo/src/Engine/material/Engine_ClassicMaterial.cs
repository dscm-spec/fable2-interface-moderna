using System;
using System.Collections.Generic;
using Godot;
using Refii.Textures;

namespace Refii.Models;

/// <summary>
/// A surface material for the classic renderer: the title's own standard-material shaders,
/// translated to Godot, with the material record's textures and constants bound by the names
/// the shaders declare.
/// </summary>
/// <remarks>
/// SEPARATE FROM THE MODERN MATERIAL. <see cref="Engine_MdlMaterial"/> builds the remaster's
/// look over Godot's lighting; this builds the shipped look over the guest's own pixel code and
/// is taken only when the lighting model is "classic" and the translated shaders exist.
/// Nothing here feeds back into the modern path.
///
/// THE PERMUTATION IS THE GUEST'S RULE (sub_82B03530 keys a standard material's shader set as
/// Nrm | Sp&lt;&lt;1 | SDif&lt;&lt;3 | Skn&lt;&lt;4 | Scr&lt;&lt;6 | Amb&lt;&lt;7; sub_822030D8 keys the
/// draw as GLT | BLT&lt;&lt;1 | MIST&lt;&lt;6):
///   NRM1, SP1 always: a texture set's diffuse, specular and normal slots are never empty
///         (an unnamed one is the loader's default), so bits 1, 2 and 4 are always set; SP2
///         when the specular texture's header flags carry 0x80 (byte +155);
///   SDIF  it names a secondary diffuse (bit 4, the record's first extra string);
///   SKN#  the mesh's skinning mode — Godot skins before vertex() runs, so always 0 here;
///   SCR1  a pass flag, not a material one — 0 here;
///   AMB#  the draw request's ambient source: 3 a lightmap, 2 radiance transfer, 1 vertex
///         colour. Only the lightmap is importable today (UV2); transfer weights are a stream
///         Godot's import expands rather than keeps, so those models take AMB0 for now;
///   BLT#  the pass's light group (1, 2, 4 or 7), MIST# whether the draw's bounds meet a mist
///         volume: both per draw, both 1 and 0 here until the frame feed carries them.
/// Entries are found by token set; a missing permutation steps UP to a superset, as the guest's
/// own fallback does.
/// </remarks>
public static class Engine_ClassicMaterial
{
    private static ImageTexture? _white;

    private static readonly StringName ExtinctionName = "ps_g_Extinction";
    private static readonly StringName InScatteringName = "ps_g_InScattering";
    private static readonly StringName ScreenShadowMapName = "ps_g_ScreenShadowMap";
    private static readonly StringName Cascade0Name = "ps_g_DirectionalLightShadowMapSampler0";
    private static readonly StringName Cascade1Name = "ps_g_DirectionalLightShadowMapSampler1";

    /// <summary>
    /// Points a classic material (and its next passes) at this session's frame-global textures:
    /// the atmosphere tables, the sun cascades and the screen shadow mask. A material built in
    /// this session already holds them; one loaded from a saved scene holds frozen copies (or
    /// nothing, for the cascades), which the feed never updates.
    /// </summary>
    public static void RebindShared(ShaderMaterial material)
    {
        for (Material? pass = material; pass is ShaderMaterial m; pass = m.NextPass)
        {
            if (m.Shader is null)
            {
                continue;
            }
            foreach (Godot.Collections.Dictionary uniform in m.Shader.GetShaderUniformList())
            {
                StringName name = uniform["name"].AsStringName();
                if (name == ExtinctionName)
                {
                    m.SetShaderParameter(name, Engine_ClassicAtmosphere.ExtinctionTexture);
                }
                else if (name == InScatteringName)
                {
                    m.SetShaderParameter(name, Engine_ClassicAtmosphere.InScatteringTexture);
                }
                else if (name == ScreenShadowMapName)
                {
                    m.SetShaderParameter(name, Engine_ClassicScreenShadow.Mask);
                }
                else if (Engine_ClassicShadows.Instance is { } shadows && (name == Cascade0Name || name == Cascade1Name))
                {
                    m.SetShaderParameter(name, shadows.CascadeTexture(name == Cascade0Name ? 0 : 1));
                }
            }
        }
    }

    /// <summary>A 1x1 white texture: a sampler the title binds that has no host source yet.</summary>
    public static ImageTexture White()
    {
        if (_white is null)
        {
            var image = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
            image.Fill(Colors.White);
            _white = ImageTexture.CreateFromImage(image);
        }
        return _white;
    }

    private static Texture2D? _stipple;
    private static bool _stippleTried;

    /// <summary>Globals/Engine/Textures/alphastipple.tex, 32x32, loaded once.</summary>
    private static Texture2D? Stipple()
    {
        if (!_stippleTried)
        {
            _stippleTried = true;
            _stipple = Refii.Textures.Engine_TexLibrary.Get(
                "Globals/Engine/Textures/alphastipple.tex", Refii.Textures.TexSemantic.Unknown,
                Engine_BnkMeshLoader.ReadBytes, out _);
        }
        return _stipple;
    }

    private static ImageTexture? _defaultDiffuse, _defaultSpecular, _defaultNormal;

    /// <summary>
    /// The loader's default diffuse (`sub_82A7AB48`): 16x16 A8R8G8B8 from the bytes
    /// 0x16, 0x1D, 0x2B, 0xFF packed at shifts 0, 8, 16, 24 (format 3 of the table at
    /// 0x8331DAD8), so R 0x2B, G 0x1D, B 0x16, opaque — a dark brown. Header flags 0.
    /// </summary>
    public static ImageTexture DefaultDiffuse() => _defaultDiffuse ??= Solid(0x2B, 0x1D, 0x16, 0xFF);

    /// <summary>The default specular (`sub_82A7AD50`): all bytes 0, no specular.</summary>
    public static ImageTexture DefaultSpecular() => _defaultSpecular ??= Solid(0, 0, 0, 0);

    /// <summary>The default normal (`sub_82A7AE50`): bytes 0xFF, 0x7F, 0x7F, 0 — R 0x7F, G 0x7F, B 0xFF, A 0: flat.</summary>
    public static ImageTexture DefaultNormal() => _defaultNormal ??= Solid(0x7F, 0x7F, 0xFF, 0);

    private static ImageTexture Solid(byte r, byte g, byte b, byte a)
    {
        var bytes = new byte[16 * 16 * 4];
        for (int i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = r;
            bytes[i + 1] = g;
            bytes[i + 2] = b;
            bytes[i + 3] = a;
        }
        return ImageTexture.CreateFromImage(Image.CreateFromData(16, 16, false, Image.Format.Rgba8, bytes));
    }

    /// <summary>Marks a material built here, so the modern path's post-build steps leave it alone.</summary>
    public static readonly StringName ClassicMeta = "xenos_classic";

    /// <summary>The token lists and render mode the material's shader pair was found by.</summary>
    private static readonly StringName PixelTokensMeta = "xenos_pixel_tokens";
    private static readonly StringName VertexTokensMeta = "xenos_vertex_tokens";
    private static readonly StringName RenderModeMeta = "xenos_render_mode";

    /// <summary>A classic material's composed render mode (its blend, cull and depth states), or empty.</summary>
    public static string RenderModeOf(ShaderMaterial material) =>
        material.HasMeta(RenderModeMeta) ? material.GetMeta(RenderModeMeta).AsString() : string.Empty;
    private static readonly StringName FamilyMeta = "xenos_family";
    /// <summary>The bank family a classic material was built for (TREEMATERIAL, ...).</summary>
    public static StringName FamilyMetaName => FamilyMeta;
    /// <summary>A vertex entry named outright, for a family whose vertex shader is not
    /// <c>VSHADER_&lt;family&gt;_tokens</c> (the particles' FACE_ME).</summary>
    private static readonly StringName VertexEntryMeta = "xenos_vertex_entry";
    /// <summary>Composition defines (see <see cref="Engine_ClassicShaders.Compose(string, string, string, int, string)"/>).</summary>
    public static readonly StringName DefinesMeta = "xenos_defines";
    /// <summary>The title draws this family once whatever its light groups: no additive passes.</summary>
    private static readonly StringName SinglePassMeta = "xenos_single_pass";

    /// <summary>
    /// THE TREE FADE, per placement. The title computes c15.w for each tree draw on the CPU
    /// (`sub_8220AF90`; Engine_PropDrawRange.TreeBand states the curves) from the eye's distance
    /// to the model's world box centre, and the tree pixel shader multiplies its alpha by it.
    /// While a draw fades, `sub_822103A0` sets ALPHAREF 0 (0x82210434) and turns alpha test and
    /// ALPHATOMASK on with offsets 0x87 (0x8221062C..0x82210648): the fade is a 2x2 coverage
    /// dither on alpha x fade, never a blend. Our batches hold many placements, so the vertex
    /// stage computes the same value from each copy's own transform, drops a copy at zero, and
    /// the pixel stage dithers on it. The 2x2 thresholds follow the offsets' 2-bit fields
    /// (3, 1, 0, 2) as (offset + 0.5) / 4; which pixel of the quad takes which field is not
    /// proven. A copy at full strength keeps the material's own alpha test.
    ///
    /// THE TREE AMBIENT, per placement. Trees always draw on the AMB2 pair (`sub_822092B0`
    /// 0x822092F4: material+0xB0, entry 2, unconditionally), main and LOD alike, lit from the
    /// placement's one environment (`sub_822DAB20` -> `sub_8220B870` for both models). A model
    /// with no transfer (every shipped Brightwood tree) reads only its cluster-mean row 0;
    /// Engine_ClassicTransfer states it per node (w = 1), which a material array cannot be on a
    /// batch, or per copy (w = 2) from the batch's instance colours, which hold each placement's
    /// flat environment times the remaster's ambient scale (xyz undoes the scale).
    /// </summary>
    private const string TreeDefines =
        "instance uniform vec4 xr_tree_ambient = vec4(0.0);\n" +
        "instance uniform vec4 xr_tree_stated_x = vec4(0.0);\n" +
        "instance uniform vec4 xr_tree_stated_y = vec4(0.0);\n" +
        "#define XR_TREE_STATED_X xr_tree_stated_x\n" +
        "#define XR_TREE_STATED_Y xr_tree_stated_y\n" +
        "#define XR_TREE_PRT_ROW0(row, cluster) (((cluster) != 0 || xr_tree_ambient.w < 0.5) ? (row) : " +
        "vec4(xr_tree_ambient.w > 1.5 ? COLOR.rgb * xr_tree_ambient.xyz : xr_tree_ambient.xyz, 0.0))\n" +
        "instance uniform vec4 xr_tree_fade_in = vec4(0.0);\n" +
        "instance uniform vec4 xr_tree_fade_out = vec4(0.0);\n" +
        "instance uniform vec3 xr_tree_centre = vec3(0.0);\n" +
        "varying float xr_tree_fade;\n" +
        "#define XR_POSITION_OUT(p) (p); " +
        "xr_tree_fade = 1.0; " +
        "{ float xr_d = distance(MAIN_CAM_INV_VIEW_MATRIX[3].xyz, (MODEL_MATRIX * vec4(xr_tree_centre, 1.0)).xyz); " +
        "if (xr_tree_fade_out.y > 0.0) { xr_tree_fade *= pow(clamp((xr_tree_fade_out.x - xr_d) * xr_tree_fade_out.y, 0.0, 1.0), xr_tree_fade_out.z); } " +
        "if (xr_tree_fade_in.y > 0.0) { xr_tree_fade *= pow(clamp((xr_d - xr_tree_fade_in.x) * xr_tree_fade_in.y, 0.0, 1.0), xr_tree_fade_in.z); } } " +
        "if (xr_tree_fade <= 0.0) { POSITION = vec4(2.0, 2.0, 2.0, 1.0); }\n" +
        "#define XR_BEFORE_OUTPUT(c) if (xr_tree_fade < 1.0) { " +
        "int xr_q = (int(FRAGCOORD.x) & 1) + 2 * (int(FRAGCOORD.y) & 1); " +
        "float xr_t = (float((135 >> (2 * xr_q)) & 3) + 0.5) * 0.25; " +
        "if ((c).a * xr_tree_fade <= xr_t) { discard; } (c).a = 1.0; }";

    /// <summary>
    /// The bank family a model draws with, by the title's own split: trees (mesh kind 1) take
    /// TREEMATERIAL, trunk unless the record's trunk slot is filled; instanced scatter (kind 4)
    /// takes REPEATEDMATERIAL with GRASS1; everything else the standard material.
    /// </summary>
    private static string FamilyFor(MdlMaterial material, MdlInfo? info, List<string> pixelTokens, List<string> vertexTokens)
    {
        switch (Engine_ShaderFamily.Resolve(info))
        {
            case ShaderFamily.TreeTrunk:
            case ShaderFamily.TreeCanopy:
                bool canopy = material.ShaderParams.Length > 1 && material.ShaderParams[1].Length != 0;
                if (!canopy)
                {
                    pixelTokens.Add("TRUNK1");
                    vertexTokens.Add("TRUNK1");
                }
                return "TREEMATERIAL";
            case ShaderFamily.Grass:
                pixelTokens.Add("GRASS1");
                vertexTokens.Add("GRASS1");
                return "REPEATEDMATERIAL";
            case ShaderFamily.Scatter:
                return "REPEATEDMATERIAL";
            default:
                return "STANDARDMATERIAL";
        }
    }

    /// <summary>
    /// The classic renderer is on: the lighting model is "classic" and the player's translated
    /// shaders are present. Without the shaders, "classic" is the authored-ambient model over the
    /// modern materials, which is what it was before the shaders existed.
    /// </summary>
    public static bool Active =>
        !Refii.Lighting.Engine_LightingFrame.ModernModel && Engine_ClassicShaders.Available;

    private static int _built;
    private static int _fellBack;
    private static bool _reportedMissing;

    /// <summary>
    /// Builds the classic material for a record, or null when its shader pair is not available
    /// (the caller then builds the modern one).
    /// </summary>
    public static Material? TryCreate(
        MdlMaterial material, MdlInfo? info, bool albedoHasAlpha,
        Texture2D? albedo, Texture2D? normal, Texture2D? emissive, Texture2D? specular,
        Cubemap? envmap)
    {
        // EVERY TEXTURE SET HAS ITS FIRST THREE SLOTS. The loader resolves diffuse, specular and
        // normal with a default for an empty name (`sub_82A9EEA0` 0x82A9EF8C/F0D0/F210 through
        // `sub_82A9E670`: a zero hash takes the default), so the presence mask `sub_82AA04A0`
        // builds always holds bits 1, 2 and 4: every model surface draws on the NRM1 SP
        // permutation, a missing map reading the default. Only the other four slots can be absent.
        albedo ??= DefaultDiffuse();
        specular ??= DefaultSpecular();
        normal ??= DefaultNormal();
        bool hasNormal = true;
        bool hasSpecular = true;
        bool hasSecondaryDiffuse = material.ShaderParams.Length > 0 && material.ShaderParams[0].Length != 0;
        bool hasLightmap = info is not null && info.LightmapUvs.Length > 0 && info.SkinnedCount == 0;

        // The draw key, then the family key, as tokens. Pixel and vertex shaders of one set
        // share the family key; the draw key names the light group and the mist.
        var pixelTokens = new List<string> { "BLT1", "GLT1", "MIST0" };
        var vertexTokens = new List<string> { "BLT1", "GLT1", "MIST0" };
        if (hasNormal)
        {
            pixelTokens.Add("NRM1");
            vertexTokens.Add("NRM1");
        }
        // Sp is 1, or 2 when the specular texture's header flags carry 0x80 (texture-set +155,
        // `sub_82AA04A0` 0x82AA04F4); the default specular's flags are 0.
        if (hasSpecular && hasNormal)
        {
            int specularFlags = material.Specular.Length != 0
                ? Refii.Textures.Engine_TexLibrary.HeaderFlags(material.Specular, Engine_BnkMeshLoader.ReadBytes)
                : 0;
            pixelTokens.Add((specularFlags & 0x80) != 0 ? "SP2" : "SP1");
        }
        if (hasSecondaryDiffuse)
        {
            pixelTokens.Add("SDIF1");
        }
        string family = FamilyFor(material, info, pixelTokens, vertexTokens);
        // The repeated family ships no specular or secondary-diffuse permutations (the bank has
        // NRM1, GRASS1, WIND1, COLL1 only), and no ambient ones: its instances carry their
        // bake as the instance colour.
        bool wind = family == "REPEATEDMATERIAL" && info is { MovementMode: 1 };
        if (family == "REPEATEDMATERIAL")
        {
            pixelTokens.Remove("SP1");
            pixelTokens.Remove("SP2");
            pixelTokens.Remove("SDIF1");
            // NormalParams_Wind: the header's movement byte is 1 (`sub_82B35990`). The vertex
            // stage only; no pixel shader varies with it.
            if (wind)
            {
                vertexTokens.Add("WIND1");
            }
        }
        if (family == "TREEMATERIAL")
        {
            // The tree draw's shader set is always entry 2 (TreeDefines): no lightmap, no bake.
            vertexTokens.Add("AMB2");
            pixelTokens.Add("AMB2");
        }
        else if (hasLightmap && family != "REPEATEDMATERIAL")
        {
            vertexTokens.Add("AMB3");
            pixelTokens.Add("AMB3");
        }

        // THE TEXTURE STATES THE ALPHA, not the material (`sub_82AA04A0`): the diffuse header's
        // flag 0x20 turns the alpha test on, 0x80 makes the surface translucent. A standard
        // material's opaque draw is the SCR1 permutation (`sub_82B31D80`: StandardParams_Scr = 1
        // when the draw is not blended), alpha tested GREATER against 254, or 128 for a texture
        // that is a cutout only; a translucent surface then draws again without SCR1, blended.
        bool standard = family == "STANDARDMATERIAL";
        int textureFlags = material.Diffuse.Length != 0
            ? Refii.Textures.Engine_TexLibrary.HeaderFlags(material.Diffuse, Engine_BnkMeshLoader.ReadBytes)
            : 0;
        bool translucent = standard && (textureFlags & 0x80) != 0;
        if (standard)
        {
            pixelTokens.Add("SCR1");
        }

        string pixel = Engine_ClassicShaders.FindEntry("PSHADER", family, pixelTokens);
        string vertex = Engine_ClassicShaders.FindEntry("VSHADER", family, vertexTokens);
        if (pixel.Length == 0 || vertex.Length == 0)
        {
            if (!_reportedMissing)
            {
                _reportedMissing = true;
                GD.Print($"[classic] no standard-material pair for {string.Join(' ', pixelTokens)} / " +
                         $"{string.Join(' ', vertexTokens)}; the modern material stands in");
            }
            System.Threading.Interlocked.Increment(ref _fellBack);
            return null;
        }

        // The guest draws the title's surfaces lit by the shader itself, so the Godot material is
        // unshaded, and OPAQUE: the standard material draws in the opaque buckets, cutouts by
        // alpha test. A solid surface shows one side; a card cut out by its alpha both.
        // A standard material never changes the cull mode: the view's CCW cull stands for every
        // draw (`sub_8221BD40`), cutouts included. Trees and scatter keep both sides.
        string renderMode = standard || !albedoHasAlpha
            ? "unshaded, depth_draw_opaque, cull_back"
            : "unshaded, depth_draw_opaque, cull_disabled";
        string defines = family == "TREEMATERIAL" ? TreeDefines : string.Empty;
        Shader? shader = Engine_ClassicShaders.Compose(vertex, pixel, renderMode, 0, defines);
        if (shader is null)
        {
            System.Threading.Interlocked.Increment(ref _fellBack);
            return null;
        }

        var result = new ShaderMaterial { Shader = shader };
        if (defines.Length != 0)
        {
            result.SetMeta(DefinesMeta, defines);
        }
        result.SetMeta(ClassicMeta, true);
        result.SetMeta(PixelTokensMeta, string.Join(' ', pixelTokens));
        result.SetMeta(VertexTokensMeta, string.Join(' ', vertexTokens));
        result.SetMeta(RenderModeMeta, renderMode);
        result.SetMeta(FamilyMeta, family);
        if (family == "TREEMATERIAL")
        {
            // c16.w: 0.035 for a canopy (its texture set names the trunk slot), -1 for a trunk.
            result.SetShaderParameter("xr_tree_c16w", pixelTokens.Contains("TRUNK1") ? -1f : 0.035f);
            // g_TreeExtinction (s16) and g_TreeInScattering (s17), the vertex stage's atmosphere:
            // the same two tables the pixel stage's g_Extinction / g_InScattering are
            // (`sub_82210750` -> `Render_MaterialConstantSetup_INFERRED` 0x8220C1B0), linear and
            // clamped. Unbound they read as zero extinction, which fogs the tree out to black.
            result.SetShaderParameter("vs_s16", Engine_ClassicAtmosphere.ExtinctionTexture);
            result.SetShaderParameter("vs_s17", Engine_ClassicAtmosphere.InScatteringTexture);
        }
        result.SetMeta(Engine_MdlMaterial.TextureNamesMeta, new[]
        {
            material.Diffuse, material.Normal, material.Specular, material.SelfIllumination,
        });

        // The texture-set record's slots, by the sampler names the pixel shader declares.
        if (albedo is not null)
        {
            result.SetShaderParameter("ps_g_DiffuseTextureSampler", albedo);
        }
        if (normal is not null)
        {
            result.SetShaderParameter("ps_g_NormalTextureSampler", normal);
        }
        if (specular is not null)
        {
            result.SetShaderParameter("ps_g_SpecularTextureSampler", specular);
        }
        if (emissive is not null)
        {
            result.SetShaderParameter("ps_g_SelfIllumTextureSampler", emissive);
        }
        if (envmap is not null)
        {
            result.SetShaderParameter("ps_g_EnvmapSampler_cube", envmap);
        }

        // g_ScreenShadowMap (s8) and g_TileOffsetSize (c0): the SCR1 variants take the sun's
        // visibility from the frame's screen-space shadow mask, not from the cascades. The title
        // binds it frame-globally and never unbinds it: the mask the cascade pass resolved, or a
        // 16x16 white texture when there is no sun caster (`sub_822965E0`, `sub_82282C38`);
        // Engine_ClassicScreenShadow is that pass. Unbound it read black and took the sun off
        // every SCR1 surface. g_TileOffsetSize (PS c0) is the frame's.
        result.SetShaderParameter("ps_g_ScreenShadowMap", Engine_ClassicScreenShadow.Mask);

        // The atmosphere tables, shared by every material and refilled in place by the feed.
        result.SetShaderParameter("ps_g_Extinction", Engine_ClassicAtmosphere.ExtinctionTexture);
        result.SetShaderParameter("ps_g_InScattering", Engine_ClassicAtmosphere.InScatteringTexture);
        // The sun's two cascades, when the shadow node is up (the session adds it on the first
        // classic frame; materials built before that are bound by its first Pump).
        if (Engine_ClassicShadows.Instance is { } shadows)
        {
            result.SetShaderParameter("ps_g_DirectionalLightShadowMapSampler0", shadows.CascadeTexture(0));
            result.SetShaderParameter("ps_g_DirectionalLightShadowMapSampler1", shadows.CascadeTexture(1));
        }

        // The material block as `sub_822015B8` uploads RC+112..+176 (c24..c27, and VS c50..53):
        //   c24 = (RC+60 specular power min, RC+64 max, RC+72 self-illumination scale, RC+88)
        //   c25 = (RC+92, RC+100, RC+104, 30.0)
        //   c26 = (1, 1, 1, 1)
        //   c27.w = 1 on a draw's first pass, 0 on the additive passes after it (`sub_82225100`:
        //         the vperm at 0x822251BC puts the float in lane w; 1.0 for pass 0, 0.0 for
        //         pass 1). Every standard pixel shader scales its ambient by it, so a later light
        //         group adds no second ambient. The lightmap only picks sampler 3's texture.
        // RC+88..+104 are the record's five block floats in file order.
        float[] block = material.BlockFloats.Length >= 5 ? material.BlockFloats : new float[5];
        result.SetShaderParameter("ps_g_StandardMaterialPSConstants", new Godot.Collections.Array
        {
            new Vector4(material.SpecularPowerMin, material.SpecularPowerMax, material.SelfIlluminationScale, block[0]),
            new Vector4(block[1], block[3], block[4], 30f),
            new Vector4(1f, 1f, 1f, 1f),
            new Vector4(0f, 0f, 0f, 1f),
        });
        // The same block's first row at VS c50 (the tree vertex shaders' g_StandardMaterialVSConstants).
        result.SetShaderParameter("vs_g_StandardMaterialVSConstants",
            new Vector4(material.SpecularPowerMin, material.SpecularPowerMax, material.SelfIlluminationScale, block[0]));
        // VS c27 g_DoubleSidedLightingSign: +1 for a draw lit from its front faces. Unset it was
        // 0, and the standard vertex shaders that read it multiply the normal by it.
        result.SetShaderParameter("vs_g_DoubleSidedLightingSign", new Vector4(1f, 1f, 1f, 1f));
        // The texture UV transforms are SCALE AND OFFSET, not a 2x2 matrix: every translated
        // stage reads `uv * M.xy + M.zw`. Identity is (1, 1, 0, 0); the earlier (1, 0, 0, 1)
        // pinned v to 1, so every classic surface sampled its texture's last row (canopy alpha
        // read as zero and was cut away; colours smeared to one row).
        var identity = new Vector4(1f, 1f, 0f, 0f);
        result.SetShaderParameter("ps_g_DiffuseTextureUVMatrix", identity);
        result.SetShaderParameter("ps_g_SpecularTextureUVMatrix", identity);
        result.SetShaderParameter("ps_g_NormalTextureUVMatrix", identity);

        // The pixel booleans the guest publishes from the record's presence bits: c6
        // g_NeedsEnvmapping and c5 g_NeedsSelfIllumination (sub_82220CA8), in the upper half of
        // the shared word.
        // b4 g_NeedsScattering: set with the tables bound, as the material state setup sets it
        // while the sky is on. b1 g_EnableMist: set once any mist has existed (byte_8335CBD2 is
        // never cleared); the per-draw gate is the volume test, not this bit.
        uint booleans = (1u << (16 + 4)) | (1u << (16 + 1));
        if (envmap is not null)
        {
            booleans |= 1u << (16 + 6);
        }
        if (emissive is not null)
        {
            booleans |= 1u << (16 + 5);
        }
        result.SetShaderParameter("xr_booleans", booleans);
        if (standard)
        {
            // ALPHATESTENABLE = texture-set +152, ALPHAREF = 128 for a cutout-only texture
            // else 254, ALPHAFUNC GREATER (`sub_822251D8` 0x82225290, frame default
            // `sub_822C2C60`): a pixel survives when its alpha is above the reference.
            if ((textureFlags & 0x20) != 0)
            {
                float reference = (textureFlags & 0xA0) == 0x20 ? 128f : 254f;
                result.SetShaderParameter("xr_spec_constants", 2u);
                result.SetShaderParameter("xr_alpha_threshold", (reference + 0.5f) / 255f);
            }
        }
        else if (albedoHasAlpha)
        {
            // Trees and scatter: their own alpha-to-coverage rule is not modelled; the midpoint.
            result.SetShaderParameter("xr_spec_constants", 2u);
            result.SetShaderParameter("xr_alpha_threshold", 0.5f);
        }
        if (family == "REPEATEDMATERIAL")
        {
            // s10 g_AlphaStippleSampler: the frame's stipple (`sub_821C3C60`, sampler 10,
            // "Textures\alphastipple.tex"), read with PS c50 = render target / 32.
            if (Stipple() is { } stipple)
            {
                result.SetShaderParameter("ps_g_AlphaStippleSampler", stipple);
            }
            if (wind)
            {
                Engine_ClassicWind.Register(result, info!.WindAmplitude, info.WindSpeed);
            }
            // VS c45 g_NormalMapGradient_Enable_Factor_Offset (`sub_82233570`): with A, B the
            // header's gradient at the top and bottom and z0..z1 the mesh's height range,
            // (1, (A - B) / (z1 - z0), B - z0 (A - B) / (z1 - z0), 0) — a ramp in model height
            // from B at the bottom to A at the top; all zero when neither is positive. The guest
            // takes z from its LOD-0 object's box; the file header's box is that box here.
            Vector4 gradient = Vector4.Zero;
            if (info is { } mesh && (mesh.NormalGradientTop > 0f || mesh.NormalGradientBottom > 0f) &&
                mesh.FileBoundingBox.Length >= 6 && mesh.FileBoundingBox[5] != mesh.FileBoundingBox[2])
            {
                float z0 = mesh.FileBoundingBox[2], z1 = mesh.FileBoundingBox[5];
                float slope = (mesh.NormalGradientTop - mesh.NormalGradientBottom) / (z1 - z0);
                gradient = new Vector4(1f, slope, mesh.NormalGradientBottom - z0 * slope, 0f);
            }
            result.SetShaderParameter("vs_g_NormalMapGradient_Enable_Factor_Offset", gradient);
        }
        if (translucent && BuildTranslucentPass(result, pixelTokens, vertex) is { } glass)
        {
            result.NextPass = glass;
            result.SetMeta(TranslucentPassMeta, glass);
        }

        System.Threading.Interlocked.Increment(ref _built);
        return result;
    }

    /// <summary>
    /// The same material on the shader pair of another ambient source — the AMB token the guest
    /// picks per instance (`sub_82219978`: 1 the authored vertex bake, 3 the authored lightmap,
    /// 2 radiance transfer) — with the instance's lightmap bound. The caller sets it as the
    /// instance's surface override; the shared material keeps the mesh's own choice.
    /// </summary>
    /// <remarks><paramref name="wait"/>: finish a stage still compiling here rather than answer null (load-time callers with no retry).</remarks>
    public static ShaderMaterial? WithAmbient(ShaderMaterial classic, int ambient, Texture2D? lightmap, bool wait = false)
    {
        if (!classic.HasMeta(PixelTokensMeta))
        {
            return null;
        }
        // One variant per (material, ambient, lightmap): every placement of a model with the
        // same bake shape shares it, as the title shares the shader set; a new duplicate per
        // placement was a material and a FindEntry walk per surface of every prop streamed.
        var key = (classic.GetInstanceId(), ambient, lightmap?.GetInstanceId() ?? 0UL);
        lock (AmbientVariants)
        {
            if (AmbientVariants.TryGetValue(key, out ShaderMaterial? cached) && GodotObject.IsInstanceValid(cached))
            {
                return cached;
            }
        }
        bool earlier = AmbientPending;
        AmbientPending = false;
        ShaderMaterial? built = BuildWithAmbient(classic, ambient, lightmap, wait);
        if (AmbientPending)
        {
            // Some stage waits for its compile (the glass pass too): nothing is kept half built.
            return null;
        }
        AmbientPending = earlier;
        // A region's build runs on a worker before its hold begins: that is still the load.
        if (built is not null && !Engine_ClassicLoad.Holding && Engine_ClassicLoad.Released &&
            OS.GetThreadCallerId() == OS.GetMainThreadId() && _lateAmbientsLogged++ < 24)
        {
            GD.Print($"[classic] ambient variant built after the load: {classic.GetMeta(FamilyMeta, "?").AsString()} AMB{ambient}");
        }
        if (built is not null)
        {
            lock (AmbientVariants)
            {
                AmbientVariants[key] = built;
            }
        }
        return built;
    }

    private static readonly Dictionary<(ulong, int, ulong), ShaderMaterial> AmbientVariants = new();
    private static int _lateAmbientsLogged;

    /// <summary>
    /// The AMB1 pair for a vertex-baked placement's surface whose block starts
    /// <paramref name="surfaceStart"/> texels into the placement's (Engine_ClassicVertexBakes):
    /// the bake read from the shared texture, so every placement of the model shares this
    /// material and its mesh. Null while its shader compiles (<see cref="AmbientPending"/>).
    /// </summary>
    public static ShaderMaterial? WithVertexBake(ShaderMaterial classic, int surfaceStart)
    {
        if (!classic.HasMeta(PixelTokensMeta))
        {
            return null;
        }
        var key = (classic.GetInstanceId(), surfaceStart);
        lock (BakeVariants)
        {
            if (BakeVariants.TryGetValue(key, out ShaderMaterial? cached) && GodotObject.IsInstanceValid(cached))
            {
                return cached;
            }
        }
        bool earlier = AmbientPending;
        AmbientPending = false;
        ShaderMaterial? built = BuildWithAmbient(classic, 1, null, false, surfaceStart);
        if (AmbientPending)
        {
            return null;
        }
        AmbientPending = earlier;
        if (built is not null)
        {
            lock (BakeVariants)
            {
                BakeVariants[key] = built;
            }
        }
        return built;
    }

    private static readonly Dictionary<(ulong, int), ShaderMaterial> BakeVariants = new();

    /// <summary>
    /// The AMB2 transfer pair every placement of <paramref name="classic"/> shares: its
    /// g_PRTConstants read from the placement's row (Engine_ClassicPrtRows), named by the
    /// node's `xr_prt_row`. Null while its shader compiles (<see cref="AmbientPending"/>).
    /// </summary>
    public static ShaderMaterial? WithPrtRows(ShaderMaterial classic, int surfaceRow = 0)
    {
        if (!classic.HasMeta(PixelTokensMeta))
        {
            return null;
        }
        var key = (classic.GetInstanceId(), surfaceRow);
        lock (PrtVariants)
        {
            if (PrtVariants.TryGetValue(key, out ShaderMaterial? cached) && GodotObject.IsInstanceValid(cached))
            {
                return cached;
            }
        }
        bool earlier = AmbientPending;
        AmbientPending = false;
        ShaderMaterial? built = BuildWithAmbient(classic, 2, null, false, -1, PrtRowDefine);
        if (AmbientPending)
        {
            return null;
        }
        AmbientPending = earlier;
        if (built is not null)
        {
            built.SetShaderParameter(PrtSurfaceUniform, surfaceRow);
            lock (PrtVariants)
            {
                PrtVariants[key] = built;
            }
        }
        return built;
    }

    private static readonly Dictionary<(ulong, int), ShaderMaterial> PrtVariants = new();

    private static readonly StringName PrtSurfaceUniform = "xr_prt_surface";

    private const string PrtRowDefine = "#define XR_PRT_ROW_FETCH";

    private const string VertexBakeDefine = "#define XR_VERTEX_BAKE_FETCH";

    /// <summary>Where an ambient variant's build goes (Stopwatch ticks, run totals): the entry walk, the composition, the duplicate, the shader bind.</summary>
    public static long AmbientFindTicks, AmbientComposeTicks, AmbientDuplicateTicks, AmbientBindTicks;

    /// <summary>Set when an ambient variant waited for its shader to settle; the caller clears it before asking.</summary>
    public static bool AmbientPending;

    private static ShaderMaterial? BuildWithAmbient(ShaderMaterial classic, int ambient, Texture2D? lightmap, bool wait = false,
        int bakeSurface = -1, string extraDefine = "")
    {
        string family = classic.GetMeta(FamilyMeta, "STANDARDMATERIAL").AsString();
        if (family == "REPEATEDMATERIAL")
        {
            return null;
        }
        string pixel = Retoken(classic.GetMeta(PixelTokensMeta).AsString(), ambient, out List<string> pixelTokens);
        string vertex = Retoken(classic.GetMeta(VertexTokensMeta).AsString(), ambient, out List<string> vertexTokens);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        string pixelEntry = Engine_ClassicShaders.FindEntry("PSHADER", family, pixelTokens);
        string vertexEntry = Engine_ClassicShaders.FindEntry("VSHADER", family, vertexTokens);
        if (pixelEntry.Length == 0 || vertexEntry.Length == 0)
        {
            return null;
        }
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        // The source's own composition defines go with it (a terrain layer's premultiply,
        // quadrant gate and vertex kill).
        string defines = classic.GetMeta(DefinesMeta, string.Empty).AsString();
        if (bakeSurface >= 0)
        {
            extraDefine = VertexBakeDefine;
        }
        if (extraDefine.Length != 0)
        {
            defines = defines.Length == 0 ? extraDefine : defines + "\n" + extraDefine;
        }
        Shader? shader = Engine_ClassicShaders.Compose(
            vertexEntry, pixelEntry, classic.GetMeta(RenderModeMeta).AsString(), 0, defines);
        if (shader is null)
        {
            return null;
        }
        if (wait)
        {
            Engine_ClassicShaders.Settle(shader);
        }
        else if (!Engine_ClassicShaders.Settled(shader))
        {
            // Bound now it would compile on the main thread; asked again once the pool has compiled it.
            AmbientPending = true;
            return null;
        }
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = DuplicateOwn(classic);
        long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        result.Shader = shader;
        long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
        AmbientFindTicks += t1 - t0;
        AmbientComposeTicks += t2 - t1;
        AmbientDuplicateTicks += t3 - t2;
        AmbientBindTicks += t4 - t3;
        result.SetMeta(PixelTokensMeta, pixel);
        result.SetMeta(VertexTokensMeta, vertex);
        if (extraDefine.Length != 0)
        {
            // The light groups derive from this one: they compose with its defines.
            result.SetMeta(DefinesMeta, defines);
        }
        if (bakeSurface >= 0)
        {
            result.SetShaderParameter(Engine_ClassicVertexBakes.SurfaceUniform, bakeSurface);
        }
        if (lightmap is not null)
        {
            result.SetShaderParameter("ps_g_TextureLightmapSampler", lightmap);
        }
        // A glass surface's blended draw takes the same ambient source.
        result.NextPass = null;
        result.RemoveMeta(TranslucentPassMeta);
        if (classic.HasMeta(TranslucentPassMeta) && classic.GetMeta(TranslucentPassMeta).Obj is ShaderMaterial glass &&
            BuildWithAmbient(glass, ambient, lightmap, wait, bakeSurface, bakeSurface >= 0 ? "" : extraDefine) is { } litGlass)
        {
            result.NextPass = litGlass;
            result.SetMeta(TranslucentPassMeta, litGlass);
        }
        return result;
    }

    /// <summary>
    /// The material on the pair for light group pass <paramref name="pass"/> with
    /// <paramref name="lightCount"/> lights: BLT from the title's table by count (one light that
    /// is not directional takes BLT2), the first pass on the material's own blend, later passes
    /// additive over it (`sub_822251D8`: DestBlend ONE). Cached per source material.
    /// </summary>
    public static ShaderMaterial? ForLightGroup(ShaderMaterial source, int lightCount, bool singleLocal, int pass) =>
        ForLightGroup(source, lightCount, singleLocal, pass, null);

    /// <summary>
    /// As above, inside <paramref name="mist"/>: the MIST1 pair with the volume's density and
    /// height textures bound (`Render_BindHighSamplersAndPixelBool`, samplers 11 and 12).
    /// </summary>
    /// <paramref name="tail"/> names what the caller chains after this pass (it sets NextPass),
    /// so two chains of different shape never share — and rewrite — one cached pass.
    public static ShaderMaterial? ForLightGroup(
        ShaderMaterial source, int lightCount, bool singleLocal, int pass, Engine_ClassicMist.Volume? mist, int tail = 0)
    {
        long from = System.Diagnostics.Stopwatch.GetTimestamp();
        ShaderMaterial? built = null;
        bool fresh = false;
        try
        {
            built = BuildLightGroup(source, lightCount, singleLocal, pass, mist, tail, out fresh);
            return built;
        }
        finally
        {
            long spent = System.Diagnostics.Stopwatch.GetTimestamp() - from;
            if (fresh)
            {
                VariantBuildTicks += spent;
                ++VariantsBuilt;
                if (!Engine_ClassicLoad.Holding && Engine_ClassicLoad.Released && _lateGroupsLogged++ < 24)
                {
                    GD.Print($"[classic] light group built after the load: {source.GetMeta(FamilyMeta, "?").AsString()} " +
                        $"count {lightCount} single {singleLocal} pass {pass} mist {mist is not null} tail {tail} " +
                        $"({spent * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0} ms)");
                }
            }
            else
            {
                VariantLookupTicks += spent;
            }
        }
    }

    /// <summary>
    /// Whether every classic surface of <paramref name="mesh"/> stands on a compiled shader
    /// (Engine_ClassicShaders.Settled). Before that, the first bind of one compiles it on the
    /// main thread: a streamed prop's light step took 15-50 ms.
    /// </summary>
    public static bool MeshSettled(Mesh? mesh)
    {
        if (!Active || mesh is null)
        {
            return true;
        }
        for (int i = 0; i < mesh.GetSurfaceCount(); i++)
        {
            if (mesh.SurfaceGetMaterial(i) is ShaderMaterial { Shader: { } shader } && !Engine_ClassicShaders.Settled(shader))
            {
                return false;
            }
        }
        return true;
    }

    private static int _lateGroupsLogged;

    /// <summary>The variant path's cost in Stopwatch ticks, taken and reset by Engine_ClassicLitInstances each frame.</summary>
    public static long VariantBuildTicks, VariantLookupTicks, VariantComposeTicks;

    /// <summary>A frame's build time past which a new variant waits (2 ms).</summary>
    private static readonly long VariantBudgetTicks = System.Diagnostics.Stopwatch.Frequency / 500;

    /// <summary>Set when a build waited for a later frame; the caller clears it before asking.</summary>
    public static bool Deferred;
    public static int VariantsBuilt;

    private static ShaderMaterial? BuildLightGroup(
        ShaderMaterial source, int lightCount, bool singleLocal, int pass, Engine_ClassicMist.Volume? mist, int tail,
        out bool fresh)
    {
        fresh = false;
        int blt = singleLocal ? 2 : BltByCount[Math.Clamp(lightCount, 0, 7)];
        string key = $"{blt}:{pass}:{mist?.Viewport?.GetInstanceId() ?? 0UL}:{tail}";
        Godot.Collections.Dictionary? variants = source.HasMeta(GroupVariantsMeta)
            ? source.GetMeta(GroupVariantsMeta).AsGodotDictionary()
            : null;
        if (variants is not null && variants.TryGetValue(key, out Variant held) &&
            held.Obj is ShaderMaterial cached && GodotObject.IsInstanceValid(cached))
        {
            return cached;
        }
        if (!source.HasMeta(PixelTokensMeta))
        {
            return null;
        }
        if (pass > 0 && source.HasMeta(SinglePassMeta))
        {
            return null;
        }
        string family = source.GetMeta(FamilyMeta, "STANDARDMATERIAL").AsString();
        string pixel = RetokenBlt(source.GetMeta(PixelTokensMeta).AsString(), blt, mist is not null, out List<string> pixelTokens);
        string vertex = RetokenBlt(source.GetMeta(VertexTokensMeta).AsString(), blt, mist is not null, out List<string> vertexTokens);
        string pixelEntry = Engine_ClassicShaders.FindEntry("PSHADER", family, pixelTokens);
        string vertexEntry = source.HasMeta(VertexEntryMeta)
            ? source.GetMeta(VertexEntryMeta).AsString()
            : Engine_ClassicShaders.FindEntry("VSHADER", family, vertexTokens);
        if (family == "TREEMATERIAL" && blt > 1 &&
            (vertexEntry.Length == 0 || TrunkStage(pixelEntry) != pixelTokens.Contains("TRUNK1")))
        {
            // NO TREE PAIR PAST ONE LIGHT. The bank's BLT2/4/7 tree pixel shaders are TRUNK1 only
            // and its BLT2/4/7 vertex shaders are never TRUNK1, and the two layouts differ (the
            // trunk stages pass eight varyings, the canopy's six). A trunk would take a BLT1 vertex
            // stage under a BLT2 pixel stage and a canopy the trunk's pixel stage: either reads
            // its in-scatter as its transmittance, and a lit tree washed out to the sky colour in
            // daylight. The tree keeps its BLT1 pair.
            return null;
        }
        if (vertexEntry.Length == 0)
        {
            // A family whose vertex stage does not vary with the light count (the landscape
            // ships BLT1 vertex shaders only) keeps its BLT1 vertex shader under any group.
            vertexEntry = Engine_ClassicShaders.FindEntry("VSHADER", family,
                RetokenBlt(vertex, 1, mist is not null, out List<string> _).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        if (pixelEntry.Length == 0 || vertexEntry.Length == 0)
        {
            return null;
        }
        // NEW VARIANTS ARE SPREAD OVER FRAMES. Binding a newly composed shader compiles it
        // on the main thread unless the pool already has (tens of ms for the title's larger stages), and a district coming
        // into view states a dozen new light-group shapes at once: one frame took 700 ms. Past
        // the frame's budget a build waits (`Deferred`), and the instance keeps its base until
        // its next assignment; the first build of a frame always runs. Under the load hold
        // nothing is on screen and there is no budget (Engine_ClassicLoad).
        if (VariantBuildTicks > VariantBudgetTicks && !Engine_ClassicLoad.Holding)
        {
            Deferred = true;
            return null;
        }
        fresh = true;
        string renderMode = source.GetMeta(RenderModeMeta).AsString();
        if (pass > 0)
        {
            // Additive on top of the first pass, same depth, no second depth write. An opaque
            // source states no blend at all, which Godot would draw as a mix.
            renderMode = renderMode.Replace("blend_mix", "blend_add").Replace("depth_draw_opaque", "depth_draw_never");
            if (!renderMode.Contains("blend_"))
            {
                renderMode += ", blend_add";
            }
        }
        long composeFrom = System.Diagnostics.Stopwatch.GetTimestamp();
        Shader? shader = Engine_ClassicShaders.Compose(
            vertexEntry, pixelEntry, renderMode, pass, source.GetMeta(DefinesMeta, string.Empty).AsString());
        VariantComposeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - composeFrom;
        if (shader is null)
        {
            return null;
        }
        if (!Engine_ClassicShaders.Settled(shader))
        {
            // Bound now it would compile on the main thread: the instance keeps its base
            // and asks again at its next assignment.
            Deferred = true;
            return null;
        }
        var result = DuplicateOwn(source);
        result.Shader = shader;
        result.SetMeta(PixelTokensMeta, pixel);
        result.SetMeta(VertexTokensMeta, vertex);
        result.SetMeta(GroupVariantMeta, true);
        result.NextPass = null;
        // An additive light group is a later pass: it fogs but adds no fog colour (`sub_82214FB0`).
        result.SetShaderParameter("ps_g_AtmosphericFactors", new Vector4(1f, pass == 0 ? 1f : 0f, 0f, 0f));
        result.SetShaderParameter("vs_g_AtmosphericAdditiveFactor", new Vector4(pass == 0 ? 1f : 0f, 0f, 0f, 0f));
        if (result.GetShaderParameter("ps_g_LandscapeAmbientLightFactor").VariantType != Variant.Type.Nil)
        {
            // c11 (`sub_822159D8`): a landscape layer's ambient on its first pass only.
            float ambient = pass == 0 ? 1f : 0f;
            result.SetShaderParameter("ps_g_LandscapeAmbientLightFactor", new Vector4(ambient, ambient, ambient, ambient));
        }
        if (result.GetShaderParameter("ps_g_StandardMaterialPSConstants").Obj is Godot.Collections.Array { Count: 4 } rows)
        {
            var passRows = new Godot.Collections.Array(rows);
            passRows[3] = new Vector4(0f, 0f, 0f, pass == 0 ? 1f : 0f);
            result.SetShaderParameter("ps_g_StandardMaterialPSConstants", passRows);
        }
        if (mist is not null)
        {
            result.SetShaderParameter("ps_g_MistDensitySampler", mist.Density64);
            result.SetShaderParameter("ps_g_MistHeightSampler", Engine_ClassicMist.FlatHeight);
        }
        if (variants is null)
        {
            variants = new Godot.Collections.Dictionary();
            source.SetMeta(GroupVariantsMeta, variants);
        }
        variants[key] = result;
        return result;
    }

    /// <summary>
    /// A source's light-group variants, held by the source so they are freed with it: a
    /// per-instance source (the transfer pair) would otherwise leave every variant behind.
    /// </summary>
    private static readonly StringName GroupVariantsMeta = "xenos_group_variants";

    /// <summary>A duplicate's own state: never its source's variants or glass pass.</summary>
    private static ShaderMaterial DuplicateOwn(ShaderMaterial source)
    {
        var copy = Refii.Models.Engine_MatCopy.Copy(source, "classic");
        copy.RemoveMeta(GroupVariantsMeta);
        return copy;
    }

    /// <summary>The define that draws a translated stage at its texture coordinate instead of its place.</summary>
    private static readonly StringName TwoSidedMeta = "xenos_two_sided";

    /// <summary>
    /// A classic material drawn with both faces, for a caster the guest draws into its cascades
    /// with culling off (twin+0x110 from DisableBackFaceCullingForShadows, `sub_8221EE60`): the
    /// same stages and parameters, `cull_disabled`. Held by the source; the source itself when it
    /// is not classic or already draws both faces.
    /// </summary>
    public static Material TwoSidedCopy(Material source)
    {
        if (source is not ShaderMaterial shader || !shader.HasMeta(RenderModeMeta) || !shader.HasMeta(PixelTokensMeta))
        {
            return source;
        }
        if (shader.HasMeta(TwoSidedMeta) && shader.GetMeta(TwoSidedMeta).Obj is ShaderMaterial held && GodotObject.IsInstanceValid(held))
        {
            return held;
        }
        string renderMode = shader.GetMeta(RenderModeMeta).AsString();
        if (renderMode.Contains("cull_disabled"))
        {
            return source;
        }
        string family = shader.GetMeta(FamilyMeta, "STANDARDMATERIAL").AsString();
        string pixelEntry = Engine_ClassicShaders.FindEntry("PSHADER", family,
            shader.GetMeta(PixelTokensMeta).AsString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        string vertexEntry = shader.HasMeta(VertexEntryMeta)
            ? shader.GetMeta(VertexEntryMeta).AsString()
            : Engine_ClassicShaders.FindEntry("VSHADER", family,
                shader.GetMeta(VertexTokensMeta).AsString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Shader? composed = pixelEntry.Length == 0 || vertexEntry.Length == 0 ? null : Engine_ClassicShaders.Compose(
            vertexEntry, pixelEntry, renderMode.Replace("cull_back", "cull_disabled").Replace("cull_front", "cull_disabled"),
            0, shader.GetMeta(DefinesMeta, string.Empty).AsString());
        if (composed is null)
        {
            return source;
        }
        ShaderMaterial copy = DuplicateOwn(shader);
        copy.Shader = composed;
        copy.NextPass = null;
        shader.SetMeta(TwoSidedMeta, copy);
        return copy;
    }

    /// <summary>A texture-space draw: no depth test, both faces, opaque (Compose then writes no ALPHA).</summary>
    public const string UvSpaceRenderMode =
        "unshaded, depth_draw_opaque, depth_test_disabled, cull_disabled, shadows_disabled, fog_disabled";

    /// <summary>A later light group's texture-space pass: added on top (SrcBlend = DestBlend = ONE).</summary>
    public const string UvSpaceAddRenderMode =
        "unshaded, blend_add, depth_draw_never, depth_test_disabled, cull_disabled, shadows_disabled, fog_disabled";

    /// <summary>An added pass writes alpha 1, so Godot's additive blend takes its colour whole.</summary>
    public const string UvSpaceAddDefines = "#define XR_WRITE_ALPHA(a) ALPHA = 1.0";

    public const string UvSpaceDefine = "#define XR_POSITION_OUT(p) vec4(UV.x * 2.0 - 1.0, 1.0 - UV.y * 2.0, 0.5, 1.0)";

    /// <summary>
    /// A classic material drawn into its own texture space: the fur's prelit skin pass, where the
    /// title draws the furred primitive's cloned skin material with VS b0 g_ProjectToTexture
    /// (`sub_82268C70`; uv -> clip (2u - 1, 1 - 2v), no depth test). The same stages with the
    /// position replaced (the standard family's skinned variants project the same way; Godot's
    /// skinning stands in for theirs), and the screen-space shadow mask white, as a draw outside
    /// the main view cannot read the main view's. Null when the source is not classic.
    /// </summary>
    public static ShaderMaterial? UvSpaceCopy(ShaderMaterial source, int pass = 0)
    {
        if (!source.HasMeta(PixelTokensMeta) || !source.HasMeta(VertexTokensMeta))
        {
            return null;
        }
        string family = source.GetMeta(FamilyMeta, "STANDARDMATERIAL").AsString();
        string[] pixelTokens = source.GetMeta(PixelTokensMeta).AsString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] vertexTokens = source.GetMeta(VertexTokensMeta).AsString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string pixelEntry = Engine_ClassicShaders.FindEntry("PSHADER", family, pixelTokens);
        string vertexEntry = source.HasMeta(VertexEntryMeta)
            ? source.GetMeta(VertexEntryMeta).AsString()
            : Engine_ClassicShaders.FindEntry("VSHADER", family, vertexTokens);
        if (pixelEntry.Length == 0 || vertexEntry.Length == 0)
        {
            return null;
        }
        string defines = source.GetMeta(DefinesMeta, string.Empty).AsString() + "\n" + UvSpaceDefine +
            (pass > 0 ? "\n" + UvSpaceAddDefines : string.Empty);
        Shader? shader = Engine_ClassicShaders.Compose(vertexEntry, pixelEntry,
            pass > 0 ? UvSpaceAddRenderMode : UvSpaceRenderMode, pass, defines);
        if (shader is null)
        {
            return null;
        }
        ShaderMaterial copy = DuplicateOwn(source);
        copy.Shader = shader;
        copy.NextPass = null;
        copy.SetShaderParameter("ps_g_ScreenShadowMap", Engine_ClassicScreenShadow.White);
        return copy;
    }

    /// <summary>
    /// A copy of a classic material for one instance's own textures (the hero's composites): its
    /// own glass pass too, and none of the source's light-group variants, which carry the
    /// source's textures.
    /// </summary>
    public static ShaderMaterial InstanceCopy(ShaderMaterial source)
    {
        ShaderMaterial copy = DuplicateOwn(source);
        if (source.HasMeta(TranslucentPassMeta) && source.GetMeta(TranslucentPassMeta).Obj is ShaderMaterial glass)
        {
            ShaderMaterial glassCopy = DuplicateOwn(glass);
            copy.NextPass = glassCopy;
            copy.SetMeta(TranslucentPassMeta, glassCopy);
        }
        return copy;
    }

    private static readonly string[] SlotSamplers =
    {
        "ps_g_DiffuseTextureSampler", "ps_g_NormalTextureSampler", "ps_g_SpecularTextureSampler", "ps_g_SelfIllumTextureSampler",
    };

    /// <summary>
    /// The parameter a texture slot (Engine_MdlMaterial.TextureParameters order: diffuse,
    /// normal, specular, self-illumination) is bound to on <paramref name="material"/>.
    /// </summary>
    public static string TextureParameter(ShaderMaterial material, int slot) =>
        material.HasMeta(ClassicMeta) && slot >= 0 && slot < SlotSamplers.Length
            ? SlotSamplers[slot]
            : Engine_MdlMaterial.TextureParameters[slot];

    /// <summary>The diffuse a material draws, classic or modern.</summary>
    public static Texture2D? Albedo(ShaderMaterial material) =>
        material.GetShaderParameter(TextureParameter(material, 0)).As<Texture2D>();

    /// <summary>
    /// Sets a uniform on a material and on every pass drawn from it — its glass pass and the
    /// light-group variants of both — so per-instance state reaches whichever is on screen.
    /// </summary>
    public static void SetOnPasses(ShaderMaterial source, StringName name, Variant value)
    {
        source.SetShaderParameter(name, value);
        if (source.HasMeta(GroupVariantsMeta))
        {
            using Variant meta = source.GetMeta(GroupVariantsMeta);
            using Godot.Collections.Dictionary variants = meta.AsGodotDictionary();
            foreach (Variant held in variants.Values)
            {
                using (held)
                {
                    if (held.Obj is ShaderMaterial variant && GodotObject.IsInstanceValid(variant))
                    {
                        variant.SetShaderParameter(name, value);
                    }
                }
            }
        }
        if (source.HasMeta(TranslucentPassMeta))
        {
            using Variant pass = source.GetMeta(TranslucentPassMeta);
            if (pass.Obj is ShaderMaterial glass)
            {
                SetOnPasses(glass, name, value);
            }
        }
    }

    /// <summary>The translucent second draw a glass material carries (its NextPass).</summary>
    public static readonly StringName TranslucentPassMeta = "xenos_translucent_pass";

    /// <summary>
    /// A translucent surface's second draw (`sub_8221BD40` bucket 3, back to front after the
    /// resolve): the same permutation without SCR1, SrcBlend ONE (`sub_822251D8`) over the
    /// frame's DestBlend INVSRCALPHA and BlendOp ADD (`sub_822C2C60`) — premultiplied — with
    /// depth writes off, no alpha test, and c26 = opacity x fade = (1, 1, 1, 1). The shader's
    /// oC0.w is c26.w x the texture's alpha; its colour is not multiplied by it.
    /// </summary>
    private static ShaderMaterial? BuildTranslucentPass(ShaderMaterial opaque, List<string> pixelTokens, string vertex)
    {
        var tokens = new List<string>(pixelTokens);
        tokens.Remove("SCR1");
        string family = opaque.GetMeta(FamilyMeta).AsString();
        string pixel = Engine_ClassicShaders.FindEntry("PSHADER", family, tokens);
        if (pixel.Length == 0)
        {
            return null;
        }
        const string mode = "unshaded, blend_premul_alpha, depth_draw_never, cull_back";
        Shader? shader = Engine_ClassicShaders.Compose(vertex, pixel, mode);
        if (shader is null)
        {
            return null;
        }
        var glass = DuplicateOwn(opaque);
        glass.Shader = shader;
        glass.NextPass = null;
        glass.RemoveMeta(TranslucentPassMeta);
        glass.SetMeta(PixelTokensMeta, string.Join(' ', tokens));
        glass.SetMeta(RenderModeMeta, mode);
        glass.SetShaderParameter("xr_spec_constants", 0u);
        return glass;
    }

    /// <summary>Marks the head of a material whose NextPass chain is layers drawn in order (a landscape patch), each lit on its own.</summary>
    public static readonly StringName LayerChainMeta = "xenos_layer_chain";

    /// <summary>Marks a light-group variant, so an instance never derives a variant from one.</summary>
    public static readonly StringName GroupVariantMeta = "xenos_group_variant";

    /// <summary>
    /// Marks a material built elsewhere on a translated pair (the terrain's patch materials), so
    /// the ambient and light-group variants can re-key it like one built here.
    /// </summary>
    public static void Register(
        ShaderMaterial material, string family, IReadOnlyList<string> pixelTokens,
        IReadOnlyList<string> vertexTokens, string renderMode)
    {
        material.SetMeta(ClassicMeta, true);
        material.SetMeta(FamilyMeta, family);
        material.SetMeta(PixelTokensMeta, string.Join(' ', pixelTokens));
        material.SetMeta(VertexTokensMeta, string.Join(' ', vertexTokens));
        material.SetMeta(RenderModeMeta, renderMode);
    }

    /// <summary>
    /// Registers a material whose vertex shader is a named entry outside the family's own
    /// permutations, composed with <paramref name="defines"/>, drawn in one pass: the
    /// light-group variants keep that vertex entry and never add passes.
    /// </summary>
    public static void RegisterSinglePass(
        ShaderMaterial material, string family, string vertexEntry, IReadOnlyList<string> pixelTokens,
        string renderMode, string defines)
    {
        Register(material, family, pixelTokens, Array.Empty<string>(), renderMode);
        material.SetMeta(VertexEntryMeta, vertexEntry);
        material.SetMeta(DefinesMeta, defines);
        material.SetMeta(SinglePassMeta, true);
    }

    private static bool TrunkStage(string entry) => entry.Contains("_TRUNK1", StringComparison.Ordinal);

    /// <summary>`dword_833216F4`: the shader's light capacity for a group's light count.</summary>
    private static readonly int[] BltByCount = { 1, 1, 2, 4, 4, 7, 7, 7 };

    /// <summary>The BLT permutation a light group of <paramref name="count"/> lights draws with.</summary>
    public static int BltFor(int count, bool singleLocal) => singleLocal ? 2 : BltByCount[Math.Clamp(count, 0, 7)];

    private static string RetokenBlt(string tokens, int blt, bool mist, out List<string> list)
    {
        list = new List<string>();
        foreach (string token in tokens.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("BLT", StringComparison.Ordinal))
            {
                list.Add("BLT" + blt);
            }
            else if (token.StartsWith("MIST", StringComparison.Ordinal))
            {
                list.Add(mist ? "MIST1" : "MIST0");
            }
            else
            {
                list.Add(token);
            }
        }
        return string.Join(' ', list);
    }

    private static string Retoken(string tokens, int ambient, out List<string> list)
    {
        list = new List<string>();
        foreach (string token in tokens.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!token.StartsWith("AMB", StringComparison.Ordinal))
            {
                list.Add(token);
            }
        }
        if (ambient != 0)
        {
            list.Add("AMB" + ambient);
        }
        return string.Join(' ', list);
    }

    /// <summary>How many classic materials were built and how many fell back, for the log.</summary>
    public static (int Built, int FellBack) Counts => (_built, _fellBack);
}
