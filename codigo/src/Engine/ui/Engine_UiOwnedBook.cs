using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Refii.Ui;

/// <summary>
/// A Godot-owned book screen: art units cloned from the screen's own `.bgf` pattern book, values
/// from the guest's stated text/texture/transform/slide channels. One implementation for every
/// book; the <see cref="Engine_UiScreenTemplate"/> differs. Positions are canvas units.
/// </summary>
public sealed partial class Engine_UiOwnedBook : Node3D
{
	/// <summary>This screen's own pattern book — art, fonts and strings, from its `.bgf`.</summary>
	public Engine_UiTheme? Book { get; set; }

	/// <summary>The parsed document, for resolving each live field's ordinal by path.</summary>
	public Engine_UiScreen? Document { get; set; }

	/// <summary>Resolves a stated texture hash to a loaded texture.</summary>
	public System.Func<uint, Texture2D?>? ResolveTexture { get; set; }

	/// <summary>
	/// Fields the host owns outright, keyed by document path, as final display text (already through
	/// Babel). Detail-page fills are edge-gated on the guest's Lua highlight (abilities compares
	/// `g_Menu:GetHighlightedMenuItemData()` against `screen+624`; `beq` at `0x826c3d24` skips the
	/// fill), so with an owned cursor the guest never re-populates. Anything unnamed stays guest.
	/// </summary>
	public System.Collections.Generic.IReadOnlyDictionary<string, string>? HostValues { get; set; }

	/// <summary>
	/// Quads the host textures outright, keyed by document path, as a resource-name hash
	/// (`Engine_GameHash.ResourcePath` over the lower-cased backslash path, what
	/// `rex_lhResourceName_ComputeHash` computes). Written into the guest's stated-texture array.
	/// </summary>
	public System.Collections.Generic.IReadOnlyDictionary<string, uint>? HostTextures { get; set; }

	/// <summary>
	/// Elements the host switches ON or OFF outright — a subject page the guest would have
	/// selected had it known the row, and the pages it would have shut. Applied after
	/// <see cref="HostValues"/> lights its fields, so a page can be revealed as well as filled.
	/// </summary>
	public System.Collections.Generic.IReadOnlyDictionary<string, bool>? HostActive { get; set; }

	/// <summary>
	/// Host-stated positions, keyed by element path. PARENT-LOCAL, NOT CANVAS: a value lands in
	/// the element's own `position.*` slot — the same slot a guest attribute write takes — under
	/// its cloned parent's transform. Convert a canvas placement with
	/// `local = (canvas − parentCanvasOrigin) / parentComposedScale` (the co-op `Buttons` group:
	/// origin (63.4, 329.3), scale 0.832). A NaN component keeps the stated/authored value.
	/// </summary>
	public System.Collections.Generic.IReadOnlyDictionary<string, Vector3>? HostPositions { get; set; }

	/// <summary>
	/// Host-stated `rotation.z`, radians, keyed by element path — the same slot a guest
	/// attribute write takes, turning the element about its authored pivot. Only the world map's
	/// minimap image states one today.
	/// </summary>
	public System.Collections.Generic.IReadOnlyDictionary<string, float>? HostRotations { get; set; }

	/// <summary>The live node of the first element named by <paramref name="path"/>, for an owned
	/// overlay that stands under one element (the minimap's markers under `Page2.Contents`).</summary>
	public Node3D? NodeFor(string path) =>
		OrdinalForPath(path) is { } ordinal ? _binding.NodeFor(ordinal) : null;

	/// <summary>The AUTHORED position of the first element named by <paramref name="path"/>, in its
	/// parent's units — what the guest reads back off the minimap image before it adds it to
	/// every marker.</summary>
	public Vector3? AuthoredPosition(string path) =>
		Document is not null && OrdinalForPath(path) is { } ordinal &&
		ordinal >= 0 && ordinal < Document.Nodes.Length
			? new Vector3(Document.Nodes[ordinal].PositionX, Document.Nodes[ordinal].PositionY,
				Document.Nodes[ordinal].PositionZ)
			: null;

	/// <summary>
	/// Components the host switches, keyed by document path, as a 1-based slide index (1 is the
	/// first slide after the master; `sub_82BB5760` rejects anything below).
	/// </summary>
	public System.Collections.Generic.IReadOnlyDictionary<string, int>? HostSlides { get; set; }

	/// <summary>
	/// Opacities the host STATES, 0..1 — the final word, applied after every drop below.
	///
	/// A value, a texture or a switch only DROPS the guest's stated opacity so the slide or the
	/// authored number takes over. That cannot reach an element authored at zero, and the co-op
	/// screen has two: both `ProfileSelector.Buttons` groups carry `auth=0`, so their children —
	/// the X and Y prompts and the profile titles — can never be lit by dropping anything.
	/// </summary>
	/// Values are 0..1; the book scales them into the stated channel's 0..100 itself.
	public System.Collections.Generic.IReadOnlyDictionary<string, float>? HostOpacity { get; set; }

	/// <summary>One live-driven text: where it lives in the doc, and the clone node it paints.</summary>
	private sealed class Field
	{
		public required string Path { get; init; }
		public required int Ordinal { get; init; }
		public required MeshInstance3D Node { get; init; }

		/// <summary>
		/// The authored placeholder tag (`Text`, `000000`, `NO_DATA`, a raw Babel tag); a field still
		/// reading it is unwritten by the guest and must not be drawn.
		/// </summary>
		public required string Authored { get; init; }

		/// <summary>The same placeholder after Babel.</summary>
		public required string AuthoredDisplay { get; init; }

		public string Shown { get; set; } = "unset";

		/// <summary>Whether a sample has written this field yet — before that the node shows the file's glyphs.</summary>
		public bool EverShown { get; set; }
	}

	/// <summary>The shared element pipeline — transform, visibility and opacity in one pass.</summary>
	private Engine_UiElementResolve? _resolve;

	/// <summary>The clone-to-element join and its per-frame apply, shared with the HUD.</summary>
	private readonly Engine_UiCloneBinding _binding = new();

	private readonly List<Field> _fields = new();
	private readonly List<string> _missing = new();

	/// <summary>Template entries dropped because a unit already cloned contains them.</summary>
	private readonly List<string> _nested = new();

	/// <summary>Element ordinal of each cloned unit's root, to the unit's name.</summary>
	private readonly Dictionary<int, string> _cloned = new();

	/// <summary>The document element a pattern-book node came from, or -1.</summary>
	private int Ordinal(Node3D source) =>
		Document is not null && source.HasMeta("object_id") &&
		Document.NodeForObjectId((uint)source.GetMeta("object_id")) is { } element
			? element.Ordinal
			: -1;

	/// <summary>Whether an element sits under a unit already cloned, and which one.</summary>
	private bool Inside(int ordinal, out string owner)
	{
		owner = string.Empty;
		if (Document is null || ordinal < 0 || ordinal >= Document.Nodes.Length)
		{
			return false;
		}

		for (UiNode? up = Document.Nodes[ordinal].Parent; up is not null; up = up.Parent)
		{
			if (_cloned.TryGetValue(up.Ordinal, out string? unit))
			{
				owner = unit;
				return true;
			}
		}

		return false;
	}


	private Node3D? _pages;

	/// <summary>The guest's slide sample last fed, for the page-stack probe.</summary>
	private IReadOnlyList<int>? _lastSlides;

	/// <summary>The guest's last stated transforms, before any host placement is written over them.</summary>
	private IReadOnlyList<float?[]?>? _lastStated;

	/// <summary>The guest's last stated texture hashes, by ordinal.</summary>
	private uint[]? _lastTextures;

	/// <summary>
	/// The texture the guest has stamped on one element, resolved, or null when it stated none —
	/// the co-op picker's card portraits (`sub_8243B520` writes each card's screenshot hash).
	/// </summary>
	public Texture2D? StatedTexture(string path) =>
		OrdinalForPath(path) is int ordinal && _lastTextures is { } hashes && ordinal >= 0 &&
		ordinal < hashes.Length && hashes[ordinal] != 0u
			? ResolveTexture?.Invoke(hashes[ordinal])
			: null;

	/// <summary>
	/// The guest's stated `scale.x` for one element, or null when it stated none. The co-op
	/// figures are sized by this one value (`sub_82438438` writes it; `sub_826D6C90` reads only it).
	/// </summary>
	public float? StatedScaleX(string path) =>
		OrdinalForPath(path) is int ordinal && _lastStated is { } stated && ordinal >= 0 &&
		ordinal < stated.Count && stated[ordinal] is { Length: > 3 } entry
			? entry[3]
			: null;

	/// <summary>The screen template naming this book's parts (<see cref="Engine_UiTemplates"/>, screen 0 by default).</summary>
	public Engine_UiScreenTemplate? Template { get; set; }

	/// <summary>
	/// Elements a Godot-owned view already draws, which this book must not clone. Registered by
	/// the owning view, same meaning as <see cref="Engine_UiPresentationHost.SuppressedElementNames"/>.
	/// </summary>
	public HashSet<string> Suppressed { get; } = new(System.StringComparer.Ordinal);


	/// <summary>A cloned node by its element name, for a host driver that owns one piece of art outright.</summary>
	public Node3D? Find(string element)
	{
		if (Document is null)
		{
			return null;
		}

		foreach (UiNode node in Document.Walk())
		{
			if (string.Equals(node.Name, element, System.StringComparison.Ordinal) &&
				_binding.NodeFor(node.Ordinal) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	/// <summary>Builds the book from its template. Safe to call once; the units live for the node's life.</summary>
	public void Build()
	{
		Template ??= Engine_UiTemplates.Screen(0);
		if (Book is null || Document is null || Template is null)
		{
			GD.PushWarning("[owned-book] no pattern book, document or template; nothing to build");
			return;
		}

		foreach (string entry in Template.ChromeUnits)
		{
			(string unit, int variant) = SplitVariant(entry);

			// A suppressed unit is not cloned at all; the bind below only suppresses inside a unit.
			if (Suppressed.Contains(unit))
			{
				continue;
			}

			// Zero bearers: the name is not in the pattern book. Too few: the occurrence index moved.
			Node3D? source = Book.ArtSourceNode(unit, variant);
			if (source is null)
			{
				int bearers = Book.ArtVariants(unit);
				_missing.Add(bearers == 0
					? $"{entry} (no bearer in the pattern book)"
					: $"{entry} (only {bearers} bearer(s), variant {variant} asked for)");
				continue;
			}

			// Never clone a unit already inside a cloned one (`Book` contains `Pages` and
			// `RightFrame`); ancestry comes from the document, not the template.
			int ordinal = Ordinal(source);
			if (Inside(ordinal, out string owner))
			{
				_nested.Add($"{entry} (inside {owner})");
				continue;
			}

			Node3D? clone = Book.Art(unit, variant);
			if (clone is null)
			{
				// Catalogued but will not clone: a different fault from absence.
				_missing.Add($"{entry} (catalogued but would not clone)");
				continue;
			}

			// Art() zeroes the transform; the authored one is read off the source chain.
			clone.Transform = Engine_UiTheme.CanvasTransform(source);
			clone.Name = unit;
			AddChild(clone);
			_unitAuthored[clone] = clone.Transform;

			if (ordinal >= 0)
			{
				_cloned[ordinal] = unit;
			}

			// The unit's own root is bound too, so the guest's draw gate reaches it.
			_binding.Bind(clone, Document, Suppressed);

			if (unit == Template.PageRootUnit)
			{
				_pages = clone;
			}
		}

		// Pages parked off-window are clipped by the file's masks, not hidden.
		AssignRegionMasks();
		_masked = Document is null ? 0 : _binding.ApplyMasks(Document);

		// The page stack may have arrived inside another unit; markers hang off it either way.
		_pages ??= Template.PageRootUnit.Length == 0 ? null : Find(Template.PageRootUnit);

		// Bound but not driven: `infowindow.lua`/`iteminfo.lua` still run in the guest
		// (`sub_82426470` is a passthrough) and their `goToSlide` arrives on the slide channel;
		// driving the port too would give the slide two owners.
		_infoWindow.Theme = Book;
		_infoWindow.Bind(this);
		_itemInfo.Theme = Book;
		_itemInfo.Bind(this);

		// One `Page` (`scripts/gameface/page.lua`) per page component, bound by structure (carries
		// `Contents` and `DimensionsImage`, as `AK.fetchObjects` asks), never by name.
		if (_pages is not null)
		{
			foreach (Node child in _pages.GetChildren())
			{
				if (child is not Node3D candidate)
				{
					continue;
				}

				var page = new Engine_UiPage();
				if (page.Bind(candidate))
				{
					_pageWidgets.Add(page);
				}
			}
		}

		// Backdrop: authored 12.8x7.2 over a 100-unit base quad (1280x720); Art() zeroes the clone's
		// transform, so restate it 4x per axis to cover any aspect, behind the book (canvas +Z is
		// toward the camera).
		if (Template.BackdropUnit.Length != 0)
		{
			if (Book.Art(Template.BackdropUnit) is { } tint)
			{
				tint.Name = Template.BackdropUnit;
				tint.Position = new Vector3(0f, 0f, -30f);
				tint.Scale = new Vector3(51.2f, 28.8f, 1f);
				AddChild(tint);
			}
			else
			{
				_missing.Add(Template.BackdropUnit);
			}
		}

		// Every text element inside a cloned unit is a live field, derived from the binding, not
		// listed. Wrap mode, size, alignment and colour are stamped on the node.
		if (Document is not null)
		{
			foreach ((MeshInstance3D mesh, int ordinal) in _binding.TextNodes(Document))
			{
				_textMeshes.Add(mesh);
				_fields.Add(new Field
				{
					Path = DocumentPath(Document.Nodes[ordinal]),
					Ordinal = ordinal,
					Node = mesh,
					Authored = Document.Nodes[ordinal].Text?.Tag ?? string.Empty,

					// The FILE's own resolved caption — the fallback for any element the host does
					// not state. A document whose tag and display are the same string never
					// resolved through Babel, and that is what marks a placeholder.
					AuthoredDisplay = Document.Nodes[ordinal].Text?.Display ?? string.Empty,
				});
			}
		}

		int units = Template.ChromeUnits.Count - CountMissingUnits();

		GD.Print($"[owned-book] '{Template.Name}' built: {units}/{Template.ChromeUnits.Count} " +
			$"art unit(s), {_binding.Count} bound node(s), {_masked} masked, " +
			$"{_fields.Count} live field(s)" +
			(_pageWidgets.Count == 0
				? string.Empty
				: $", {_pageWidgets.Count} page(s) [{string.Join("; ", _pageWidgets.ConvertAll(p => p.Describe()))}]") +
			(_infoWindow.IsBound ? $", {_infoWindow.Describe()}" : string.Empty) +
			(_itemInfo.IsBound ? $", {_itemInfo.Describe()}" : string.Empty) +
			(_binding.SuppressedCount == 0
				? string.Empty
				: $", {_binding.SuppressedCount} subtree(s) left to their owning view") +
			(_nested.Count == 0
				? string.Empty
				: $"; already cloned: {string.Join(", ", _nested)}") +
			(_missing.Count == 0 ? string.Empty : $"; NOT FOUND: {string.Join(", ", _missing)}"));
	}

	/// <summary>
	/// Splits a chrome entry into element name and bearer index: `Name#N`, bare name = first.
	/// Names are not unique; bearers are indexed in document order.
	/// </summary>
	private static (string Name, int Variant) SplitVariant(string entry)
	{
		int hash = entry.LastIndexOf('#');
		if (hash <= 0 || !int.TryParse(entry[(hash + 1)..], out int variant) || variant < 0)
		{
			return (entry, 0);
		}

		return (entry[..hash], variant);
	}

	private int CountMissingUnits()
	{
		int count = 0;
		foreach (string unit in Template?.ChromeUnits ?? new List<string>())
		{
			// Entries carry a cause suffix (`Name (…)`), so match the name, not the whole line.
			foreach (string entry in _missing)
			{
				if (entry == unit || entry.StartsWith(unit + " (", System.StringComparison.Ordinal))
				{
					++count;
					break;
				}
			}
		}

		return count;
	}

	/// <summary>Applies one sample of the guest's statements; every write is change-keyed.</summary>
	public void SetLive(
		string?[]? texts, uint[]? textures, IReadOnlyList<float?[]?>? transform,
		IReadOnlyList<bool>? active = null, IReadOnlyList<float?>? opacity = null,
		IReadOnlyList<int>? slides = null)
	{
		if (Book is null || Document is null)
		{
			return;
		}

		// One resolve for the whole document: position, visibility, opacity.
		if (_resolve is null)
		{
			_resolve = new Engine_UiElementResolve(Document);
			RegisterLive();
		}
		_lastSlides = slides;
		_lastStated = transform;
		_lastTextures = textures;
		_resolve.Apply(Slid(slides), Activated(active), Opaqued(opacity), SwitchedOn());

		// Draw gate, accumulated opacity and stated art for every node, in one pass.
		// Host-supplied textures first (a registry key), the guest's path hashes otherwise.
		_binding.ResolveTexture = hash =>
			Engine_UiHostTextures.TryGet(hash) ?? ResolveTexture?.Invoke(hash);
		_binding.Apply(_resolve, Textured(textures), Positioned(transform));

		// The page arranges every frame (`NeedToUpdateState` is set once in `Initialize`, page.dis
		// main.0 pc17, never cleared) and must run after `_binding.Apply`, which rewrites every
		// bound transform from the stated position.
		bool textMoved = ApplyText(texts);
		foreach (Engine_UiPage page in _pageWidgets)
		{
			page.PivotOf ??= _binding.PivotOf;
			page.Arrange();
			page.Scroll(Refii.Game.GameInput.RightStickY, ScrollDelta);
		}

		if (textMoved)
		{
			// Frames re-measure on the text edge (`TilStopUpdating` cadence).
			DriveFrames();

			// And the masks re-paint: a field re-meshed after the build's one-time mask pass
			// carries no `clip_rect`, so its glyphs drew outside the window — the region page's
			// amounts leaking under the mask while their captions clipped.
			if (Document is not null)
			{
				_masked = _binding.ApplyMasks(Document);
			}
		}

		// The template-declared reflow and scroll region, after the binding and the text pass
		// have both stood everything at its stated place — these move nodes directly, exactly as
		// the page widgets above do, so per frame the deltas start from authored, never compound.
		FlowMeasuredText();
		DriveScrollRegion();
		FitTextToPage();
		ApplyWideSpread();

		ReportHostChannels();
		ReportDark();
		ProbeHostKeys();
		ProbeExtents();
		ProbeLegend(texts);
		ProbePageStack();
	}

	/// <summary>
	/// Where every host switch-ON and host position LANDED: the node's ordinal, whether it is
	/// drawn, its own and accumulated opacity, its global position, and what darkened it. A
	/// channel report says the key bound; this says what the frame did with it. Change-keyed.
	/// </summary>
	private void ProbeHostKeys()
	{
		if (_resolve is null || Document is null)
		{
			return;
		}

		var keys = new List<string>();
		if (HostActive is not null)
		{
			foreach (var pair in HostActive)
			{
				if (pair.Value)
				{
					keys.Add(pair.Key);
				}
			}
		}

		if (HostPositions is not null)
		{
			foreach (var pair in HostPositions)
			{
				if (!keys.Contains(pair.Key))
				{
					keys.Add(pair.Key);
				}
			}
		}

		if (HostOpacity is not null)
		{
			foreach (var pair in HostOpacity)
			{
				if (!pair.Key.EndsWith(".*", System.StringComparison.Ordinal) && !keys.Contains(pair.Key))
				{
					keys.Add(pair.Key);
				}
			}
		}

		if (keys.Count == 0)
		{
			return;
		}

		var line = new System.Text.StringBuilder();
		foreach (string key in keys)
		{
			string leaf = key.LastIndexOf('.') is var dot && dot >= 0 ? key[(dot + 1)..] : key;
			bool any = false;
			foreach (int ordinal in OrdinalsForPath(key))
			{
				any = true;
				bool drawn = ordinal >= 0 && ordinal < _resolve.Drawn.Length && _resolve.Drawn[ordinal];
				float own = ordinal >= 0 && ordinal < _resolve.Own.Length ? _resolve.Own[ordinal] : -1f;
				float op = ordinal >= 0 && ordinal < _resolve.Opacity.Length ? _resolve.Opacity[ordinal] : -1f;
				Node3D? node = _binding.NodeFor(ordinal);
				string at = node is not null && GodotObject.IsInstanceValid(node)
					? (node.IsInsideTree() ? node.GlobalPosition : node.Position).ToString("0")
					: "no node";
				string why = drawn ? string.Empty : $" dark:{_resolve.WhyDark(ordinal)}";
				line.Append($"  {leaf}#{ordinal} {(drawn ? "DRAWN" : "hidden")} own={own:0.00} op={op:0.00} at={at}{why}");
			}

			if (!any)
			{
				line.Append($"  {leaf} UNBOUND");
			}
		}

		string text = line.ToString();
		if (text == _hostKeysReported)
		{
			return;
		}

		_hostKeysReported = text;
		GD.Print($"[owned-book] '{Template?.Name}' host keys landed:{text}");
	}

	private string _hostKeysReported = string.Empty;

	/// <summary>
	/// The drawn X extent of every page-background and frame quad, once per book: the canvas
	/// edges the page art actually reaches against the frame pieces beside it. A gap between a
	/// page and its frame is a size question, and no other report carries sizes.
	/// </summary>
	private void ProbeExtents()
	{
		if (_extentsReported || Document is null)
		{
			return;
		}

		var lines = new List<string>();
		foreach (int ordinal in Enumerable.Range(0, Document.Nodes.Length))
		{
			string name = Document.Nodes[ordinal].Name ?? string.Empty;
			if (!name.Contains("frames_page", System.StringComparison.Ordinal) &&
				!name.StartsWith("frame_new_", System.StringComparison.Ordinal))
			{
				continue;
			}

			if (_binding.NodeFor(ordinal) is not MeshInstance3D mesh || !mesh.IsInsideTree() ||
				mesh.Mesh is null)
			{
				continue;
			}

			Aabb box = mesh.GlobalTransform * mesh.Mesh.GetAabb();
			bool drawn = ordinal < _resolve?.Drawn.Length && _resolve.Drawn[ordinal];
			lines.Add($"{name}#{ordinal} {(drawn ? "drawn" : "hidden")} x {box.Position.X:0}..{box.End.X:0} " +
				$"y {box.Position.Y:0}..{box.End.Y:0}");
		}

		if (lines.Count == 0)
		{
			return;
		}

		_extentsReported = true;
		GD.Print($"[owned-book] '{Template?.Name}' page/frame extents: {string.Join("  |  ", lines)}");
	}

	private bool _extentsReported;

	/// <summary>How much of the book draws and what darkened the rest, by ancestor. Change-keyed.</summary>
	private void ReportDark()
	{
		if (_resolve is null || Document is null)
		{
			return;
		}

		var by = new Dictionary<string, int>(System.StringComparer.Ordinal);
		foreach (int ordinal in _binding.Dark)
		{
			string why = _resolve.WhyDark(ordinal);
			if (why.Length != 0)
			{
				by[why] = by.TryGetValue(why, out int seen) ? seen + 1 : 1;
			}
		}

		var parts = new List<string>();
		foreach ((string why, int count) in by)
		{
			parts.Add($"{why} x{count}");
		}

		parts.Sort();
		string line = $"{_binding.DrawnCount}/{_binding.Count} node(s) drawing" +
			(parts.Count == 0 ? string.Empty : $"; dark at: {string.Join("  ", parts)}");

		if (line == _darkReported)
		{
			return;
		}

		_darkReported = line;
		GD.Print($"[owned-book] '{Template?.Name}' {line}");
	}

	private string _darkReported = string.Empty;

	private string _legendReported = string.Empty;

	/// <summary>
	/// The `clip_rect` of the first clipped mesh under <paramref name="node"/>, as
	/// (minX, minY, maxX, maxY) in world space, read back off the material rather than recomputed.
	/// </summary>
	private static Vector4? FirstClipRect(Node node)
	{
		if (node is MeshInstance3D mesh && mesh.MaterialOverride is ShaderMaterial material &&
			material.GetShaderParameter("clip_rect").VariantType == Variant.Type.Vector4)
		{
			return material.GetShaderParameter("clip_rect").AsVector4();
		}

		foreach (Node child in node.GetChildren())
		{
			if (FirstClipRect(child) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	private void ProbePageStack()
	{
		if (_pages is null || !GodotObject.IsInstanceValid(_pages) || _resolve is null)
		{
			return;
		}

		var line = new System.Text.StringBuilder();
		var key = new System.Text.StringBuilder();
		int up = 0;
		foreach (Node child in _pages.GetChildren())
		{
			if (child is not Node3D page)
			{
				continue;
			}

			int ordinal = _binding.OrdinalOf(page);
			bool drawn = ordinal >= 0 && ordinal < _resolve.Drawn.Length && _resolve.Drawn[ordinal];
			float opacity = ordinal >= 0 && ordinal < _resolve.Opacity.Length
				? _resolve.Opacity[ordinal]
				: -1f;
			bool shows = drawn && opacity > 0.01f;
			if (shows)
			{
				++up;
			}

			// The resolve knows nothing about the shader clip; the page's position does.
			Vector3 at = page.IsInsideTree() ? page.GlobalPosition : page.Position;

			// The page selector writes own opacity 100 for the page in the window and 100/1024 for
			// parked ones; `op` is the accumulated product.
			float own = ordinal >= 0 && ordinal < _resolve.Own.Length
				? _resolve.Own[ordinal]
				: -1f;
			bool stated = ordinal >= 0 && ordinal < _resolve.Stated.Length &&
				_resolve.Stated[ordinal];

			string clip = "clip=none";
			if (FirstClipRect(page) is { } band)
			{
				bool inside = at.X >= band.X && at.X <= band.Z &&
					at.Y >= band.Y && at.Y <= band.W;
				clip = $"clip=({band.X:0},{band.Y:0})..({band.Z:0},{band.W:0}) " +
					(inside ? "CONTAINS it" : "EXCLUDES it");
			}

			// The guest's stated slide for the page and where its Contents resolved to — the
			// two numbers that say whether a page the guest never pushed is parked or over the
			// one that is up.
			int slide = ordinal >= 0 && _lastSlides is not null && ordinal < _lastSlides.Count
				? _lastSlides[ordinal]
				: -1;
			int contentsOrdinal = page.GetNodeOrNull<Node3D>("Contents") is { } contents
				? _binding.OrdinalOf(contents)
				: -1;
			float contentsX = contentsOrdinal >= 0 && contentsOrdinal < _resolve.SlidPosition.Length
				? _resolve.SlidPosition[contentsOrdinal].X
				: float.NaN;

			key.Append(page.Name).Append(drawn ? '+' : '-')
				.Append(opacity.ToString("0.00")).Append(own.ToString("0.000"))
				.Append(stated ? 'g' : 'f').Append(clip)
				.Append(" s").Append(slide).Append(" cx").Append(contentsX.ToString("0")).Append(';');

			line.Append(line.Length == 0 ? string.Empty : "  ")
				.Append(shows ? '*' : ' ')
				.Append(page.Name)
				.Append('#').Append(ordinal)
				.Append(" drawn=").Append(drawn)
				.Append(" op=").Append(opacity.ToString("0.00"))
				.Append(" own=").Append(own.ToString("0.000"))
				.Append(stated ? " (GUEST)" : " (file)")
				.Append(" at=(").Append(at.X.ToString("0")).Append(',')
				.Append(at.Y.ToString("0")).Append(',')
				.Append(at.Z.ToString("0")).Append(") ")
				.Append(clip)
				.Append(" slide=").Append(slide)
				.Append(" contents.x=").Append(contentsX.ToString("0"));
		}

		// The change key excludes the position: the scrolling backdrop moves every frame.
		string report = $"{up} up | {line}";
		if (key.ToString() == _pageStackShown)
		{
			return;
		}

		_pageStackShown = key.ToString();
		GD.Print($"[owned-book] '{Template?.Name}' page stack: {report}");
	}

	private int _masked;

	private string _pageStackShown = string.Empty;

	private void ProbeLegend(string?[]? texts)
	{
		// A PROBE, so a diagnostic: it re-logged on every opacity step of the legend's fade.
		if (!Engine_UiTrace.Verbose || _resolve is null || Document is null || Template?.ScreenId != 0)
		{
			return;
		}

		var lines = new List<string>();
		foreach (UiNode node in Document.Nodes)
		{
			bool interesting = node.Name.StartsWith("KeyName", System.StringComparison.Ordinal) ||
				node.Name is "Legend" or "Fixed" or "CurrentObjective" or "CurrentQuest";
			if (!interesting || node.Ordinal < 0 || node.Ordinal >= _resolve.Drawn.Length)
			{
				continue;
			}

			int i = node.Ordinal;
			string text = texts is not null && i < texts.Length && texts[i] is { Length: > 0 } t
				? $"'{t}'"
				: "-";
			Vector3? at = _binding.NodeFor(i) is { } live
				? Engine_UiTheme.CanvasTransform(live).Origin
				: null;
			lines.Add($"{node.Name}#{i} drawn={_resolve.Drawn[i]} " +
				$"active={_resolve.Active[i]} slid={_resolve.Slid[i]} " +
				$"op={_resolve.Opacity[i]:0.00} text={text} " +
				(at is { } pos ? $"at=({pos.X:0},{pos.Y:0})" : "at=UNBOUND"));
		}

		string joined = string.Join("  |  ", lines);
		if (joined.Length == 0 || joined == _legendReported)
		{
			return;
		}

		_legendReported = joined;
		GD.Print($"[legend-probe] {joined}");
	}

	/// <summary>
	/// The guest's stated textures with the host's overrides written over them, on EVERY bearer
	/// of the key (see <see cref="OrdinalsForPath"/>). Copy-on-write: the array belongs to the
	/// caller and is reused across books.
	/// </summary>
	private uint[]? Textured(uint[]? stated)
	{
		if (HostTextures is null || HostTextures.Count == 0 || Document is null)
		{
			return stated;
		}

		uint[]? patched = null;
		foreach (var pair in HostTextures)
		{
			if (pair.Value == 0u)
			{
				continue;
			}

			foreach (int ordinal in OrdinalsForPath(pair.Key))
			{
				patched ??= stated is null
					? new uint[Document.Nodes.Length]
					: (uint[])stated.Clone();

				if (ordinal >= 0 && ordinal < patched.Length)
				{
					patched[ordinal] = pair.Value;
				}
			}
		}

		return patched ?? stated;
	}

	/// <summary>
	/// The guest's stated transforms with the host's placements written over them, on every
	/// bearer. Copy-on-write. An entry is six slots (x, y, z, three scales).
	/// </summary>
	private IReadOnlyList<float?[]?>? Positioned(IReadOnlyList<float?[]?>? stated)
	{
		bool values = HostValues is { Count: > 0 };
		bool textures = HostTextures is { Count: > 0 };
		bool positions = HostPositions is { Count: > 0 };
		bool rotations = HostRotations is { Count: > 0 };
		bool slid = HostSlides is { Count: > 0 };
		if ((!values && !textures && !positions && !rotations && !slid) || Document is null)
		{
			return stated;
		}

		List<float?[]?>? patched = null;

		// GODOT OWNS THE POSE OF ANYTHING GODOT WRITES — the transform twin of the opacity rule
		// in Opaqued. The guest's stated transform for a field it used to fill is frozen at its
		// stuck highlight, and it moved the item picture to a different corner each run;
		// dropping the whole entry lets the AUTHORED pose (plus any host position) stand. The
		// chrome's transforms are untouched: the guest laid the screen out once at open and
		// those are real.
		void DropEntry(int ordinal)
		{
			if (ordinal < 0 || ordinal >= (stated?.Count ?? 0) || stated![ordinal] is null)
			{
				return;
			}

			patched ??= new List<float?[]?>(stated);
			patched[ordinal] = null;
		}

		if (values)
		{
			foreach (var pair in HostValues!)
			{
				if (pair.Value.Length == 0)
				{
					continue;
				}

				foreach (int ordinal in OrdinalsForPath(pair.Key))
				{
					DropEntry(ordinal);
				}
			}
		}

		if (textures)
		{
			foreach (var pair in HostTextures!)
			{
				if (pair.Value == 0u)
				{
					continue;
				}

				foreach (int ordinal in OrdinalsForPath(pair.Key))
				{
					DropEntry(ordinal);
				}
			}
		}

		// A HOST SLIDE OWNS ITS SUBTREE'S SCALE, for the same reason it owns the opacity (see
		// Opaqued): `Engine_UiCloneBinding.ApplyTransform` starts from the slide's `SlidScale`
		// but the guest's stated transform overrides `scale.x/y/z` whenever it states them, and
		// the guest's is FROZEN at its stuck highlight — so the abilities orbs never grew for
		// the lit discipline (On is scale 0.8, Off 0.6). Nulling the guest's stated scale
		// (transform slots 3..5) for exactly the elements the chosen slide writes a `scale.*` command on lets
		// the slide's own scale decide;
		// position (0..2) is left alone, so a host that also places the element still wins.
		void DropScale(int ordinal)
		{
			if (ordinal < 0 || ordinal >= (stated?.Count ?? 0) || stated![ordinal] is not { } entry)
			{
				return;
			}

			if (entry.Length <= 3 || (entry[3] is null && entry[4] is null && entry[5] is null))
			{
				return;
			}

			patched ??= new List<float?[]?>(stated);
			float?[] slots = (float?[])entry.Clone();
			slots[3] = null;
			slots[4] = null;
			slots[5] = null;
			patched[ordinal] = slots;
		}

		// Only the elements the chosen slide writes a `scale.*` command on — see the opacity
		// pass. A slide that scales nothing (the page fold) leaves every element's guest scale
		// alone, which is what the item detail pages need.
		if (slid)
		{
			ForEachSlideCommand(ScaleId, DropScale);
		}

		// A host rotation rides slot 6, beside the position (0..2) and scale (3..5) slots.
		if (rotations)
		{
			foreach (var pair in HostRotations!)
			{
				foreach (int ordinal in OrdinalsForPath(pair.Key))
				{
					if (patched is null)
					{
						patched = new List<float?[]?>(Document.Nodes.Length);
						for (int i = 0; i < Document.Nodes.Length; ++i)
						{
							patched.Add(stated is not null && i < stated.Count ? stated[i] : null);
						}
					}

					if (ordinal < 0 || ordinal >= patched.Count)
					{
						continue;
					}

					var widened = new float?[7];
					if (patched[ordinal] is { } existing)
					{
						CopyInto(existing, widened);
					}

					widened[6] = pair.Value;
					patched[ordinal] = widened;
				}
			}
		}

		if (!positions)
		{
			return patched ?? stated;
		}

		foreach (var pair in HostPositions!)
		{
			foreach (int ordinal in OrdinalsForPath(pair.Key))
			{
				if (patched is null)
				{
					patched = new List<float?[]?>(Document.Nodes.Length);
					for (int i = 0; i < Document.Nodes.Length; ++i)
					{
						patched.Add(stated is not null && i < stated.Count ? stated[i] : null);
					}
				}

				if (ordinal < 0 || ordinal >= patched.Count)
				{
					continue;
				}

				float?[] slots = patched[ordinal] is { } existing
					? (float?[])existing.Clone()
					: new float?[6];
				if (slots.Length < 6)
				{
					var widened = new float?[6];
					CopyInto(slots, widened);
					slots = widened;
				}

				// A NaN component keeps what the guest stated — a page that moves a row up has
				// no business restating its depth.
				if (!float.IsNaN(pair.Value.X)) slots[0] = pair.Value.X;
				if (!float.IsNaN(pair.Value.Y)) slots[1] = pair.Value.Y;
				if (!float.IsNaN(pair.Value.Z)) slots[2] = pair.Value.Z;

				// A host-placed element takes the slide's scale: the guest's stated scale for an
				// element it keeps off is not a pose. Unstated falls back to the slide's.
				slots[3] = null;
				slots[4] = null;
				slots[5] = null;
				patched[ordinal] = slots;
			}
		}

		return patched ?? stated;

		static void CopyInto(float?[] from, float?[] into)
		{
			for (int i = 0; i < from.Length && i < into.Length; ++i)
			{
				into[i] = from[i];
			}
		}
	}

	/// <summary>The guest's stated slides with the host's switches written over them, on every bearer. Copy-on-write.</summary>
	private IReadOnlyList<int>? Slid(IReadOnlyList<int>? stated)
	{
		if (HostSlides is null || HostSlides.Count == 0 || Document is null)
		{
			return stated;
		}

		List<int>? patched = null;
		foreach (var pair in HostSlides)
		{
			if (pair.Value <= 0)
			{
				continue;
			}

			foreach (int ordinal in OrdinalsForPath(pair.Key))
			{
				if (patched is null)
				{
					patched = new List<int>(Document.Nodes.Length);
					for (int i = 0; i < Document.Nodes.Length; ++i)
					{
						patched.Add(stated is not null && i < stated.Count ? stated[i] : 0);
					}
				}

				if (ordinal >= 0 && ordinal < patched.Count)
				{
					patched[ordinal] = pair.Value;
				}
			}
		}

		return patched ?? stated;
	}

	/// <summary>
	/// The guest's stated draw gates with the host's fields lit, its switches applied and its
	/// slides' own entry actives stated. Cannot reveal a shut page: `Drawn[i]` is
	/// `parentDraws &amp;&amp; active[i]`.
	/// </summary>
	private IReadOnlyList<bool>? Activated(IReadOnlyList<bool>? stated)
	{
		bool values = HostValues is { Count: > 0 };
		bool switches = HostActive is { Count: > 0 };
		bool slid = HostSlides is { Count: > 0 };
		if ((!values && !switches && !slid) || Document is null)
		{
			return stated;
		}

		List<bool>? patched = null;

		List<bool> Patched()
		{
			if (patched is null)
			{
				patched = new List<bool>(Document.Nodes.Length);
				for (int i = 0; i < Document.Nodes.Length; ++i)
				{
					patched.Add(stated is not null && i < stated.Count && stated[i]);
				}
			}

			return patched;
		}

		if (values)
		{
			foreach (var pair in HostValues!)
			{
				// An empty value is not a statement; do not light it.
				if (pair.Value.Length == 0)
				{
					continue;
				}

				foreach (int ordinal in OrdinalsForPath(pair.Key))
				{
					List<bool> lit = Patched();
					if (ordinal >= 0 && ordinal < lit.Count)
					{
						lit[ordinal] = true;
					}
				}
			}
		}

		// A HOST SLIDE STATES ITS OWN ENTRIES' ACTIVES. `goToSlide` rewrites exactly the bits
		// the slide's entry list names, so the guest's stale copy of them — its own last fill of
		// the component — must not gate the new slide: that intersection is how the region
		// page's star strip showed one lit and one dark star (stale `1star` actives over a host
		// `4star` slide). ONLY the named entries: a first cut forced the whole subtree true,
		// and `Foreground.Book` is a host-slid component on every book screen — the force
		// overrode the page switches above and drew all seven world-map pages at once.
		if (slid && Document.Document is { } file)
		{
			foreach (var pair in HostSlides!)
			{
				if (pair.Value <= 0)
				{
					continue;
				}

				foreach (int owner in OrdinalsForPath(pair.Key))
				{
					int seen = 0;
					foreach (BgfSlide slide in file.Slides)
					{
						if (slide.OwnerElement != owner || slide.IsMaster)
						{
							continue;
						}

						if (++seen != pair.Value)
						{
							continue;
						}

						List<bool> active = Patched();
						foreach (BgfSlideEntry entry in slide.Entries)
						{
							if (entry.ElementOrdinal >= 0 && entry.ElementOrdinal < active.Count)
							{
								active[entry.ElementOrdinal] = entry.Active;
							}
						}

						break;
					}
				}
			}
		}

		// THE HOST'S SWITCHES WIN, so they go LAST. The fold slide on `Foreground.Book`
		// (`ShowPages`) names every page as active — Page1 AND Page2, and on the world map all
		// seven — so applying the slide's entries after the switches put back every page the
		// switch had just shut: the items book drew both pages over each other ("3 up") and
		// the world map buried its quest page under six others. A switch is the host's
		// decision about THIS row; a slide entry is the artist's default.
		if (switches)
		{
			// A SWITCH ON RELEASES ITS SUBTREE. The guest's mirror of a page it never opened
			// carries every descendant inactive (the logbook's MoralityPage, its Contents and
			// all below); the host opening that page owns the whole page, so the descendants
			// fall back to the slide state and the authored defaults — the resolver's own
			// gate — rather than the guest's stale bits. Subtrees first, so a descendant the
			// host switches OFF in the same statement still wins below.
			foreach (var pair in HostActive!)
			{
				if (!pair.Value)
				{
					continue;
				}

				foreach (int ordinal in OrdinalsForPath(pair.Key))
				{
					List<bool> released = Patched();
					for (int i = ordinal + 1; i < Document.Nodes.Length; ++i)
					{
						if (IsUnder(Document.Nodes[i], ordinal))
						{
							released[i] = true;
						}
					}
				}
			}

			foreach (var pair in HostActive!)
			{
				foreach (int ordinal in OrdinalsForPath(pair.Key))
				{
					List<bool> switched = Patched();
					if (ordinal >= 0 && ordinal < switched.Count)
					{
						switched[ordinal] = pair.Value;
					}
				}
			}
		}

		return patched ?? stated;
	}

	private static bool IsUnder(UiNode node, int ancestor)
	{
		for (UiNode? up = node.Parent; up is not null; up = up.Parent)
		{
			if (up.Ordinal == ancestor)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// The elements the host switched ON, for the resolver: a switch is a later write of the
	/// same bit a slide entry wrote, so it is not gated by that entry.
	/// </summary>
	private IReadOnlyList<bool>? SwitchedOn()
	{
		if (HostActive is not { Count: > 0 } || Document is null)
		{
			return null;
		}

		bool[]? on = null;
		foreach (var pair in HostActive)
		{
			if (!pair.Value)
			{
				continue;
			}

			foreach (int ordinal in OrdinalsForPath(pair.Key))
			{
				if (ordinal >= 0 && ordinal < Document.Nodes.Length)
				{
					on ??= new bool[Document.Nodes.Length];
					on[ordinal] = true;
				}
			}
		}

		return on;
	}

	/// <summary>
	/// The guest's stated opacity with the host's ownership honoured: an element the host
	/// writes (a value, a texture, a switch ON, a slide command) drops the guest's stated value
	/// and falls back to the slide/authored opacity — see the notes inside.
	/// </summary>
	private IReadOnlyList<float?>? Opaqued(IReadOnlyList<float?>? stated)
	{
		bool values = HostValues is { Count: > 0 };
		bool textures = HostTextures is { Count: > 0 };
		bool switches = HostActive is { Count: > 0 };
		bool slid = HostSlides is { Count: > 0 };
		bool opacities = HostOpacity is { Count: > 0 };
		if ((!values && !textures && !switches && !slid && !opacities) || Document is null)
		{
			return stated;
		}

		// A STATED OPACITY NEEDS NO GUEST MIRROR TO STAND ON. The drops below only make sense
		// against a stated list, but a host that states a number outright must be able to do so
		// on a book the guest never mirrored — that was the co-op screen, whose profile block
		// stayed dark with every claim correct because `stated` was null and this returned.
		stated ??= new List<float?>(new float?[Document.Nodes.Length]);

		List<float?>? patched = null;

		void Drop(int ordinal)
		{
			if (ordinal >= 0 && ordinal < stated.Count && stated[ordinal] is not null)
			{
				patched ??= new List<float?>(stated);
				patched[ordinal] = null;
			}
		}

		// GODOT OWNS THE OPACITY OF ANYTHING GODOT WRITES. The resolve reads an element's
		// opacity from the GUEST's stated mirror whenever it has one, and the guest's is frozen
		// at its stuck highlight — so an element the host FILLS (an item name, its picture) drew
		// at whatever opacity the guest last left it: 1.0 for the item its highlight froze on,
		// but 0.0 for the rest, which is why the item detail page showed for one weapon and was
		// blank for the next. Dropping the guest opacity where the host writes a value or a
		// texture lets it fall to the element's authored/slide opacity (full), so a host-written
		// field is always visible. An empty value is skipped — it blanks the text, not the box.
		if (values)
		{
			foreach (var pair in HostValues!)
			{
				if (pair.Value.Length == 0)
				{
					continue;
				}

				foreach (int ordinal in OrdinalsForPath(pair.Key))
				{
					Drop(ordinal);
				}
			}
		}

		if (textures)
		{
			foreach (var pair in HostTextures!)
			{
				if (pair.Value == 0u)
				{
					continue;
				}

				foreach (int ordinal in OrdinalsForPath(pair.Key))
				{
					Drop(ordinal);
				}
			}
		}

		if (switches)
		{
			foreach (var pair in HostActive!)
			{
				if (!pair.Value)
				{
					continue;
				}

				foreach (int ordinal in OrdinalsForPath(pair.Key))
				{
					Drop(ordinal);
				}
			}
		}

		// A HOST SLIDE OWNS ONLY THE OPACITY IT COMMANDS. The resolve takes an element's opacity
		// from the GUEST's stated value whenever it has one (`fromGuest` in
		// Engine_UiElementResolve), and the guest's is frozen at its stuck highlight — the
		// abilities orbs stayed lit on the first ability's discipline because the guest's own
		// On-slide opacity outvoted the host's Off slide. So drop the guest opacity for exactly
		// the elements the chosen slide writes an `opacity` command on — NOT the whole subtree:
		// the PAGE-FOLD slide sits on `Foreground.Book`, whose subtree is the entire detail
		// page, and claiming all of it blanked every item book (the guest's per-element opacity
		// the detail contents rely on was dropped, with no slide command to replace it).
		if (slid)
		{
			ForEachSlideCommand(OpacityId, Drop);
		}

		// STATED OPACITY IS THE LAST WORD, after every drop above — a drop only hands the
		// element back to its slide or its authored number, and this is for the elements where
		// both of those are zero.
		if (HostOpacity is { Count: > 0 })
		{
			foreach (var pair in HostOpacity)
			{
				// A `.*` SUFFIX CLAIMS THE SUBTREE. These books author a whole group dark — the
				// co-op profile block is 40-odd elements at 0.01 under a parent at 1.00 — and
				// opacity does not inherit, so naming the container lit one node and nothing
				// inside it. Listing every leaf instead is the same claim written forty times
				// and wrong the moment the art changes.
				bool subtree = pair.Key.EndsWith(".*", System.StringComparison.Ordinal);
				string key = subtree ? pair.Key[..^2] : pair.Key;

				foreach (int ordinal in subtree ? OrdinalsUnderPath(key) : OrdinalsForPath(key))
				{
					// NaN RELEASES rather than states: the guest's number is dropped and the
					// element falls back to its slide's or its authored opacity — for a group the
					// guest blanked on its own stuck cursor, whose children author their own
					// numbers (a dimmed arrow at 50, a stick frame kept at 0).
					if (float.IsNaN(pair.Value))
					{
						Drop(ordinal);
						continue;
					}

					if (ordinal >= 0 && ordinal < stated.Count)
					{
						// THE STATED CHANNEL IS 0..100, NOT 0..1. The resolve clamps it to 100 and
						// divides (Engine_UiElementResolve, the `opacity` parameter's own contract),
						// so a host claim of 1.0 written raw landed as ONE PERCENT — which is the
						// `op=0.01` every claimed element showed for two runs, with the claim
						// reaching every one of them exactly as intended.
						patched ??= new List<float?>(stated);
						patched[ordinal] = Mathf.Clamp(pair.Value, 0f, 1f) * 100f;
					}
				}
			}
		}

		return patched ?? stated;
	}

	private static readonly uint OpacityAttr = Refii.Core.Engine_GameHash.AttributeId("opacity");
	private static readonly uint ScaleXAttr = Refii.Core.Engine_GameHash.AttributeId("scale.x");
	private static readonly uint ScaleYAttr = Refii.Core.Engine_GameHash.AttributeId("scale.y");
	private static readonly uint ScaleZAttr = Refii.Core.Engine_GameHash.AttributeId("scale.z");

	private static readonly System.Func<uint, bool> OpacityId = id => id == OpacityAttr;

	private static readonly System.Func<uint, bool> ScaleId =
		id => id == ScaleXAttr || id == ScaleYAttr || id == ScaleZAttr;

	/// <summary>
	/// For every element the host's chosen slide writes a matching attribute command on, runs
	/// <paramref name="onOrdinal"/>. The RESOLVE reads an element's opacity and scale from the
	/// GUEST's stated mirror whenever it has one, and the guest's is frozen at its stuck
	/// highlight — so a host slide's own opacity/scale never took (the abilities orbs). Dropping
	/// the guest value lets the host slide decide, but ONLY for the attributes the slide actually
	/// writes: a slide that just moves a page (the page fold on `Foreground.Book`) must not have
	/// the guest opacity/scale of the WHOLE detail page dropped under it, which blanked the item
	/// books.
	/// </summary>
	private void ForEachSlideCommand(System.Func<uint, bool> matches, System.Action<int> onOrdinal)
	{
		if (HostSlides is null || Document?.Document is not { } file)
		{
			return;
		}

		foreach (var pair in HostSlides)
		{
			if (pair.Value <= 0)
			{
				continue;
			}

			foreach (int owner in OrdinalsForPath(pair.Key))
			{
				int seen = 0;
				foreach (BgfSlide slide in file.Slides)
				{
					if (slide.OwnerElement != owner || slide.IsMaster)
					{
						continue;
					}

					if (++seen != pair.Value)
					{
						continue;
					}

					foreach (BgfSlideEntry entry in slide.Entries)
					{
						foreach (BgfAttribute command in entry.Commands)
						{
							if (matches(command.Id))
							{
								onOrdinal(entry.ElementOrdinal);
								break;
							}
						}
					}

					break;
				}
			}
		}
	}

	/// <summary>What the host's channels landed on; a key matching no element is otherwise silent.</summary>
	private void ReportHostChannels()
	{
		var lostKeys = new List<string>();
		int values = Landed(HostValues?.Keys, out int valuesLost, lostKeys);
		int textures = Landed(HostTextures?.Keys, out int texturesLost, lostKeys);
		int positions = Landed(HostPositions?.Keys, out int positionsLost, lostKeys);
		int slides = Landed(HostSlides?.Keys, out int slidesLost, lostKeys);
		int switches = Landed(HostActive?.Keys, out int switchesLost, lostKeys);
		int opacities = OpacityLandings(out int opacitiesLost, lostKeys);
		if (values + textures + positions + slides + switches + opacities +
			valuesLost + texturesLost + positionsLost + slidesLost + switchesLost + opacitiesLost == 0)
		{
			return;
		}

		string report = $"{values} value(s), {textures} texture(s), {positions} position(s), " +
			$"{slides} slide(s), {switches} switch(es), {opacities} opacity element(s) bound";
		int lost = valuesLost + texturesLost + positionsLost + slidesLost;
		if (lost != 0)
		{
			report += $" — {lost} key(s) MATCHED NO ELEMENT" +
				$" (values {valuesLost}, textures {texturesLost}, " +
				$"positions {positionsLost}, slides {slidesLost}): " +
				string.Join(", ", lostKeys);
		}

		if (report == _channelsShown)
		{
			return;
		}

		_channelsShown = report;
		GD.Print($"[owned-book] '{Template?.Name}' host channels: {report}");
	}

	/// <summary>How many of these paths resolve, how many name nothing at all, and which.</summary>
	/// <summary>How many ELEMENTS the host's opacity claims reach — a `.*` key counts its subtree.</summary>
	private int OpacityLandings(out int lost, List<string>? lostKeys)
	{
		lost = 0;
		if (HostOpacity is null)
		{
			return 0;
		}

		int found = 0;
		foreach (string key in HostOpacity.Keys)
		{
			bool subtree = key.EndsWith(".*", System.StringComparison.Ordinal);
			string bare = subtree ? key[..^2] : key;
			int hit = 0;
			foreach (int _ in subtree ? OrdinalsUnderPath(bare) : OrdinalsForPath(bare))
			{
				++hit;
			}

			if (hit != 0)
			{
				found += hit;
			}
			else
			{
				++lost;
				if (lostKeys is not null && !lostKeys.Contains(key))
				{
					lostKeys.Add(key);
				}
			}
		}

		return found;
	}

	private int Landed(System.Collections.Generic.IEnumerable<string>? paths, out int lost,
		List<string>? lostKeys = null)
	{
		lost = 0;
		if (paths is null)
		{
			return 0;
		}

		int found = 0;
		foreach (string path in paths)
		{
			if (OrdinalForPath(path) is not null)
			{
				++found;
			}
			else
			{
				++lost;
				if (lostKeys is not null && !lostKeys.Contains(path))
				{
					lostKeys.Add(path);
				}
			}
		}

		return found;
	}

	private string _channelsShown = string.Empty;

	/// <summary>Every node's dotted path to its ordinal, built once.</summary>
	private Dictionary<string, int>? _pathOrdinals;


	/// <summary>The ordinal of the node a key names, matched on trailing whole segments.</summary>
	private int? OrdinalForPath(string key)
	{
		if (Document is null)
		{
			return null;
		}

		if (_pathOrdinals is null)
		{
			_pathOrdinals = new Dictionary<string, int>();
			foreach (UiNode node in Document.Nodes)
			{
				string path = DocumentPath(node);
				if (path.Length != 0)
				{
					_pathOrdinals[path] = node.Ordinal;
				}
			}
		}

		foreach (var pair in _pathOrdinals)
		{
			if (PathNames(pair.Key, key))
			{
				return pair.Value;
			}
		}

		return null;
	}

	/// <summary>
	/// EVERY element the key names. The value pass has always written all bearers — a page-less
	/// key like `Contents.experienceorbs.Will` deliberately serves the Page1 and Page2 copies
	/// both — but the slide, switch, texture and position channels resolved a single ordinal,
	/// so they landed on whichever copy the dictionary iterated first: the invisible one, on
	/// the abilities book, which left the visible orb strip on the guest's stale slide.
	/// </summary>
	/// <summary>
	/// Every ordinal at <paramref name="key"/> OR BENEATH IT. The same trailing-segment match as
	/// <see cref="PathNames"/>, plus anything whose path carries that run followed by a dot.
	/// </summary>
	private IEnumerable<int> OrdinalsUnderPath(string key)
	{
		// `_pathOrdinals` is built LAZILY by OrdinalForPath. Testing it for null here answered
		// "nothing" on any book where no exact lookup had run first — which is exactly the case
		// for a screen whose only host claims are opacities.
		OrdinalForPath(key);
		if (_pathOrdinals is null)
		{
			yield break;
		}

		foreach (var pair in _pathOrdinals)
		{
			if (PathNames(pair.Key, key) || PathUnder(pair.Key, key))
			{
				yield return pair.Value;
			}
		}
	}

	/// <summary>Whether <paramref name="path"/> is a DESCENDANT of <paramref name="key"/>.</summary>
	private static bool PathUnder(string path, string key)
	{
		int at = path.IndexOf(key, System.StringComparison.Ordinal);
		return at >= 0 &&
			(at == 0 || path[at - 1] == '.') &&
			at + key.Length < path.Length &&
			path[at + key.Length] == '.';
	}

	private IEnumerable<int> OrdinalsForPath(string key)
	{
		if (OrdinalForPath(key) is null)
		{
			yield break;
		}

		foreach (var pair in _pathOrdinals!)
		{
			if (PathNames(pair.Key, key))
			{
				yield return pair.Value;
			}
		}
	}

	/// <summary>The first bearer of a key, for a host driver that owns one element by path (the
	/// abilities preview quad). Null when nothing in the document is named so.</summary>
	public int? OrdinalOf(string key) => OrdinalForPath(key);

	/// <summary>The document path of an element by ordinal — the key a host texture for that
	/// quad is filed under (the abilities preview frame). Null when out of range.</summary>
	public string? PathOfOrdinal(int ordinal) =>
		Document is not null && ordinal >= 0 && ordinal < Document.Nodes.Length
			? DocumentPath(Document.Nodes[ordinal])
			: null;

	/// <summary>
	/// The element's dotted path from its authored ancestors (<c>Foreground.Book.Pages.Page1.Contents.AbilityName</c>);
	/// parser-synthesised <c>Type_Ordinal</c> ancestors are skipped, so the path is file-order independent.
	/// </summary>
	private static string DocumentPath(UiNode node)
	{
		var parts = new List<string>();
		for (UiNode? walk = node; walk is not null; walk = walk.Parent)
		{
			if (walk.HasAuthoredName)
			{
				parts.Add(walk.Name);
			}
		}

		parts.Reverse();
		return string.Join('.', parts);
	}

	/// <summary>
	/// Whether <paramref name="key"/> equals <paramref name="path"/> or is a trailing run of whole
	/// segments of it (templates and files disagree about the root name).
	/// </summary>
	private static bool PathNames(string path, string key) =>
		string.Equals(path, key, System.StringComparison.Ordinal) ||
		(path.Length > key.Length &&
		 path[path.Length - key.Length - 1] == '.' &&
		 path.EndsWith(key, System.StringComparison.Ordinal));

	private bool ApplyText(string?[]? texts)
	{
		if (Book is null)
		{
			return false;
		}

		bool changed = false;

		// THE GUEST IS NOT IN THE TEXT PATH — except for the units a template names in
		// `GuestTextUnits`, read below as data. `texts` is its mirrored element sample; the
		// same call carries the art, the transforms and the draw gate, which ARE the guest's
		// statements.

		foreach (Field field in _fields)
		{
			if (!GodotObject.IsInstanceValid(field.Node))
			{
				continue;
			}

			string display;

			// TWO SOURCES, IN THIS ORDER: what the host states for this element, and otherwise
			// what the FILE authored. The guest's mirrored text used to sit in second place, and
			// that one line was the biggest remaining hole in "Godot owns the UI":
			//
			//   - It made an empty host value mean "leave the guest's text alone", so roughly a
			//     hundred and eighty-five blanking statements the page classes already write
			//     were silently discarded. `Engine_UiAbilityPage` clears `Target`/`Range` on
			//     every fill precisely so the previous ability's line cannot stand, and it never
			//     took effect — which is exactly how "Brutal Style 2: Flourishes" came to sit
			//     under Physique.
			//   - A field with no host source showed whatever the guest last wrote, so it was
			//     right until the guest stopped writing and then quietly wrong. That is the same
			//     failure as the ability movie and the region page, spread across every book.
			//
			// The authored string keeps every STATIC caption working without the guest: the
			// `.bgf` carries them, and they are captions, not state. An unresolved tag still
			// shows nothing — `NO_DATA`, `DEFAULT_TEXT_ID` and the literal 'Text' are
			// placeholders, and a document whose tag and display are identical never resolved.
			bool owned = false;
			display = string.Empty;
			if (HostValues is not null)
			{
				foreach (var pair in HostValues)
				{
					if (PathNames(field.Path, pair.Key))
					{
						// AN EMPTY HOST VALUE IS NOW A BLANKING ORDER — the page said this
						// element has nothing to show, and that is an answer.
						owned = true;
						display = pair.Value;
						break;
					}
				}
			}

			// A GUEST-TEXT UNIT: the guest's own live write for this field, when the template
			// names its unit and the guest has written one. Read as data, not as a channel —
			// see Engine_UiScreenTemplate.GuestTextUnits.
			if (!owned && texts is not null && Template is { GuestTextUnits.Count: > 0 } template &&
				field.Ordinal >= 0 && field.Ordinal < texts.Length &&
				texts[field.Ordinal] is { Length: > 0 } guest)
			{
				foreach (string unit in template.GuestTextUnits)
				{
					if (PathNames(field.Path, unit) || field.Path.Contains("." + unit + ".", System.StringComparison.Ordinal) ||
						field.Path.StartsWith(unit + ".", System.StringComparison.Ordinal))
					{
						owned = true;
						display = guest;
						break;
					}
				}
			}

			if (!owned)
			{
				display = IsPlaceholder(field) ? string.Empty : field.AuthoredDisplay;
			}

			if (display == field.Shown)
			{
				continue;
			}

			// THE FIRST SAMPLE KEEPS THE AUTHORED GLYPHS. A clone arrives showing the file's own
			// caption; when that is also what the host shows, only the material has to change
			// hands — see Engine_UiTheme.KeepAuthoredText. Still a text edge below: the frames
			// and masks re-measure on the first sample exactly as they did.
			bool kept = !field.EverShown && display == field.AuthoredDisplay &&
				Book.KeepAuthoredText(field.Node, display);
			if (!kept)
			{
				Book.SetMeshText(field.Node, display);
			}

			field.Shown = display;
			field.EverShown = true;
			changed = true;
		}

		return changed;
	}

	private static readonly (string Name, uint Id)[] KnownAttributes =
		new[] { "opacity", "position.x", "position.y", "position.z", "scale.x", "scale.y", "scale.z",
			"rotation.x", "rotation.y", "rotation.z", "globalactive", "active" }
		.Select(name => (name, Refii.Core.Engine_GameHash.AttributeId(name))).ToArray();

	private static string AttributeNameOf(uint id)
	{
		foreach ((string name, uint known) in KnownAttributes)
		{
			if (known == id)
			{
				return name;
			}
		}

		return $"attr:{id:X}";
	}

	/// <summary>Every book that has resolved at least once, for the live debug channel.</summary>
	private static readonly List<Engine_UiOwnedBook> LiveBooks = new();

	private void RegisterLive()
	{
		if (!LiveBooks.Contains(this))
		{
			LiveBooks.Add(this);
		}
	}

	/// <summary>One quad per painting mesh under <paramref name="node"/> whose box covers the point.</summary>
	private static void CollectPaint(Node node, Engine_UiOwnedBook book, string bookName, float px, float py,
		List<(float Z, string What)> over)
	{
		foreach (Node child in node.GetChildren())
		{
			CollectPaint(child, book, bookName, px, py, over);
		}

		if (node is not MeshInstance3D mesh || !mesh.IsVisibleInTree())
		{
			return;
		}

		// The 3D preview model hangs under the page's anchor and is not a quad: it is reported
		// wherever it is, painting or not, because "is the item in the ring" is the question.
		bool previewModel = mesh.Name == Engine_UiPreviewObject.MeshName;
		Engine_UiSceneBuilder.QuadPaint paint = previewModel
			? default
			: Engine_UiSceneBuilder.DescribeQuad(mesh);
		if (!previewModel && !paint.Painted)
		{
			return;
		}

		Aabb box = mesh.GlobalTransform * mesh.GetAabb();
		if (!previewModel &&
			(px < box.Position.X || px > box.End.X || py < box.Position.Y || py > box.End.Y))
		{
			return;
		}

		var path = new System.Text.StringBuilder(mesh.Name);
		for (Node? up = mesh.GetParent(); up is not null && up != book; up = up.GetParent())
		{
			path.Insert(0, '.').Insert(0, up.Name);
		}

		if (previewModel)
		{
			over.Add((box.Position.Z,
				$"[{bookName}] {path} <3D PREVIEW visible={mesh.IsVisibleInTree()} scale={mesh.Scale.X:0.###} " +
				$"pos=({mesh.GlobalPosition.X:0},{mesh.GlobalPosition.Y:0},{mesh.GlobalPosition.Z:0})> " +
				$"box=({box.Position.X:0},{box.Position.Y:0},{box.Position.Z:0})..({box.End.X:0},{box.End.Y:0},{box.End.Z:0})"));
			return;
		}

		string texture = paint.Texture is { } albedo
			? (albedo.ResourcePath.GetFile() is { Length: > 0 } file ? file : "runtime")
			: "NO-TEXTURE";
		over.Add((box.Position.Z,
			$"[{bookName}] {path} <{texture} op={paint.Opacity:0.00} tint={paint.Tint.R:F2},{paint.Tint.G:F2},{paint.Tint.B:F2}@{paint.TintAmount:F2}{(paint.DetailMapped ? " detail" : string.Empty)}> box=({box.Position.X:0},{box.Position.Y:0})..({box.End.X:0},{box.End.Y:0})"));
	}

	/// <summary>
	/// The RESOLVED state of every element whose path contains <paramref name="match"/>, across
	/// every live book — active, drawn, accumulated opacity, the bound node's position, and for
	/// a text element its authored tag, its display and any host value. The ground truth a
	/// bound-but-ineffective channel write is caught against.
	/// </summary>
	public static List<string> DescribeLive(string match, int limit)
	{
		var lines = new List<string>();
		LiveBooks.RemoveAll(book => !GodotObject.IsInstanceValid(book));

		// `paint:X,Y` — WHAT PAINTS AT A CANVAS POINT, nearest first, across every live book: each
		// painting quad whose box covers the point, with its texture, tint and z. The question
		// the resolved state cannot answer — an element drawn at opacity 1 that the player
		// still cannot see is behind something, and this names it.
		if (match.StartsWith("paint:", System.StringComparison.Ordinal))
		{
			string[] at = match["paint:".Length..].Split(',');
			if (at.Length != 2 ||
				!float.TryParse(at[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float px) ||
				!float.TryParse(at[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float py))
			{
				lines.Add("paint: expects X,Y in canvas units");
				return lines;
			}

			var over = new List<(float Z, string What)>();
			foreach (Engine_UiOwnedBook book in LiveBooks)
			{
				if (!book.IsInsideTree() || !book.IsVisibleInTree())
				{
					continue;
				}

				string bookName = book.Template?.Name ?? "?";
				CollectPaint(book, book, bookName, px, py, over);
			}

			over.Sort((a, b) => b.Z.CompareTo(a.Z));
			foreach ((float z, string what) in over)
			{
				if (lines.Count >= limit)
				{
					break;
				}

				lines.Add($"z{z:0} {what}");
			}

			if (lines.Count == 0)
			{
				lines.Add($"NOTHING PAINTS at ({px:0},{py:0})");
			}

			return lines;
		}

		// `slides:<owner path fragment>` — the authored slides of every matching owner: the
		// entries' actives, their direct commands and each activated track's LAST key, i.e.
		// exactly what the resolver folds in. The other channel of the same document.
		if (match.StartsWith("slides:", System.StringComparison.Ordinal))
		{
			string want = match["slides:".Length..];
			foreach (Engine_UiOwnedBook book in LiveBooks)
			{
				if (book.Document?.Document is not { } file)
				{
					continue;
				}

				string bookName = book.Template?.Name ?? "?";
				var ordinalOfSlide = new Dictionary<int, int>();
				foreach (BgfSlide slide in file.Slides)
				{
					if (lines.Count >= limit)
					{
						return lines;
					}

					if (slide.OwnerElement < 0 || slide.OwnerElement >= book.Document.Nodes.Length)
					{
						continue;
					}

					string owner = DocumentPath(book.Document.Nodes[slide.OwnerElement]);
					if (!$"[{bookName}] {owner}".Contains(want, System.StringComparison.Ordinal))
					{
						continue;
					}

					int index = slide.IsMaster ? 0 : (ordinalOfSlide[slide.OwnerElement] = ordinalOfSlide.GetValueOrDefault(slide.OwnerElement) + 1);
					var entries = new System.Text.StringBuilder();
					foreach (BgfSlideEntry entry in slide.Entries)
					{
						if (entry.ElementOrdinal < 0 || entry.ElementOrdinal >= book.Document.Nodes.Length)
						{
							continue;
						}

						UiNode target = book.Document.Nodes[entry.ElementOrdinal];
						entries.Append("  ").Append(target.HasAuthoredName ? target.Name : $"#{entry.ElementOrdinal}")
							.Append(entry.Active ? " on" : " OFF");
						foreach (BgfAttribute command in entry.Commands)
						{
							entries.Append(' ').Append(AttributeNameOf(command.Id))
								.Append('=').Append(command.Type == BgfValueType.Float ? command.AsFloat().ToString("0.##") : command.RawValue.ToString());
						}

						foreach (int animation in entry.ActivatedAnimations ?? System.Array.Empty<int>())
						{
							if (animation < 0 || animation >= file.Animations.Length)
							{
								continue;
							}

							BgfAnimation track = file.Animations[animation];
							if (track.Keys.Length == 0)
							{
								continue;
							}

							string trackTarget = track.ElementOrdinal >= 0 && track.ElementOrdinal < book.Document.Nodes.Length
								? book.Document.Nodes[track.ElementOrdinal].Name
								: $"#{track.ElementOrdinal}";
							entries.Append(' ').Append(trackTarget).Append('.')
								.Append(AttributeNameOf(track.AttributeId))
								.Append('~').Append(track.Keys[0].Value.ToString("0.##")).Append('→').Append(track.Keys[^1].Value.ToString("0.##"));
						}
					}

					lines.Add($"[{bookName}] slide {index}{(slide.IsMaster ? " (master)" : string.Empty)} '{slide.Name}' on {owner}: {slide.Entries.Length} entry(ies){entries}");
				}
			}

			return lines;
		}
		foreach (Engine_UiOwnedBook book in LiveBooks)
		{
			if (book.Document is null || book._resolve is null)
			{
				continue;
			}

			string name = book.Template?.Name ?? "?";
			foreach (UiNode node in book.Document.Nodes)
			{
				if (lines.Count >= limit)
				{
					return lines;
				}

				string path = DocumentPath(node);
				// The match sees the line's own prefix too, so `[options]` narrows to one book.
				if (!$"[{name}] {path}".Contains(match, System.StringComparison.Ordinal))
				{
					continue;
				}

				int i = node.Ordinal;
				bool active = i >= 0 && i < book._resolve.Active.Length && book._resolve.Active[i];
				bool drawn = i >= 0 && i < book._resolve.Drawn.Length && book._resolve.Drawn[i];
				float op = i >= 0 && i < book._resolve.Opacity.Length ? book._resolve.Opacity[i] : -1f;
				int slide = book._lastSlides is { } slid && i >= 0 && i < slid.Count ? slid[i] : 0;

				string at = "(unbound)";
				if (book._binding.NodeFor(i) is { } bound && GodotObject.IsInstanceValid(bound))
				{
					Vector3 p = bound.IsInsideTree() ? bound.GlobalPosition : bound.Position;
					Basis b = bound.Transform.Basis;
					at = $"({p.X:0},{p.Y:0},{p.Z:0}) scale=({b.X.Length():0.##},{b.Y.Length():0.##})" +
						(bound.Visible ? string.Empty : " HIDDEN") +
						(bound is MeshInstance3D { Mesh: null } ? " NO-MESH" : string.Empty);
				}

				string says = string.Empty;
				if (node.Text is { } text)
				{
					string live = book.HostValues is { } host && host.TryGetValue(path, out string? written)
						? written
						: string.Empty;
					says = $" tag='{Short(text.Tag)}' shows='{Short(text.Display)}'" +
						(live.Length != 0 ? $" host='{Short(live)}'" : string.Empty);
				}

				lines.Add($"[{name}] #{i} {path} active={active} drawn={drawn} op={op:0.00} auth={node.Opacity:0.##} slide={slide} at={at}{says}");
			}
		}

		return lines;

		static string Short(string s) => s.Length > 48 ? s[..48] + "…" : s;
	}

	/// <summary>Each top-level unit's authored canvas transform — unit roots are not restated
	/// by the binding, so anything that moves one starts from here.</summary>
	private readonly System.Collections.Generic.Dictionary<Node3D, Transform3D> _unitAuthored = new();

	private readonly System.Collections.Generic.List<MeshInstance3D> _textMeshes = new();

	private readonly System.Collections.Generic.Dictionary<MeshInstance3D, Vector3> _textFitted = new();

	/// <summary>How far inside the 16:9 edge a page's text must end — the cover's width.</summary>
	private const float PageEdgeMargin = 40f;

	/// <summary>The smallest a too-long line is drawn, as a fraction of its authored size.</summary>
	private const float MinimumTextFit = 0.6f;

	/// <summary>
	/// TEXT STAYS ON ITS PAGE. A single-line field never folds, so a long name (a co-op
	/// partner's, the dog's, a localised ability title) ran off the parchment and under the
	/// cover. A line that passes its page's outer edge is drawn smaller about its own anchor,
	/// down to a floor. Measured in the unspread 16:9 layout, before the wide layout rides the
	/// pages out, and from the transform this frame stands at — a line already fitted
	/// measures as fitting, so nothing compounds.
	/// </summary>
	private void FitTextToPage()
	{
		float edge = Engine_UiSceneBuilder.DesignHalfWidth - PageEdgeMargin;
		Transform3D toBook = GlobalTransform.AffineInverse();
		foreach (MeshInstance3D mesh in _textMeshes)
		{
			if (!GodotObject.IsInstanceValid(mesh) || mesh.Mesh is null || !mesh.IsVisibleInTree())
			{
				continue;
			}

			Transform3D inBook = toBook * mesh.GlobalTransform;
			float origin = inBook.Origin.X;

			// A line anchored past the edge belongs to a parked page, not to this one.
			if (Mathf.Abs(origin) >= edge)
			{
				continue;
			}

			Aabb box = inBook * mesh.GetAabb();
			float fit = 1f;
			if (box.End.X > edge && box.End.X - origin > 1f)
			{
				fit = Mathf.Min(fit, (edge - origin) / (box.End.X - origin));
			}

			if (box.Position.X < -edge && origin - box.Position.X > 1f)
			{
				fit = Mathf.Min(fit, (origin + edge) / (origin - box.Position.X));
			}

			// At the floor a line can still measure long; one this pass already shrank and
			// nothing restated since is left alone.
			if (fit < 0.999f &&
				!(_textFitted.TryGetValue(mesh, out Vector3 left) && mesh.Scale.IsEqualApprox(left)))
			{
				fit = Mathf.Max(fit, MinimumTextFit);
				Vector3 scale = mesh.Scale;
				Engine_UiSceneBuilder.SetLiveScale(
					mesh, new Vector3(scale.X * fit, scale.Y * fit, scale.Z));
				_textFitted[mesh] = mesh.Scale;
			}
		}
	}

	/// <summary>What the wide layout last did to a node: the transform it started from, the
	/// one it left, and the amount — so a node nothing restated this frame is not moved twice.</summary>
	private readonly System.Collections.Generic.Dictionary<Node3D, (Transform3D From, Transform3D To, float Amount)> _spreadAt = new();

	private float _wideShift;

	private float _groundWiden = 1f;

	/// <summary>
	/// THE BOOK OPENS TO THE WINDOW — the template's wide layout
	/// (<see cref="Engine_UiScreenTemplate.WideLeft"/>, `WideRight`, `WideStretch`,
	/// `WideGround`). After the binding has restated every bound transform, like the reflow
	/// and the scroll.
	/// </summary>
	private void ApplyWideSpread()
	{
		bool stated = Template is { } t &&
			(t.WideLeft.Count + t.WideRight.Count + t.WideStretch.Count + t.WideGround.Count) > 0;
		float shift = stated ? Engine_UiSceneBuilder.WideShift : 0f;

		if (!Mathf.IsEqualApprox(shift, _wideShift))
		{
			_wideShift = shift;
			_binding.ClipShiftX = shift;

			// The masks are measured from the unspread layout: put back whatever the binding
			// does not restate (unit roots) before measuring.
			foreach ((Node3D node, var was) in _spreadAt)
			{
				if (GodotObject.IsInstanceValid(node) && node.Transform.IsEqualApprox(was.To))
				{
					node.Transform = was.From;
				}
			}

			_spreadAt.Clear();
			if (Document is not null)
			{
				_masked = _binding.ApplyMasks(Document);
			}
		}

		if (!stated)
		{
			return;
		}

		// Unit roots are not restated by the binding: written on a change, from authored.
		float widen = 1f + (shift / Engine_UiSceneBuilder.DesignHalfWidth);
		if (!Mathf.IsEqualApprox(widen, _groundWiden))
		{
			_groundWiden = widen;
			foreach ((Node3D unit, Transform3D authored) in _unitAuthored)
			{
				if (GodotObject.IsInstanceValid(unit) &&
					Template!.WideGround.Contains(unit.Name.ToString()))
				{
					unit.Transform = new Transform3D(
						authored.Basis.Scaled(new Vector3(widen, widen, 1f)),
						authored.Origin * new Vector3(widen, widen, 1f));
				}
			}
		}

		if (shift <= 0f)
		{
			return;
		}

		Transform3D toBook = GlobalTransform.AffineInverse();

		foreach (string path in Template!.WideLeft)
		{
			Ride(path, -shift);
		}

		foreach (string path in Template.WideRight)
		{
			Ride(path, shift);
		}

		foreach (string path in Template.WideStretch)
		{
			Stretch(path);
		}

		// The transform a piece starts this frame from: what the binding just restated, or —
		// for one nothing restated — what it stood at before the last spread.
		bool Rebase(Node3D piece, float amount)
		{
			if (_spreadAt.TryGetValue(piece, out var last) && piece.Transform.IsEqualApprox(last.To))
			{
				if (Mathf.IsEqualApprox(last.Amount, amount))
				{
					return false;
				}

				piece.Transform = last.From;
			}

			return true;
		}

		void Ride(string path, float canvas)
		{
			if (NodeByPath(path) is not { } piece || piece.GetParent() is not Node3D parent ||
				!Rebase(piece, canvas))
			{
				return;
			}

			// Canvas units into the piece's parent space.
			Transform3D from = piece.Transform;
			float scale = (toBook * parent.GlobalTransform).Basis.X.Length();
			piece.Position += new Vector3(canvas / Mathf.Max(scale, 0.0001f), 0f, 0f);
			_spreadAt[piece] = (from, piece.Transform, canvas);
		}

		// A page's paper keeps its gutter edge and reaches `shift` further out.
		void Stretch(string path)
		{
			if (NodeByPath(path) is not { } piece || piece.GetParent() is not Node3D parent ||
				!Rebase(piece, shift))
			{
				return;
			}

			Transform3D from = piece.Transform;
			float low = float.MaxValue;
			float high = float.MinValue;
			Measure(piece);
			if (high - low <= 1f)
			{
				return;
			}

			bool rightHand = (low + high) * 0.5f > 0f;
			float anchor = rightHand ? low : high;
			float factor = (high - low + shift) / (high - low);

			// Scale x about the gutter edge, in book space, then back into the parent's.
			Transform3D inBook = toBook * piece.GlobalTransform;
			var widened = new Transform3D(
				new Basis(new Vector3(factor, 0f, 0f), Vector3.Up, Vector3.Back),
				new Vector3(anchor * (1f - factor), 0f, 0f)) * inBook;
			piece.Transform = (toBook * parent.GlobalTransform).AffineInverse() * widened;
			_spreadAt[piece] = (from, piece.Transform, shift);

			void Measure(Node node)
			{
				if (node is MeshInstance3D { Mesh: not null } mesh && !mesh.HasMeta("text"))
				{
					Aabb box = (toBook * mesh.GlobalTransform) * mesh.GetAabb();
					low = Mathf.Min(low, box.Position.X);
					high = Mathf.Max(high, box.End.X);
				}

				foreach (Node child in node.GetChildren())
				{
					Measure(child);
				}
			}
		}
	}

	/// <summary>Seconds since the last sample, for the page scroll (`SCROLL_SPEED` is per second: page.dis main.1 pc15..27).</summary>
	public double ScrollDelta { get; set; } = 1.0 / 30.0;

	/// <summary>
	/// The region's offset, overflow and held inputs — the one scroll state every text region
	/// shares (<see cref="Engine_UiScrollRegion"/>). The wheel and the scroll bar reach it the way
	/// the stick does, in steps and absolute positions, held until the next sample, the only place
	/// the offset is applied (it runs on the guest's frames).
	/// </summary>
	private readonly Engine_UiScrollRegion _scroll = new();

	/// <summary>The scroll region's window on screen, in canvas units; null when nothing scrolls.</summary>
	public Rect2? ScrollRegionRect { get; private set; }

	/// <summary>The authored scroll bar's arrows, top and bottom, in canvas units.</summary>
	private (float Top, float Bottom, float X)? _scrollBarSpan;

	/// <summary>Wheel notches over the page: positive scrolls down.</summary>
	public void AddScrollWheel(int notches) => _scroll.AddWheel(notches);

	/// <summary>
	/// The 0..1 scroll position a canvas point on the authored scroll bar stands for — top arrow
	/// 0, bottom arrow 1 — or null off it. <paramref name="anyHeight"/> clamps a drag that has
	/// left the bar instead of declining it.
	/// </summary>
	public float? ScrollBarAt(Vector2 at, bool anyHeight = false)
	{
		if (_scrollBarSpan is not { } span || (!anyHeight &&
			(Mathf.Abs(at.X - span.X) > 20f || at.Y > span.Top + 10f || at.Y < span.Bottom - 10f)))
		{
			return null;
		}

		float length = span.Top - span.Bottom;
		return length <= 0f ? 0f : Mathf.Clamp((span.Top - at.Y) / length, 0f, 1f);
	}

	/// <summary>Scrolls the region to a 0..1 position — the scroll bar's drag.</summary>
	public void ScrollRegionTo(float fraction) => _scroll.ScrollTo(fraction);

	private string _scrollReport = string.Empty;

	/// <summary>The scroll region driving now, by its content path; a change resets the offset.</summary>
	private string _scrollContentPath = string.Empty;

	/// <summary>What the scroll region is showing (the highlighted row); a change resets the offset.</summary>
	public string ScrollKey
	{
		get => _scrollKey;
		set
		{
			if (_scrollKey != value)
			{
				_scrollKey = value;
				_scroll.Rewind();
			}
		}
	}

	private string _scrollKey = string.Empty;

	/// <summary>The bound clone for a template path, through the same trailing-segment match
	/// every host channel uses.</summary>
	private Node3D? NodeByPath(string path) =>
		OrdinalForPath(path) is { } ordinal && ordinal >= 0 ? _binding.NodeFor(ordinal) : null;

	/// <summary>A node's stated `position.y` — origin plus the pivot term, as the page widget
	/// recovers it.</summary>
	private float StatedYOf(Node3D node) =>
		node.Position.Y + (node.Transform.Basis * _binding.PivotOf(node)).Y;

	private void SetStatedYOf(Node3D node, float y)
	{
		Vector3 pivot = _binding.PivotOf(node);
		Basis basis = node.Transform.Basis;
		Vector3 stated = node.Position + (basis * pivot);
		stated.Y = y;
		node.Position = stated - (basis * pivot);
	}

	/// <summary>
	/// THE MEASURED-TEXT REFLOW — `sub_829AB8A8`. The guest never clips or shortens the measured
	/// element: it re-measures the wrapped text, takes the overflow past the authored base mark,
	/// and moves every row the template names by that delta; the tail rows stand at the anchor
	/// row's Y, less 30 while the anchor itself draws. Deltas are applied against the positions
	/// the binding just rewrote, so nothing compounds.
	/// </summary>
	private void FlowMeasuredText()
	{
		if (Template is not { FlowMeasured.Length: > 0, FlowBase.Length: > 0 } template ||
			Document is null)
		{
			return;
		}

		if (NodeByPath(template.FlowMeasured) is not MeshInstance3D measured ||
			OrdinalForPath(template.FlowBase) is not { } baseOrdinal ||
			baseOrdinal < 0 || baseOrdinal >= Document.Nodes.Length)
		{
			return;
		}

		// The wrapped height the guest's re-measure reads (component+184). Aabb is in canvas
		// units — the text mesh is built unscaled, as the page widget already relies on.
		float height = measured.Mesh is not null && measured.Visible
			? measured.GetAabb().Size.Y
			: 0f;
		float bottom = StatedYOf(measured) - height;
		float delta = bottom - Document.Nodes[baseOrdinal].PositionY;

		foreach (string path in template.FlowMoved)
		{
			if (NodeByPath(path) is { } row)
			{
				SetStatedYOf(row, StatedYOf(row) + delta);
			}
		}

		if (template.FlowTailAnchor.Length == 0 ||
			NodeByPath(template.FlowTailAnchor) is not { } anchor ||
			OrdinalForPath(template.FlowTailAnchor) is not { } anchorOrdinal)
		{
			return;
		}

		bool standing = _resolve is not null && anchorOrdinal >= 0 &&
			anchorOrdinal < _resolve.Drawn.Length && _resolve.Drawn[anchorOrdinal];
		float tail = StatedYOf(anchor) - (standing ? 30f : 0f);
		foreach (string path in template.FlowTailMoved)
		{
			if (NodeByPath(path) is { } row)
			{
				SetStatedYOf(row, tail);
			}
		}
	}

	/// <summary>
	/// THE SCROLL REGION — `sub_828C3250`/`sub_829ED788`. The right stick slides the content
	/// group inside the mask's band; the clip is the mask's and already painted on the content
	/// by the binding. The guest moves each registered row; moving the group is the same
	/// transform. Arrows dim at their limit (slide 1 of 2) and the thumb rides the fraction.
	/// </summary>
	private void DriveScrollRegion()
	{
		if (Template is not { ScrollContent.Length: > 0 } template || Document is null)
		{
			return;
		}

		// ONE REGION PER PAGE. A book may author a scroll region on several of its pages
		// (the logbook's Stats and Renown pages, each behind the same `Mask`); the template
		// lists them `|`-separated, index-parallel across content, mask and bar, and the one
		// whose content is on screen drives. Changing region drops the offset.
		string[] contents = template.ScrollContent.Split('|');
		string[] masks = template.ScrollMask.Split('|');
		string[] bars = template.ScrollBar.Split('|');
		Node3D? content = null;
		string maskPath = string.Empty;
		string barPath = string.Empty;
		for (int i = 0; i < contents.Length; ++i)
		{
			if (NodeByPath(contents[i]) is { } candidate && candidate.IsInsideTree() &&
				candidate.IsVisibleInTree())
			{
				content = candidate;
				maskPath = i < masks.Length ? masks[i] : masks[0];
				barPath = i < bars.Length ? bars[i] : (bars.Length > 0 ? bars[0] : string.Empty);
				if (_scrollContentPath != contents[i])
				{
					_scrollContentPath = contents[i];
					_scroll.Rewind();
				}
				break;
			}
		}

		if (content is null)
		{
			// Off screen: the next showing starts at the top.
			_scroll.Reset();
			ScrollRegionRect = null;
			_scrollBarSpan = null;
			return;
		}

		// Both bands in CANVAS space: content from its meshes through the book's own transform,
		// the mask from its authored quad (100 units by its scale) — the masks live at Layer
		// level, outside every cloned unit, so the document is their one source.
		// EVERY DRAWN ELEMENT, not only text: the guest's bounds (`sub_82427250`, counted by
		// `sub_829EDA40` for rows whose opacity passes 0.1) include sprites, so a family
		// portrait or a legend icon at the bottom is part of the length.
		Transform3D toCanvas = GlobalTransform.AffineInverse();
		float lowest = float.MaxValue;
		float highest = float.MinValue;
		Measure(content);

		void Measure(Node node)
		{
			if (node is Node3D { Visible: false })
			{
				return;
			}

			if (node is MeshInstance3D mesh && mesh.Mesh is not null)
			{
				Aabb box = toCanvas * mesh.GlobalTransform * mesh.GetAabb();
				lowest = Mathf.Min(lowest, box.Position.Y);
				highest = Mathf.Max(highest, box.End.Y);
			}

			foreach (Node child in node.GetChildren())
			{
				Measure(child);
			}
		}

		float range = 0f;
		if (highest > lowest && OrdinalForPath(maskPath) is { } maskOrdinal &&
			maskOrdinal >= 0 && maskOrdinal < Document.Nodes.Length)
		{
			UiNode mask = Document.Nodes[maskOrdinal];

			// The guest's overflow (`sub_829ED788` against `sub_829EDA40`'s bounds): the
			// content's height less the window's. The binding just rewrote every stated
			// position, so this is the authored (plus reflow) layout — this frame's offset has
			// not been applied yet.
			range = Mathf.Max(0f, (highest - lowest) - (100f * Mathf.Abs(mask.ScaleY)));
		}

		// The guest's rate (`sub_828C30A8`, ~3 units a 30 Hz tick at full stick) is the region's.
		float offset = _scroll.Step(Refii.Game.GameInput.RightStickY, ScrollDelta, range);

		// Where the page sits on screen, for the pointer: the painted clip, which carries the
		// wide-layout shift the document's mask does not.
		// The clip is laid out; the pointer is the window's, so a seated book's rect is moved there.
		ScrollRegionRect = range > 1f && FirstClipRect(content) is { } clip
			? Engine_UiSeats.RectToWindow(content, new Rect2(clip.X, clip.Y, clip.Z - clip.X, clip.W - clip.Y))
			: null;
		_scrollBarSpan = null;

		if (offset != 0f)
		{
			SetStatedYOf(content, StatedYOf(content) + offset);
		}

		if (barPath.Length != 0 && NodeByPath(barPath) is { } bar)
		{
			bool scrollable = range > 1f;
			bar.Visible = scrollable;
			if (scrollable)
			{
				// The guest HIDES the bar at registration (`sub_828C33F0` → opacity ÷ 1024)
				// and its own scroll object shows it when the content overflows; that hide is
				// what the mirror carries, so the whole bar comes back to its authored alpha
				// here before the arrows dim at their limits.
				Engine_UiSceneBuilder.FadeSubtree(bar, 1f);
				float fraction = _scroll.Fraction;
				var up = Engine_UiTheme.FindByName(bar, "UpArrow") as Node3D;
				var down = Engine_UiTheme.FindByName(bar, "DownArrow") as Node3D;
				var icon = Engine_UiTheme.FindByName(bar, "Icon") as Node3D;

				// Slide 1 is at-limit, slide 2 active (`sub_828C3488`) — expressed as a fade on
				// the clone, the same stand-in the menu box uses for a disabled row.
				if (up is not null)
				{
					Engine_UiSceneBuilder.FadeSubtree(up, fraction > 0.01f ? 1f : 0.35f);
				}

				if (down is not null)
				{
					Engine_UiSceneBuilder.FadeSubtree(down, fraction < 0.99f ? 1f : 0.35f);
				}

				if (icon is not null && up is not null && down is not null)
				{
					SetStatedYOf(icon,
						Mathf.Lerp(StatedYOf(up), StatedYOf(down), fraction));
					_scrollBarSpan = (up.GlobalPosition.Y, down.GlobalPosition.Y, icon.GlobalPosition.X);
				}
			}
		}

		string report = $"range {range:0} offset {offset:0}";
		if (report != _scrollReport)
		{
			_scrollReport = report;
			GD.Print($"[owned-book] '{Template.Name}' scroll region: {report}");
		}
	}

	private readonly List<Engine_UiPage> _pageWidgets = new();

	/// <summary>
	/// Gives every element under a scroll region's content the region's mask, where the file
	/// authors none — what the guest does when it registers the region: `sub_82426F00(content,
	/// mask, pres)` walks the subtree and writes the mask into each drawable (logbook pages
	/// `sub_829BCD58`, `sub_829BD418`, `sub_829BDA90`). Without it the Expressions, Completed
	/// Quests and Help pages scroll their rows straight over the page edge.
	/// </summary>
	private void AssignRegionMasks()
	{
		if (Template is not { ScrollContent.Length: > 0 } template || Document is null)
		{
			return;
		}

		string[] contents = template.ScrollContent.Split('|');
		string[] masks = template.ScrollMask.Split('|');
		for (int i = 0; i < contents.Length; ++i)
		{
			string maskPath = i < masks.Length ? masks[i] : masks[0];
			if (contents[i].Length == 0 || OrdinalForPath(contents[i]) is not { } content ||
				OrdinalForPath(maskPath) is not { } mask ||
				content < 0 || content >= Document.Nodes.Length)
			{
				continue;
			}

			var stack = new Stack<UiNode>();
			stack.Push(Document.Nodes[content]);
			while (stack.Count > 0)
			{
				UiNode node = stack.Pop();
				if (node.Ordinal >= 0 && node.Ordinal < Document.MaskByOrdinal.Length &&
					Document.MaskByOrdinal[node.Ordinal] < 0)
				{
					Document.MaskByOrdinal[node.Ordinal] = mask;
				}

				foreach (UiNode child in node.Children)
				{
					stack.Push(child);
				}
			}
		}
	}

	/// <summary>The ported `g_InfoWindow`, when this screen's art carries one.</summary>
	private readonly Engine_UiInfoWindow _infoWindow = new();

	/// <summary>The ported `g_ItemInfo`, same condition.</summary>
	private readonly Engine_UiItemInfo _itemInfo = new();

	/// <summary>The runtime border cloner — `framecloning.lua`.</summary>
	private Engine_UiFrameCloning? _frameCloning;

	private int _framesPlaced = -1;

	/// <summary>
	/// Clones and places the nine-slice borders for every frame this book's clones carry — the
	/// slices are not in the file. A frame whose parent has no live clone is left alone.
	/// </summary>
	public void DriveFrames()
	{
		if (Book is null || Document is null)
		{
			return;
		}

		_frameCloning ??= new Engine_UiFrameCloning(Book);
		int placed = _frameCloning.Dress(Document, _binding);

		if (placed != _framesPlaced)
		{
			_framesPlaced = placed;
			GD.Print($"[owned-book] '{Template?.Name}' framed: {placed} border piece(s) placed, " +
				$"{_frameCloning.Describe()}");
		}
	}

	private readonly List<Node3D> _markers = new();
	private string _markersShown = string.Empty;

	/// <summary>
	/// Draws the quest markers: a clone of the matching `ReusableTokens` art at the guest's stated
	/// page-local centre, under the Pages unit.
	/// </summary>
	public void SetMarkers(IReadOnlyList<Engine_UiModel.MapMarker> markers)
	{
		if (_pages is null || Book is null)
		{
			return;
		}

		// Change-keyed: one rebuild per map opening.
		var key = new System.Text.StringBuilder();
		foreach (Engine_UiModel.MapMarker marker in markers)
		{
			key.Append($"{marker.Key:X8}@{marker.X:0},{marker.Y:0};");
		}

		string shown = key.ToString();
		if (shown == _markersShown)
		{
			return;
		}

		_markersShown = shown;
		foreach (Node3D old in _markers)
		{
			if (GodotObject.IsInstanceValid(old))
			{
				old.QueueFree();
			}
		}

		_markers.Clear();

		foreach (Engine_UiModel.MapMarker marker in markers)
		{
			// Matched on the authored UV rect; the marker key is an opaque guest pointer.
			string token = MatchToken(marker);
			if (Book.Art(token) is not { } art)
			{
				continue;
			}

			art.Name = $"Marker_{_markers.Count}";

			// Art() zeroed the transform; the token's authored scale is 0.1.
			art.Position = new Vector3(marker.X, marker.Y, -5f);
			art.Scale = new Vector3(0.1f, 0.1f, 1f);
			_pages.AddChild(art);
			_markers.Add(art);
		}

		GD.Print($"[owned-book] {_markers.Count} quest marker(s) on the minimap" +
			(_markers.Count == 0 ? string.Empty : $": {shown}"));
	}

	/// <summary>
	/// The ReusableTokens entry whose authored UV rect matches a marker's corners; "Quest" is the
	/// default for anything unmatched.
	/// </summary>
	private string MatchToken(Engine_UiModel.MapMarker marker)
	{
		if (Document is null)
		{
			return "Quest";
		}

		string best = "Quest";
		float bestError = float.MaxValue;
		foreach (string token in new[] { "Quest", "QuestGiver", "QuestExit", "LevelExit" })
		{
			UiNode? node = Engine_UiScreenPanel.Resolve(Document, $"ReusableTokens.{token}");
			UiQuad? quad = node?.Quad;
			if (quad is null && node is not null)
			{
				foreach (UiNode child in node.Children)
				{
					if (child.Quad is not null)
					{
						quad = child.Quad;
						break;
					}
				}
			}

			if (quad is null)
			{
				continue;
			}

			float error =
				System.Math.Abs(quad.UMin - marker.UMin) + System.Math.Abs(quad.VMin - marker.VMin) +
				System.Math.Abs(quad.UMax - marker.UMax) + System.Math.Abs(quad.VMax - marker.VMax);
			if (error < bestError)
			{
				bestError = error;
				best = token == "LevelExit" ? "QuestExit" : token;
			}
		}

		return best;
	}

	/// <summary>
	/// Whether a field the host does not state is still showing an AUTHORED PLACEHOLDER, and
	/// so must not be drawn.
	///
	/// THE OLD TEST WAS "the tag equals its display", i.e. Babel did not translate it. That is
	/// true of every LITERAL caption in every book — `Hero`, `Dog`, `Melee`, a legend entry —
	/// because a literal has nothing to translate, so it blanked them all. The pause minimap
	/// lost its legend and the equipment pages lost their captions, and the emptier a page got
	/// the more it looked like missing DATA rather than a drawing rule.
	///
	/// A placeholder is an UNRESOLVED BABEL KEY or one of the authored dummies — never prose.
	/// Babel keys are upper-case, underscore-joined and never contain a space, which is what
	/// separates `GUI_SCREEN_SHOP_VALUE` from `Hero`.
	/// </summary>
	private static bool IsPlaceholder(Field field)
	{
		if (field.AuthoredDisplay.Length == 0)
		{
			return true;
		}

		// Resolved through Babel — real text by definition.
		if (!string.Equals(field.Authored, field.AuthoredDisplay, System.StringComparison.Ordinal))
		{
			return false;
		}

		foreach (string dummy in AuthoredDummies)
		{
			if (string.Equals(field.AuthoredDisplay, dummy, System.StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		// An unresolved KEY: upper-case, underscore-joined, no spaces.
		bool underscored = false;
		foreach (char c in field.AuthoredDisplay)
		{
			if (c == '_')
			{
				underscored = true;
				continue;
			}

			if (!char.IsUpper(c) && !char.IsDigit(c))
			{
				return false;
			}
		}

		return underscored;
	}

	/// <summary>The dummies the books author into an unwritten field.</summary>
	private static readonly string[] AuthoredDummies =
	{
		"Text", "000000", "NO_DATA", "0", "XXX", "TBD",
	};

}
