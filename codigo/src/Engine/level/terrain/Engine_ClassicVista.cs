using Godot;
using Refii.Models;

namespace Refii.Level;

/// <summary>
/// A background landscape (a vista) on the title's own background pixel shaders.
/// </summary>
/// <remarks>
/// THE TITLE'S DRAW (`sub_82282F48` -> `sub_8226D908` -> `sub_82236508` -> `sub_822059E8`): VS 15
/// over each background patch's 16-cell tiles, and PS = table[4 mist + 2 lightmap + blend] of
/// `PSHADER_LANDSCAPE_BACKGROUND_{RENDER, LIGHTMAPPED, BLENDING, ...}` (`sub_82A8E7A0`). The
/// diffuse is the patch's pre-baked atlas page — only the last record of its page set is ever
/// loaded (0x82A90D18..D20: index = count - 1); no bake pass runs. A landscape with no foreground
/// patches (a vista) never takes the blending variant. Per draw: PS c11
/// g_LandscapeDistantShadowFactor = splat(theme index 5) (`sub_821C9400`), c28 = (1, 1, 0, 0);
/// blending is forced off.
/// </remarks>
public static class Engine_ClassicVista
{
    private const string VertexEntry = "res://src/Engine/level/terrain/classic_background_vertex.gdshaderinc";
    private const string RenderMode = "unshaded, depth_draw_opaque, cull_disabled";

    /// <summary>(origin x, origin y, metres per cell) of the landscape a page belongs to, guest axes.</summary>
    public static readonly StringName GridMeta = "xenos_vista_grid";

    /// <summary>Marks a vista page material built here.</summary>
    public static readonly StringName VistaMeta = "xenos_vista";

    /// <summary>The page's material on PSHADER_LANDSCAPE_BACKGROUND_RENDER, or null without the pair.</summary>
    public static ShaderMaterial? PageMaterial(Texture2D page)
    {
        Shader? shader = Engine_ClassicShaders.Compose(VertexEntry, "PSHADER_LANDSCAPE_BACKGROUND_RENDER", RenderMode);
        if (shader is null)
        {
            return null;
        }
        var material = new ShaderMaterial { Shader = shader };
        material.SetMeta(VistaMeta, true);
        material.SetShaderParameter("ps_g_BackgroundDiffuseTexture", page);
        material.SetShaderParameter("ps_g_AtmosphericFactors", new Vector4(1f, 1f, 0f, 0f));
        material.SetShaderParameter("ps_g_Extinction", Engine_ClassicAtmosphere.ExtinctionTexture);
        material.SetShaderParameter("ps_g_InScattering", Engine_ClassicAtmosphere.InScatteringTexture);
        Engine_ClassicVistaShadow.Register(material);
        return material;
    }

    private static readonly StringName MistedMeta = "xenos_vista_misted";
    private static readonly StringName LightmappedMeta = "xenos_vista_lightmapped";

    /// <summary>The page on its MIST variant (lightmapped or not, as the page is), the volume's textures bound.</summary>
    public static ShaderMaterial? Misted(ShaderMaterial page, Engine_ClassicMist.Volume mist)
    {
        bool lightmapped = page.HasMeta(LightmappedMeta);
        Shader? shader = Engine_ClassicShaders.Compose(VertexEntry,
            lightmapped ? "PSHADER_LANDSCAPE_BACKGROUND_MIST_LIGHTMAPPED_RENDER" : "PSHADER_LANDSCAPE_BACKGROUND_MIST_RENDER",
            RenderMode);
        if (shader is null)
        {
            return null;
        }
        var misted = Refii.Models.Engine_MatCopy.Copy(page, "vista-misted");
        misted.Shader = shader;
        misted.SetMeta(MistedMeta, true);
        misted.SetShaderParameter("ps_g_MistDensitySampler", mist.Density64);
        misted.SetShaderParameter("ps_g_MistHeightSampler", Engine_ClassicMist.FlatHeight);
        // b1 g_EnableMist (the mist binder's pixel bool).
        misted.SetShaderParameter("xr_booleans", 1u << (16 + 1));
        Engine_ClassicVistaShadow.Register(misted);
        return misted;
    }

    /// <summary>
    /// The same page on PSHADER_LANDSCAPE_BACKGROUND_LIGHTMAPPED, the bake on sampler 3 with the
    /// landscape's origin and g_LandscapeLightmapUVFactor (VS c14, `sub_82A8EA50`).
    /// </summary>
    public static ShaderMaterial? Lightmapped(ShaderMaterial page, Texture2D lightmap, Vector2 origin, Vector2 factor)
    {
        Shader? shader = Engine_ClassicShaders.Compose(VertexEntry, "PSHADER_LANDSCAPE_BACKGROUND_LIGHTMAPPED_RENDER", RenderMode);
        if (shader is null)
        {
            return null;
        }
        var lit = Refii.Models.Engine_MatCopy.Copy(page, "vista-lit");
        lit.Shader = shader;
        lit.SetShaderParameter("ps_g_TextureLightmapSampler", lightmap);
        lit.SetShaderParameter("xr_background_lightmap", new Vector4(origin.X, origin.Y, factor.X, factor.Y));
        lit.SetMeta(LightmappedMeta, true);
        Engine_ClassicVistaShadow.Register(lit);
        return lit;
    }
}

/// <summary>
/// The background patches' mist (`sub_82224458`): a patch whose bounds meet a mist volume
/// draws PSHADER_LANDSCAPE_BACKGROUND_MIST_{RENDER, LIGHTMAPPED_RENDER}, the volume's registers
/// (c72..c76, `Render_BindHighSamplersAndPixelBool`) in a row of its own.
/// </summary>
public static class Engine_ClassicVistaMist
{
    private sealed class Entry
    {
        public MeshInstance3D Node = null!;
        public int Row;
        public ulong Viewport;
    }

    private static readonly System.Collections.Generic.List<Entry> _entries = new();
    private static readonly StringName RowUniform = "xr_light_row";
    private static int _frame;

    /// <summary>A vista patch to keep on the mist variant while a volume covers it.</summary>
    public static void Track(MeshInstance3D node)
    {
        lock (_entries)
        {
            _entries.Add(new Entry { Node = node });
        }
    }

    /// <summary>Main thread, once a frame; patches are re-tested every 15th.</summary>
    public static void Pump()
    {
        if (++_frame % 15 != 0)
        {
            return;
        }
        lock (_entries)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry entry = _entries[i];
                if (!GodotObject.IsInstanceValid(entry.Node) || !entry.Node.IsInsideTree())
                {
                    Engine_ClassicConstants.ReleaseRow(entry.Row);
                    _entries.RemoveAt(i);
                    continue;
                }
                Aabb bounds = entry.Node.GlobalTransform * entry.Node.GetAabb();
                Engine_ClassicMist.Volume? mist = Engine_ClassicMist.VolumeFor(bounds);
                ulong viewport = mist?.Viewport?.GetInstanceId() ?? 0UL;
                if (mist is not null)
                {
                    if (entry.Row == 0)
                    {
                        entry.Row = Engine_ClassicConstants.AcquireRow();
                    }
                    if (entry.Row != 0)
                    {
                        Engine_ClassicMist.WriteRegisters(mist, entry.Row);
                        entry.Node.SetInstanceShaderParameter(RowUniform, entry.Row);
                    }
                }
                if (viewport == entry.Viewport)
                {
                    continue;
                }
                entry.Viewport = viewport;
                Mesh? mesh = entry.Node.Mesh;
                for (int s = 0; mesh is not null && s < mesh.GetSurfaceCount(); s++)
                {
                    ShaderMaterial? misted = mist is not null && entry.Row != 0 && mesh.SurfaceGetMaterial(s) is ShaderMaterial page
                        ? Engine_ClassicVista.Misted(page, mist)
                        : null;
                    entry.Node.SetSurfaceOverrideMaterial(s, misted);
                }
                if (mist is null && entry.Row != 0)
                {
                    entry.Node.SetInstanceShaderParameter(RowUniform, 0);
                    Engine_ClassicConstants.ReleaseRow(entry.Row);
                    entry.Row = 0;
                }
            }
        }
    }
}

/// <summary>PS c11 on every vista material: the theme's distant shadow factor, as the feed reads it.</summary>
public static class Engine_ClassicVistaShadow
{
    private static readonly StringName Factor = "ps_g_LandscapeDistantShadowFactor";
    private static readonly System.Collections.Generic.List<System.WeakReference<ShaderMaterial>> _materials = new();
    private static float _value = 1f;

    public static void Register(ShaderMaterial material)
    {
        lock (_materials)
        {
            material.SetShaderParameter(Factor, new Vector4(_value, _value, _value, _value));
            _materials.Add(new System.WeakReference<ShaderMaterial>(material));
        }
    }

    /// <summary>The frame's value; main thread.</summary>
    public static void Pump(float value)
    {
        lock (_materials)
        {
            if (value == _value)
            {
                return;
            }
            _value = value;
            for (int i = _materials.Count - 1; i >= 0; i--)
            {
                if (!_materials[i].TryGetTarget(out ShaderMaterial? material) || !GodotObject.IsInstanceValid(material))
                {
                    _materials.RemoveAt(i);
                    continue;
                }
                material.SetShaderParameter(Factor, new Vector4(value, value, value, value));
            }
        }
    }
}
