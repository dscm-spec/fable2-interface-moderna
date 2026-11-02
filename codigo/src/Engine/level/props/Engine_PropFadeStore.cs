using System.Runtime.CompilerServices;
using Godot;

namespace Refii.Level;

/// <summary>What the store holds for one node: the band <see cref="Engine_PropFade"/> last wrote, and its arrival.</summary>
/// <remarks>
/// The defaults are the shaders' own (standard_material_body.gdshaderinc and
/// shadow_caster.gdshader): no band is (0,0,0,0)/(0,0,0,0)/(0,0,0), no arrival is alpha 1.
/// </remarks>
public readonly record struct FadeSnapshot(
	bool HasBand, Vector4 FadeOut, Vector4 FadeIn, Vector3 Centre, bool HasAlpha, float Alpha);

/// <summary>
/// The fade instance uniforms as last written, per node, so a copy to a shadow twin or proxy
/// never reads them back from the RenderingServer.
/// </summary>
/// <remarks>
/// <c>GeometryInstance3D.GetInstanceShaderParameter</c> is a synchronous call into the render
/// thread: with the multi-threaded server it waits for every queued command before it, and while
/// an area streams in that queue holds its texture and buffer uploads (608 ms measured on
/// MysteryIsland's present, VRAM +999 MB that frame). Every writer of the four uniforms
/// (<see cref="Engine_PropFade"/> and <see cref="Engine_PropDrawRange.Apply"/>) writes here as
/// well, so the last value a node was given is known without asking.
/// Weak keys: an entry goes with its node. Thread-safe: a scene may be built off the main thread.
/// Generic only so the bookkeeping can be tested without the engine.
/// </remarks>
public sealed class Engine_PropFadeStore<TKey> where TKey : class
{
	private sealed class Entry
	{
		public bool HasBand;
		public Vector4 FadeOut;
		public Vector4 FadeIn;
		public Vector3 Centre;
		public bool HasAlpha;
		public float Alpha;
	}

	private readonly ConditionalWeakTable<TKey, Entry> _entries = new();

	private Entry For(TKey key) => _entries.GetValue(key, static _ => new Entry());

	/// <summary>Records a band written to <paramref name="key"/>.</summary>
	public void SetBand(TKey key, Vector4 fadeOut, Vector4 fadeIn, Vector3 centre)
	{
		Entry entry = For(key);
		lock (entry)
		{
			entry.HasBand = true;
			entry.FadeOut = fadeOut;
			entry.FadeIn = fadeIn;
			entry.Centre = centre;
		}
	}

	/// <summary>Records an arrival alpha written to <paramref name="key"/>.</summary>
	public void SetAlpha(TKey key, float alpha)
	{
		Entry entry = For(key);
		lock (entry)
		{
			entry.HasAlpha = true;
			entry.Alpha = alpha;
		}
	}

	/// <summary>What was written to <paramref name="key"/>; false when nothing ever was.</summary>
	public bool TryGet(TKey key, out FadeSnapshot snapshot)
	{
		if (!_entries.TryGetValue(key, out Entry? entry))
		{
			snapshot = default;
			return false;
		}

		lock (entry)
		{
			snapshot = new FadeSnapshot(entry.HasBand, entry.FadeOut, entry.FadeIn, entry.Centre,
				entry.HasAlpha, entry.Alpha);
		}

		return true;
	}

	/// <summary>Whether anything was ever written to <paramref name="key"/>.</summary>
	public bool Has(TKey key) => _entries.TryGetValue(key, out _);
}
