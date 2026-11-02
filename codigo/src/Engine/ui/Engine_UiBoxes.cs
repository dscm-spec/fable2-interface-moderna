using Godot;

namespace Refii.Ui;

// The owned box views — the Node3D each surface in Engine_UiOwnedSurfaces.cs drives. Every one
// implements IUiSurfaceView, clones its art from the file-only pattern book, and degrades to
// text-only stand-ins rather than to null when the art is missing.

/// <summary>
/// The MenuBox (screen id 9), Godot-owned: shipped Lua opens an option list — the bed's sleep
/// menu, the world map's destination stop, DLC prompts — and Godot draws it and answers.
/// </summary>
/// <remarks>
/// The guest states the list: option labels are live text on
/// `Group/Choices/Multiplayer_Button{i+1}/Choice1/Choice1`, the title on `Group/Title`, and the
/// model behind them (count, selection, cancelable, per-option enabled) comes from the controller
/// struct through <see cref="Engine_UiSource.Current.GetMenuBox"/>. Six options is the guest's cap.
///
/// Dispatch is the guest's own accept: the host writes the index and calls `sub_823DC720`, which
/// re-checks the enabled byte, fires the Lua callback, publishes the result (player message
/// 35/101) and tears the box down.
/// </remarks>
public partial class Engine_UiMenuBox : Node3D, IUiSurfaceView
{
	/// <summary>An element path inside this box's art, through its template record — the
	/// built-in path is the key, so a mod's art re-points it in `boxPaths`.</summary>
	private static string P(string path) => Engine_UiTemplates.BoxPath(Engine_UiTemplates.MenuBoxId, path);

	/// <summary>The bank entry name of the guest's presentation.</summary>
	public static string Presentation => Engine_UiTemplates.BoxPresentation(Engine_UiTemplates.MenuBoxId, "menubox");

	/// <summary>The authored option cap — the guest refuses a seventh.</summary>
	public const int MaxOptions = 6;

	/// <summary>The remaster's readability bump on the option labels — see FromPatternBook.</summary>
	private const float ChoiceTextScale = 2f;

	public static string GroupName => Engine_UiTemplates.BoxUnit(Engine_UiTemplates.MenuBoxId, 0, "Group");

	public Engine_UiTheme? Theme { get; set; }

	public bool HasShippedArt { get; private set; }

	public string LastReport { get; private set; } = string.Empty;

	private Node3D? _group;

	/// <summary>
	/// Which option a canvas point is on, 0-based; -2 for the cancel prompt; -1 for neither.
	/// </summary>
	public int OptionAt(Vector2 at)
	{
		if (_group is null)
		{
			return -1;
		}

		for (int i = 0; i < MaxOptions; ++i)
		{
			if (_group.GetNodeOrNull<Node3D>(P("Choices/Multiplayer_Button") + $"{i + 1}") is
					{ Visible: true } button &&
				Engine_UiPointer.CanvasBounds(button) is { } bounds && bounds.HasPoint(at))
			{
				return i;
			}
		}

		return _group.GetNodeOrNull<Node3D>(P("OK/CANCEL/Negative")) is { Visible: true } cancel &&
			Engine_UiPointer.CanvasBounds(cancel) is { } box && box.HasPoint(at)
				? -2
				: -1;
	}

	/// <summary>Document ordinals: the title, and per option its button root and label.</summary>
	public sealed class Joins
	{
		public int Title = -1;
		public readonly int[] Buttons = new int[MaxOptions];
		public readonly int[] Labels = new int[MaxOptions];
		public int Cancel = -1;

		/// <summary>`Group/GamerTag/gamertag` — the player card (name, reputation,
		/// gamerscore). AUTHORED AT OPACITY 0: only the online Orb box (`sub_823DB968` →
		/// `sub_823DB850`, the card object at controller+40) raises it. -1 when absent.</summary>
		public int GamerTag = -1;

		/// <summary>The card's authored opacity, 0..100, for when the guest states none.</summary>
		public float GamerTagAuthoredOpacity;
	}

	public static Joins? Resolve(Engine_UiScreen screen)
	{
		UiNode? group = Engine_UiStatedPanel.FindByName(screen.Roots, GroupName);
		UiNode? choices = Engine_UiStatedPanel.Child(group, P("Choices"));
		UiNode? title = Engine_UiStatedPanel.Child(group, P("Title"));
		if (choices is null || title is null)
		{
			return null;
		}

		var joins = new Joins { Title = title.Ordinal };
		for (int i = 0; i < MaxOptions; ++i)
		{
			// The shipped file frames the label (`Choice1/Frame/Choice1`), as the art clone
			// already knows; the bare form is kept for a file without the frame.
			UiNode? button = Engine_UiStatedPanel.Child(choices, P("Multiplayer_Button") + $"{i + 1}");
			UiNode? choice = Engine_UiStatedPanel.Child(button, P("Choice1"));
			UiNode? label =
				Engine_UiStatedPanel.Child(Engine_UiStatedPanel.Child(choice, P("Frame")), P("Choice1")) ??
				Engine_UiStatedPanel.Child(choice, P("Choice1"));
			if (button is null || label is null)
			{
				return null;
			}

			joins.Buttons[i] = button.Ordinal;
			joins.Labels[i] = label.Ordinal;
		}

		joins.Cancel = Engine_UiStatedPanel.Child(
			Engine_UiStatedPanel.Child(group, P("OK/CANCEL")), P("Negative"))?.Ordinal ?? -1;
		if (Engine_UiStatedPanel.Child(Engine_UiStatedPanel.Child(group, P("GamerTag")), P("gamertag"))
			is { } card)
		{
			joins.GamerTag = card.Ordinal;
			joins.GamerTagAuthoredOpacity = card.Opacity;
		}

		return joins;
	}

	public static Engine_UiMenuBox FromPatternBook(Engine_UiTheme? book, string name = "MenuBox")
	{
		var panel = new Engine_UiMenuBox { Name = name, Visible = false, Theme = book };

		if (book is null)
		{
			panel.LastReport = "no pattern book — the menu box has no art";
			return panel;
		}

		Node3D? source = book.ArtSourceNode(GroupName);
		Node3D? clone = book.Art(GroupName);
		if (source is not null && clone is not null)
		{
			clone.Transform = Engine_UiTheme.CanvasTransform(source);

			// Private materials with their authored alpha remembered: rows are ghosted per
			// option, and a shared material would fade the pattern book.
			Engine_UiSceneBuilder.OwnSubtreeMaterials(clone);

			// REMASTER: the option labels are authored at node scale 0.4 over an 18pt face —
			// about seven pixels at the original 720 canvas, and the shipped game showed them
			// that small too. The frame band around each row is ~24 units tall, so doubling the
			// label still sits inside it. Only the TEXT scales; the art keeps its layout.
			for (int i = 0; i < MaxOptions; ++i)
			{
				// The shipped file frames the label: `Choice1/Frame/Choice1`.
				Node3D? label =
					clone.GetNodeOrNull<Node3D>(
						$"Choices/Multiplayer_Button{i + 1}/Choice1/Frame/Choice1") ??
					clone.GetNodeOrNull<Node3D>(
						$"Choices/Multiplayer_Button{i + 1}/Choice1/Choice1");
				if (label is not null)
				{
					label.Scale *= ChoiceTextScale;
					if (label is MeshInstance3D { MaterialOverride: ShaderMaterial material } text)
						text.MaterialOverride = Engine_UiSceneBuilder.CopyMaterial(material);
				}
				if (clone.GetNodeOrNull<Node3D>(P("Choices/Multiplayer_Button") + $"{i + 1}/" + P("Choice1/RimSelect")) is { } rim)
					RevealSelectionArt(rim);
			}

			panel.AddChild(clone);
			panel._group = clone;
			panel.HasShippedArt = true;
		}

		panel.LastReport = panel.HasShippedArt ? "authored group cloned" : "group NOT found";
		return panel;
	}

	private static void RevealSelectionArt(Node node)
	{
		if (node is Node3D visible) visible.Show();
		if (node is MeshInstance3D mesh && Engine_UiSceneBuilder.SetQuadOpacity(mesh, 1f))
			mesh.SetMeta(Engine_UiSceneBuilder.AuthoredAlphaMeta, 1f);
		foreach (Node child in node.GetChildren()) RevealSelectionArt(child);
	}

	/// <summary>
	/// Shows the guest's stated list: the title, one row per option with its live label, the
	/// selection on the guest's slide convention, disabled rows ghosted, and the cancel prompt only
	/// when the box allows it.
	/// </summary>
	public void ShowStated(
		string? title, string?[] labels, int count, int selected,
		uint enabledMask, bool cancelable, bool gamerTag = false)
	{
		if (_group is null || Theme is null)
		{
			LastReport = "no art — not standing in";
			return;
		}

		// The player card is cloned with the group and shown only while the guest raises it —
		// see Joins.GamerTag.
		if (_group.GetNodeOrNull<Node3D>(P("GamerTag")) is { } card)
		{
			card.Visible = gamerTag;
		}

		if (!string.IsNullOrEmpty(title) &&
			_group.GetNodeOrNull<MeshInstance3D>(P("Title")) is { } titleNode)
		{
			LastReport = Theme.SetMeshText(titleNode, title);
		}

		for (int i = 0; i < MaxOptions; ++i)
		{
			if (_group.GetNodeOrNull<Node3D>(P("Choices/Multiplayer_Button") + $"{i + 1}") is not { } button)
			{
				continue;
			}

			bool present = i < count;
			button.Visible = present;
			if (!present)
			{
				continue;
			}

			bool enabled = (enabledMask & (1u << i)) != 0;
			float opacity = !enabled ? 0.35f : i == selected ? 1f : 0.7f;
			if ((button.GetNodeOrNull<MeshInstance3D>(P("Choice1/Frame/Choice1")) ??
				 button.GetNodeOrNull<MeshInstance3D>(P("Choice1/Choice1"))) is { } labelNode)
			{
				if (labels[i] is { Length: > 0 } label) Theme.SetMeshText(labelNode, label);
				if (labelNode.MaterialOverride is ShaderMaterial material)
					material.SetShaderParameter("opacity", opacity);
			}

			// THE SELECTION RIM — `Choice1/RimSelect` and its `sliderframe` quad, authored OFF on
			// every row and lit by the guest on the selected one. The cursor is ours (the guest's
			// follows through MenuBoxSelect), so the rim follows the stated selection.
			if (button.GetNodeOrNull<Node3D>(P("Choice1/RimSelect")) is { } rim)
			{
				rim.Visible = i == selected;
			}

			// Host-owned highlight: the selected row at full strength, the rest dimmed, a
			// disabled option dimmer still.
			Engine_UiSceneBuilder.FadeSubtree(button, opacity);
		}

		if (_group.GetNodeOrNull<Node3D>(P("OK/CANCEL/Negative")) is { } cancel)
		{
			cancel.Visible = cancelable;
		}

		Visible = true;
	}
}

/// <summary>
/// The in-game message box (modal mode 2), Godot-owned: the guest opens it, fits its text,
/// and states the size; Godot draws it and dismisses through the guest's own input router.
/// </summary>
/// <remarks>
/// The opener's fitter (`sub_823CEFA8`) measures the text, writes it into the first of
/// `MessageText1..6` whose authored box fits, and sizes the `paper` frame by putting it on that
/// size's element slide. The live state therefore carries the chosen text ordinal (the one with
/// non-empty text) and the nine-slice's resolved scale (the live transform on `paperbox`); this
/// view re-states both on its clone and never re-measures.
///
/// The `Action` row (X) belongs to the marketplace-offer box alone (query id 42), which stays
/// mirrored, so the row is hidden here unconditionally.
/// </remarks>
public partial class Engine_UiMessageBox : Node3D, IUiSurfaceView
{
	/// <summary>An element path inside this box's art, through its template record — the
	/// built-in path is the key, so a mod's art re-points it in `boxPaths`.</summary>
	private static string P(string path) => Engine_UiTemplates.BoxPath(Engine_UiTemplates.MessageBoxId, path);

	/// <summary>The bank entry name of the guest's presentation.</summary>
	public static string Presentation => Engine_UiTemplates.BoxPresentation(Engine_UiTemplates.MessageBoxId, "messageboxmulti");

	/// <summary>Authored group names, as `messageboxmulti.bgf` names them.</summary>
	public static string PaperName => Engine_UiTemplates.BoxUnit(Engine_UiTemplates.MessageBoxId, 1, "paper");
	public static string DimName => Engine_UiTemplates.BoxUnit(Engine_UiTemplates.MessageBoxId, 0, "Background");

	/// <summary>The authored text-size variants, smallest first.</summary>
	public const int TextSizes = 6;

	public Engine_UiTheme? Theme { get; set; }

	public bool HasShippedArt { get; private set; }

	public string LastReport { get; private set; } = string.Empty;

	private Node3D? _paper;

	/// <summary>Document ordinals: the six size-variant texts, and `paperbox` (with its `paper`
	/// parent as fallback) for the guest's live scale — the size slide may write either.</summary>
	public sealed class Joins
	{
		public readonly int[] Texts = new int[TextSizes];
		public int PaperBox = -1;
		public int Paper = -1;
	}

	public static Joins? Resolve(Engine_UiScreen screen)
	{
		UiNode? paper = Engine_UiStatedPanel.FindByName(screen.Roots, PaperName);
		UiNode? box = Engine_UiStatedPanel.Child(paper, P("MessageBox"));
		UiNode? paperBox = Engine_UiStatedPanel.Child(paper, P("paperbox"));
		if (paper is null || box is null || paperBox is null)
		{
			return null;
		}

		var joins = new Joins { PaperBox = paperBox.Ordinal, Paper = paper.Ordinal };
		for (int i = 0; i < TextSizes; ++i)
		{
			UiNode? text = Engine_UiStatedPanel.Child(box, P("MessageText") + $"{i + 1}");
			if (text is null)
			{
				return null;
			}

			joins.Texts[i] = text.Ordinal;
		}

		return joins;
	}

	public static Engine_UiMessageBox FromPatternBook(Engine_UiTheme? book, string name = "MessageBox")
	{
		var panel = new Engine_UiMessageBox { Name = name, Visible = false, Theme = book };

		if (book is null)
		{
			panel.LastReport = "no pattern book — the box has no art";
			return panel;
		}

		int cloned = 0;
		foreach (string piece in new[] { DimName, PaperName })
		{
			Node3D? source = book.ArtSourceNode(piece);
			Node3D? clone = book.Art(piece);
			if (source is null || clone is null)
			{
				continue;
			}

			clone.Transform = Engine_UiTheme.CanvasTransform(source);
			Engine_UiStatedPanel.CoverWindow(clone, piece);
			panel.AddChild(clone);
			if (piece == PaperName)
			{
				panel._paper = clone;
			}

			++cloned;
		}

		panel.HasShippedArt = cloned == 2;
		panel.LastReport = $"{cloned}/2 authored groups cloned";

		// The X row is the unowned marketplace box's; see the class remarks.
		if (panel._paper?.GetNodeOrNull<Node3D>(P("MessageBox/Action")) is { } action)
		{
			action.Visible = false;
		}

		return panel;
	}

	/// <summary>
	/// Shows the guest's stated text on the guest's chosen size variant, with the paper put on
	/// that size's slide.
	/// </summary>
	/// <remarks>
	/// THE SIZE IS THE WHOLE SLIDE, not a scale. `sub_823CEFA8` states the size with one
	/// slide switch (`sub_82426BA8(paper, i)`) and writes nothing else; each `typeN` scales
	/// `paperbox`, switches the nine-slice's `middle` pieces on for sizes 4..6, re-seats its
	/// rows and moves the `Affirmative` prompt. Taking the scale alone drew a size-4 notice
	/// on the two-column paper with the prompt in the middle of the text.
	/// </remarks>
	public void ShowStated(string body, int textIndex, BgfSlide? size)
	{
		if (_paper is null || Theme is null)
		{
			LastReport = "no art — not standing in";
			return;
		}

		if (size is { } slide)
		{
			ApplySlide(slide);
		}

		for (int i = 0; i < TextSizes; ++i)
		{
			if (_paper.GetNodeOrNull<Node3D>(P("MessageBox/MessageText") + $"{i + 1}") is { } text)
			{
				text.Visible = i == textIndex;
			}
		}

		// The X row is the unowned marketplace box's; see the class remarks.
		if (_paper.GetNodeOrNull<Node3D>(P("MessageBox/Action")) is { } action)
		{
			action.Visible = false;
		}

		if (_paper.GetNodeOrNull<MeshInstance3D>(P("MessageBox/MessageText") + $"{textIndex + 1}") is { } node)
		{
			LastReport = Theme.SetMeshText(node, body);
		}

		Visible = true;
	}

	private System.Collections.Generic.Dictionary<int, Node3D>? _byOrdinal;

	/// <summary>
	/// States one slide's entries on the clone: each named element's switch, and its
	/// position / scale / rotation.z writes, through the pivot-keeping writers.
	/// </summary>
	private void ApplySlide(BgfSlide slide)
	{
		if (_byOrdinal is null)
		{
			_byOrdinal = new System.Collections.Generic.Dictionary<int, Node3D>();
			Index(_paper!);

			void Index(Node node)
			{
				if (node is Node3D element && element.HasMeta("ordinal"))
				{
					_byOrdinal![(int)element.GetMeta("ordinal")] = element;
				}

				foreach (Node child in node.GetChildren())
				{
					Index(child);
				}
			}
		}

		uint positionX = Refii.Core.Engine_GameHash.AttributeId("position.x");
		uint positionY = Refii.Core.Engine_GameHash.AttributeId("position.y");
		uint scaleX = Refii.Core.Engine_GameHash.AttributeId("scale.x");
		uint scaleY = Refii.Core.Engine_GameHash.AttributeId("scale.y");
		uint rotationZ = Refii.Core.Engine_GameHash.AttributeId("rotation.z");

		foreach (BgfSlideEntry entry in slide.Entries)
		{
			if (!_byOrdinal.TryGetValue(entry.ElementOrdinal, out Node3D? node) || node == _paper)
			{
				continue;
			}

			node.Visible = entry.Active;
			if (entry.Commands is null)
			{
				continue;
			}

			float? x = null, y = null, sx = null, sy = null, rz = null;
			foreach (BgfAttribute command in entry.Commands)
			{
				if (command.Type != BgfValueType.Float)
				{
					continue;
				}

				if (command.Id == positionX) x = command.AsFloat();
				else if (command.Id == positionY) y = command.AsFloat();
				else if (command.Id == scaleX) sx = command.AsFloat();
				else if (command.Id == scaleY) sy = command.AsFloat();
				else if (command.Id == rotationZ) rz = command.AsFloat();
			}

			if (rz is { } turn)
			{
				Engine_UiSceneBuilder.SetLiveRotationZ(node, turn);
			}

			if (sx is > 0f && sy is > 0f)
			{
				Engine_UiSceneBuilder.SetLiveScale(node, new Vector3(sx.Value, sy.Value, 1f));
			}

			if (x is not null || y is not null)
			{
				Vector3 offset = node.Transform.Basis * Engine_UiSceneBuilder.PivotOf(node);
				Vector3 stated = node.Position + offset;
				node.Position = new Vector3(x ?? stated.X, y ?? stated.Y, stated.Z) - offset;
			}
		}
	}

}

/// <summary>
/// The in-game query box, Godot-owned: the guest opens it and states its text; Godot draws
/// it and answers through the guest's own input router.
/// </summary>
/// <remarks>
/// The body is written by the guest's `querybox.lua` as a live `textstring` on the
/// `QueryBox/Text` element, so display text is read from the live elements — never a host-side
/// tag lookup. The art is `querybox.bgf`'s: the `QueryBox` group (text and the authored A/B
/// choice rows), the 50%-opacity `Background` dim, and the nine-slice `Paper` frame, cloned from
/// the file-only pattern book at their authored transforms
/// (<see cref="Engine_UiTheme.CanvasTransform"/>; a zero-rooted clone loses the placement).
///
/// The file authors three choice rows — `Choices` (A yes / B no), `XAChoices` and
/// `ReloadChoices`, the latter two shipped inactive. Which row a box uses is the guest's
/// statement (the opener slides the root for ids 20 and 13), read off the live active flags and
/// passed to <see cref="ShowStated"/>.
/// </remarks>
public partial class Engine_UiQueryBox : Node3D, IUiSurfaceView
{
	/// <summary>An element path inside this box's art, through its template record — the
	/// built-in path is the key, so a mod's art re-points it in `boxPaths`.</summary>
	private static string P(string path) => Engine_UiTemplates.BoxPath(Engine_UiTemplates.QueryBoxId, path);

	/// <summary>The bank entry name of the guest's presentation.</summary>
	public static string Presentation => Engine_UiTemplates.BoxPresentation(Engine_UiTemplates.QueryBoxId, "querybox");

	/// <summary>Authored group names, as `querybox.bgf` names them.</summary>
	public static string BoxName => Engine_UiTemplates.BoxUnit(Engine_UiTemplates.QueryBoxId, 0, "QueryBox");
	public static string DimName => Engine_UiTemplates.BoxUnit(Engine_UiTemplates.QueryBoxId, 1, "Background");
	public static string PaperName => Engine_UiTemplates.BoxUnit(Engine_UiTemplates.QueryBoxId, 2, "Paper");

	/// <summary>Fonts and strings for the re-meshed text.</summary>
	public Engine_UiTheme? Theme { get; set; }

	/// <summary>True when the shipped art was cloned; false means text-only stand-ins.</summary>
	public bool HasShippedArt { get; private set; }

	public string LastReport { get; private set; } = string.Empty;

	private Node3D? _box;

	/// <summary>
	/// The three authored choice rows and their child choices. The opener puts the root on a slide
	/// (`Choices` is the default A/B pair; box 20 gets `XAChoices`, box 13 `ReloadChoices`) and the
	/// live active flags say which one stood up.
	/// </summary>
	public static readonly (string Row, string First, string Second)[] ChoiceRows =
	{
		(P("Choices"), P("Affirmative"), P("Negative")),
		(P("XAChoices"), P("EquipAndContinue"), P("Continue")),
		(P("ReloadChoices"), P("Reload"), P("Cancel")),
	};

	/// <summary>One row's document ordinals: the row root and its two label texts.</summary>
	public readonly record struct RowJoin(string Name, int Row, int First, int Second);

	/// <summary>
	/// Document ordinals of the live-text elements, resolved by path: the screen has five
	/// elements named "Text", so a flat name join re-meshes somebody else's label.
	/// </summary>
	public sealed class Joins
	{
		public int Body = -1;
		public readonly System.Collections.Generic.List<RowJoin> Rows = new();
	}

	/// <summary>Resolves the live-text ordinals from the parsed document. Null when the
	/// authored structure is not what this class knows how to drive.</summary>
	public static Joins? Resolve(Engine_UiScreen screen)
	{
		UiNode? box = Engine_UiStatedPanel.FindByName(screen.Roots, BoxName);
		UiNode? body = Engine_UiStatedPanel.Child(box, P("Text"));
		if (box is null || body is null)
		{
			return null;
		}

		var joins = new Joins { Body = body.Ordinal };
		foreach ((string rowName, string first, string second) in ChoiceRows)
		{
			UiNode? row = Engine_UiStatedPanel.Child(box, rowName);
			UiNode? firstLabel = Engine_UiStatedPanel.Child(Engine_UiStatedPanel.Child(row, first), P("Text"));
			UiNode? secondLabel = Engine_UiStatedPanel.Child(Engine_UiStatedPanel.Child(row, second), P("Text"));
			if (row is not null && firstLabel is not null && secondLabel is not null)
			{
				joins.Rows.Add(new RowJoin(
					rowName, row.Ordinal, firstLabel.Ordinal, secondLabel.Ordinal));
			}
		}

		// The default A/B row is the one every owned box id needs.
		return joins.Rows.Count > 0 && joins.Rows[0].Name == ChoiceRows[0].Row ? joins : null;
	}

	/// <summary>
	/// Builds the view from the file-only pattern book: the three authored groups cloned at their
	/// authored canvas transforms. A missing book or art degrades to an empty view that reports
	/// itself.
	/// </summary>
	public static Engine_UiQueryBox FromPatternBook(Engine_UiTheme? book, string name = "QueryBox")
	{
		var panel = new Engine_UiQueryBox { Name = name, Visible = false, Theme = book };

		if (book is null)
		{
			panel.LastReport = "no pattern book — the box has no art";
			return panel;
		}

		int cloned = 0;
		Node3D? paper = null;
		foreach (string piece in new[] { BoxName, DimName, PaperName })
		{
			Node3D? source = book.ArtSourceNode(piece);
			Node3D? clone = book.Art(piece);
			if (source is null || clone is null)
			{
				continue;
			}

			// Art() zeroes the clone's root, and this screen authors its depth ordering across the
			// group roots, so the authored placement is restored.
			clone.Transform = Engine_UiTheme.CanvasTransform(source);
			Engine_UiStatedPanel.CoverWindow(clone, piece);
			panel.AddChild(clone);
			if (piece == BoxName)
			{
				panel._box = clone;
			}
			else if (piece == PaperName)
			{
				paper = clone;
			}

			++cloned;
		}

		panel.HasShippedArt = cloned == 3;
		panel.LastReport = $"{cloned}/3 authored groups cloned";

		// The A/B row is the pause family's; the alternates stay down until a box using them is
		// owned.
		panel.SetRow(P("Choices"), true);
		panel.SetRow(P("XAChoices"), false);
		panel.SetRow(P("ReloadChoices"), false);

		// The paper sized to the text, the choice rows riding its bottom edge.
		panel.Paper = Engine_UiPaperBox.For(panel, paper,
			panel._box?.GetNodeOrNull<Node3D>(P("Choices")),
			panel._box?.GetNodeOrNull<Node3D>(P("XAChoices")),
			panel._box?.GetNodeOrNull<Node3D>(P("ReloadChoices")));

		return panel;
	}

	/// <summary>
	/// The paper sized to the body (<see cref="Engine_UiPaperBox"/>, the front end's dialog's too):
	/// grown for a body longer than the authored window, scrolled past the window's maximum. Null
	/// without the shipped nine-slice, when the box keeps its authored size.
	/// </summary>
	public Engine_UiPaperBox? Paper { get; private set; }

	/// <summary>One frame of the body's scroll: the stick, the d-pad, and the pointer's drag or wheel.</summary>
	public void Scroll(float stick, int direction, double delta)
	{
		if (Paper is null)
		{
			return;
		}

		Paper.Pointer();
		Paper.Scroll(stick, direction, delta);
	}

	/// <summary>A click (already taken) on the scroll bar starts a drag; false leaves it for the answers.</summary>
	public bool PressBar(Vector2 at) => Paper?.Press(at) ?? false;

	/// <summary>The box closed: the next body starts at the top of an authored-size paper.</summary>
	public void Release() => Paper?.Release();

	/// <summary>
	/// Puts the guest's stated strings on the authored elements and stands the row the guest chose.
	/// Empty label strings keep the authored tags, which the book's Babel strings already resolved
	/// at build time. <paramref name="single"/> stands the row's first choice alone — a host notice,
	/// which has one answer.
	/// </summary>
	public void ShowStated(string body, string activeRow, string? first, string? second, bool single = false)
	{
		if (_box is null || Theme is null)
		{
			// No art refuses to stand rather than drawing an empty panel: the caller's mirror
			// fallback and the guest's input stay live only while this view is invisible.
			LastReport = "no art — not standing in";
			return;
		}

		// `QueryBox.Open` writes `g_Theme:GetNormalTextColor()` into the body's `textcolor.r/g/b`
		// (`querybox.dis main.1 pc32..53`). The colour must lead here, since the re-mesh reads it.
		string report = SetMeshTextSafe(
			BodyNode(), body, Engine_UiBoxConstants.NormalText);
		if (Paper is not null && BodyNode() is { } bodyNode)
		{
			Paper.Layout(bodyNode, body);
			report += ", " + Paper.Report;
		}

		(string Row, string First, string Second) descriptor = ChoiceRows[0];
		foreach ((string Row, string First, string Second) candidate in ChoiceRows)
		{
			SetRow(candidate.Row, candidate.Row == activeRow);
			if (candidate.Row == activeRow)
			{
				descriptor = candidate;
			}
		}

		if (!string.IsNullOrEmpty(first))
		{
			SetMeshTextSafe(LabelNode(descriptor.Row, descriptor.First), first);
		}

		if (!string.IsNullOrEmpty(second))
		{
			SetMeshTextSafe(LabelNode(descriptor.Row, descriptor.Second), second);
		}

		if (_box?.GetNodeOrNull<Node3D>($"{descriptor.Row}/{descriptor.Second}") is { } secondChoice)
		{
			secondChoice.Visible = !single;
		}

		LastReport = report;
		Visible = true;
	}

	/// <summary>
	/// Which answer a canvas point is on: +1 the A choice, -1 the B choice, 0 neither. The
	/// default and reload rows are A then B; `XAChoices` is X then A, and X has no answer here.
	/// </summary>
	public int AnswerAt(Vector2 at)
	{
		foreach ((string Row, string First, string Second) choice in ChoiceRows)
		{
			if (_box?.GetNodeOrNull<Node3D>(choice.Row) is not { Visible: true } row)
			{
				continue;
			}

			bool xa = choice.Row == ChoiceRows[1].Row;
			if (Covers(row.GetNodeOrNull<Node3D>(choice.First), at))
			{
				return xa ? 0 : 1;
			}

			if (Covers(row.GetNodeOrNull<Node3D>(choice.Second), at))
			{
				return xa ? 1 : -1;
			}
		}

		return 0;
	}

	private static bool Covers(Node3D? node, Vector2 at) =>
		node is not null && Engine_UiPointer.CanvasBounds(node) is { } bounds && bounds.HasPoint(at);

	private MeshInstance3D? BodyNode() => _box?.GetNodeOrNull<MeshInstance3D>(P("Text"));

	private MeshInstance3D? LabelNode(string row, string choice) =>
		_box?.GetNodeOrNull<MeshInstance3D>($"{row}/{choice}/" + P("Text"));

	/// <summary>
	/// Re-meshes one text node, applying the script's theme colour first: `text_colour` is what
	/// <see cref="Engine_UiTheme.SetMeshText"/> reads.
	/// </summary>
	private string SetMeshTextSafe(MeshInstance3D? node, string display, Color? colour = null)
	{
		if (node is null || Theme is null)
		{
			return "text node not found";
		}

		if (colour is { } stated)
		{
			node.SetMeta("text_colour", stated);
		}

		return Theme.SetMeshText(node, display);
	}

	private void SetRow(string name, bool shown)
	{
		if (_box?.GetNodeOrNull<Node3D>(name) is { } row)
		{
			row.Visible = shown;
		}
	}

}

/// <summary>
/// The numeric adjuster (modal mode 5), Godot-owned: the guest opens it over the real-estate
/// and gift screens with a question, a value and its bounds; Godot draws it and drives it
/// through the guest's own input router.
/// </summary>
/// <remarks>
/// The widget (guiOwner+64) formats the value — including the %-suffix and +-prefix its flags
/// select — into the live `textstring` of `QueryBox/PriceWidget/Text`, and highlights the up/down
/// arrows by whether a step fits the remaining range; the question is live text on
/// `QueryBox/Text`. The value never lives host-side. Input codes through the router: 28 increment,
/// 29 decrement (the guest's accelerating step logic runs), 46 commit (the underlying screen's
/// handler reads the value off the widget), 47 cancel.
/// </remarks>
public partial class Engine_UiNumericBox : Node3D, IUiSurfaceView
{
	/// <summary>An element path inside this box's art, through its template record — the
	/// built-in path is the key, so a mod's art re-points it in `boxPaths`.</summary>
	private static string P(string path) => Engine_UiTemplates.BoxPath(Engine_UiTemplates.NumericBoxId, path);

	/// <summary>The bank entry name of the guest's presentation.</summary>
	public static string Presentation => Engine_UiTemplates.BoxPresentation(Engine_UiTemplates.NumericBoxId, "numericadjusterbox");

	/// <summary>Authored group names, as `numericadjusterbox.bgf` names them.</summary>
	public static string BoxName => Engine_UiTemplates.BoxUnit(Engine_UiTemplates.NumericBoxId, 0, "QueryBox");
	public static string DimName => Engine_UiTemplates.BoxUnit(Engine_UiTemplates.NumericBoxId, 1, "Background");
	public static string PaperName => Engine_UiTemplates.BoxUnit(Engine_UiTemplates.NumericBoxId, 2, "Paper");

	public Engine_UiTheme? Theme { get; set; }

	public bool HasShippedArt { get; private set; }

	public string LastReport { get; private set; } = string.Empty;

	private Node3D? _box;

	/// <summary>Document ordinals of the live-stated elements.</summary>
	public sealed class Joins
	{
		public int Body = -1;
		public int Value = -1;
		public int ArrowUp = -1;
		public int ArrowDown = -1;
	}

	public static Joins? Resolve(Engine_UiScreen screen)
	{
		UiNode? box = Engine_UiStatedPanel.FindByName(screen.Roots, BoxName);
		UiNode? body = Engine_UiStatedPanel.Child(box, P("Text"));
		UiNode? widget = Engine_UiStatedPanel.Child(box, P("PriceWidget"));
		UiNode? value = Engine_UiStatedPanel.Child(widget, P("Text"));
		UiNode? arrows = Engine_UiStatedPanel.Child(widget, P("Arrows"));
		UiNode? up = Engine_UiStatedPanel.Child(arrows, P("up"));
		UiNode? down = Engine_UiStatedPanel.Child(arrows, P("down"));

		if (body is null || value is null || up is null || down is null)
		{
			return null;
		}

		return new Joins
		{
			Body = body.Ordinal,
			Value = value.Ordinal,
			ArrowUp = up.Ordinal,
			ArrowDown = down.Ordinal,
		};
	}

	public static Engine_UiNumericBox FromPatternBook(Engine_UiTheme? book, string name = "NumericBox")
	{
		var panel = new Engine_UiNumericBox { Name = name, Visible = false, Theme = book };

		if (book is null)
		{
			panel.LastReport = "no pattern book — the box has no art";
			return panel;
		}

		int cloned = 0;
		foreach (string piece in new[] { BoxName, DimName, PaperName })
		{
			Node3D? source = book.ArtSourceNode(piece);
			Node3D? clone = book.Art(piece);
			if (source is null || clone is null)
			{
				continue;
			}

			clone.Transform = Engine_UiTheme.CanvasTransform(source);
			Engine_UiStatedPanel.CoverWindow(clone, piece);
			panel.AddChild(clone);
			if (piece == BoxName)
			{
				panel._box = clone;
			}

			++cloned;
		}

		panel.HasShippedArt = cloned == 3;
		panel.LastReport = $"{cloned}/3 authored groups cloned";
		return panel;
	}

	/// <summary>Shows the guest's stated question, formatted value and arrow availability.</summary>
	public void ShowStated(string body, string value, bool canIncrement, bool canDecrement)
	{
		if (_box is null || Theme is null)
		{
			LastReport = "no art — not standing in";
			return;
		}

		if (body.Length != 0 && _box.GetNodeOrNull<MeshInstance3D>(P("Text")) is { } bodyNode)
		{
			LastReport = Theme.SetMeshText(bodyNode, body);
		}

		if (_box.GetNodeOrNull<MeshInstance3D>(P("PriceWidget/Text")) is { } valueNode)
		{
			Theme.SetMeshText(valueNode, value);
		}

		SetArrow("up", canIncrement);
		SetArrow("down", canDecrement);

		Visible = true;
	}

	/// <summary>Which arrow a canvas point is on: -1 up (increment), +1 down, 0 neither.</summary>
	public int ArrowAt(Vector2 at)
	{
		foreach ((string arrow, int step) in new[] { ("up", -1), ("down", 1) })
		{
			if (_box?.GetNodeOrNull<Node3D>(P("PriceWidget/Arrows/") + arrow) is { Visible: true } node &&
				Engine_UiPointer.CanvasBounds(node) is { } bounds && bounds.HasPoint(at))
			{
				return step;
			}
		}

		return 0;
	}

	private void SetArrow(string arrow, bool enabled)
	{
		if (_box?.GetNodeOrNull<Node3D>(P("PriceWidget/Arrows/") + $"{arrow}") is { } node)
		{
			node.Visible = enabled;
		}
	}

}

/// <summary>
/// The received-item card (screen id 8), Godot-owned: the guest pushes it on every pickup
/// and states the item's name, description, icon and which side is up; Godot draws it and
/// presses its buttons through the handler's own screen-input method.
/// </summary>
/// <remarks>
/// `Foreground/NameAndIcon/Title` and `.../Text` carry the localised strings, `IconGraphic` the
/// item's icon as a live texture (the file's copy is a placeholder), the button prompts' active
/// flags say which of Done/Info/Action stand, and the card flip (Y) is the Foreground/CardBack
/// groups' active flags. Input: 36 done, 39 flip, 75 close — the pump's
/// "ReceiveItemsAction:&lt;code&gt;". This screen does not pause the game, so only A and Y are
/// masked while it stands.
/// </remarks>
public partial class Engine_UiReceiveItems : Node3D, IUiSurfaceView
{
	/// <summary>An element path inside this box's art, through its template record — the
	/// built-in path is the key, so a mod's art re-points it in `boxPaths`.</summary>
	private static string P(string path) => Engine_UiTemplates.BoxPath(8u, path);

	/// <summary>The bank entry name of the guest's presentation.</summary>
	public static string Presentation => Engine_UiTemplates.BoxPresentation(8u, "receiveitemsscreen");

	public static string FrontName => Engine_UiTemplates.BoxUnit(8u, 0, "Foreground");
	public static string BackName => Engine_UiTemplates.BoxUnit(8u, 1, "CardBack");

	public Engine_UiTheme? Theme { get; set; }

	public bool HasShippedArt { get; private set; }

	public string LastReport { get; private set; } = string.Empty;

	private Node3D? _front;
	private Node3D? _back;
	private ShaderMaterial? _iconMaterial;
	private uint _iconHash;

	/// <summary>Document ordinals for the live joins.</summary>
	public sealed class Joins
	{
		public int Title = -1;
		public int Name = -1;
		public int BackText = -1;
		public int Icon = -1;
		public int Front = -1;
		public int Back = -1;

		/// <summary>The slide owner `sub_82537128` flips: 3 `Flip2Back`, 4 `Flip2Front`.</summary>
		public int Root = -1;

		/// <summary>The front `Buttons` component: slide 1 usable, 2 not (`sub_82538910`).</summary>
		public int FrontButtons = -1;

		public int BackButtons = -1;

		/// <summary>The three prompt labels, front side: Action, Info, Done.</summary>
		public int[] Labels = { -1, -1, -1 };

		/// <summary>
		/// The file's `Mask` quad in canvas units — what the back's description is clipped to.
		/// Null when the file authors none.
		/// </summary>
		public Rect2? BackClip;
	}

	/// <summary>The prompt groups under `Buttons/Buttons`, in <see cref="Joins.Labels"/> order.</summary>
	public static readonly string[] Prompts = { "Action", "Info", "Done" };

	/// <summary>The drawn prompt a canvas point is on (one of <see cref="Prompts"/>), or null.</summary>
	public string? PromptAt(Vector2 at)
	{
		foreach (Node3D? side in new[] { _front, _back })
		{
			if (side is not { Visible: true })
			{
				continue;
			}

			foreach (string name in Prompts)
			{
				if (side.GetNodeOrNull<Node3D>(P("Buttons/Buttons/") + name) is { } prompt &&
					prompt.IsVisibleInTree() &&
					Engine_UiPointer.CanvasBounds(prompt) is { } bounds && bounds.HasPoint(at))
				{
					return name;
				}
			}
		}

		return null;
	}

	/// <summary>Root slides that leave the reverse up: `Flip2Back` and its `FlipOutro`.</summary>
	public const int FlipToBackSlide = 3;

	public const int FlipOutroSlide = 5;

	/// <summary>`Buttons` slide 2: the item cannot be used, `Action` stands at opacity 50.</summary>
	public const int UnusableSlide = 2;

	public static Joins? Resolve(Engine_UiScreen screen)
	{
		UiNode? front = Engine_UiStatedPanel.FindByName(screen.Roots, FrontName);
		UiNode? back = Engine_UiStatedPanel.FindByName(screen.Roots, BackName);
		UiNode? nameAndIcon = Engine_UiStatedPanel.Child(front, P("NameAndIcon"));
		UiNode? title = Engine_UiStatedPanel.Child(nameAndIcon, P("Title"));
		UiNode? name = Engine_UiStatedPanel.Child(nameAndIcon, P("Text"));
		UiNode? icon = Engine_UiStatedPanel.Child(nameAndIcon, P("IconGraphic"));
		UiNode? backText = back is null
			? null
			: Engine_UiStatedPanel.FindByName(back.Children, P("Text"));

		if (front is null || back is null || title is null || name is null || icon is null)
		{
			return null;
		}

		UiNode? frontButtons = Engine_UiStatedPanel.Child(front, P("Buttons"));
		UiNode? backButtons = Engine_UiStatedPanel.Child(back, P("Buttons"));
		UiNode? prompts = Engine_UiStatedPanel.Child(frontButtons, P("Buttons"));
		int[] labels = { -1, -1, -1 };
		for (int i = 0; i < Prompts.Length; ++i)
		{
			UiNode? prompt = Engine_UiStatedPanel.Child(prompts, Prompts[i]);
			labels[i] = Engine_UiStatedPanel.Child(prompt, Prompts[i])?.Ordinal ?? -1;
		}

		UiNode? root = front;
		while (root?.Parent is { } parent)
		{
			root = parent;
		}

		// `position ± 50·scale`, the builder's own mask rule; `Mask` sits at Layer level under
		// identity parents, so its own numbers are already canvas.
		Rect2? clip = null;
		if (Engine_UiStatedPanel.FindByName(screen.Roots, P("Mask")) is { } mask)
		{
			float halfW = 0.5f * Engine_UiSceneBuilder.QuadBaseSize * Mathf.Abs(mask.ScaleX);
			float halfH = 0.5f * Engine_UiSceneBuilder.QuadBaseSize * Mathf.Abs(mask.ScaleY);
			clip = new Rect2(
				mask.PositionX - halfW, mask.PositionY - halfH, 2f * halfW, 2f * halfH);
		}

		return new Joins
		{
			BackClip = clip,
			Root = root?.Ordinal ?? -1,
			FrontButtons = frontButtons?.Ordinal ?? -1,
			BackButtons = backButtons?.Ordinal ?? -1,
			Labels = labels,
			Title = title.Ordinal,
			Name = name.Ordinal,
			BackText = backText?.Ordinal ?? -1,
			Icon = icon.Ordinal,
			Front = front.Ordinal,
			Back = back.Ordinal,
		};
	}

	public static Engine_UiReceiveItems FromPatternBook(Engine_UiTheme? book, string name = "ReceiveItems")
	{
		var panel = new Engine_UiReceiveItems { Name = name, Visible = false, Theme = book };

		if (book is null)
		{
			panel.LastReport = "no pattern book — the card has no art";
			return panel;
		}

		int cloned = 0;
		foreach (string piece in new[] { FrontName, BackName })
		{
			Node3D? source = book.ArtSourceNode(piece);
			Node3D? clone = book.Art(piece);
			if (source is null || clone is null)
			{
				continue;
			}

			clone.Transform = Engine_UiTheme.CanvasTransform(source);
			Engine_UiStatedPanel.CoverWindow(clone, piece);
			panel.AddChild(clone);
			if (piece == FrontName)
			{
				panel._front = clone;
			}
			else
			{
				panel._back = clone;
				clone.Visible = false;
			}

			++cloned;
		}

		// The icon's material is privatised once: the clone shares the pattern book's, and swapping
		// a texture on a shared material would restyle the book.
		if (panel._front?.GetNodeOrNull<MeshInstance3D>(P("NameAndIcon/IconGraphic")) is { } iconMesh &&
			iconMesh.MaterialOverride is ShaderMaterial shared)
		{
			panel._iconMaterial = Engine_UiSceneBuilder.CopyMaterial(shared);
			iconMesh.MaterialOverride = panel._iconMaterial;
		}

		panel.HasShippedArt = cloned == 2;
		panel.LastReport = $"{cloned}/2 authored groups cloned" +
			$"{(panel._iconMaterial is null ? ", icon material NOT owned" : string.Empty)}";
		return panel;
	}

	/// <summary>Puts the guest's stated card on screen: strings, icon, and which side is up.</summary>
	/// <param name="reverse">The root stands on `Flip2Back`: the description side is up.</param>
	/// <param name="usable">`Buttons` slide 1. Slide 2 only dims `Action` to opacity 50 in the
	/// original; here a prompt that does nothing is not drawn.</param>
	/// <param name="labels">The stated prompt labels, in <see cref="Prompts"/> order.</param>
	public void ShowStated(
		string? title, string? name, string? backText,
		uint iconHash, Texture2D? icon,
		bool up, bool reverse, bool usable, string?[] labels)
	{
		if (_front is null || Theme is null)
		{
			LastReport = "no art — not standing in";
			return;
		}

		// Written even when empty, like the name below: `sub_82538DF8` starts the title at
		// hash("") and replaces it only when the item is found in the GUI player's inventory
		// (0x82538FDC), while the model is set regardless. Skipping an empty title left the
		// previous card's name over a new item's model.
		if (_front.GetNodeOrNull<MeshInstance3D>(P("NameAndIcon/Title")) is { } titleNode)
		{
			LastReport = Theme.SetMeshText(titleNode, title ?? string.Empty);
		}

		// WRITTEN EVEN WHEN EMPTY. `sub_82538910` writes this element on every refresh, and
		// for most items it writes nothing — leaving the file's own placeholder, the word
		// "Text", standing under the ring.
		if (_front.GetNodeOrNull<MeshInstance3D>(P("NameAndIcon/Text")) is { } nameNode)
		{
			Theme.SetMeshText(nameNode, name ?? string.Empty);
		}

		if (_back is not null && FindMesh(_back, "Text") is { } backNode)
		{
			// A new description, or the front coming back up, starts from the top.
			if (_backText != backNode || backText != _backShown || !reverse)
			{
				if (_backText is not null && GodotObject.IsInstanceValid(_backText) && _scroll != 0f)
				{
					_backText.Position = _backTextRest;
				}

				_backText = backNode;
				_backTextRest = backNode.Position;
				_backShown = backText;
				_scroll = 0f;
			}

			Theme.SetMeshText(backNode, backText ?? string.Empty);
			backNode.Visible = true;

			// CLIPPED IN THE CARD'S OWN SPACE. The builder bound this text to the file's `Mask`
			// in the pattern book's coordinates; the shader clips in WORLD xy, and the card is
			// drawn scaled and placed by its surface, so the rect is carried through this
			// panel's transform or the description is discarded whole.
			if (backNode.MaterialOverride is ShaderMaterial text && IsInsideTree())
			{
				Vector4 rect = new(-1e9f, -1e9f, 1e9f, 1e9f);
				if (BackClip is { } clip)
				{
					Vector3 a = GlobalTransform * new Vector3(clip.Position.X, clip.Position.Y, 0f);
					Vector3 b = GlobalTransform * new Vector3(clip.End.X, clip.End.Y, 0f);
					rect = new Vector4(
						Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y),
						Mathf.Max(a.X, b.X), Mathf.Max(a.Y, b.Y));
				}

				text.SetShaderParameter("clip_rect", rect);
				text.SetShaderParameter("opacity", 1f);

				string said = $"{backText?.Length ?? -1}|{rect}|{backNode.GlobalPosition}";
				if (reverse && said != _backSaid)
				{
					_backSaid = said;
					GD.Print($"[game-ui] received-item card back: {backText?.Length ?? -1} char(s), " +
						$"text at {backNode.GlobalPosition}, box {backNode.GetAabb().Size}, " +
						$"clip {rect}, in tree visible {backNode.IsVisibleInTree()}");
				}
			}
		}

		if (icon is not null && iconHash != _iconHash && _iconMaterial is not null)
		{
			_iconHash = iconHash;
			_iconMaterial.SetShaderParameter("diffuse_tex", icon);
		}

		foreach (Node3D? side in new[] { _front, _back })
		{
			if (side is null)
			{
				continue;
			}

			for (int i = 0; i < Prompts.Length && i < labels.Length; ++i)
			{
				if (side.GetNodeOrNull<Node3D>(P("Buttons/Buttons/") + $"{Prompts[i]}") is not { } prompt)
				{
					continue;
				}

				if (i == 0)
				{
					prompt.Visible = usable;
				}

				if (!string.IsNullOrEmpty(labels[i]) &&
					prompt.GetNodeOrNull<MeshInstance3D>(Prompts[i]) is { } label)
				{
					Theme.SetMeshText(label, labels[i]!);
				}
			}
		}

		// ONE SIDE AT A TIME. The original turns the card: the front's paper rotates edge-on
		// while its contents fade, then the back's comes round. The clone carries neither the
		// rotation nor the fade, so the sides swap outright — and the back's own parchment,
		// which the file authors off until its slide, is switched on with it.
		_front.Visible = up && !reverse;
		if (_back is not null)
		{
			_back.Visible = up && reverse;

			// `Flip2Back` commands `CardBack` opacity 100; the art was built from the master
			// slide, which authors 70.9 — the faded card.
			if (!_backRestated)
			{
				_backRestated = true;
				Engine_UiSceneBuilder.OwnSubtreeMaterials(_back);
				Engine_UiSceneBuilder.RestateOpacity(_back, 1f);
			}

			if (_back.GetNodeOrNull<Node3D>(P("PaperBackground")) is { } paper)
			{
				paper.Visible = true;
			}
		}

		Visible = up;
	}

	/// <summary>`sub_828C30A8` feeds ~3 canvas units a 30 Hz tick at full stick; as the books.</summary>
	private const float ScrollUnitsPerSecond = 150f;

	private MeshInstance3D? _backText;

	private Vector3 _backTextRest;

	private string? _backShown;

	private float _scroll;

	private float _scrollRange = -1f;

	/// <summary>
	/// THE BACK'S SCROLL REGION — `sub_828C30A8(handler+24, 27, …)` with the bar the handler
	/// registers in Open (`sub_828C33F0(handler+24, "CardBack.Group.Scrollbar")`). The stick
	/// slides the description inside the `Mask` band, which already clips it; the bar stands only
	/// while the text overflows, its arrows dim at their limits and the thumb rides the fraction.
	/// </summary>
	/// <summary>Canvas units one wheel notch scrolls the description.</summary>
	private const float WheelUnitsPerNotch = 30f;

	public void ScrollBack(float stick, double delta, int wheelNotches = 0)
	{
		if (_back is null || !_back.Visible || _backText is not { } text ||
			!GodotObject.IsInstanceValid(text) || !IsInsideTree() || BackClip is not { } clip)
		{
			return;
		}

		// How far the laid-out text runs below the band, in canvas units, measured at rest.
		float range = 0f;
		float unitsPerLocal = 1f;
		if (text.Mesh is not null && text.GetParent() is Node3D parent)
		{
			Transform3D toCanvas = GlobalTransform.AffineInverse() * parent.GlobalTransform;
			unitsPerLocal = Mathf.Max(0.0001f, toCanvas.Basis.Y.Length());
			float restY = (toCanvas * _backTextRest).Y;
			float lowest = restY + (text.GetAabb().Position.Y * unitsPerLocal);
			range = Mathf.Max(0f, clip.Position.Y - lowest);
		}

		_scroll = Mathf.Clamp(
			_scroll - (stick * ScrollUnitsPerSecond * (float)delta) + (wheelNotches * WheelUnitsPerNotch),
			0f, range);
		text.Position = _backTextRest + (Vector3.Up * (_scroll / unitsPerLocal));

		if (_back.GetNodeOrNull<Node3D>(P("Group")) is not { } group)
		{
			return;
		}

		bool scrollable = range > 1f;
		group.Visible = scrollable;
		if (!scrollable)
		{
			_scrollRange = range;
			return;
		}

		// `Group` stands at opacity 0 outside its slide; stated once per overflow, not per frame.
		if (!Mathf.IsEqualApprox(range, _scrollRange))
		{
			_scrollRange = range;
			Engine_UiSceneBuilder.RestateOpacity(group, 1f);
			GD.Print($"[game-ui] received-item card back scrolls {range:0} canvas unit(s)");
		}

		float fraction = _scroll / range;
		var up = group.GetNodeOrNull<Node3D>(P("Scrollbar/UpArrow"));
		var down = group.GetNodeOrNull<Node3D>(P("Scrollbar/DownArrow"));
		if (up is null || down is null)
		{
			return;
		}

		SetLimit(up, fraction > 0.01f, ref _upLit);
		SetLimit(down, fraction < 0.99f, ref _downLit);
		if (group.GetNodeOrNull<Node3D>(P("Scrollbar/Icon")) is { } thumb)
		{
			Vector3 at = thumb.Position;
			thumb.Position = new Vector3(
				at.X, Mathf.Lerp(up.Position.Y, down.Position.Y, fraction), at.Z);
		}
	}

	private bool? _upLit;

	private bool? _downLit;

	/// <summary>Slide 1 of an arrow is at-limit, 2 active (`sub_828C3488`); drawn as a dim.</summary>
	private static void SetLimit(Node3D arrow, bool lit, ref bool? was)
	{
		if (was == lit)
		{
			return;
		}

		was = lit;
		Engine_UiSceneBuilder.RestateOpacity(arrow, lit ? 1f : 0.35f);
	}

	/// <summary>The back's clip, canvas units; set by the surface from <see cref="Joins.BackClip"/>.</summary>
	public Rect2? BackClip { get; set; }

	private bool _backRestated;

	private string _backSaid = string.Empty;

	private static MeshInstance3D? FindMesh(Node node, string name)
	{
		if (node is MeshInstance3D mesh && node.Name == name)
		{
			return mesh;
		}

		foreach (Node child in node.GetChildren())
		{
			if (FindMesh(child, name) is { } found)
			{
				return found;
			}
		}

		return null;
	}
}

/// <summary>
/// A display-only owned panel: authored art cloned from the pattern book, live text the
/// guest states re-meshed onto it, no input of its own.
/// </summary>
/// <remarks>
/// The intermission strip and the info/acceptance display box are this shape: the guest queues
/// them, writes one or two live strings, and closes them itself. Owning one is a configuration —
/// which presentation, which authored groups to clone, which text elements to follow.
/// </remarks>
public partial class Engine_UiStatedPanel : Node3D, IUiSurfaceView
{
	/// <summary>
	/// A box's `Background` dim is a flat tint authored 2000 units wide — short of a 32:9
	/// window. Cloned oversized, like a book's backdrop, so it covers any aspect.
	/// </summary>
	public static void CoverWindow(Node3D clone, string piece)
	{
		if (piece == Engine_UiMessageBox.DimName && clone is MeshInstance3D)
		{
			clone.Scale *= new Vector3(4f, 4f, 1f);
		}
	}

	/// <summary>
	/// One followed text: the '/'-separated authored path from a cloned group's root to the node,
	/// and the theme colour the script writes onto it — `displaybox.dis main.6 pc3..56` writes
	/// `g_Theme:GetSubheadingColor()` onto `Title` and `g_Theme:GetNormalTextColor()` onto `Text`.
	/// Null colour leaves the authored `text_colour` standing.
	/// </summary>
	/// <param name="BoxWidth">
	/// A wrap width the script states over the authored one, in canvas units: `PrepareDisplayBox`
	/// writes `boxwidth = 1.4` (`displaybox.dis main.6 pc57..62`) onto the body before any text.
	/// Null keeps the authored box.
	/// </param>
	public readonly record struct TextBinding(
		string Group, string Path, Color? Colour = null, float? BoxWidth = null);

	public sealed class Config
	{
		public string Presentation = string.Empty;

		/// <summary>Top-level authored groups to clone at their authored transforms.</summary>
		public string[] Groups = System.Array.Empty<string>();

		public TextBinding[] Texts = System.Array.Empty<TextBinding>();

		/// <summary>
		/// Wear the HUD TopBox's look: the authored slab art is hidden, the texts take the
		/// TopBox's sizes and colours (<see cref="Engine_UiHudTextStyle"/>; a binding whose
		/// path ends in `Title` is the title, the rest are body), and a rounded panel is fitted
		/// behind them. One style for every in-game notice.
		/// </summary>
		public bool HudStyle;
	}

	private Engine_UiRoundedPanel? _panel;

	/// <summary>Document ordinals for the config's texts, in the same order.</summary>
	public sealed class Joins
	{
		public int[] Texts = System.Array.Empty<int>();
	}

	public Engine_UiTheme? Theme { get; set; }

	public bool HasShippedArt { get; private set; }

	public string LastReport { get; private set; } = string.Empty;

	private Config? _config;

	private readonly System.Collections.Generic.Dictionary<string, Node3D> _groups = new();

	public static Joins? Resolve(Engine_UiScreen screen, Config config)
	{
		var joins = new Joins { Texts = new int[config.Texts.Length] };
		for (int i = 0; i < config.Texts.Length; ++i)
		{
			UiNode? node = FindByName(screen.Roots, config.Texts[i].Group);
			foreach (string step in config.Texts[i].Path.Split('/'))
			{
				node = Child(node, step);
			}

			if (node is null)
			{
				return null;
			}

			joins.Texts[i] = node.Ordinal;
		}

		return joins;
	}

	public static Engine_UiStatedPanel FromPatternBook(
		Engine_UiTheme? book, Config config, string name)
	{
		var panel = new Engine_UiStatedPanel
		{
			Name = name,
			Visible = false,
			Theme = book,
			_config = config,
		};

		if (book is null)
		{
			panel.LastReport = "no pattern book — the panel has no art";
			return panel;
		}

		int cloned = 0;
		foreach (string piece in config.Groups)
		{
			Node3D? source = book.ArtSourceNode(piece);
			Node3D? clone = book.Art(piece);
			if (source is null || clone is null)
			{
				continue;
			}

			clone.Transform = Engine_UiTheme.CanvasTransform(source);
			Engine_UiStatedPanel.CoverWindow(clone, piece);
			panel.AddChild(clone);
			panel._groups[piece] = clone;
			++cloned;
		}

		panel.HasShippedArt = cloned == config.Groups.Length;
		panel.LastReport = $"{cloned}/{config.Groups.Length} authored groups cloned";
		if (config.HudStyle)
		{
			panel.HideArtExceptTexts();
		}

		return panel;
	}

	/// <summary>
	/// Hides every mesh of the cloned art that is not one of the bound texts — the slab, its
	/// frame and corners. The texts stay where the presentation authored them.
	/// </summary>
	private void HideArtExceptTexts()
	{
		if (_config is null)
		{
			return;
		}

		var keep = new System.Collections.Generic.HashSet<MeshInstance3D>();
		foreach (TextBinding binding in _config.Texts)
		{
			if (_groups.TryGetValue(binding.Group, out Node3D? group) &&
				group.GetNodeOrNull<MeshInstance3D>(binding.Path) is { } node)
			{
				keep.Add(node);
			}
		}

		foreach (Node3D group in _groups.Values)
		{
			HideMeshes(group, keep);
		}
	}

	private static void HideMeshes(Node node, System.Collections.Generic.HashSet<MeshInstance3D> keep)
	{
		if (node is MeshInstance3D mesh && !keep.Contains(mesh))
		{
			mesh.Visible = false;
		}

		foreach (Node child in node.GetChildren())
		{
			HideMeshes(child, keep);
		}
	}

	/// <summary>Fits the rounded panel behind whatever text is currently showing.</summary>
	private void FitHudPanel()
	{
		if (_config is null)
		{
			return;
		}

		Rect2? bounds = null;
		float behind = 1.5f;
		foreach (TextBinding binding in _config.Texts)
		{
			if (!_groups.TryGetValue(binding.Group, out Node3D? group) ||
				group.GetNodeOrNull<MeshInstance3D>(binding.Path) is not { } node ||
				!node.Visible || node.Mesh is null)
			{
				continue;
			}

			Aabb box = node.GlobalTransform * node.Mesh.GetAabb();
			if (box.Size.X <= 0f || box.Size.Y <= 0f)
			{
				continue;
			}

			var rect = new Rect2(box.Position.X, box.Position.Y, box.Size.X, box.Size.Y);
			bounds = bounds is null ? rect : bounds.Value.Merge(rect);
			behind = System.Math.Min(behind, node.GlobalPosition.Z - 0.5f);
		}

		if (bounds is not { } fit)
		{
			if (_panel is not null)
			{
				_panel.Visible = false;
			}

			return;
		}

		if (_panel is null)
		{
			_panel = new Engine_UiRoundedPanel { Name = "hud_panel" };
			AddChild(_panel);
		}

		// The panel is a child of this node; Fit positions it in the parent's space, and the
		// bounds above are global — so the parent's transform is taken back out.
		Transform3D inverse = GlobalTransform.AffineInverse();
		Vector3 a = inverse * new Vector3(fit.Position.X, fit.Position.Y, behind);
		Vector3 b = inverse * new Vector3(fit.End.X, fit.End.Y, behind);
		var local = new Rect2(new Vector2(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y)),
			new Vector2(Mathf.Abs(b.X - a.X), Mathf.Abs(b.Y - a.Y)));
		_panel.Fit(local, a.Z);
	}

	/// <summary>Puts the guest's stated strings on the followed elements, in config order.
	/// A null entry leaves that element as it stands.</summary>
	public void ShowStated(string?[] texts)
	{
		if (_config is null || Theme is null || _groups.Count == 0)
		{
			LastReport = "no art — not standing in";
			return;
		}

		for (int i = 0; i < _config.Texts.Length && i < texts.Length; ++i)
		{
			if (texts[i] is not { } display)
			{
				continue;
			}

			if (_groups.TryGetValue(_config.Texts[i].Group, out Node3D? group) &&
				group.GetNodeOrNull<MeshInstance3D>(_config.Texts[i].Path) is { } node)
			{
				// The theme colour goes on first: `text_colour` is what the re-mesh reads.
				if (_config.Texts[i].Colour is { } colour)
				{
					node.SetMeta("text_colour", colour);
				}

				if (_config.HudStyle)
				{
					bool isTitle = _config.Texts[i].Path.EndsWith("Title", System.StringComparison.OrdinalIgnoreCase);
					node.SetMeta("text_size", isTitle ? Engine_UiHudTextStyle.TitleSize : Engine_UiHudTextStyle.BodySize);
					node.SetMeta("text_colour", isTitle ? Engine_UiHudTextStyle.TitleColour : Engine_UiHudTextStyle.BodyColour);
				}

				if (_config.Texts[i].BoxWidth is { } width)
				{
					Vector2 box = node.HasMeta("text_box") ? (Vector2)node.GetMeta("text_box") : Vector2.Zero;
					node.SetMeta("text_box", new Vector2(width, box.Y));
				}

				LastReport = Theme.SetMeshText(node, display);
			}
		}

		Visible = true;
		if (_config.HudStyle)
		{
			FitHudPanel();
		}
	}

	/// <summary>Finds a named node anywhere under the roots — shared by every owned view's
	/// ordinal resolver.</summary>
	public static UiNode? FindByName(
		System.Collections.Generic.IReadOnlyList<UiNode> roots, string name)
	{
		foreach (UiNode root in roots)
		{
			if (root.Name == name)
			{
				return root;
			}

			if (FindByName(root.Children, name) is { } inner)
			{
				return inner;
			}
		}

		return null;
	}

	/// <summary>A direct child by authored name, null-tolerant for chaining.</summary>
	public static UiNode? Child(UiNode? node, string name)
	{
		if (node is null)
		{
			return null;
		}

		foreach (UiNode child in node.Children)
		{
			if (child.Name == name)
			{
				return child;
			}
		}

		return null;
	}
}
