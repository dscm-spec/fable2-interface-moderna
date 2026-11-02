using System.Collections.Generic;
using Godot;

namespace Refii.Models;

/// <summary>
/// Copies a world ShaderMaterial without <c>Resource.Duplicate</c>. Duplicate builds the
/// material's property list, which asks the render thread for the shader's uniform list: a
/// synchronous round trip per copy with the separate render thread (G06: hero morph copies
/// averaged 25-29 ms, translucent second passes 100-150 ms; this copy takes ~0.05 ms).
/// Keeps everything Duplicate keeps for a ShaderMaterial: shader, render priority, next pass,
/// every shader parameter, resource name, local-to-scene and metadata (containers get their own
/// copy). G06 verify mode compared both copies in play: 0 differences.
/// Uniform names are cached per shader and per shader code, so a recompiled shader gets a fresh list.
/// </summary>
public static class Engine_MatCopy
{
    private static readonly Dictionary<Shader, (int Hash, int Length, StringName[] Names)> UniformNames = new();

    public static ShaderMaterial Copy(ShaderMaterial source, string site)
    {
        Shader? shader = source.Shader;
        if (shader is null)
        {
            return (ShaderMaterial)source.Duplicate();
        }

        var copy = new ShaderMaterial
        {
            Shader = shader,
            RenderPriority = source.RenderPriority,
            NextPass = source.NextPass,
            ResourceName = source.ResourceName,
            ResourceLocalToScene = source.ResourceLocalToScene,
        };
        foreach (StringName name in Names(shader))
        {
            Variant value = source.GetShaderParameter(name);
            if (value.VariantType != Variant.Type.Nil)
            {
                copy.SetShaderParameter(name, value);
            }
        }

        foreach (StringName key in source.GetMetaList())
        {
            Variant v = source.GetMeta(key);
            if (v.VariantType == Variant.Type.Array)
            {
                v = v.AsGodotArray().Duplicate();
            }
            else if (v.VariantType == Variant.Type.Dictionary)
            {
                v = v.AsGodotDictionary().Duplicate();
            }

            copy.SetMeta(key, v);
        }

        return copy;
    }

    private static StringName[] Names(Shader shader)
    {
        string code = shader.Code ?? "";
        int hash = code.GetHashCode();
        lock (UniformNames)
        {
            if (UniformNames.TryGetValue(shader, out var entry) && entry.Hash == hash && entry.Length == code.Length)
            {
                return entry.Names;
            }
        }

        var list = new List<StringName>();
        foreach (Godot.Collections.Dictionary uniform in shader.GetShaderUniformList())
        {
            list.Add((string)uniform["name"]);
        }

        StringName[] names = list.ToArray();
        lock (UniformNames)
        {
            UniformNames[shader] = (hash, code.Length, names);
        }

        return names;
    }
}
