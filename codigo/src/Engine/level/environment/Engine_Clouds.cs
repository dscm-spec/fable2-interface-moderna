using System;
using System.Collections.Generic;
using Godot;
using Refii.Gdb;
using Refii.Textures;

namespace Refii.Level;

public static class Engine_Clouds
{
    public const string ShaderPath = "res://src/Engine/level/environment/clouds.gdshader";

    public const float VelocityScale = 0.001f;

    public const float AlphaTestThreshold = 5f / 255f;

    public delegate byte[]? ReadAsset(string virtualPath);

    public delegate string? FindByName(string fileName);

    public static Node3D? Build(
        CloudTheme theme,
        IReadOnlyList<byte[]> gdbs,
        float[] mainLightDirection,
        float[] mainLightColour,
        ReadAsset read,
        FindByName? findByName)
    {
        if (theme.LayerCount == 0)
        {
            return null;
        }

        var shader = GD.Load<Shader>(ShaderPath);
        if (shader is null)
        {
            GD.Print($"[clouds] shader missing at {ShaderPath}");
            return null;
        }

        var order = new List<int>();
        for (int i = 0; i < theme.Layers.Length; i++)
        {
            CloudLayerTheme layer = theme.Layers[i];
            if (layer.Enabled && layer.HasDensityMap && layer.Transparency > 0f)
            {
                order.Add(i);
            }
        }

        if (order.Count == 0)
        {
            return null;
        }

        order.Sort((a, b) => theme.Layers[b].Height.CompareTo(theme.Layers[a].Height));

        var container = new Node3D { Name = "Clouds" };
        int built = 0;

        foreach (int index in order)
        {
            CloudLayerTheme layer = theme.Layers[index];

            Texture2D? density = ResolveDensityMap(layer.DensityMapHash, gdbs, read, findByName);
            if (density is null)
            {
                GD.Print($"[clouds] layer {index}: density map 0x{layer.DensityMapHash:X8} unresolved");
                continue;
            }

            ShaderMaterial? classic = Engine_ClassicClouds.Material(density, layer);
            classic?.SetMeta(Engine_ClassicClouds.LayerMeta, index);
            var material = classic ?? new ShaderMaterial { Shader = shader };
            material.SetShaderParameter(CloudDensityName, density);

            material.SetShaderParameter(LayerParamsName, new Vector4(
                layer.Transparency,
                layer.AmbientLight,
                layer.Brightness,
                ShaderNormalStrength(layer.NormalStrength)));

            material.SetShaderParameter(UvScaleOffsetName, new Vector4(
                layer.TextureScaleX,
                layer.TextureScaleY,
                0f,
                0f));

            material.SetShaderParameter(CloudGlobalsName,
                new Vector4(0f, 0f, AlphaTestThreshold, 0f));

            material.SetShaderParameter(LightColourName, new Vector4(
                mainLightColour[0], mainLightColour[1], mainLightColour[2], 1f));

            material.SetShaderParameter(LightDirectionWorldName, new Vector3(
                -mainLightDirection[0], mainLightDirection[1], mainLightDirection[2]));

            var node = new MeshInstance3D
            {
                Layers = Refii.Level.Engine_ReflectionLayer.ReflectedLayers,
                Name = $"CloudLayer{index}",
                Mesh = BuildQuad(layer.SizeX, layer.SizeY, layer.Height),
                MaterialOverride = material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };

            container.AddChild(node);
            built++;
        }

        if (built == 0)
        {
            container.QueueFree();
            return null;
        }

        GD.Print($"[clouds] {built} layer(s) of {theme.LayerCount}");
        return container;
    }

    public static void Update(
        Node3D container,
        CloudTheme theme,
        float[] lightDirection,
        float[] lightColour)
    {
        // By index, names by instance: GetChildren() and Node.Name each make a finalizable
        // handle, and this runs at the atmosphere rate.
        for (int i = 0, count = container.GetChildCount(); i < count; i++)
        {
            Node child = container.GetChild(i);
            if (child is not MeshInstance3D mesh ||
                mesh.MaterialOverride is not ShaderMaterial material)
            {
                continue;
            }

            string name = Refii.Core.Engine_NodeNames.Of(child);
            if (!name.StartsWith("CloudLayer", StringComparison.Ordinal) ||
                !int.TryParse(name.AsSpan("CloudLayer".Length), out int index) ||
                index < 0 || index >= theme.Layers.Length)
            {
                continue;
            }

            CloudLayerTheme layer = theme.Layers[index];
            if (material.HasMeta(Engine_ClassicClouds.ClassicCloudMeta))
            {
                Engine_ClassicClouds.SetLayer(material, layer);
            }
            material.SetShaderParameter(LayerParamsName, new Vector4(
                layer.Transparency,
                layer.AmbientLight,
                layer.Brightness,
                ShaderNormalStrength(layer.NormalStrength)));

            Vector4 current = material.GetShaderParameter(UvScaleOffsetName).AsVector4();
            material.SetShaderParameter(UvScaleOffsetName, new Vector4(
                layer.TextureScaleX,
                layer.TextureScaleY,
                current.Z,
                current.W));
            material.SetShaderParameter(LightColourName, new Vector4(
                lightColour[0], lightColour[1], lightColour[2], 1f));
            material.SetShaderParameter(LightDirectionWorldName, new Vector3(
                -lightDirection[0], lightDirection[1], lightDirection[2]));
            mesh.Visible = layer.Enabled && layer.Transparency > 0.001f;
        }
    }

    public static float ShaderNormalStrength(float authored)
    {
        float transformed = 1f - authored;
        if (!(transformed >= 0.1f))
        {
            transformed = 0.1f;
        }

        return transformed > 2f ? 2f : transformed;
    }

    private static ArrayMesh BuildQuad(float sizeX, float sizeY, float height)
    {
        var vertices = new Vector3[]
        {
            new(-sizeX, height, -sizeY),
            new(sizeX, height, -sizeY),
            new(-sizeX, height, sizeY),
            new(sizeX, height, sizeY),
        };

        var uvs = new Vector2[]
        {
            new(0f, 0f), new(1f, 0f), new(0f, 1f), new(1f, 1f),
        };

        var indices = new[] { 0, 1, 2, 1, 3, 2 };

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetName(0, "CloudLayer");
        return mesh;
    }

    public static void SetTexturePathSource(Func<IEnumerable<string>>? source) =>
        Engine_EnvironmentTextures.SetTexturePathSource(source);

    public static uint EnvironmentTextureHash(string value, bool lowercase) =>
        Engine_EnvironmentTextures.Hash(value, lowercase);

    public static string? ResolveEnvironmentTextureHash(uint hash) =>
        Engine_EnvironmentTextures.ResolvePath(hash);

    private static Texture2D? ResolveDensityMap(
        uint hash, IReadOnlyList<byte[]> gdbs, ReadAsset read, FindByName? findByName)
    {
        string? path = ResolveEnvironmentTextureHash(hash);
        if (path is null)
        {
            return null;
        }

        byte[]? bytes = read(path);
        if (bytes is null)
        {
            GD.Print($"[clouds] density map '{path}' did not read");
            return null;
        }

        // As Engine_TexImage.TryCreateTexture, keeping the decoded image: the sky reads the map's
        // alpha on the CPU (Engine_SkyEnvironmentNode.TryCloudAlpha), and Texture2D.GetImage
        // would fetch it back from the render thread, a synchronous call that waits for every
        // queued upload (440 ms on the first frame after Bowerstone Market streamed in).
        Image? image = Engine_TexImage.TryCreateImage(bytes, out _);
        if (image is null || image.IsEmpty())
        {
            return null;
        }

        ImageTexture texture = ImageTexture.CreateFromImage(image);
        DensitySources.AddOrUpdate(texture, image);
        return texture;
    }

    /// <summary>
    /// The decoded image a density map from <see cref="Build"/> was made from, or null for a
    /// texture this class did not make. Shared, and possibly still being read by the render
    /// thread's upload: callers that change it must Duplicate it first.
    /// </summary>
    public static Image? DensitySource(Texture2D? texture) =>
        texture is not null && DensitySources.TryGetValue(texture, out Image? image) ? image : null;

    // Weak keys: an image goes with its texture.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Texture2D, Image>
        DensitySources = new();

    // Made once: a string literal converts to a new StringName on every call.
    private static readonly StringName CloudDensityName = "cloud_density";
    private static readonly StringName LayerParamsName = "layer_params";
    private static readonly StringName UvScaleOffsetName = "uv_scale_offset";
    private static readonly StringName CloudGlobalsName = "cloud_globals";
    private static readonly StringName LightColourName = "light_colour";
    private static readonly StringName LightDirectionWorldName = "light_direction_world";
}
