using System;
using System.Collections.Generic;
using Godot;

using Refii.Core;

using Refii.Text;

using Refii.Textures;

namespace Refii.Ui;

/// <summary>
/// Turns a <see cref="Engine_UiScreen"/> into a Godot node tree.
///
/// Fable 2's UI is a lit 3D scene — every screen carries a camera and lights — so the
/// output is <see cref="Node3D"/>-based rather than a Control tree. Building it through
/// Godot's own API (instead of writing <c>.tscn</c> text) means the engine does the
/// serialising: call <see cref="SaveAsScene"/> to get a real scene file out.
///
/// The presentation's <c>node → material → texture</c> chain is collapsed into a single
/// <see cref="MeshInstance3D"/>. That is safe because material and texture elements are
/// effectively properties, not structure: across all 52 shipped screens a texture is
/// always a leaf and a material has at most one child.
/// </summary>
public sealed class Engine_UiSceneBuilder
{
	/// <summary>
	/// Supplies a texture for a resolved asset path such as <c>art\gui\frames\frames_04.tex</c>.
	/// Return null and the quad still builds, just untextured — useful before a
	/// <c>.tex</c> decoder exists.
	/// </summary>
	public Func<string, Texture2D?>? TextureResolver;

	/// <summary>
	/// Supplies the bitmap font for a text element's point size. Return null and the text element
	/// still builds, carrying its placement metadata but drawing nothing.
	/// </summary>
	public Func<int, (UiFont Font, float Scale)?>? FontResolver;

	/// <summary>
	/// Authored rotations are RADIANS: every non-zero rotation in the shipped `.bgf` is a
	/// multiple of π/4, and the guest's transform build (`sub_821E52F8`) feeds the raw floats to
	/// `sin`/`cos` with no π/180 anywhere.
	/// </summary>

	/// <summary>Emit <see cref="BgfElementType.Behavior"/> elements as marker nodes.</summary>
	public bool IncludeBehaviors = true;

	/// <summary>
	/// Honour the slide baseline. On, a screen renders the state it was authored to
	/// show; off, it renders the authored SUPERSET — every stacked variant at once,
	/// which is useful for seeing what a screen contains but is not what the game shows.
	/// </summary>
	public bool ApplySlideVisibility = true;

	/// <summary>
	/// Keep slide-inactive subtrees in the tree — built, hidden, marked `slide_inactive` —
	/// instead of dropping them. For the pattern book, which is a catalogue: owned screens take
	/// their art from the file-only build, so a parked element must still have a bearer. The kept
	/// subtree is invisible, so a drawn consumer that ignores the flag renders identically.
	/// </summary>
	public bool KeepInactiveForCatalogue;

	/// <summary>
	/// Authored scales are relative to a 100-unit quad — it spans -50..+50 — not a unit
	/// one. The <c>pivot</c> attribute is in the same units, which is why the shipped
	/// values are exactly ±50: they anchor a slice to its own edge.
	///
	/// Solved from menubox's <c>top</c> band, whose three slices share scale.x = 0.256
	/// and sit at pos.x = 0, 12.80, 25.60 with pivot.x = +50, 0, -50. Requiring the left
	/// slice's right edge to meet the middle's left edge, for a quad spanning ±H:
	///
	///     (H - 50)(0.256) = -H(0.256) + 12.8   =>   0.512H = 25.6   =>   H = 50
	///
	/// giving extents [-25.6, 0], [0, 25.6], [25.6, 51.2] — exactly edge to edge.
	///
	/// Holds only with the pivot term in <see cref="BuildTransform"/>; a base of 50 without it
	/// only cancels the missing pivot offset for this band and fails for any pivot but ±50.
	/// </summary>
	public const float QuadBaseSize = 100f;

	private static readonly ArrayMesh SharedQuad = BuildQuadMesh(QuadBaseSize);

	/// <summary>
	/// The one shader every Gameface quad draws through, loaded once — a Shader is the program,
	/// a ShaderMaterial the parameter set, so per-material copies would each compile.
	/// </summary>
	private static readonly Shader QuadShader =
		GD.Load<Shader>("res://src/Engine/ui/ui_quad.gdshader");

	/// <summary>
	/// Builds the shared quad: U increases with +X, V downward from the top-left, normal toward
	/// +Z where <see cref="BuildTransform"/> puts the viewer. No mirror is baked in — the
	/// handedness belongs in the transform, once; compensating here fixes textures and leaves
	/// geometry mirrored.
	/// </summary>
	private static ArrayMesh BuildQuadMesh(float size)
	{
		float h = size * 0.5f;

		// Wound counter-clockwise as seen from +Z, so the front face is the viewer's.
		var vertices = new[]
		{
			new Vector3(-h, +h, 0f),
			new Vector3(-h, -h, 0f),
			new Vector3(+h, -h, 0f),
			new Vector3(+h, +h, 0f),
		};
		var uvs = new[]
		{
			new Vector2(0f, 0f),
			new Vector2(0f, 1f),
			new Vector2(1f, 1f),
			new Vector2(1f, 0f),
		};
		var normals = new[]
		{
			// Facing the viewer, so lighting behaves as authored.
			new Vector3(0f, 0f, 1f),
			new Vector3(0f, 0f, 1f),
			new Vector3(0f, 0f, 1f),
			new Vector3(0f, 0f, 1f),
		};
		var indices = new[] { 0, 1, 2, 0, 2, 3 };

		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = vertices;
		arrays[(int)Mesh.ArrayType.TexUV] = uvs;
		arrays[(int)Mesh.ArrayType.Normal] = normals;
		arrays[(int)Mesh.ArrayType.Index] = indices;

		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		return mesh;
	}

	public int QuadsBuilt { get; private set; }

	/// <summary>Quads whose atlas rectangle has no area — see <see cref="UiQuad.IsDegenerate"/>.</summary>
	public int DegenerateQuads { get; private set; }
	public int TexturesResolved { get; private set; }
	public int TextsBuilt { get; private set; }

	/// <summary>Glyph quads emitted. Zero with a non-zero <see cref="TextsBuilt"/> means no font resolved.</summary>
	public int TextGlyphsBuilt { get; private set; }

	/// <summary>Elements dropped by the slide baseline, subtrees included.</summary>
	public int SkippedInactive { get; private set; }

	/// <summary>
	/// The built node for each element ORDINAL, or null where the slide baseline dropped one.
	/// Everything the guest states arrives keyed by ordinal, so this is what lets a live screen
	/// be updated rather than rebuilt. Empty until <see cref="Build"/> has run.
	/// </summary>
	public Node3D?[] ByOrdinal { get; private set; } = System.Array.Empty<Node3D?>();

	/// <summary>Each element's pivot, in Godot space, for the live transform's origin term.</summary>
	public Vector3[] PivotByOrdinal { get; private set; } = System.Array.Empty<Vector3>();

	/// <summary>
	/// Writes a live X and/or Y POSITION onto a built element, pivot term included. The only
	/// route for outside writers (the widescreen layout): assigning `transform.Origin` directly
	/// drops the pivot term and displaces the element by `basis * pivot`.
	/// </summary>
	public bool SetLivePosition(int ordinal, float? x, float? y)
	{
		if (ordinal < 0 || ordinal >= ByOrdinal.Length || ByOrdinal[ordinal] is not { } node)
		{
			return false;
		}

		Vector3 position = PositionByOrdinal[ordinal];
		if (x is { } px) position.X = px;
		if (y is { } py) position.Y = py;

		Transform3D before = node.Transform;
		var after = new Transform3D(before.Basis, position - before.Basis * PivotByOrdinal[ordinal]);
		PositionByOrdinal[ordinal] = position;

		if (after == before)
		{
			return false;
		}

		node.Transform = after;
		return true;
	}

	/// <summary>
	/// A node's authored pivot, in Godot space — the meta <see cref="Build"/> stamps and
	/// <c>Duplicate()</c> carries onto a clone. Zero when absent.
	/// </summary>
	public static Vector3 PivotOf(Node3D node) =>
		node.HasMeta(PivotMeta) ? node.GetMeta(PivotMeta).AsVector3() : Vector3.Zero;

	/// <summary>Static: read per animated element per frame, and a literal allocates a StringName.</summary>
	private static readonly StringName PivotMeta = "pivot";

	// Shader uniforms and metadata keys written on per-frame paths (opacity chains, tints, UV
	// scrubs, clip bands): one StringName each, never a literal — a literal is a fresh StringName
	// per call, and every one is a weak handle the next GC frees inside its pause.
	private static readonly StringName OpacityName = "opacity";
	private static readonly StringName ColourName = "colour";
	private static readonly StringName ClipRectName = "clip_rect";
	private static readonly StringName DiffuseTexName = "diffuse_tex";
	private static readonly StringName TintRgbName = "tint_rgb";
	private static readonly StringName TintAmountName = "tint_amount";
	private static readonly StringName Uv1OffsetName = "uv1_offset";
	private static readonly StringName Uv1ScaleName = "uv1_scale";
	private static readonly StringName DetailMappedName = "detail_mapped";
	private static readonly StringName AtlasName = "atlas";
	private static readonly StringName OpacityLiveMeta = "opacity_live";
	private static readonly StringName LiveAccumMeta = "live_accum";
	private static readonly StringName OpacityAccumulatedMeta = "opacity_accumulated";
	private static readonly StringName LookAtPointMeta = "lookatpoint";
	private static readonly StringName AuthoredAlphaMetaName = AuthoredAlphaMeta;

	/// <summary>
	/// Writes a live SCALE onto a built or cloned node, keeping the position it states.
	/// </summary>
	/// <remarks>
	/// THE ORIGIN DEPENDS ON THE SCALE. <see cref="BuildTransform"/> folds the pivot in as
	/// <c>origin = position - basis * pivot</c> and the basis carries the scale, so assigning
	/// <c>Scale</c> alone leaves the term computed for the old one and displaces the element by
	/// <c>(basisOld - basisNew) * pivot</c>. Under a rotated element the displacement is
	/// sideways: `messageboxmulti`'s `paperbox` is <c>pivot = (0, -26)</c> and every size slide
	/// turns it a quarter turn, giving <c>26 * deltaScale</c> of horizontal drift.
	///
	/// <see cref="PositionByOrdinal"/> is the built path's answer to this. A clone has no such
	/// table, so the position is recovered from the node — sound only while the origin still
	/// agrees with its basis, which holds iff every scale write comes through here.
	/// </remarks>
	public static void SetLiveScale(Node3D node, Vector3 scale)
	{
		Vector3 pivot = PivotOf(node);
		Vector3 stated = node.Position + (node.Transform.Basis * pivot);
		node.Scale = scale;
		node.Position = stated - (node.Transform.Basis * pivot);
	}

	/// <summary>
	/// Writes a live rotation.z onto a built or cloned node, turning it about its authored pivot.
	/// The same origin contract as <see cref="SetLiveScale"/>: the basis carries the rotation, so
	/// assigning <c>Rotation</c> alone turns the element about its own middle instead.
	/// </summary>
	public static void SetLiveRotationZ(Node3D node, float radians)
	{
		Vector3 pivot = PivotOf(node);
		Vector3 stated = node.Position + (node.Transform.Basis * pivot);
		node.Rotation = node.Rotation with { Z = radians };
		node.Position = stated - (node.Transform.Basis * pivot);
	}

	/// <summary>
	/// Each element's POSITION, in Godot space — not its node origin, which has the pivot term
	/// folded in and cannot be separated once a scale has changed underneath it.
	/// </summary>
	public Vector3[] PositionByOrdinal { get; private set; } = System.Array.Empty<Vector3>();

	public Node3D Build(Engine_UiScreen screen)
	{
		QuadsBuilt = TexturesResolved = TextsBuilt = DegenerateQuads = TextGlyphsBuilt = 0;
		MasksApplied = 0;
		MasksSkipped = 0;
		_clipped.Clear();
		_screen = screen;
		_inheritedOpacity = 1f;
		ByOrdinal = new Node3D?[screen.Nodes.Length];
		PivotByOrdinal = new Vector3[screen.Nodes.Length];
		PositionByOrdinal = new Vector3[screen.Nodes.Length];

		var root = new Node3D { Name = SanitizeName(screen.Name) };
		root.SetMeta("canvas_width", screen.CanvasWidth);
		root.SetMeta("canvas_height", screen.CanvasHeight);
		if (screen.ContractNames.Count > 0)
			root.SetMeta("contract", new Godot.Collections.Array(
				screen.ContractNames.ConvertAll(n => Variant.From(n))));

		foreach (UiNode n in screen.Roots)
		{
			Node3D? child = BuildNode(n);
			if (child is not null)
				root.AddChild(child);
		}
		return root;
	}

	/// <summary>
	/// Writes the guest's live transforms onto an already-built tree and reports how many
	/// elements moved. Animation such as `sub_82990730`'s `Foreground.Book` advance and the
	/// ambient presentation's `"Scrolling"` element arrives as attribute writes, not slides or
	/// visibility changes, so this is the only path that picks them up short of a rebuild.
	///
	/// <paramref name="transform"/> is keyed by ordinal, with a null row where the guest states
	/// nothing for that element, in which case the file's own transform stands.
	/// </summary>
	/// <summary>The unit vector for a basis column index, for a basis with nothing left to keep.</summary>
	private static Vector3 UnitAxis(int axis) => axis switch
	{
		0 => new Vector3(1f, 0f, 0f),
		1 => new Vector3(0f, 1f, 0f),
		_ => new Vector3(0f, 0f, 1f),
	};

	/// <summary>Meta name for a host scale factor a node keeps through every live transform.</summary>
	public const string RemasterScaleMeta = "remaster_scale";

	/// <summary>Scales a node by a factor the live transform will keep applying.</summary>
	public static void RemasterScale(Node3D node, float factor)
	{
		node.SetMeta(RemasterScaleMeta, factor);
		node.Scale *= factor;
	}

	public int ApplyLiveTransform(System.Collections.Generic.IReadOnlyList<float?[]?> transform)
	{
		int moved = 0;

		for (int i = 0; i < ByOrdinal.Length && i < transform.Count; ++i)
		{
			if (ByOrdinal[i] is not { } node || transform[i] is not { } values)
			{
				continue;
			}

			Transform3D before = node.Transform;

			// The position, not the origin: `BuildTransform` stores
			// `origin = position - basis * pivot`.
			Vector3 position = PositionByOrdinal[i];
			Vector3 pivot = PivotByOrdinal[i];

			if (values.Length > 0 && values[0] is { } x) position.X = x;
			if (values.Length > 1 && values[1] is { } y) position.Y = y;

			// Through the handedness flip, as BuildTransform builds it: Fable authors UI
			// left-handed (+Z away), so a raw z inverts depth.
			if (values.Length > 2 && values[2] is { } z) position.Z = ToGodotZ(z);

			// Scale through the basis COLUMNS, never through `node.Scale`: `Basis.Scale` is
			// derived and lies on a singular basis — one zero column makes the determinant zero,
			// so it reports (0,0,0) for all three axes and a read-modify-write flattens the two
			// axes the guest did not state. Every meter bar in `hud.bgf` is authored
			// `scale.x = 0`, so this is live here.
			//
			// A zero column has no direction to scale along, so rebuild the axis from the cross
			// product of the other two, keeping the basis right-handed.
			Basis basis = before.Basis;
			bool scaled = false;
			for (int axis = 0; axis < 3; ++axis)
			{
				if (values.Length <= 3 + axis || values[3 + axis] is not { } length)
				{
					continue;
				}

				Vector3 column = basis[axis];
				if (column.LengthSquared() <= 0.000001f)
				{
					column = basis[(axis + 1) % 3].Cross(basis[(axis + 2) % 3]);
				}

				basis[axis] = column.LengthSquared() > 0.000001f
					? column.Normalized() * length
					: UnitAxis(axis) * length;
				scaled = true;
			}

			// A REMASTER SCALE RIDES OVER THE GUEST'S: a node marked `remaster_scale` keeps
			// that factor however the guest restates its columns (the menu box's option labels,
			// authored at seven pixels).
			if (scaled && node.HasMeta(RemasterScaleMeta))
			{
				float factor = (float)node.GetMeta(RemasterScaleMeta);
				basis = basis.Scaled(new Vector3(factor, factor, factor));
			}

			// Rebuilt from the CURRENT basis — a live scale changes the pivot term too.
			Vector3 origin = position - basis * pivot;

			var after = new Transform3D(basis, origin);
			if (after == before)
			{
				continue;
			}

			// Every number here is a raw guest word — `LiveAttribute.AsFloat` bit-casts the
			// attribute slot, so an unset, freed or mid-write slot is a NaN, and the
			// `LengthSquared() <= 1e-6f` tests above admit it (every NaN comparison is false).
			// A NaN reaching the render-list sort walks libstdc++ off the array into an endless
			// fault; keeping the previous transform costs one stale frame on one element.
			if (!origin.IsFinite() || !basis.X.IsFinite() ||
				!basis.Y.IsFinite() || !basis.Z.IsFinite())
			{
				++NonFiniteLiveTransforms;
				continue;
			}

			node.Transform = after;

			// The merged position, not the authored one: the guest stops stating an axis once it
			// settles, and the authored value would snap it back.
			PositionByOrdinal[i] = position;
			++moved;
		}

		return moved;
	}

	/// <summary>
	/// Applies stated opacity changes to the BUILT tree, without a rebuild — a stated opacity is
	/// a material parameter, so it is applied as one.
	///
	/// Opacity is a chain (see <see cref="_inheritedOpacity"/>): a change on one element
	/// re-derives its whole subtree's accumulated product. The chain state lives in metas —
	/// `opacity` (authored own), `opacity_live` (stated own, written here), `live_accum` (the
	/// product as applied) — and ordinals are document order, parents first.
	///
	/// Returns how many elements' stated value CHANGED, not how many nodes were retinted.
	/// </summary>
	public int ApplyLiveOpacity(System.Collections.Generic.IReadOnlyList<float?> opacity)
	{
		int changed = 0;
		for (int i = 0; i < ByOrdinal.Length && i < opacity.Count; ++i)
		{
			if (ByOrdinal[i] is not { } node || opacity[i] is not { } stated ||
				!float.IsFinite(stated))
			{
				continue;
			}

			float own = Mathf.Clamp(stated / 100f, 0f, 1f);
			float applied = node.HasMeta(OpacityLiveMeta)
				? (float)node.GetMeta(OpacityLiveMeta)
				: node.HasMeta(OpacityName) ? (float)node.GetMeta(OpacityName) : 1f;
			if (Mathf.IsEqualApprox(own, applied))
			{
				continue;
			}

			node.SetMeta(OpacityLiveMeta, own);
			ReapplyOpacityChain(node, ParentAccumulated(node));
			++changed;
		}

		return changed;
	}

	/// <summary>
	/// States one element's OWN opacity on a built or cloned tree and re-derives its subtree —
	/// the single-node form of <see cref="ApplyLiveOpacity"/>, for an owned surface standing on
	/// a slide other than the one its art was built from.
	/// </summary>
	public static void RestateOpacity(Node node, float own)
	{
		node.SetMeta(OpacityLiveMeta, Mathf.Clamp(own, 0f, 1f));
		ReapplyOpacityChain(node, ParentAccumulated(node));
	}

	/// <summary>The accumulated opacity ABOVE a node — live where a live value has been applied.</summary>
	private static float ParentAccumulated(Node node) =>
		node.GetParent() is { } parent
			? (parent.HasMeta(LiveAccumMeta)
				? (float)parent.GetMeta(LiveAccumMeta)
				: parent.HasMeta(OpacityAccumulatedMeta)
					? (float)parent.GetMeta(OpacityAccumulatedMeta)
					: 1f)
			: 1f;

	/// <summary>Recomputes one subtree's accumulated products and writes them to the materials.</summary>
	private static void ReapplyOpacityChain(Node node, float parentAccumulated)
	{
		float own = node.HasMeta(OpacityLiveMeta)
			? (float)node.GetMeta(OpacityLiveMeta)
			: node.HasMeta(OpacityName) ? (float)node.GetMeta(OpacityName) : 1f;
		float accumulated = parentAccumulated * own;
		node.SetMeta(LiveAccumMeta, accumulated);

		if (Engine_UiTheme.SetIconOpacity(node, accumulated))
		{
			return;
		}

		if (node is MeshInstance3D mesh && mesh.MaterialOverride is ShaderMaterial material)
		{
			if (material.Shader == QuadShader)
			{
				material.SetShaderParameter(OpacityName, accumulated);
			}
			else if (material.Shader == TextShader)
			{
				Color colour = material.GetShaderParameter(ColourName).AsColor();
				material.SetShaderParameter(
					ColourName, new Color(colour.R, colour.G, colour.B, accumulated));
			}
		}

		for (int i = 0, count = node.GetChildCount(); i < count; ++i)
		{
			ReapplyOpacityChain(node.GetChild(i), accumulated);
		}
	}

	/// <summary>
	/// Opacity accumulated from the root down to the element being built, 0..1.
	///
	/// Opacity is a chain, not a per-element value: <c>sub_8229E250</c> multiplies the parent
	/// frame's accumulated value by this element's own <c>opacity/100</c> and stores the product
	/// on the render association, so an element at 100 under a group at 0 draws at 0. That is how
	/// <c>hud.lua</c>'s <c>Enable</c>/<c>Disable</c> hides the whole HUD with one
	/// <c>setAttribute(getElement('Scene.Layer'), 'opacity', 100 | 0)</c>.
	///
	/// Not a visibility gate: the traversal <c>sub_822849A0</c> never reads opacity, so at 0 the
	/// subtree is still walked, still reports <c>globalactive</c> true and is still submitted.
	/// Collapsing it to <c>Visible = false</c> gives the right pixels and the wrong element state.
	/// </summary>
	private float _inheritedOpacity = 1f;

	private Node3D? BuildNode(UiNode n)
	{
		// Material and texture elements carry parameters folded into the parent's quad rather
		// than emitted — but they are not always leaves: the nine-slice dialogs author real
		// geometry inside them (`querybox`'s and the title dialog's `paperA/B/C` groups and
		// `middle` quads under `Material574/590`). A childless material stays un-emitted; one
		// with children becomes a bare pass-through node so its subtree builds.
		if (n.Type is BgfElementType.Material or BgfElementType.Texture)
		{
			if (n.Children.Count == 0)
				return null;

			var passthrough = new Node3D { Name = SanitizeName(n.Name) };
			passthrough.SetMeta("element_type", "Material");
			foreach (UiNode materialChild in n.Children)
			{
				Node3D? builtChild = BuildNode(materialChild);
				if (builtChild is not null)
					passthrough.AddChild(builtChild);
			}

			if (passthrough.GetChildCount() != 0)
				return passthrough;

			// Never parented, so nothing else frees it: an orphan at exit otherwise.
			passthrough.Free();
			return null;
		}

		if (n.Type == BgfElementType.Behavior && !IncludeBehaviors)
			return null;

		// An inactive element takes its whole subtree with it: sub_8229E400 tests the cached
		// globalactive byte and branches straight to the next SIBLING.
		bool parked = ApplySlideVisibility && !n.Visible;
		if (parked && !KeepInactiveForCatalogue)
		{
			SkippedInactive++;
			return null;
		}

		// This element's own opacity folded into the chain, BEFORE the node is created —
		// CreateNodeFor builds the quad and needs the accumulated alpha, not the local one.
		float parentOpacity = _inheritedOpacity;
		_inheritedOpacity = parentOpacity * (n.Opacity / 100f);

		Node3D node = CreateNodeFor(n);
		node.Name = SanitizeName(n.Name);
		node.Transform = BuildTransform(n);

		if (n.Ordinal >= 0 && n.Ordinal < ByOrdinal.Length)
		{
			ByOrdinal[n.Ordinal] = node;

			// The two terms `ApplyLiveTransform` rebuilds the origin from: an origin is
			// `position - basis * pivot`, so it needs both, for stated axes and unstated.
			PivotByOrdinal[n.Ordinal] = new Vector3(n.PivotX, n.PivotY, ToGodotZ(n.PivotZ));
			PositionByOrdinal[n.Ordinal] =
				new Vector3(n.PositionX, n.PositionY, ToGodotZ(n.PositionZ));
		}

		node.SetMeta("object_id", n.ObjectId);
		node.SetMeta("ordinal", n.Ordinal);

		// A clone inherits its pivot and nothing restates it: `framecloning.dis main.1/main.2`
		// call clone(source, self.parent) and set nothing, and the placement pass writes only
		// position.x/y, opacity, scale.x/y (`framesizeandpositioning.dis main.6` constant table).
		// Duplicate() carries this meta across.
		node.SetMeta("pivot", new Vector3(n.PivotX, n.PivotY, ToGodotZ(n.PivotZ)));

		node.SetMeta("element_type", n.Type.ToString());
		if (!n.HasAuthoredName)
			node.SetMeta("name_synthesised", true);
		if (parked)
		{
			// Present for the catalogue, absent from the frame — see KeepInactiveForCatalogue.
			node.SetMeta("slide_inactive", true);
			node.Visible = false;
		}
		if (n.Opacity < 100f)
			node.SetMeta(OpacityName, n.Opacity / 100f);
		// The accumulated value, separately: a consumer must tell "this element is transparent"
		// from "something above it is".
		if (_inheritedOpacity < 1f)
			node.SetMeta(OpacityAccumulatedMeta, _inheritedOpacity);
		// What was inherited FROM ABOVE — the one of the three that cannot be derived from the
		// other two, since dividing the accumulated by the group's own value fails exactly when
		// the group sits at zero. A host re-running a group opacity write needs it.
		if (parentOpacity < 1f)
			node.SetMeta("opacity_inherited", parentOpacity);

		foreach (UiNode child in n.Children)
		{
			Node3D? built = BuildNode(child);
			if (built is not null)
				node.AddChild(built);
		}

		// Restore on the way back up, or later siblings inherit the child's transparency.
		_inheritedOpacity = parentOpacity;
		return node;
	}

	private Node3D CreateNodeFor(UiNode n)
	{
		// A mask publishes a rect and draws nothing — `sub_8286E748` returns at 0x8286E784
		// before emitting the quad. The node is still built: RefreshClips reads its transform.
		if (_screen is { } screen && n.Ordinal >= 0 &&
			n.Ordinal < screen.MaskByOrdinal.Length && screen.MaskElements.Contains(n.Ordinal))
		{
			++MasksSkipped;
			return new Node3D();
		}

		switch (n.Type)
		{
			case BgfElementType.Camera:
			{
				var cam = new Camera3D();
				if (n.Element.TryGetAttribute("fov", out var fov) && fov.Type == BgfValueType.Float)
					cam.Fov = fov.AsFloat();
				if (n.Element.TryGetAttribute("clipnear", out var cn) && cn.Type == BgfValueType.Float && cn.AsFloat() > 0f)
					cam.Near = cn.AsFloat();
				if (n.Element.TryGetAttribute("clipfar", out var cf) && cf.Type == BgfValueType.Float && cf.AsFloat() > 0f)
					cam.Far = cf.AsFloat();
				if (n.Element.TryGetAttribute("orthographic", out var ortho) && ortho.AsBool())
					cam.Projection = Camera3D.ProjectionType.Orthogonal;

				// Cameras are aimed by an explicit look-at point, not by their own rotation.
				if (TryReadVector(n, "lookatpoint", out Vector3 target))
					cam.SetMeta(LookAtPointMeta, new Vector3(target.X, target.Y, ToGodotZ(target.Z)));

				cam.Current = false;
				return cam;
			}

			case BgfElementType.Light:
			{
				// Every shipped light is near-white and local in extent; omni is the closest
				// match without a decoded light-type attribute.
				var light = new OmniLight3D();
				light.LightColor = ReadColour(n, "lightdiffuse", Colors.White);
				if (n.Element.TryGetAttribute("brightness", out var b) && b.Type == BgfValueType.Float)
					light.LightEnergy = b.AsFloat();
				return light;
			}

			case BgfElementType.Text:
				return BuildTextNode(n);

			default:
				return n.Quad is not null ? BuildQuad(n) : new Node3D();
		}
	}

	private Node3D BuildTextNode(UiNode n)
	{
		if (n.Text is null)
			return new Node3D();

		UiText text = n.Text;
		TextsBuilt++;

		// A MeshInstance3D even when nothing draws, so the metadata always hangs off the same
		// node type.
		var node = new MeshInstance3D();
		node.SetMeta("text", text.Display);
		node.SetMeta("text_tag", text.Tag);
		node.SetMeta("text_localised", text.Localised);
		node.SetMeta("text_size", text.Size);
		node.SetMeta("text_halign", text.HorzAlign);
		node.SetMeta("text_valign", text.VertAlign);
		node.SetMeta("text_wrap", text.WrapMode);
		node.SetMeta("text_box", new Vector2(text.BoxWidth, text.BoxHeight));
		node.SetMeta("text_colour", new Color(text.R, text.G, text.B));

		if (FontResolver?.Invoke(text.Size) is not { } selection)
			return node;

		(UiFont font, float scale) = selection;
		Engine_UiTextMesh.Built built = Engine_UiTextMesh.Build(text, font.Font, scale);
		int lines = built.LineCount;

		// Icons first: a string can carry them with no glyphs at all (a lone `<gold_img>` is a
		// legitimate label), so an early return on a null mesh would drop them.
		foreach (Engine_UiTextMesh.IconPlacement icon in built.Icons)
		{
			if (TextureResolver?.Invoke(icon.Tag.Texture + ".tex") is not { } art)
				continue;

			var quad = Engine_UiTheme.MarkInlineIcon(new MeshInstance3D
			{
				Mesh = Engine_UiTheme.IconQuad(icon.Size),
				Position = new Vector3(icon.TopLeft.X, icon.TopLeft.Y, 0f),
				MaterialOverride = Engine_UiTheme.IconMaterial(art, _inheritedOpacity),
			});
			node.AddChild(quad);
		}

		ArrayMesh? mesh = built.Mesh;
		if (mesh is null)
			return node;

		node.Mesh = mesh;
		// The authored alpha is overwritten with 1.0 by the engine — a .bgf's text colour is
		// always opaque. The opacity CHAIN is a separate value and does apply, so a label under
		// a group faded to 0 fades with it.
		var textMaterial =
			TextMaterial(new Color(text.R, text.G, text.B, _inheritedOpacity), font.Atlas);
		ApplyClip(textMaterial, n);
		node.MaterialOverride = textMaterial;

		node.SetMeta("text_lines", lines);
		TextGlyphsBuilt += mesh.SurfaceGetArrayLen(0) / 4;
		return node;
	}

	/// <summary>
	/// The material every piece of UI text is drawn with, `.bgf` or native. Unshaded, because a
	/// glyph atlas is emissive art and shading it against the screen's lights leaves the text
	/// black; alpha SCISSOR rather than blending, because the atlas is padded with transparent
	/// black and the bilinear tail of the neighbouring cell shows as a halo otherwise.
	///
	/// <paramref name="colour"/> carries its own alpha; the scene builder passes 1.
	/// </summary>
	/// <summary>Live UI transforms refused as not finite. Run total.</summary>
	public static int NonFiniteLiveTransforms;

	public static ShaderMaterial TextMaterial(Color colour, Texture2D? atlas)
	{
		var material = new ShaderMaterial { Shader = TextShader };
		material.SetShaderParameter(ColourName, colour);
		if (atlas is not null)
		{
			material.SetShaderParameter(AtlasName, atlas);
			material.SetShaderParameter("supersample", Engine_BitmapFontTexture.SupersampleOf(atlas));
		}

		return material;
	}

	/// <summary>Whether a material draws through the UI quad or text shader (the clipped ones).</summary>
	public static bool IsUiShader(Shader? shader) => shader == QuadShader || shader == TextShader;

	/// <summary>The text shader, loaded once — see <see cref="QuadShader"/> for why it is shared.</summary>
	private static readonly Shader TextShader =
		GD.Load<Shader>("res://src/Engine/ui/ui_text.gdshader");

	/// <summary>U11: whether a mesh is a built text label (has the label material).</summary>
	public static bool IsLabel(MeshInstance3D mesh) => LabelMaterial(mesh) is not null;

	/// <summary>A built label's colour, or white when the node is not one.</summary>
	public static Color TextColour(MeshInstance3D label) =>
		LabelMaterial(label) is { } m ? m.GetShaderParameter(ColourName).AsColor() : Colors.White;

	/// <summary>Recolours a built label. Silently does nothing if it has no material.</summary>
	public static bool SetTextColour(MeshInstance3D label, Color colour)
	{
		if (LabelMaterial(label) is not { } m)
		{
			return false;
		}

		m.SetShaderParameter(ColourName, colour);
		Engine_UiTheme.FadeInlineIcons(label, colour.A);
		return true;
	}

	private MeshInstance3D BuildQuad(UiNode n)
	{
		UiQuad q = n.Quad!;
		QuadsBuilt++;
		if (q.IsDegenerate)
			DegenerateQuads++;

		var mesh = new MeshInstance3D { Mesh = SharedQuad };

		// One shader for every quad, as the guest has: a single `guiquad` with a `g_DetailMapped`
		// branch. See ui_quad.gdshader for the contract.
		var mat = new ShaderMaterial { Shader = QuadShader };

		// The accumulated alpha: BuildNode has already folded n.Opacity into _inheritedOpacity.
		mat.SetShaderParameter(OpacityName, _inheritedOpacity);
		mat.SetShaderParameter("quad_base_size", QuadBaseSize);

		// Colour and WEIGHT kept apart: the weight is 0 on most records, so most quads are untinted.
		mat.SetShaderParameter(TintRgbName, new Vector3(q.R, q.G, q.B));
		mat.SetShaderParameter(TintAmountName, q.TintAmount);

		// The atlas rectangle rides on the material rather than custom mesh UVs, so
		// every quad can share one QuadMesh.
		mat.SetShaderParameter(Uv1ScaleName, new Vector2(q.UMax - q.UMin, q.VMax - q.VMin));
		mat.SetShaderParameter(Uv1OffsetName, new Vector2(q.UMin, q.VMin));

		// A controller button cut from the shared atlas carries its keycap, for the keyboard.
		if (Engine_UiKeyGlyphs.AtlasButton(q.TexturePath, n, q.UMin, q.VMax) is { } button)
		{
			mat.SetShaderParameter("keycap_tex", Engine_UiKeyGlyphs.KeycapFor(button));
			mat.SetShaderParameter("has_keycap", true);
		}

		ApplyDetailMap(mat, q, n);
		ApplyClip(mat, n);

		if (q.TexturePath.Length > 0)
		{
			Texture2D? tex = TextureResolver?.Invoke(q.TexturePath);
			if (tex is not null)
			{
				mat.SetShaderParameter(DiffuseTexName, tex);
				TexturesResolved++;

				// Inset half a texel off every edge. Frame art is CELLS of a shared atlas
				// (`frames_elements.tex`, `frames_04.tex`) selected by a sub-rect, so a bilinear
				// tap on a cell boundary reaches into the neighbouring cell and no wrap mode
				// helps — the sampler never leaves the texture. Skipped for a degenerate rect.
				//
				// Sized at build time: an enhanced texture replaces its image in place, so a
				// material built at 512 over-insets by half an original texel when sampling 1024,
				// which crops rather than bleeds. Layout is unaffected — the quad is the shared
				// mesh scaled by the node transform.
				float du = 0.5f / Mathf.Max(tex.GetWidth(), 1);
				float dv = 0.5f / Mathf.Max(tex.GetHeight(), 1);

				// Per axis: a nine-slice's edge cells are zero-extent on ONE axis only —
				// `UpperMiddle/Rim` is authored u[0.5..0.5] v[0.25..0.5], a single atlas column
				// stretched along the top edge.
				(float uOffset, float uScale) = InsetAxis(q.UMin, q.UMax, du);
				(float vOffset, float vScale) = InsetAxis(q.VMin, q.VMax, dv);

				mat.SetShaderParameter(Uv1OffsetName, new Vector2(uOffset, vOffset));
				mat.SetShaderParameter(Uv1ScaleName, new Vector2(uScale, vScale));
			}
			mesh.SetMeta("texture_path", q.TexturePath);
		}
		mesh.SetMeta("texture_hash", q.TextureHash);
		if (q.IsDegenerate)
			mesh.SetMeta("degenerate_uv", true);

		mesh.MaterialOverride = mat;
		return mesh;
	}

	/// <summary>
	/// A built quad's material, or null when the node is not one.
	/// </summary>
	private static ShaderMaterial? QuadMaterial(MeshInstance3D mesh) =>
		mesh.MaterialOverride is ShaderMaterial m && m.Shader == QuadShader ? m : null;

	/// <summary>
	/// A built label's material, or null when the node is not one. The SHADER discriminates —
	/// text and quads are both ShaderMaterial with different uniforms.
	/// </summary>
	private static ShaderMaterial? LabelMaterial(MeshInstance3D mesh) =>
		mesh.MaterialOverride is ShaderMaterial m && m.Shader == TextShader ? m : null;

	/// <summary>Gives a cloned quad a private material, so writing it cannot leak.</summary>
	public static bool OwnQuadMaterial(MeshInstance3D mesh)
	{
		if (QuadMaterial(mesh) is not { } shared)
		{
			return false;
		}

		// The Shader itself is not duplicated: the copy carries the parameter set and keeps
		// pointing at the one compiled program.
		mesh.MaterialOverride = CopyMaterial(shared);
		return true;
	}

	private static readonly System.Collections.Generic.Dictionary<Shader, StringName[]> UniformNames = new();

	/// <summary>
	/// A private copy of a shader material: the same parameter set <c>Resource.Duplicate</c>
	/// copies, without its cost. Duplicate walks the material's property list, and building
	/// that list asks the render thread for the shader's uniforms — a synchronous round trip
	/// with the separate render thread, paid once per quad (measured U01: 420 list quads 85 ms
	/// in game, a one-quad popup up to 47 ms). The uniform names are fixed per compiled shader,
	/// so they are read once and kept; the values come from the material's own cache.
	/// </summary>
	public static ShaderMaterial CopyMaterial(ShaderMaterial shared)
	{
		Shader? shader = shared.Shader;
		if (shader is null)
		{
			return (ShaderMaterial)shared.Duplicate();
		}

		StringName[]? names;
		lock (UniformNames)
		{
			if (!UniformNames.TryGetValue(shader, out names))
			{
				var list = new System.Collections.Generic.List<StringName>();
				foreach (Godot.Collections.Dictionary uniform in shader.GetShaderUniformList())
				{
					list.Add((string)uniform["name"]);
				}

				names = list.ToArray();
				UniformNames[shader] = names;
			}
		}

		var copy = new ShaderMaterial
		{
			Shader = shader,
			RenderPriority = shared.RenderPriority,
			NextPass = shared.NextPass,
		};
		foreach (StringName name in names)
		{
			Variant value = shared.GetShaderParameter(name);
			if (value.VariantType != Variant.Type.Nil)
			{
				copy.SetShaderParameter(name, value);
			}
		}

		return copy;
	}

	/// <summary>Replaces a built quad's primary texture. Used where the guest names art we clone.</summary>
	public static bool SetQuadTexture(MeshInstance3D mesh, Texture2D texture)
	{
		if (QuadMaterial(mesh) is not { } material)
		{
			return false;
		}

		material.SetShaderParameter(DiffuseTexName, texture);
		return true;
	}

	/// <summary>
	/// What a built mesh will actually paint — the one reader that knows every material kind.
	/// </summary>
	/// <param name="Painted">False when the mesh cannot draw at all — no material, or fully transparent.</param>
	/// <param name="Texture">The primary texture, or null for an untextured quad.</param>
	/// <param name="Opacity">Accumulated opacity.</param>
	/// <param name="Tint">The authored tint colour. Only acts at <paramref name="TintAmount"/>.</param>
	/// <param name="TintAmount">The subtractive weight. Zero means the tint does nothing.</param>
	/// <param name="DetailMapped">True when the RGB comes from a detail map, not from <paramref name="Texture"/>.</param>
	public readonly record struct QuadPaint(
		bool Painted, Texture2D? Texture, float Opacity, Color Tint, float TintAmount,
		bool DetailMapped);

	/// <summary>Reads what a mesh paints, whichever kind of material it carries.</summary>
	public static QuadPaint DescribeQuad(MeshInstance3D mesh)
	{
		if (QuadMaterial(mesh) is { } shader)
		{
			float opacity = shader.GetShaderParameter(OpacityName).AsSingle();
			Vector3 tint = shader.GetShaderParameter(TintRgbName).AsVector3();
			return new QuadPaint(
				opacity > 0.01f,
				shader.GetShaderParameter(DiffuseTexName).As<Texture2D>(),
				opacity,
				new Color(tint.X, tint.Y, tint.Z),
				shader.GetShaderParameter(TintAmountName).AsSingle(),
				shader.GetShaderParameter(DetailMappedName).AsBool());
		}

		// A label carries its colour and coverage rather than a tint and an atlas rect.
		if (LabelMaterial(mesh) is { } label)
		{
			Color c = label.GetShaderParameter(ColourName).AsColor();
			return new QuadPaint(
				c.A > 0.01f, label.GetShaderParameter(AtlasName).As<Texture2D>(), c.A, c, 1f, false);
		}

		// Anything a host-authored screen built with a plain material of its own.
		if ((mesh.MaterialOverride ?? mesh.GetActiveMaterial(0)) is BaseMaterial3D basic)
		{
			Color albedo = basic.AlbedoColor;
			return new QuadPaint(
				albedo.A > 0.01f, basic.AlbedoTexture, albedo.A, albedo, 1f, false);
		}

		return new QuadPaint(false, null, 0f, Colors.White, 0f, false);
	}

	/// <summary>A built quad's accumulated opacity, or 1 when it is not one of ours.</summary>
	/// <summary>Where a cloned quad's authored alpha is kept, so fading never compounds.</summary>
	public const string AuthoredAlphaMeta = "authored_alpha";

	/// <summary>
	/// Gives every mesh in a cloned subtree a private material and records its authored alpha, so
	/// a later <see cref="FadeSubtree"/> is relative to the artist's value and cannot restyle the
	/// pattern book the clone came from.
	/// </summary>
	public static void OwnSubtreeMaterials(Node node)
	{
		if (node is MeshInstance3D mesh && OwnQuadMaterial(mesh))
		{
			mesh.SetMeta(AuthoredAlphaMetaName, QuadOpacity(mesh));
		}

		for (int i = 0, count = node.GetChildCount(); i < count; ++i)
		{
			OwnSubtreeMaterials(node.GetChild(i));
		}
	}

	/// <summary>Scales a whole subtree's art by an opacity, from each quad's authored alpha.
	/// Meshes without the recorded alpha are skipped — they were never owned.</summary>
	public static void FadeSubtree(Node node, float opacity)
	{
		if (node is MeshInstance3D mesh && mesh.HasMeta(AuthoredAlphaMetaName))
		{
			SetQuadOpacity(mesh, (float)mesh.GetMeta(AuthoredAlphaMetaName) * opacity);
		}

		for (int i = 0, count = node.GetChildCount(); i < count; ++i)
		{
			FadeSubtree(node.GetChild(i), opacity);
		}
	}

	public static float QuadOpacity(MeshInstance3D mesh) =>
		QuadMaterial(mesh)?.GetShaderParameter(OpacityName).AsSingle() ?? 1f;

	/// <summary>
	/// Writes a built quad's tint — the pair `SetTintingAmountAndDiffuseColour` writes. Both
	/// together: the subtract is `rgb -= (1 - tint) * amount`.
	/// </summary>
	public static bool SetQuadTint(MeshInstance3D mesh, Color colour, float amount)
	{
		if (QuadMaterial(mesh) is not { } material)
		{
			return false;
		}

		material.SetShaderParameter(TintRgbName, new Vector3(colour.R, colour.G, colour.B));
		material.SetShaderParameter(TintAmountName, amount);
		return true;
	}

	/// <summary>Scales a built quad's opacity.</summary>
	public static bool SetQuadOpacity(MeshInstance3D mesh, float opacity)
	{
		if (QuadMaterial(mesh) is not { } material)
		{
			return false;
		}

		material.SetShaderParameter(OpacityName, opacity);
		return true;
	}

	/// <summary>
	/// The detail map, keyed by the <c>.fac</c> record's secondary-texture slot. The slot is an
	/// index into a theme table, not a texture name: `sub_8286F578` reads `[[0x83496B08+48]+36]`
	/// as a row selector into a 3-theme by 3-slot table, and row 0 ships. Any slot but 1/2/3
	/// takes the default, as the guest does.
	/// </summary>
	private static string DetailMapPath(int slot) => slot switch
	{
		1 => @"art\gui\frames\buttondetailmap.tex",
		2 => @"art\gui\frames\frames_01.tex",
		3 => @"art\gui\persistant\frame_tiled_background.tex",
		_ => @"art\gui\frames\framedetailmap.tex",
	};

	/// <summary>
	/// Points a quad's material at its detail map, when the record asks for one. Mode 4 is the
	/// only value that acts — `if (v17 == 4)` at 0x8286E4B4; 0/1/2/3 fall through. That branch's
	/// write of `buttondetailmap`'s hash to `+0x20C` is a dead store overwritten by
	/// `sub_8286F578`, so the slot alone chooses the texture.
	/// </summary>
	private void ApplyDetailMap(ShaderMaterial mat, UiQuad q, UiNode n)
	{
		const int DetailMapped = 4;
		if (q.Source.DetailMapMode != DetailMapped)
		{
			mat.SetShaderParameter(DetailMappedName, false);
			return;
		}

		string path = DetailMapPath(q.Source.SecondaryTexture);
		if (!_detailMaps.TryGetValue(path, out Texture2D? detail))
		{
			detail = TextureResolver?.Invoke(path);
			_detailMaps[path] = detail;
		}

		if (detail is null)
		{
			// Without one the primary's RGB stands, which for these quads is flat magenta.
			mat.SetShaderParameter(DetailMappedName, false);
			++DetailMapsMissing;
			return;
		}

		mat.SetShaderParameter(DetailMappedName, true);
		mat.SetShaderParameter("detail_tex", detail);

		// Deterministic where the guest is random (`sub_8286F830` uses two
		// LHMath_GFFloatRandom(1.0)); hashing the ordinal keeps a rebuild pixel-identical.
		uint h = (uint)n.Ordinal * 2654435761u;
		mat.SetShaderParameter("detail_origin", new Vector2(
			(h & 0xFFFF) / 65535f, ((h >> 16) & 0xFFFF) / 65535f));

		++DetailQuads;
	}

	private Engine_UiScreen? _screen;

	private readonly List<(ShaderMaterial Material, int Mask)> _clipped = new();

	/// <summary>
	/// Recomputes every clip rect from the mask nodes' BUILT transforms, so a mask the guest
	/// moves clips where it now is. Call after the live transform. One pass over the clipped
	/// materials, not the element tree.
	/// </summary>
	public void RefreshClips()
	{
		foreach ((ShaderMaterial material, int mask) in _clipped)
		{
			if (mask < 0 || mask >= ByOrdinal.Length || ByOrdinal[mask] is not { } node ||
				!node.IsInsideTree())
			{
				continue;
			}

			Transform3D world = Engine_UiSeats.LayoutTransform(node);
			float halfW = 0.5f * QuadBaseSize * world.Basis.X.Length();
			float halfH = 0.5f * QuadBaseSize * world.Basis.Y.Length();

			material.SetShaderParameter(ClipRectName, new Vector4(
				world.Origin.X - halfW, world.Origin.Y - halfH,
				world.Origin.X + halfW, world.Origin.Y + halfH));
		}
	}

	/// <summary>Quads clipped to a mask's rect. Zero on a screen that authors none.</summary>
	public int MasksApplied;

	/// <summary>Mask elements built as bare nodes rather than drawn. One per mask.</summary>
	public int MasksSkipped;

	/// <summary>
	/// Clips a quad to its mask's rect. The binding is by NAME, not hierarchy, so this reads
	/// <see cref="Engine_UiScreen.MaskByOrdinal"/> rather than walking the tree. The region is
	/// the mask's own quad, `position ± 50·scale`; its texture plays no part.
	/// </summary>
	private void ApplyClip(ShaderMaterial mat, UiNode n)
	{
		if (_screen is not { } screen || n.Ordinal < 0 ||
			n.Ordinal >= screen.MaskByOrdinal.Length)
		{
			return;
		}

		int mask = screen.MaskByOrdinal[n.Ordinal];
		if (mask < 0 || mask >= screen.Nodes.Length)
		{
			return;
		}

		// Centre and half-extent, from the slide-resolved values BuildTransform uses.
		UiNode m = screen.Nodes[mask];
		float halfW = 0.5f * QuadBaseSize * Mathf.Abs(m.ScaleX);
		float halfH = 0.5f * QuadBaseSize * Mathf.Abs(m.ScaleY);
		float cx = m.PositionX;
		float cy = m.PositionY;

		for (UiNode? up = m.Parent; up is not null; up = up.Parent)
		{
			cx = cx * up.ScaleX + up.PositionX;
			cy = cy * up.ScaleY + up.PositionY;
			halfW *= Mathf.Abs(up.ScaleX);
			halfH *= Mathf.Abs(up.ScaleY);
		}

		mat.SetShaderParameter(ClipRectName,
			new Vector4(cx - halfW, cy - halfH, cx + halfW, cy + halfH));

		// Remembered so RefreshClips can recompute when the guest moves a mask: a live
		// transform arrives per frame without a rebuild.
		_clipped.Add((mat, mask));
		++MasksApplied;
	}

	private readonly Dictionary<string, Texture2D?> _detailMaps = new();

	/// <summary>Quads drawn through a detail map — the frames, the wheels, the beams.</summary>
	public int DetailQuads;

	/// <summary>Quads that asked for a detail map and did not get one. Should be zero.</summary>
	public int DetailMapsMissing;

	/// <summary>
	/// Insets one axis of an atlas rect to texel centres, returning its offset and scale. A rect
	/// wider than a texel is pulled in half a texel each side, signed so a flipped rect
	/// (max &lt; min) insets inward. A zero-extent rect (a nine-slice stretch strip) keeps scale
	/// 0 and moves to the centre of the texel at <paramref name="min"/>. Anything narrower than
	/// a texel is left alone.
	/// </summary>
	private static (float Offset, float Scale) InsetAxis(float min, float max, float half)
	{
		float span = max - min;

		if (Mathf.Abs(span) > 2f * half)
		{
			float step = Mathf.Sign(span) * half;
			return (min + step, span - (2f * step));
		}

		return span == 0f ? (min + half, 0f) : (min, span);
	}

	private Transform3D BuildTransform(UiNode n)
	{
		var basis = Basis.Identity;
		if (n.RotationX != 0f || n.RotationY != 0f || n.RotationZ != 0f)
		{
			float sx = n.RotationX, sy = n.RotationY, sz = n.RotationZ;
			// Handedness, half one: conjugating a rotation by diag(1,1,-1) negates the
			// angles about X and Y and leaves the one about Z alone.
			basis = Basis.FromEuler(new Vector3(-sx, -sy, sz));
		}
		basis = basis.Scaled(new Vector3(n.ScaleX, n.ScaleY, n.ScaleZ));

		// The engine's local transform is ((p - pivot) * scale) * R + position — sub_8229E4B8
		// seeds the matrix translation with -(scale * pivot) BEFORE concatenating the rotation,
		// then adds position. In column-vector form T(position) * R * S * T(-pivot), so the
		// origin carries an extra -(R*S)*pivot term; dropping it displaces by pivot*scale.
		var pivot = new Vector3(n.PivotX, n.PivotY, ToGodotZ(n.PivotZ));
		var origin = new Vector3(n.PositionX, n.PositionY, ToGodotZ(n.PositionZ)) - basis * pivot;
		return new Transform3D(basis, origin);
	}

	/// <summary>
	/// Fable authors UI left-handed (X right, Y up, +Z away from the viewer); Godot is
	/// right-handed, so negate Z — not X, which renders mirrored. Applied per node rather than
	/// as one flip on the root, so no transform ends with a negative determinant.
	///
	/// Public because the clone path needs the same flip: an owned book writes the guest's
	/// stated transforms onto cloned nodes and must convert depth exactly as the build did.
	/// </summary>
	public static float ToGodotZ(float fableZ) => -fableZ;

	/// <summary>Reads an <c>x/y/z</c> attribute triple, if all three are present.</summary>
	public static bool TryReadVector(UiNode n, string prefix, out Vector3 value)
	{
		value = Vector3.Zero;
		if (!n.Element.TryGetAttribute(prefix + ".x", out var ax) || ax.Type != BgfValueType.Float)
			return false;
		if (!n.Element.TryGetAttribute(prefix + ".y", out var ay) || ay.Type != BgfValueType.Float)
			return false;
		if (!n.Element.TryGetAttribute(prefix + ".z", out var az) || az.Type != BgfValueType.Float)
			return false;
		value = new Vector3(ax.AsFloat(), ay.AsFloat(), az.AsFloat());
		return true;
	}

	/// <summary>
	/// Aims every camera in a built tree at its authored look-at point. Call after the
	/// tree is in the scene, since <see cref="Node3D.LookAt"/> needs global transforms.
	/// </summary>
	public static void ApplyCameraAiming(Node root)
	{
		// Not while detached: the aim comes from GLOBAL transforms and a detached node's is the
		// identity. `BuildDetached` calls this before attaching; the attaching caller calls it
		// again.
		if (root is Camera3D cam && cam.HasMeta(LookAtPointMeta) && cam.IsInsideTree())
		{
			// The look-at point is authored in the camera's parent space, so it rides through
			// the parent's global transform, handedness flip included.
			var local = (Vector3)cam.GetMeta(LookAtPointMeta);
			Vector3 target = cam.GetParent() is Node3D parent
				? parent.GlobalTransform * local
				: local;

			if (!cam.GlobalPosition.IsEqualApprox(target))
				cam.LookAt(target, Vector3.Up);
		}
		for (int i = 0, count = root.GetChildCount(); i < count; ++i)
			ApplyCameraAiming(root.GetChild(i));
	}

	/// <summary>
	/// Makes one camera current — every camera built from screen data starts off. Returns the
	/// camera activated; when the screen ships none, a framing camera is added under
	/// <paramref name="root"/>.
	/// </summary>
	public static Camera3D ActivateCamera(Node3D root, bool preferFraming = false)
	{
		if (preferFraming)
		{
			return AddFramingCamera(root);
		}

		return AddCanvasCamera(root);
	}

	/// <summary>
	/// The reference height a Gameface screen is RENDERED against — a fixed 720, not the
	/// presentation's own authored canvas (960x720 on 40 shipped presentations, 1280x720 on 8,
	/// 800x450 on 4; all scale-mode 1 "Fixed", so the aspect-fit branch at 0x82BB89B0 is dead).
	///
	/// The render scale divides by a literal 720: sub_822271A0 stores screenHeight * (1/720)
	/// (flt_82000BD4) into both axis scales before the draw (0x822272DC / 0x822272E4), and
	/// sub_826D6878 computes the same ratio at 0x826D697C-0x826D699C. A screen authored at
	/// 800x450 occupies 450 of the same 720 units, which is what makes it an overlay.
	/// </summary>
	public const float CanvasHeight = 720f;

	/// <summary>
	/// The guest ignores the authored camera: sub_82226DE0 hands the layer's camera and fov to
	/// SetCamera at vtable+0x20, which for the Gameface renderer (vtable 0x82006CC0) is
	/// 0x82B96E78 — <c>nullsub_1</c>, a single <c>blr</c>. clipnear/clipfar (0x834BE554 /
	/// 0x834BE558) have no readers at all.
	///
	/// Instead it scales uniformly by HEIGHT: sub_822271A0 stores screenHeight * (1/720) into
	/// both axis scales (+0xB0, +0xAC) before the draw, overwriting the constructor's
	/// aspect-corrected X. No letterbox, pillarbox or stretch — 720 units fill the viewport
	/// height and a wider window sees more. An orthographic camera of size 720 with
	/// KeepAspect.Height is that contract, with no perspective foreshortening by position.z.
	/// </summary>
	/// <summary>
	/// The widescreen design box the shipped screens paint into. The <c>.bgf</c> canvas field
	/// says 960x720, but the 16:9 art reaches ±640 (`pausemainmenu`'s backdrop is 1280 wide, its
	/// outermost frames end at ±636). Some screens overscan beyond it (`giftgivingscreen`'s
	/// backdrop is 1472), which is what makes an ultra-wide window safe.
	/// </summary>
	public const float DesignWidth = 1280f;

	/// <summary>
	/// The canvas scale. The console's is UI camera desc B (`sub_821A5F20`): fovY 10.2° at
	/// y -4000 over a 1280x720 backbuffer, one unit = 360 / (4000 · tan 5.1°) = 1.00843 px, a
	/// visible 16:9 canvas of 1269.3 units — and the in-game books are authored -636..+633, so
	/// even at the console's scale the narrower right cover stops 1.7 px short of the edge,
	/// hidden there by overscan. The remaster has no overscan: the scale is set so the +633
	/// side meets the edge exactly (640 / 633), which crops 3 px of leather on the left.
	/// </summary>
	public const float PixelsPerUnit = 640f / 633f;

	/// <summary>
	/// Sizes a canvas camera to the console's scale: <see cref="CanvasHeight"/> units of height
	/// at <see cref="PixelsPerUnit"/>, so the width the display shows follows its aspect. A
	/// deliberate step past the guest's `sub_822271A0`, which scales both axes from screen
	/// height alone and loses the sides on a narrow window; identical at 16:9.
	/// </summary>
	public static void FitCanvasCamera(Camera3D camera, Vector2 viewport)
	{
		if (viewport.X <= 0f || viewport.Y <= 0f)
		{
			return;
		}

		// Height is the anchor until the display is narrower than the authored 4:3 box. Both
		// layouts are 720 tall (in-game 960x720, front end 1280x720, widescreen markers moving
		// in-game furniture out to 1280 at 16:9), so pinning height gives 960 units at 4:3,
		// 1280 at 16:9, 1680 at 21:9. Below 4:3 pin the WIDTH instead, or authored content is
		// cropped; the slack goes vertical.
		if (viewport.X / viewport.Y >= MinimumWidth / CanvasHeight)
		{
			camera.KeepAspect = Camera3D.KeepAspectEnum.Height;
			camera.Size = CanvasHeight / PixelsPerUnit;
			VisibleHalfWidth = camera.Size * (viewport.X / viewport.Y) / 2f;
		}
		else
		{
			camera.KeepAspect = Camera3D.KeepAspectEnum.Width;
			camera.Size = MinimumWidth / PixelsPerUnit;
			VisibleHalfWidth = camera.Size / 2f;
		}
	}

	/// <summary>
	/// Half of the canvas width the fitted camera shows, in canvas units: 633 at 16:9, more on
	/// a wider window. A page whose art is authored to a 1280 box and has no backdrop (the
	/// co-op screen's frames stop at +630 / −672) anchors its outer pieces to this edge.
	/// </summary>
	public static float VisibleHalfWidth { get; private set; } = DesignWidth / 2f / PixelsPerUnit;

	/// <summary>Half of the 16:9 canvas, in canvas units — where the books' covers end.</summary>
	public const float DesignHalfWidth = DesignWidth / 2f / PixelsPerUnit;

	/// <summary>
	/// How far each half of a two-page book rides outward on a window wider than 16:9, in
	/// canvas units; 0 at 16:9 and narrower. The books are authored to the 16:9 edges, so a
	/// wider window opens the book — left half to the left edge, right half to the right —
	/// over the backdrop, rather than leaving it centred with bare sides.
	/// </summary>
	public static float WideShift => Mathf.Max(0f, VisibleHalfWidth - DesignHalfWidth);

	/// <summary>
	/// The narrowest canvas the game authors — 960x720, the 4:3 layout 31 of the 36 shipped
	/// presentations are built on (the other five are the 1280-wide front end and co-op screens).
	/// Below this aspect the camera pins width instead of revealing more.
	/// </summary>
	public const float MinimumWidth = 960f;

	private static Camera3D AddCanvasCamera(Node3D root)
	{
		var camera = new Camera3D
		{
			Name = "CanvasCamera",
			Projection = Camera3D.ProjectionType.Orthogonal,
			Size = CanvasHeight,
			KeepAspect = Camera3D.KeepAspectEnum.Height,
			Near = 0.05f,
			Far = 100_000f,
			Current = true,

			// Orthographic, so this sets clipping only and not scale. Far enough back that
			// no authored position.z falls behind the near plane.
			Position = new Vector3(0f, 0f, 10_000f),
		};

		root.AddChild(camera);

		// Sized from the viewport, so a non-16:9 window is right on the first frame.
		if (root.IsInsideTree())
		{
			FitCanvasCamera(camera, root.GetViewport().GetVisibleRect().Size);
		}

		return camera;
	}

	/// <summary>
	/// Adds an orthographic camera that frames every quad in the tree — orthographic because a
	/// UI screen is authored flat.
	/// </summary>
	private static Camera3D AddFramingCamera(Node3D root)
	{
		Aabb bounds = ComputeBounds(root);
		var camera = new Camera3D
		{
			Name = "FramingCamera",
			Projection = Camera3D.ProjectionType.Orthogonal,
			Near = 0.05f,
			Far = 100_000f,
			Current = true,
		};
		root.AddChild(camera);

		if (bounds.Size == Vector3.Zero)
		{
			camera.Size = 1000f;
			camera.GlobalPosition = new Vector3(0f, 0f, 1000f);
		}
		else
		{
			Vector3 centre = bounds.GetCenter();
			// After the handedness conversion the viewer is on the +Z side, which is
			// where the authored cameras land too.
			camera.Size = Mathf.Max(bounds.Size.X, bounds.Size.Y) * 1.05f;
			camera.GlobalPosition = new Vector3(centre.X, centre.Y, centre.Z + Mathf.Max(bounds.Size.Z, 1f) + 1000f);
		}

		camera.LookAt(bounds.Size == Vector3.Zero ? Vector3.Zero : bounds.GetCenter(), Vector3.Up);
		return camera;
	}

	/// <summary>Union of every visual's world-space AABB, or a zero box when the tree has none.</summary>
	private static Aabb ComputeBounds(Node root)
	{
		Aabb acc = default;
		bool seeded = false;
		Accumulate(root, ref acc, ref seeded);
		return acc;

		static void Accumulate(Node node, ref Aabb acc, ref bool seeded)
		{
			if (node is VisualInstance3D visual)
			{
				Aabb world = visual.GlobalTransform * visual.GetAabb();
				acc = seeded ? acc.Merge(world) : world;
				seeded = true;
			}
			for (int i = 0, count = node.GetChildCount(); i < count; ++i)
				Accumulate(node.GetChild(i), ref acc, ref seeded);
		}
	}

	private static Color ReadColour(UiNode n, string prefix, Color fallback)
	{
		bool any = false;
		float Read(string suffix, float d)
		{
			if (n.Element.TryGetAttribute(prefix + suffix, out var a) && a.Type == BgfValueType.Float)
			{
				any = true;
				// Authored colour channels are 0..255.
				return a.AsFloat() / 255f;
			}
			return d;
		}
		float r = Read(".r", fallback.R);
		float g = Read(".g", fallback.G);
		float b = Read(".b", fallback.B);
		return any ? new Color(r, g, b) : fallback;
	}

	/// <summary>
	/// The node name a document element is built under — Godot node names cannot contain
	/// <c>. : @ / " %</c> and authored names do. Public because a caller joining a built subtree
	/// back to the document must key on the same string.
	/// </summary>
	public static string NodeNameFor(string name) => SanitizeName(name);

	private static string SanitizeName(string name)
	{
		if (name.Length == 0)
			return "Node";

		Span<char> buf = stackalloc char[name.Length];
		for (int i = 0; i < name.Length; i++)
		{
			char c = name[i];
			buf[i] = c is '.' or ':' or '@' or '/' or '"' or '%' ? '_' : c;
		}
		return new string(buf);
	}

	/// <summary>
	/// Packs a built tree into a scene file; Godot does the serialising.
	/// </summary>
	public static Error SaveAsScene(Node3D root, string path)
	{
		// PackedScene only keeps descendants whose owner is the root.
		SetOwnerRecursive(root, root);
		var packed = new PackedScene();
		Error err = packed.Pack(root);
		return err != Error.Ok ? err : ResourceSaver.Save(packed, path);
	}

	private static void SetOwnerRecursive(Node node, Node owner)
	{
		foreach (Node child in node.GetChildren())
		{
			child.Owner = owner;
			SetOwnerRecursive(child, owner);
		}
	}
}
