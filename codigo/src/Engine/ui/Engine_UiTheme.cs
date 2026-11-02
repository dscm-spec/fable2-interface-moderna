using System.Collections.Generic;
using Godot;

using Refii.Text;

namespace Refii.Ui;

/// <summary>
/// The shipped game's look, in a form a screen we AUTHORED can draw with: the fonts the game
/// ships, the strings from its own book, and art cloned out of a real presentation by name — so
/// a custom screen (one the original never had) is new layout over old pixels.
///
/// Nothing here is extracted: the catalogue duplicates nodes of a presentation already standing
/// in the scene tree, so no derived asset ever reaches disk.
///
/// Not a widget toolkit — no button class, no layout container, no focus manager. This holds
/// the RESOURCES; the screen holds the design.
/// </summary>
public sealed class Engine_UiTheme
{
	// Text re-paints carry the fade and clip band across to the fresh material on every string
	// change: one StringName each, never a literal (a literal is a weak handle per call).
	private static readonly StringName ClipRectName = "clip_rect";
	private static readonly StringName OpacityName = "opacity";

	/// <summary>The bitmap fonts. Without them <see cref="Label"/> produces nothing.</summary>
	public Engine_UiFontLibrary? Fonts { get; init; }

	/// <summary>The localised string database a text tag resolves through.</summary>
	public Engine_BabelDatabase? Strings { get; set; }

	/// <summary>
	/// The canvas a sourced screen was authored on — the space a custom screen places itself in.
	/// Read off the presentation in <see cref="Catalogue"/>, never assumed: it is 1280x720 on the
	/// front end but the value belongs to the file.
	/// </summary>
	public Vector2 Canvas { get; private set; } = new(1280f, 720f);

	/// <summary>The presentation the art was catalogued from, for diagnostics. Empty until then.</summary>
	public string ArtSource { get; private set; } = string.Empty;

	private readonly Dictionary<string, List<Node3D>> _art = new();

	/// <summary>How many distinct element names the catalogue holds.</summary>
	public int ArtCount => _art.Count;

	/// <summary>Every catalogued name, for a tool that wants to list what is available.</summary>
	public IReadOnlyCollection<string> ArtNames => _art.Keys;

	/// <summary>
	/// Walks a built presentation and records every named element as clonable art.
	///
	/// Names repeat (the title screen has 77 elements over 47 names), so a name is not a key:
	/// each keeps the full list in depth-first order and <see cref="Art"/> takes an index.
	/// Synthesised names (<c>Type_Ordinal</c>) are skipped — only an authored name is stable.
	/// </summary>
	public void Catalogue(Node presentation, string sourceName = "")
	{
		_art.Clear();
		ArtSource = sourceName;

		if (presentation.HasMeta("canvas_width") && presentation.HasMeta("canvas_height"))
		{
			Canvas = new Vector2(
				(float)presentation.GetMeta("canvas_width"),
				(float)presentation.GetMeta("canvas_height"));
		}

		// Two phases, and the order is a contract: slide-parked subtrees (kept by
		// `Engine_UiSceneBuilder.KeepInactiveForCatalogue`) are catalogued AFTER everything
		// else, so every pre-existing name keeps its variant index — templates address art by
		// occurrence (`Money`'s money bar is bearer two on the logbook). A parked element whose
		// name already has bearers becomes a later variant; one with none becomes variant 0.
		Walk(presentation, parked: false);
		foreach (Node subtree in _parkedRoots)
		{
			Walk(subtree, parked: true);
		}

		_parkedRoots.Clear();
	}

	private readonly List<Node> _parkedRoots = new();

	/// <summary>
	/// Forgets the catalogue when the presentation it was taken from is freed — every entry is a
	/// node inside that tree. Clones already taken out are unaffected.
	/// </summary>
	public void Clear()
	{
		_art.Clear();
		ArtSource = string.Empty;
	}

	private void Walk(Node node, bool parked = false)
	{
		// Phase one PRUNES at a parked subtree and remembers it; phase two walks only those.
		if (!parked && node is Node3D held && held.HasMeta("slide_inactive"))
		{
			_parkedRoots.Add(node);
			return;
		}

		if (node is Node3D element && !element.HasMeta("name_synthesised"))
		{
			string name = element.Name.ToString();
			if (name.Length > 0)
			{
				if (!_art.TryGetValue(name, out List<Node3D>? matches))
				{
					matches = new List<Node3D>();
					_art[name] = matches;
				}

				matches.Add(element);
			}
		}

		foreach (Node child in node.GetChildren())
		{
			Walk(child, parked);
		}
	}

	/// <summary>
	/// How many elements in the catalogue carry a given name — 0 means this presentation does
	/// not carry that art; above 1 means <see cref="Art"/> is picking one of several.
	/// </summary>
	public int ArtVariants(string name) =>
		_art.TryGetValue(name, out List<Node3D>? matches) ? matches.Count : 0;

	/// <summary>
	/// A fresh CLONE of a catalogued element, ready to parent into a custom screen — a clone
	/// rather than the node, which is still live inside the presentation it came from.
	///
	/// The clone's transform is zeroed; callers wanting the authored position read it back off
	/// the source node. Null when the name is not catalogued, which is not a failure.
	/// </summary>
	/// <param name="keepAuthoredBasis">
	/// Keep the artist's own scale and rotation instead of resetting them. The default discards
	/// both, which suits a caller that places the clone against constants tuned to a unit-scaled
	/// piece; a caller whose layout states only a POSITION (the rebuilt HUD) needs them, or a
	/// bar authored `scale.y 0.24` draws as a bare 100-unit quad.
	///
	/// The whole BASIS is carried rather than the scale: `.Scale` off a singular basis answers
	/// (0,0,0) on every axis (see <see cref="Engine_UiBasisScale"/>). Position is zeroed either way.
	/// </param>
	/// <summary>
	/// The art for a NAMED element by its document ORDINAL — the variant whose source node
	/// carries that `object_id`, or null when none does. A name is not an identity in hud.bgf:
	/// `Spell1..5` are rows of both the will assigner and the spell charger.
	/// </summary>
	public Node3D? ArtForOrdinal(string name, int ordinal, bool keepAuthoredBasis = false)
	{
		if (!_art.TryGetValue(name, out List<Node3D>? matches))
		{
			return null;
		}

		// By the element's ORDINAL, which the builder stamps; `object_id` is the file's object id
		// and never equalled an ordinal, so this lookup fell back to the first bearer of the name.
		for (int variant = 0; variant < matches.Count; ++variant)
		{
			if (matches[variant].HasMeta("ordinal") &&
				(int)matches[variant].GetMeta("ordinal") == ordinal)
			{
				return Art(name, variant, keepAuthoredBasis);
			}
		}

		return null;
	}

	public Node3D? Art(string name, int variant = 0, bool keepAuthoredBasis = false)
	{
		if (!_art.TryGetValue(name, out List<Node3D>? matches) ||
			variant < 0 || variant >= matches.Count)
		{
			return null;
		}

		var clone = (Node3D)matches[variant].Duplicate();
		if (keepAuthoredBasis)
		{
			clone.Transform = new Transform3D(matches[variant].Transform.Basis, Vector3.Zero);
		}
		else
		{
			clone.Position = Vector3.Zero;
			clone.Rotation = Vector3.Zero;
			clone.Scale = Vector3.One;
		}

		clone.Visible = true;
		OwnMaterials(clone);
		return clone;
	}

	/// <summary>
	/// Gives a fresh clone its OWN materials: `Duplicate()` copies nodes but SHARES
	/// sub-resources, so without this a shader-parameter write reaches every clone of that art
	/// and the pattern book it came from.
	/// </summary>
	public static void OwnMaterials(Node node)
	{
		if (node is MeshInstance3D mesh && mesh.MaterialOverride is ShaderMaterial shared)
		{
			mesh.MaterialOverride = Engine_UiSceneBuilder.CopyMaterial(shared);
		}

		foreach (Node child in node.GetChildren())
		{
			OwnMaterials(child);
		}
	}

	/// <summary>
	/// Re-meshes a named text element in a cloned subtree with new display text, keeping the
	/// authored layout the scene builder stamped as metadata (size, alignment, wrap, box,
	/// colour). The one path every owned dialog's text goes through. Returns a short description.
	/// </summary>
	public string SetMeshText(Node3D? root, string name, string display)
	{
		if (root is null || FindByName(root, name) is not MeshInstance3D node)
		{
			return $"'{name}' NOT FOUND";
		}

		return SetMeshText(node, display);
	}

	/// <summary>
	/// Keeps a cloned text node's AUTHORED glyphs where the string to show is the one the file
	/// authored, replacing only the material — the part of <see cref="SetMeshText"/> that
	/// differs from what the scene builder stamped (the builder folds the authored opacity into
	/// the colour's alpha; the live path draws alpha 1 and lets the binding's `opacity` carry
	/// it). The mesh is bit-identical by construction: the builder laid the same string out
	/// through <see cref="Engine_UiTextMesh.Build"/> with the same font, size, box, wrap and
	/// alignment the metadata here restates, and <see cref="Layout"/> only rebuilds that.
	///
	/// Why: an owned book's first sample re-meshed EVERY field — 94 on the world map — to show
	/// the caption it already showed, a glyph layout and an ArrayMesh each, on the frame the
	/// book opened.
	///
	/// False when identity is not guaranteed — no mesh, a string carrying tags (an inline icon
	/// is a child quad the builder may or may not have resolved), icons already standing — and
	/// the caller re-meshes as before.
	/// </summary>
	public bool KeepAuthoredText(MeshInstance3D node, string display)
	{
		if (node.Mesh is null || display.Length == 0 || display.IndexOf('<') >= 0)
		{
			return false;
		}

		foreach (Node child in node.GetChildren())
		{
			if (IsInlineIcon(child))
			{
				return false;
			}
		}

		int size = node.HasMeta("text_size") ? (int)node.GetMeta("text_size") : 18;
		if (Fonts?.For(size) is not { } selection)
		{
			return false;
		}

		Color colour = node.HasMeta("text_colour") ? (Color)node.GetMeta("text_colour") : Colors.White;
		var opaque = new Color(colour.R, colour.G, colour.B, 1f);

		Variant? clip = null;
		Variant? opacity = null;
		if (node.MaterialOverride is ShaderMaterial previous)
		{
			clip = previous.GetShaderParameter(ClipRectName);
			opacity = previous.GetShaderParameter(OpacityName);
		}

		node.MaterialOverride = Engine_UiSceneBuilder.TextMaterial(opaque, selection.Font.Atlas);
		if (node.MaterialOverride is ShaderMaterial fresh)
		{
			if (clip is { } c && c.VariantType != Variant.Type.Nil)
			{
				fresh.SetShaderParameter(ClipRectName, c);
			}

			if (opacity is { } o && o.VariantType != Variant.Type.Nil)
			{
				fresh.SetShaderParameter(OpacityName, o);
			}
		}

		return true;
	}

	/// <summary>The node-in-hand form, for a caller that resolved the element itself —
	/// names repeat (the query box has five elements named "Text"), so a by-name find from
	/// the wrong root re-meshes somebody else's label.</summary>
	public string SetMeshText(MeshInstance3D node, string display)
	{
		int size = node.HasMeta("text_size") ? (int)node.GetMeta("text_size") : 18;
		int horizontal = node.HasMeta("text_halign") ? (int)node.GetMeta("text_halign") : 0;
		int vertical = node.HasMeta("text_valign") ? (int)node.GetMeta("text_valign") : 1;
		int wrap = node.HasMeta("text_wrap") ? (int)node.GetMeta("text_wrap") : 0;
		Vector2 box = node.HasMeta("text_box") ? (Vector2)node.GetMeta("text_box") : Vector2.Zero;
		Color colour = node.HasMeta("text_colour") ? (Color)node.GetMeta("text_colour") : Colors.White;

		// Opaque, as Engine_UiSceneBuilder builds text: the engine overwrites a .bgf's own
		// text alpha with 1.0, and the accumulated opacity belongs to the render association
		// rather than to the glyphs.
		var opaque = new Color(colour.R, colour.G, colour.B, 1f);

		// ICONS GO WITH THE STRING. A re-mesh that only swapped the glyphs left the node's
		// inline-icon quads from its LAST string standing — the logbook's expression page is
		// authored as the apologise description, whose D-pad glyph then sat on every
		// expression and achievement page — and never drew the icons the new string carries
		// (`<gold_img>`, the tutorial's button glyphs). Same contract as Paint.
		foreach (Node child in node.GetChildren())
		{
			if (IsInlineIcon(child))
			{
				node.RemoveChild(child);
				child.QueueFree();
			}
		}

		// The clip and the fade ride the material, so a re-mesh must carry them over or a masked
		// list loses its `clip_rect` on the first text write. Read first: the new icons take the
		// same fade, or a faded label re-texted would flash its glyphs at full alpha.
		Variant? clip = null;
		Variant? opacity = null;
		if (node.MaterialOverride is ShaderMaterial previous)
		{
			clip = previous.GetShaderParameter(ClipRectName);
			opacity = previous.GetShaderParameter(OpacityName);
		}

		float iconAlpha = opacity is { } carried && carried.VariantType == Variant.Type.Float
			? carried.AsSingle()
			: 1f;

		Engine_UiTextMesh.Built built = Layout(
			display, size, opaque, horizontal, vertical, box.X, box.Y, wrap,
			out Texture2D? atlas);
		ArrayMesh? mesh = built.Mesh;
		foreach (Engine_UiTextMesh.IconPlacement icon in built.Icons)
		{
			if (Textures?.Invoke(icon.Tag.Texture + ".tex") is not { } art)
			{
				continue;
			}

			node.AddChild(MarkInlineIcon(new MeshInstance3D
			{
				Mesh = IconQuad(icon.Size),
				Position = new Vector3(icon.TopLeft.X, icon.TopLeft.Y, 0f),
				MaterialOverride = IconMaterial(art, iconAlpha),
			}));
		}

		node.Mesh = mesh;
		node.MaterialOverride = mesh is null
			? null
			: Engine_UiSceneBuilder.TextMaterial(opaque, atlas);
		if (node.MaterialOverride is ShaderMaterial fresh)
		{
			if (clip is { } c && c.VariantType != Variant.Type.Nil)
			{
				fresh.SetShaderParameter(ClipRectName, c);
			}

			if (opacity is { } o && o.VariantType != Variant.Type.Nil)
			{
				fresh.SetShaderParameter(OpacityName, o);
			}
		}

		return mesh is null
			? $"'{node.Name}' has no glyphs"
			: $"'{node.Name}' set ({display.Length} chars)";
	}

	/// <summary>
	/// A catalogued node's transform composed up to its tree root — its position in the
	/// presentation's CANVAS space, working on a detached tree (a pattern book) where
	/// <c>GlobalTransform</c> is not reliable. How authored placement survives cloning:
	/// <see cref="Art"/> zeroes the clone's root, so read the artist's spot from the SOURCE.
	/// The offset can live anywhere on the chain (`MenuHighlight` keeps it in an inner wrapper).
	/// </summary>
	public static Transform3D CanvasTransform(Node3D node)
	{
		Transform3D transform = node.Transform;
		for (Node? parent = node.GetParent(); parent is Node3D spatial; parent = parent.GetParent())
		{
			transform = spatial.Transform * transform;
		}

		return transform;
	}

	/// <summary>The element a clone would be taken from, for its authored transform.</summary>
	public Node3D? ArtSourceNode(string name, int variant = 0) =>
		_art.TryGetValue(name, out List<Node3D>? matches) && variant >= 0 && variant < matches.Count
			? matches[variant]
			: null;

	/// <summary>Every live instance of a named element — screens ship a 4:3 AND a 16:9 copy of
	/// their furniture, so one lookup hides only one layout's.</summary>
	public System.Collections.Generic.IReadOnlyList<Node3D> ArtSourceNodes(string name) =>
		_art.TryGetValue(name, out List<Node3D>? matches)
			? matches
			: System.Array.Empty<Node3D>();

	/// <summary>
	/// A tag to its display string, through the game's own book. An unresolved tag is returned
	/// VERBATIM rather than blanked, as the engine does (<see cref="Engine_UiScreen"/>).
	/// </summary>
	public string Resolve(string tag) =>
		Strings is not null && Strings.TryGetString(tag, out string value) ? value : tag;

	/// <summary>
	/// Builds a text mesh in the game's own fonts, as a node ready to parent.
	/// <paramref name="colour"/> carries the alpha. The atlas is chosen by POINT SIZE through
	/// <see cref="Engine_UiFontLibrary.For"/> — three shipped buckets, nearest face scaled.
	/// Returns a MeshInstance3D with no mesh (never null) for an empty string or no font library.
	/// </summary>
	public MeshInstance3D Label(
		string display, int pointSize, Color colour,
		int horizontalAlign = 0, int verticalAlign = 1,
		float boxWidth = 0f, float boxHeight = 0f, int wrapMode = 0)
	{
		var node = new MeshInstance3D { Name = "Label" };
		Paint(node, display, pointSize, colour, horizontalAlign, verticalAlign,
			boxWidth, boxHeight, wrapMode);
		return node;
	}

	/// <summary>
	/// Resolves an inline icon's art — the same resolver the scene builder uses, set by whoever
	/// mounted the banks. Null leaves icons undrawn.
	/// </summary>
	public System.Func<string, Texture2D?>? Textures { get; init; }

	/// <summary>The inline icon quad's node name — the builder and the panels agree on it.</summary>
	public const string InlineIconName = "InlineIcon";

	/// <summary>
	/// The mark every inline-icon quad carries. BY META, NOT BY NAME: Godot renames same-named
	/// siblings (`InlineIcon`, `InlineIcon2`, `@InlineIcon@7`), so a name test matched only the
	/// first icon of a string — the rest survived every re-mesh and the help pages filled with
	/// the glyphs of every description shown before.
	/// </summary>
	private const string InlineIconMeta = "inline_icon";

	public static bool IsInlineIcon(Node node) => node.HasMeta(InlineIconMeta);

	/// <summary>Names and marks a freshly built icon quad.</summary>
	public static MeshInstance3D MarkInlineIcon(MeshInstance3D icon)
	{
		icon.Name = InlineIconName;
		icon.SetMeta(InlineIconMeta, true);
		return icon;
	}

	/// <summary>
	/// Draws a string onto an existing label node — mesh, material AND its inline icons. The one
	/// place that paints text: an icon-bearing string needs child quads placed against the same
	/// layout as the glyphs.
	///
	/// Icons take the element's alpha and nothing else — `sub_828706F0` never applies the text
	/// colour, so `&lt;b&gt;&lt;a_img&gt;&lt;/b&gt;` draws a white button glyph.
	/// </summary>
	/// <returns>True when anything was drawn.</returns>
	public bool Paint(
		MeshInstance3D label, string display, int pointSize, Color colour,
		int horizontalAlign = 0, int verticalAlign = 1,
		float boxWidth = 0f, float boxHeight = 0f, int wrapMode = 0)
	{
		foreach (Node child in label.GetChildren())
		{
			if (IsInlineIcon(child))
			{
				label.RemoveChild(child);
				child.QueueFree();
			}
		}

		Engine_UiTextMesh.Built built = Layout(
			display, pointSize, colour, horizontalAlign, verticalAlign,
			boxWidth, boxHeight, wrapMode, out Texture2D? atlas);

		label.Mesh = built.Mesh;
		label.MaterialOverride =
			built.Mesh is null ? null : Engine_UiSceneBuilder.TextMaterial(colour, atlas);

		foreach (Engine_UiTextMesh.IconPlacement icon in built.Icons)
		{
			if (Textures?.Invoke(icon.Tag.Texture + ".tex") is not { } art)
			{
				continue;
			}

			var quad = MarkInlineIcon(new MeshInstance3D
			{
				Mesh = IconQuad(icon.Size),
				// Top-left anchored, drawing down in -Y, matching the glyph cells.
				Position = new Vector3(icon.TopLeft.X, icon.TopLeft.Y, 0f),
				MaterialOverride = IconMaterial(art, colour.A),
			});

			label.AddChild(quad);
		}

		return built.Mesh is not null || built.Icons.Count != 0;
	}

	/// <summary>
	/// One icon's quad, top-left at the origin and running down in -Y.
	///
	/// The whole texture, every time — `sub_828706F0` writes the four corners from the
	/// constants 0.0 and 1.0 only, so an inline icon is never a sub-rect of a sheet.
	/// </summary>
	/// <summary>
	/// An inline icon's material. Unshaded — GUI art is emissive and shading it against the
	/// screen's lights leaves it black. Tinted by alpha alone: `sub_828706F0` passes only the
	/// element's alpha into the quad's vertices, never the text colour.
	/// </summary>
	public static StandardMaterial3D IconMaterial(Texture2D art, float alpha) => new()
	{
		AlbedoTexture = art,
		AlbedoColor = new Color(1f, 1f, 1f, alpha),
		ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
		CullMode = BaseMaterial3D.CullModeEnum.Disabled,
		NoDepthTest = true,
		TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
	};

	/// <summary>
	/// Fades one inline-icon quad, or answers false for anything else. The icons ride a
	/// StandardMaterial3D, not the text shader, so every fade that reaches a label's material
	/// — a retint, an opacity chain, a clone's fade — has to reach its icon children too, or a
	/// scrolled-away menu row leaves its `<exclamation.img>` standing at full alpha.
	/// </summary>
	public static bool SetIconOpacity(Node node, float alpha)
	{
		if (node is not MeshInstance3D icon || !IsInlineIcon(icon) ||
			icon.MaterialOverride is not StandardMaterial3D material)
		{
			return false;
		}

		Color albedo = material.AlbedoColor;
		if (!Mathf.IsEqualApprox(albedo.A, alpha))
		{
			material.AlbedoColor = new Color(albedo.R, albedo.G, albedo.B, alpha);
		}

		return true;
	}

	/// <summary>Fades every inline-icon quad directly under a label.</summary>
	public static void FadeInlineIcons(Node label, float alpha)
	{
		for (int i = 0, count = label.GetChildCount(); i < count; ++i)
		{
			SetIconOpacity(label.GetChild(i), alpha);
		}
	}

	public static ArrayMesh IconQuad(Vector2 size)
	{
		var mesh = new ArrayMesh();
		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);

		arrays[(int)Mesh.ArrayType.Vertex] = new[]
		{
			new Vector3(0f, 0f, 0f),
			new Vector3(0f, -size.Y, 0f),
			new Vector3(size.X, -size.Y, 0f),
			new Vector3(size.X, 0f, 0f),
		};
		arrays[(int)Mesh.ArrayType.TexUV] = new[]
		{
			new Vector2(0f, 0f), new Vector2(0f, 1f),
			new Vector2(1f, 1f), new Vector2(1f, 0f),
		};
		arrays[(int)Mesh.ArrayType.Normal] = new[]
		{
			Vector3.Back, Vector3.Back, Vector3.Back, Vector3.Back,
		};
		arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2, 0, 2, 3 };

		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		return mesh;
	}

	/// <summary>
	/// The glyph mesh and its atlas, without a node wrapped round them — lets a view with
	/// persistent nodes retext a row in place instead of rebuilding the node it is tweening.
	/// Returns (null, null) for an empty string or a missing font library, meaning "draws
	/// nothing", not an error.
	/// </summary>
	/// <remarks>
	/// Glyphs only: a string's inline icons are child quads and there is no node here to hang
	/// them on. <see cref="Paint"/> draws a whole label.
	/// </remarks>
	public (ArrayMesh? Mesh, Texture2D? Atlas) Glyphs(
		string display, int pointSize, Color colour,
		int horizontalAlign = 0, int verticalAlign = 1,
		float boxWidth = 0f, float boxHeight = 0f, int wrapMode = 0)
	{
		Engine_UiTextMesh.Built built = Layout(
			display, pointSize, colour, horizontalAlign, verticalAlign,
			boxWidth, boxHeight, wrapMode, out Texture2D? atlas);
		return built.Mesh is null ? (null, null) : (built.Mesh, atlas);
	}

	/// <summary>
	/// Selects the font bucket and lays the string out. The single place a
	/// <see cref="UiText"/> is assembled from loose arguments, so <see cref="Paint"/> and
	/// <see cref="Glyphs"/> cannot drift apart in what they ask for.
	/// </summary>
	private Engine_UiTextMesh.Built Layout(
		string display, int pointSize, Color colour,
		int horizontalAlign, int verticalAlign,
		float boxWidth, float boxHeight, int wrapMode, out Texture2D? atlas)
	{
		atlas = null;
		if (display.Length == 0 || Fonts?.For(pointSize) is not { } selection)
		{
			// An empty list, not a default struct: `Paint` walks it unconditionally.
			return new Engine_UiTextMesh.Built(
				null, 0, System.Array.Empty<Engine_UiTextMesh.IconPlacement>());
		}

		(UiFont font, float scale) = selection;
		atlas = font.Atlas;

		var text = new UiText
		{
			Display = display,
			Size = pointSize,
			HorzAlign = horizontalAlign,
			VertAlign = verticalAlign,
			WrapMode = wrapMode,
			BoxWidth = boxWidth,
			BoxHeight = boxHeight,
			R = colour.R,
			G = colour.G,
			B = colour.B,
		};

		return Engine_UiTextMesh.Build(text, font.Font, scale);
	}

	/// <summary>
	/// Retints an existing label without rebuilding its glyphs — a scrolling row changes opacity
	/// every frame. Does nothing when the node has no material, the state <see cref="Label"/>
	/// leaves an empty string in.
	/// </summary>
	public static void Retint(MeshInstance3D label, Color colour)
	{
		// Through the builder, not a cast — see Engine_UiSceneBuilder.SetTextColour.
		Engine_UiSceneBuilder.SetTextColour(label, colour);
	}

	/// <summary>Depth-first search by node name. Godot's own find is by path or by pattern.</summary>
	public static Node? FindByName(Node root, string name)
	{
		if (root.Name == name)
		{
			return root;
		}

		for (int i = 0, count = root.GetChildCount(); i < count; ++i)
		{
			if (FindByName(root.GetChild(i), name) is { } hit)
			{
				return hit;
			}
		}

		return null;
	}

	/// <summary>
	/// Resolves one of the guest's own dotted element paths — <c>Tab.InnerRed.LeftBorder</c>,
	/// <c>Foreground.Book.Pages.Page1.Contents.ModelHider.model_proxy.model</c> — against a
	/// built tree.
	///
	/// The first segment is searched, the rest walked as children — the engine's own
	/// <c>getElement(path, from)</c>, not a Godot node path: a built presentation wraps the
	/// artist's elements in the file's component and layer nodes
	/// (<c>Component_0/Layer/Foreground/…</c>), so a rigid relative path resolves nothing.
	/// Accepts '.' or '/' as the separator.
	/// </summary>
	public static Node3D? FindByPath(Node root, string path)
	{
		string[] segments = path.Split('.', '/');
		if (segments.Length == 0 || FindByName(root, segments[0]) is not Node3D at)
		{
			return null;
		}

		for (int i = 1; i < segments.Length; i++)
		{
			Node? next = null;
			foreach (Node child in at.GetChildren())
			{
				if (child.Name == segments[i])
				{
					next = child;
					break;
				}
			}

			if (next is not Node3D found)
			{
				return null;
			}

			at = found;
		}

		return at;
	}

	/// <summary>A one-line description of what was sourced, for a tool's log.</summary>
	public string Describe() =>
		$"[ui-theme] art={ArtCount} names from '{(ArtSource.Length == 0 ? "nothing" : ArtSource)}'  " +
		$"canvas={Canvas.X:F0}x{Canvas.Y:F0}  " +
		$"fonts={(Fonts is null || Fonts.IsEmpty ? "MISSING" : Fonts.Directory)}  " +
		$"strings={(Strings is null ? "MISSING" : "loaded")}";
}
