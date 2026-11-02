using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Level;

/// <summary>
/// The edge of a prop's cull radius, dithered instead of cut.
/// </summary>
/// <remarks>
/// <see cref="Engine_PropStreamer.ApplyVisibilityRange"/> culls small dressing by size tier
/// (15/20/30/40 m times <c>prop_cull_distance_scale</c>). A Godot visibility range is one test
/// per NODE, measured to the node's box centre, and a batch is one node per 32 m cell: a whole
/// cell of undergrowth appeared the frame its centre came into range. The radius stays as set —
/// that is the cost decision — and the node still culls there; what changes is the edge. Each
/// COPY dithers over the last <see cref="BandShare"/> of the radius, measured from the main
/// camera to its own origin in the body shader (<c>xr_tree_fade_out</c>, the tree band's
/// uniforms, which the shadow caster shares), so a plant fades in on its own as it crosses into
/// range and the cell's cull only ever removes copies the dither has already taken to nothing.
///
/// A copy the streamer delivers late stands inside its radius already, so distance cannot fade
/// it: it dithers in over <see cref="ArrivalSeconds"/> from the frame its mesh stood
/// (<c>xr_fade_alpha</c>), the cast-only twin or proxy beside it alike.
///
/// Per view by construction: the shader measures from each viewport's own camera
/// (<c>MAIN_CAM_INV_VIEW_MATRIX</c>, the main camera in the shadow passes too) and Godot culls
/// each viewport with its own, so on split screen each eye sees its own edge.
///
/// Only the surfaces that may discard take part: the body under <c>FABLE_SOLID</c> compiles
/// without one so Godot's shared depth-only shadow material still covers opaque props, and a
/// classic material has the band only in its tree family.
/// </remarks>
public static class Engine_PropFade
{
	/// <summary>
	/// The share of a copy's cull radius over which it dithers: 3 m at the 15 m tier, 8 m at the
	/// 40 m one. A share rather than metres so the edge keeps its proportion under
	/// <c>prop_cull_distance_scale</c>; a fifth is short enough that a copy standing inside the
	/// radius is whole, and long enough to take a walking pace a second or more to cross.
	/// </summary>
	public const float BandShare = 0.2f;

	/// <summary>
	/// How long a copy that arrives inside its radius takes to dither in: well past a few frames,
	/// so it never reads as a pop, and well short of a stride, so it never reads as an animation.
	/// </summary>
	public const float ArrivalSeconds = 0.5f;

	/// <summary>The band a radius dithers over, in metres.</summary>
	public static float Band(float radius) => radius > 0f ? radius * BandShare : 0f;

	/// <summary>
	/// The fraction of a copy drawn at <paramref name="distance"/> from the eye: whole inside
	/// the band, nothing at the radius, linear between. A radius of zero is no cull and whole.
	/// </summary>
	public static float DistanceFactor(float distance, float radius)
	{
		float band = Band(radius);
		if (band <= 0f)
		{
			return 1f;
		}

		return Math.Clamp((radius - distance) / band, 0f, 1f);
	}

	/// <summary>
	/// The body shader's out band for a radius, as <c>xr_tree_fade_out</c> reads it: (end,
	/// 1/length, power) with the end at the radius and a linear curve. All zero for no cull.
	/// </summary>
	public static (float End, float InverseLength, float Power) OutBand(float radius)
	{
		float band = Band(radius);
		return band > 0f ? (radius, 1f / band, 1f) : (0f, 0f, 0f);
	}

	/// <summary>The fraction of a copy drawn <paramref name="elapsedSeconds"/> after it stood.</summary>
	public static float ArrivalFactor(double elapsedSeconds) =>
		(float)Math.Clamp(elapsedSeconds / ArrivalSeconds, 0.0, 1.0);

	private static readonly StringName FadeIn = "xr_tree_fade_in";
	private static readonly StringName FadeOut = "xr_tree_fade_out";
	private static readonly StringName Centre = "xr_tree_centre";
	private static readonly StringName Alpha = "xr_fade_alpha";

	/// <summary>
	/// States the radius a node's copies dither to, or clears it with zero. A batch measures each
	/// copy at its origin, the point its cell's spread was taken about, so the cull at radius plus
	/// spread never removes a copy the dither still shows; a single measures at its box centre,
	/// the point Godot culls it by.
	/// </summary>
	public static void SetRadius(GeometryInstance3D node, Mesh mesh, float radius)
	{
		if (Refii.Models.Engine_ClassicMaterial.Active)
		{
			return;
		}

		(float end, float inverseLength, float power) = OutBand(radius);
		WriteBand(node, new Vector4(end, inverseLength, power, 0f), Vector4.Zero,
			node is MultiMeshInstance3D ? Vector3.Zero : mesh.GetAabb().GetCenter());
	}

	/// <summary>
	/// A classic tree's stated band (<see cref="Engine_PropDrawRange.Apply"/>), written through the
	/// same store as a tier radius so its shadow twin copies it without a server read.
	/// </summary>
	public static void SetTreeBand(GeometryInstance3D node, Vector4 fadeIn, Vector4 fadeOut, Vector3 centre) =>
		WriteBand(node, fadeOut, fadeIn, centre);

	// ---- The store: every value the four uniforms are given, kept on this side -------------------

	/// <summary>The last band and arrival each node was given; see <see cref="Engine_PropFadeStore{TKey}"/>.</summary>
	internal static readonly Engine_PropFadeStore<GeometryInstance3D> Store = new();

	private static void WriteBand(GeometryInstance3D node, Vector4 fadeOut, Vector4 fadeIn, Vector3 centre)
	{
		node.SetInstanceShaderParameter(FadeOut, fadeOut);
		node.SetInstanceShaderParameter(FadeIn, fadeIn);
		node.SetInstanceShaderParameter(Centre, centre);
		Store.SetBand(node, fadeOut, fadeIn, centre);
	}

	private static void WriteAlpha(GeometryInstance3D node, float alpha)
	{
		node.SetInstanceShaderParameter(Alpha, alpha);
		Store.SetAlpha(node, alpha);
	}

	/// <summary>A node's four fade uniforms as the shader sees them, unset ones at the shader's default.</summary>
	public readonly record struct FadeState(Vector4 FadeOut, Vector4 FadeIn, Vector3 Centre, float Alpha)
	{
		/// <summary>The shaders' declared defaults: no band, standing (alpha 1).</summary>
		public static readonly FadeState Defaults = new(Vector4.Zero, Vector4.Zero, Vector3.Zero, 1f);

		/// <summary>What a node holds given what was written to it (nothing: the defaults).</summary>
		public static FadeState Of(bool known, in FadeSnapshot written) => !known
			? Defaults
			: new FadeState(
				written.HasBand ? written.FadeOut : Vector4.Zero,
				written.HasBand ? written.FadeIn : Vector4.Zero,
				written.HasBand ? written.Centre : Vector3.Zero,
				written.HasAlpha ? written.Alpha : 1f);
	}

	/// <summary>How <see cref="Copy"/> serves one body/caster pair.</summary>
	public enum CopyAction
	{
		/// <summary>Nothing was ever written to either: the caster already holds what the body does.</summary>
		Skip,

		/// <summary>The body's band (and arrival, if it has one) from the store; no server read.</summary>
		FromStore,

		/// <summary>A case the store cannot vouch for: read the server, as before.</summary>
		Fallback,
	}

	/// <summary>
	/// Chooses how to copy. The old copy read the body's four uniforms back from the server and
	/// wrote them to the caster, skipping when the band read back as nothing. Every writer of the
	/// four is in this file, so:
	/// <list type="bullet">
	/// <item>a body given a band copies it from the store, with its arrival when it has one;</item>
	/// <item>a body never given a band holds the defaults or nothing, and a caster never written
	/// holds the defaults too, so copying would change nothing: skip;</item>
	/// <item>any other mix (a caster already written, a body with an arrival but no band) is rare
	/// and reads the server exactly as before.</item>
	/// </list>
	/// </summary>
	public static CopyAction PlanCopy(bool fromKnown, in FadeSnapshot from, bool toKnown)
	{
		if (fromKnown && from.HasBand)
		{
			return CopyAction.FromStore;
		}

		if (!toKnown && !(fromKnown && from.HasAlpha))
		{
			return CopyAction.Skip;
		}

		return CopyAction.Fallback;
	}

	/// <summary>The caster's state after a <see cref="CopyAction.FromStore"/> copy.</summary>
	public static FadeState AfterStoreCopy(in FadeState toBefore, in FadeSnapshot from) =>
		new(from.FadeOut, from.FadeIn, from.Centre, from.HasAlpha ? from.Alpha : toBefore.Alpha);

	// ---- Counters, for the log and the A/B ----------------------------------------------------

	private static long _copies, _skipped, _fromStore, _fallbacks, _verified, _mismatches;
	private static int _mismatchLines;
	private const int MaxMismatchLines = 40;

	/// <summary>One line for the log: how every copy so far was served.</summary>
	public static string Summary() =>
		$"[prop-fade] copies {_copies}: from store {_fromStore}, skipped {_skipped}, server fallback {_fallbacks}" +
		(VerifyEnabled ? $"; verify: {_verified} checked, {_mismatches} mismatch(es)" : "; verify off");

	private static int _verify = -1;

	/// <summary>
	/// VERIFY MODE, for validation only: a file named <c>refii_fade_verify.flag</c> beside the
	/// executable. Every copy then also reads the server as the old code did, compares what each
	/// path would leave on the caster, and does what the OLD code does, so the run looks exactly
	/// like the build before the store; the log counts and names every disagreement.
	/// </summary>
	public static bool VerifyEnabled
	{
		get
		{
			if (_verify < 0)
			{
				bool on = false;
				try
				{
					string dir = System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? "";
					on = System.IO.File.Exists(System.IO.Path.Combine(dir, "refii_fade_verify.flag"));
				}
				catch (Exception)
				{
					on = false;
				}

				_verify = on ? 1 : 0;
				GD.Print($"[prop-fade] state store active; verify mode {(on ? "ON (old server reads kept, results compared)" : "off")}");
			}

			return _verify == 1;
		}
	}

	/// <summary>Gives a cast-only twin or proxy its body's band and arrival, so the shadow follows the copy.</summary>
	public static void Copy(GeometryInstance3D from, GeometryInstance3D to)
	{
		_copies++;
		bool fromKnown = Store.TryGet(from, out FadeSnapshot fromWritten);
		bool toKnown = Store.TryGet(to, out FadeSnapshot toWritten);
		CopyAction action = PlanCopy(fromKnown, fromWritten, toKnown);

		if (VerifyEnabled)
		{
			FadeState toBefore = FadeState.Of(toKnown, toWritten);
			FadeState old = CopyFromServer(from, to, toBefore);
			_verified++;
			FadeState mine = action switch
			{
				CopyAction.Skip => toBefore,
				CopyAction.FromStore => AfterStoreCopy(toBefore, fromWritten),
				_ => old,
			};
			if (mine != old)
			{
				_mismatches++;
				if (_mismatchLines++ < MaxMismatchLines)
				{
					GD.Print($"[prop-fade] VERIFY MISMATCH ({action}) {Describe(from)} -> {Describe(to)}: " +
						$"server {old} / store {mine}");
				}
			}

			return;
		}

		switch (action)
		{
			case CopyAction.Skip:
				_skipped++;
				return;
			case CopyAction.FromStore:
			{
				_fromStore++;
				to.SetInstanceShaderParameter(FadeOut, fromWritten.FadeOut);
				to.SetInstanceShaderParameter(FadeIn, fromWritten.FadeIn);
				to.SetInstanceShaderParameter(Centre, fromWritten.Centre);
				Store.SetBand(to, fromWritten.FadeOut, fromWritten.FadeIn, fromWritten.Centre);
				if (fromWritten.HasAlpha)
				{
					WriteAlpha(to, fromWritten.Alpha);
				}

				return;
			}
			default:
			{
				_fallbacks++;
				if (_fallbacks <= 10)
				{
					GD.Print($"[prop-fade] server fallback ({_fallbacks}): {Describe(from)} -> {Describe(to)}");
				}

				CopyFromServer(from, to, FadeState.Of(toKnown, toWritten));
				return;
			}
		}
	}

	/// <summary>
	/// The copy as it was: four synchronous reads of the body's uniforms, written to the caster.
	/// Returns what it leaves on the caster and records it in the store.
	/// </summary>
	private static FadeState CopyFromServer(GeometryInstance3D from, GeometryInstance3D to, FadeState toBefore)
	{
		Variant band = from.GetInstanceShaderParameter(FadeOut);
		if (band.VariantType == Variant.Type.Nil)
		{
			return toBefore;
		}

		to.SetInstanceShaderParameter(FadeOut, band);
		Variant fadeIn = from.GetInstanceShaderParameter(FadeIn);
		to.SetInstanceShaderParameter(FadeIn, fadeIn);
		Variant centre = from.GetInstanceShaderParameter(Centre);
		to.SetInstanceShaderParameter(Centre, centre);
		Variant alpha = from.GetInstanceShaderParameter(Alpha);
		if (alpha.VariantType != Variant.Type.Nil)
		{
			to.SetInstanceShaderParameter(Alpha, alpha);
		}

		// What the caster now holds. A nil read was written as nil, which Godot turns into the
		// shader's default; the defaults here are those.
		var after = new FadeState(
			band.VariantType == Variant.Type.Vector4 ? band.AsVector4() : Vector4.Zero,
			fadeIn.VariantType == Variant.Type.Vector4 ? fadeIn.AsVector4() : Vector4.Zero,
			centre.VariantType == Variant.Type.Vector3 ? centre.AsVector3() : Vector3.Zero,
			alpha.VariantType == Variant.Type.Float ? alpha.AsSingle() : toBefore.Alpha);
		Store.SetBand(to, after.FadeOut, after.FadeIn, after.Centre);
		if (alpha.VariantType == Variant.Type.Float)
		{
			Store.SetAlpha(to, after.Alpha);
		}

		return after;
	}

	private static string Describe(GeometryInstance3D node)
	{
		try
		{
			string shader = node.MaterialOverride is ShaderMaterial { Shader: { } s } ? s.ResourcePath : "-";
			Mesh? mesh = node switch
			{
				MeshInstance3D m => m.Mesh,
				MultiMeshInstance3D mm => mm.Multimesh?.Mesh,
				_ => null,
			};
			if (shader == "-" && mesh is not null && mesh.GetSurfaceCount() > 0 &&
				mesh.SurfaceGetMaterial(0) is ShaderMaterial { Shader: { } s0 })
			{
				shader = s0.ResourcePath;
			}

			return $"{node.GetClass()} '{node.Name}' [{shader}]";
		}
		catch (Exception)
		{
			return node.GetClass();
		}
	}

	private readonly record struct Arrival(GeometryInstance3D Node, GeometryInstance3D? Caster, long Since);

	private static readonly List<Arrival> Arrivals = new();

	private static ulong _tickedFrame = ulong.MaxValue;

	/// <summary>Whether any copy is still dithering in from its arrival.</summary>
	public static bool Fading => Arrivals.Count != 0;

	/// <summary>
	/// Starts a node that just stood dithering in, with the twin or proxy that casts for it. Called
	/// after the shadow hand-off, so the caster exists to be found.
	/// </summary>
	public static void BeginArrival(GeometryInstance3D node)
	{
		if (Refii.Models.Engine_ClassicMaterial.Active)
		{
			return;
		}

		GeometryInstance3D? caster = node switch
		{
			MultiMeshInstance3D batch => batch.GetNodeOrNull<GeometryInstance3D>(Engine_PropShadow.NodeName),
			MeshInstance3D single => Refii.Entities.Engine_ShadowProxy.ProxyOf(single),
			_ => null,
		};

		WriteAlpha(node, 0f);
		if (caster is not null)
		{
			WriteAlpha(caster, 0f);
		}
		Arrivals.Add(new Arrival(node, caster, System.Diagnostics.Stopwatch.GetTimestamp()));
	}

	/// <summary>Advances every arrival; once per frame however many streamers call it.</summary>
	public static void Tick()
	{
		ulong frame = Engine.GetProcessFrames();
		if (frame == _tickedFrame || Arrivals.Count == 0)
		{
			return;
		}

		_tickedFrame = frame;
		long now = System.Diagnostics.Stopwatch.GetTimestamp();
		for (int i = Arrivals.Count - 1; i >= 0; i--)
		{
			Arrival arrival = Arrivals[i];
			if (!GodotObject.IsInstanceValid(arrival.Node))
			{
				Arrivals.RemoveAt(i);
				continue;
			}

			float alpha = ArrivalFactor((now - arrival.Since) / (double)System.Diagnostics.Stopwatch.Frequency);
			WriteAlpha(arrival.Node, alpha);
			if (arrival.Caster is { } caster && GodotObject.IsInstanceValid(caster))
			{
				WriteAlpha(caster, alpha);
			}

			if (alpha >= 1f)
			{
				Arrivals.RemoveAt(i);
			}
		}
	}
}
