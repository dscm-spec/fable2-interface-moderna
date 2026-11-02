using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Ui;

/// <summary>
/// THE MODS' PICKS on the spell selectors: every enabled mod's spell slot pick
/// (<see cref="Engine_UiSpellPicks.Mods"/>, <c>ui/spellwheel.json</c>), beside the slot's own
/// spell (<see cref="Engine_UiSpellPicks.Game"/>). On both selectors (the quick wheel and the
/// abilities blade) X on the highlighted will picks it into the player's ACTIVE pick
/// (<see cref="Engine_UiSpellPicks.Active"/>) for the highlighted slot and Y clears that pick,
/// through <see cref="Engine_UiSpellPicks.Choose"/> — the engine raises <c>SpellPick</c> and keeps the
/// pick as the selectors' player's saved value. Every read is for the selectors' player.
///
/// WHICH PICK X AND Y ACT ON, with two or more: a press that has nothing to do on the active pick
/// steps to the next one, in load order, wrapping — X on the slot's own spell, on a will the pick
/// excludes or on the will it already holds; Y on a pick with nothing to clear. On the abilities
/// blade LB and RB step back and on as well (the book leaves them unused while the blade is up;
/// the quick wheel's held trigger leaves the guest every button but X and Y). With one pick
/// nothing steps, and X and Y behave as they always have.
///
/// <c>apply</c> and <c>remove</c> are the Babel tags of the two PROMPTS the abilities blade shows
/// once a pick is unlocked — an X row and a Y row in the page's own prompt style (glyph, then the
/// words, as the book's "Back"), on the free left side of the page's prompt band, beside the
/// authored A "Copy Spell to Slots Above" and B "Done". The words are the active pick's.
///
/// WHAT IS DRAWN: a small frame per pick under every ASSIGNED slot's icon
/// (<see cref="Engine_UiSpellPicks.Layout"/>), always there once a mod states a pick, so the player
/// can see the slot takes one. With two or more the active pick's frame is drawn larger. Each
/// frame's state:
/// <list type="bullet">
/// <item>empty — the <c>empty</c> art alone, faint: the slot can take the pick and has none;</item>
/// <item>picked — the will <c>inEffect</c> states, at full strength;</item>
/// <item>picked but not in effect — the stored will when <c>inEffect</c> says -1, at half
/// strength (unlearned, or beyond the mod's own gate);</item>
/// <item>disabled — the frame greyed down when the slot's own will is in that pick's
/// <c>exclude</c>; it comes back the moment a will the pick allows is assigned there.</item>
/// </list>
/// The host knows nothing of what a pick means; its mod does.
/// </summary>
public sealed partial class Engine_UiSpellAssign
{
	public const string SecondaryPath = Engine_UiSpellPicks.SpellWheelPath;

	private const float EmptyOpacity = 0.45f;
	private const float DisabledOpacity = 0.22f;
	private const float PendingOpacity = 0.5f;

	/// <summary>How much larger the active pick's frame is drawn when a slot carries two or more.</summary>
	private const float ActiveEmphasis = 1.2f;

	/// <summary>The pick X and Y act on for player one; null when no mod states one (the input mask's gate).</summary>
	public static Engine_UiSpellPicks.Pick? Secondary => Engine_UiSpellPicks.Active(1);

	/// <summary>Drops the loaded picks; the next read loads them again.</summary>
	public static void ReloadSecondary() => Engine_UiSpellPicks.Reload();

	/// <summary>Which player's hero this copy shows: 1, or 2 for the joiner's wheel.</summary>
	public int Player { get; set; } = 1;

	/// <summary>What a press on the selectors asks of the active pick: X applies the highlighted will, Y removes it.</summary>
	public enum SecondaryAction
	{
		None,
		Apply,
		Remove,
	}

	public static SecondaryAction SecondaryPressed(bool flip, bool equip) =>
		Secondary is null ? SecondaryAction.None : equip ? SecondaryAction.Apply : flip ? SecondaryAction.Remove : SecondaryAction.None;

	/// <summary>Whether <paramref name="player"/> has any of the picks: an <c>unlocked</c> source positive for them.</summary>
	public static bool SecondaryUnlocked(int player)
	{
		foreach (Engine_UiSpellPicks.Pick pick in Engine_UiSpellPicks.Mods)
		{
			if (Engine_UiSpellPicks.Unlocked(pick, player))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>Steps <paramref name="player"/>'s active pick <paramref name="delta"/> on; nothing with one pick or none.</summary>
	public static void StepSecondary(int player, int delta)
	{
		IReadOnlyList<Engine_UiSpellPicks.Pick> picks = Engine_UiSpellPicks.Mods;
		if (picks.Count < 2 || delta == 0)
		{
			return;
		}

		int at = Engine_UiSpellPicks.StepCursor(player, delta, picks.Count);
		GD.Print($"[spell-wheel] player {player} now picks {picks[at].Name} ({at + 1} of {picks.Count})");
	}

	/// <summary>The prompt glyphs this copy added to the book's blade, X then Y.</summary>
	private readonly List<Node3D> _promptGlyphs = new();

	/// <summary>Each pick's prompt words, in load order: the X row's, then the Y row's.</summary>
	private readonly List<(Node3D Apply, Node3D Remove)> _promptWords = new();

	/// <summary>The book the missing-band warning was last given for.</summary>
	private Engine_UiOwnedBook? _promptWarnedBook;

	private const string GlyphX = "icon_button_x";
	private const string GlyphY = "icon_button_y";

	private static string ApplyName(int pick) => "SetPick_" + pick;

	private static string RemoveName(int pick) => "RemovePick_" + pick;

	/// <summary>
	/// THE PROMPTS, in the page's own band. `abilitiesscreen.bgf` authors `Blade.Buttons` at
	/// (80, -265) with the A pair on its row y 3 and the B pair on row y 35, all to the right of
	/// x 218; the left of the band, from the page margin at x 24, is empty. The X row goes on the
	/// B row and the Y row on the A row, laid out as the book's own glyph-first `Back` prompt
	/// (glyph at x, words left-aligned 21 units on). The glyph is the authored B quad duplicated
	/// and re-cut to the X or Y column of the `abyx` atlas (columns A, B, Y, X), with its keycap
	/// for the keyboard; the words — one pair per pick, only the active pick's shown — are labels
	/// in the page's prompt colour and size. Built once per Buttons node and adopted on a rebuild,
	/// so a blade that comes and goes never doubles them. Hidden until <see cref="ShowPrompts"/>
	/// says otherwise.
	/// </summary>
	public void EnsurePrompts(Engine_UiOwnedBook book)
	{
		IReadOnlyList<Engine_UiSpellPicks.Pick> picks = Engine_UiSpellPicks.Mods;
		if (picks.Count == 0 || Book is null)
		{
			return;
		}

		if (_promptGlyphs.Count == 2 && _promptWords.Count == picks.Count && GodotObject.IsInstanceValid(_promptGlyphs[0]))
		{
			return;
		}

		if (book.Find("Blade") is not { } blade ||
			Engine_UiTheme.FindByName(blade, "Buttons") is not Node3D buttons ||
			Engine_UiTheme.FindByName(buttons, "icon_button_b") is not MeshInstance3D glyph ||
			Engine_UiTheme.FindByName(buttons, "Done") is not MeshInstance3D done)
		{
			// Once per book: this runs every frame the wheel is up.
			if (!ReferenceEquals(_promptWarnedBook, book))
			{
				_promptWarnedBook = book;
				GD.PushWarning("[spell-wheel] the blade's prompt band is not in this book; no pick prompts");
			}

			return;
		}

		// A rebuild of the widget finds the rows a previous copy left on the same Buttons node,
		// for as many picks as are stated now.
		_promptGlyphs.Clear();
		_promptWords.Clear();
		var kept = new List<Node3D>();
		foreach (string name in new[] { GlyphX, GlyphY })
		{
			if (Engine_UiTheme.FindByName(buttons, name) is Node3D node)
			{
				kept.Add(node);
			}
		}

		bool whole = kept.Count == 2;
		for (int i = 0; whole && i < picks.Count; ++i)
		{
			if (Engine_UiTheme.FindByName(buttons, ApplyName(i)) is Node3D apply &&
				Engine_UiTheme.FindByName(buttons, RemoveName(i)) is Node3D remove)
			{
				_promptWords.Add((apply, remove));
			}
			else
			{
				whole = false;
			}
		}

		if (whole && Engine_UiTheme.FindByName(buttons, ApplyName(picks.Count)) is null)
		{
			_promptGlyphs.AddRange(kept);
			return;
		}

		// Stale or partial (the picks changed on a remount): every row of ours goes, then all are built.
		_promptWords.Clear();
		foreach (Node child in buttons.GetChildren())
		{
			if (child.Name.ToString() is GlyphX or GlyphY || child.Name.ToString().StartsWith("SetPick_", System.StringComparison.Ordinal) ||
				child.Name.ToString().StartsWith("RemovePick_", System.StringComparison.Ordinal))
			{
				buttons.RemoveChild(child);
				child.QueueFree();
			}
		}

		int size = done.HasMeta("text_size") ? (int)done.GetMeta("text_size") : 18;
		Color colour = Engine_UiSceneBuilder.TextColour(done);
		const float rowStep = 32f;  // the authored A row (y 3) to the B row (y 35)
		float applyY = done.Position.Y;
		float removeY = done.Position.Y - rowStep;
		foreach ((string button, int column, float y) in new[] { ("X", 3, applyY), ("Y", 2, removeY) })
		{
			var quad = (MeshInstance3D)glyph.Duplicate();
			quad.Name = button == "X" ? GlyphX : GlyphY;
			quad.Position = new Vector3(PromptGlyphX, y + (glyph.Position.Y - done.Position.Y), glyph.Position.Z);
			if (quad.MaterialOverride is ShaderMaterial shared)
			{
				var material = Engine_UiSceneBuilder.CopyMaterial(shared);
				Vector2 offset = material.GetShaderParameter("uv1_offset").AsVector2();
				float inset = offset.X - (Mathf.Floor((offset.X * 4f) + 0.01f) * 0.25f);
				material.SetShaderParameter("uv1_offset", new Vector2((column * 0.25f) + inset, offset.Y));
				material.SetShaderParameter("keycap_tex", Engine_UiKeyGlyphs.KeycapFor(button));
				quad.MaterialOverride = material;
			}

			quad.Visible = false;
			buttons.AddChild(quad);
			_promptGlyphs.Add(quad);
		}

		for (int i = 0; i < picks.Count; ++i)
		{
			MeshInstance3D Words(string tag, string name, float y)
			{
				MeshInstance3D words = Book.Label(Book.Resolve(tag), size, colour);
				words.Name = name;
				words.Position = new Vector3(PromptGlyphX + PromptWordsOffset, y, done.Position.Z);
				words.Visible = false;
				buttons.AddChild(words);
				return words;
			}

			_promptWords.Add((Words(picks[i].Apply, ApplyName(i), applyY), Words(picks[i].Remove, RemoveName(i), removeY)));
		}
	}

	/// <summary>The glyph column, in Buttons' own frame: page x 44, twenty units in from the margin.</summary>
	private const float PromptGlyphX = -36f;

	/// <summary>Words start 21 units right of the glyph's centre — the book's `Back` prompt spacing.</summary>
	private const float PromptWordsOffset = 21f;

	/// <summary>Shows or hides the pick prompts — up while the blade is known and a pick unlocked; the words are <paramref name="player"/>'s active pick's.</summary>
	public void ShowPrompts(bool shown, int player)
	{
		foreach (Node3D node in _promptGlyphs)
		{
			if (GodotObject.IsInstanceValid(node))
			{
				node.Visible = shown;
			}
		}

		int active = Engine_UiSpellPicks.Cursor(player, _promptWords.Count);
		for (int i = 0; i < _promptWords.Count; ++i)
		{
			(Node3D apply, Node3D remove) = _promptWords[i];
			bool on = shown && i == active;
			if (GodotObject.IsInstanceValid(apply))
			{
				apply.Visible = on;
			}

			if (GodotObject.IsInstanceValid(remove))
			{
				remove.Visible = on;
			}
		}
	}

	/// <summary>
	/// Y on a slot: the active pick is cleared there (the engine's <c>spell_pick</c> with -1), and its
	/// frame goes back to empty. With nothing to clear and more than one pick, the next pick becomes
	/// the active one instead.
	/// </summary>
	public static void RemoveSecondary(int player, int slot)
	{
		if (Engine_UiSpellPicks.Active(player) is not { } pick || slot < 0 || slot >= Engine_UiModel.SpellAssignState.Slots)
		{
			return;
		}

		int was = SecondaryChosen(pick, player, slot);
		if (was < 0 && Engine_UiSpellPicks.Mods.Count > 1)
		{
			StepSecondary(player, 1);
			return;
		}

		GD.Print($"[spell-wheel] {pick.Name} cleared: player {player} slot {slot + 1} (was {was})");
		Engine_UiSpellPicks.Choose(pick, player, slot, -1);
	}

	/// <summary>The will <paramref name="pick"/>'s mod states as IN EFFECT for <paramref name="player"/> on <paramref name="slot"/>, or -1.</summary>
	public static int SecondaryWill(Engine_UiSpellPicks.Pick pick, int player, int slot) =>
		Engine_UiSpellPicks.InEffect(pick, player, slot);

	/// <summary>The will stored as the player's <paramref name="pick"/> there, in effect or not; falls back to the one in effect.</summary>
	public static int SecondaryChosen(Engine_UiSpellPicks.Pick pick, int player, int slot)
	{
		int chosen = Engine_UiSpellPicks.Stored(pick, player, slot);
		return chosen >= 0 ? chosen : Engine_UiSpellPicks.InEffect(pick, player, slot);
	}

	/// <summary>
	/// The pick on a slot: the will under the cursor goes into the player's active pick. The slot's
	/// OWN spell (<paramref name="primary"/>) can never be one, a will the pick excludes is refused,
	/// and the will already chosen is kept, never toggled off (a second press on the same will used
	/// to clear it, which read as the pick not taking). Each of those presses has nothing to do, so
	/// with more than one pick it steps to the next instead.
	/// </summary>
	public static void ChooseSecondary(int player, int slot, int? will, int primary = -1)
	{
		if (Engine_UiSpellPicks.Active(player) is not { } binding || will is not { } chosen || chosen < 0 ||
			slot < 0 || slot >= Engine_UiModel.SpellAssignState.Slots)
		{
			return;
		}

		int current = SecondaryChosen(binding, player, slot);
		string? idle = chosen == primary ? "is the slot's own spell"
			: binding.Excludes(chosen) ? "cannot be picked"
			: chosen == current ? "is already picked"
			: null;
		if (idle is not null)
		{
			GD.Print($"[spell-wheel] {binding.Name}: player {player} slot {slot + 1}: will {chosen} {idle}; kept {current}");
			StepSecondary(player, 1);
			return;
		}

		GD.Print($"[spell-wheel] {binding.Name}: player {player} slot {slot + 1}: will {chosen} (was {current})");
		Engine_UiSpellPicks.Choose(binding, player, slot, chosen);
	}

	/// <summary>The will under the carousel's centre on this copy's own cursor — the abilities blade's pick.</summary>
	public int? CursorWill()
	{
		List<int> choices = _state.Choices(Row_);
		return _cell >= 0 && _cell < choices.Count ? choices[_cell] : null;
	}

	/// <summary>The small frames under each assigned slot's icon, one per pick, each in one of its four states.</summary>
	private void PaintSecondary()
	{
		IReadOnlyList<Engine_UiSpellPicks.Pick> picks = Engine_UiSpellPicks.Mods;
		int active = picks.Count > 1 ? Engine_UiSpellPicks.Cursor(Player, picks.Count) : -1;
		for (int slot = 0; slot < _rows.Count; ++slot)
		{
			Row row = _rows[slot];
			bool assigned = row.Shown >= 0 && row.Icon is not null && GodotObject.IsInstanceValid(row.Icon);
			for (int i = 0; i < row.Minis.Count; ++i)
			{
				if ((!assigned || i >= picks.Count) && GodotObject.IsInstanceValid(row.Minis[i]))
				{
					row.Minis[i].Visible = false;
				}
			}

			if (!assigned || picks.Count == 0)
			{
				continue;
			}

			PlaceMinis(row, picks, active);
			for (int i = 0; i < picks.Count; ++i)
			{
				Engine_UiSpellPicks.Pick pick = picks[i];
				MeshInstance3D? mini = i < row.Minis.Count ? row.Minis[i] : null;
				if (mini is null || !GodotObject.IsInstanceValid(mini))
				{
					continue;
				}

				// The state, as one number the row remembers: -3 disabled, -2 empty, will + 8 pending,
				// will in effect.
				int inEffect = SecondaryWill(pick, Player, slot);
				int chosen = SecondaryChosen(pick, Player, slot);
				int state = pick.Excludes(row.Shown) ? -3
					: inEffect >= 0 ? inEffect
					: chosen >= 0 ? chosen + 8
					: -2;
				if (state != row.MinisShown[i])
				{
					row.MinisShown[i] = state;
					switch (state)
					{
						case -3:
							PaintFrame(mini, pick.Empty, DisabledOpacity);
							break;
						case -2:
							PaintFrame(mini, pick.Empty, EmptyOpacity);
							break;
						case >= 8:
							Paint(mini, state - 8);
							Engine_UiSceneBuilder.SetQuadOpacity(mini, PendingOpacity);
							break;
						default:
							Paint(mini, state);
							break;
					}

					// What the paint left: hidden only when the frame's art is missing.
					row.MinisDrawn[i] = mini.Visible;
				}

				mini.Visible = row.MinisDrawn[i];
			}
		}
	}

	/// <summary>The frame art, the mod's own <c>.tex</c>, at an opacity; the quad is hidden when the art is missing.</summary>
	private void PaintFrame(MeshInstance3D node, string texture, float opacity)
	{
		Texture2D? picture = texture.Length == 0 ? null : ResolveTexture?.Invoke(texture);
		if (picture is null && texture.Length != 0)
		{
			picture = ResolveTexture?.Invoke(texture.Replace('/', '\\'));
		}

		if (picture is null || node.MaterialOverride is not ShaderMaterial material)
		{
			node.Visible = false;
			return;
		}

		node.Visible = true;
		material.SetShaderParameter("diffuse_tex", picture);
		Engine_UiSceneBuilder.SetQuadOpacity(node, opacity);
	}

	/// <summary>
	/// One copy of the slot's icon quad per pick, under it, placed by
	/// <see cref="Engine_UiSpellPicks.Layout"/> at the smallest of the picks' <c>scale</c>s, the active
	/// one drawn larger and in front; re-placed only when the count or the active pick changes.
	/// </summary>
	private static void PlaceMinis(Row row, IReadOnlyList<Engine_UiSpellPicks.Pick> picks, int active)
	{
		if (row.Icon is not { } icon || icon.Mesh is null)
		{
			return;
		}

		while (row.Minis.Count < picks.Count)
		{
			var mini = new MeshInstance3D
			{
				Name = "Secondary" + row.Minis.Count,
				Mesh = icon.Mesh,
				MaterialOverride = icon.MaterialOverride?.Duplicate() as Material,
				Visible = false,
			};
			icon.AddChild(mini);
			row.Minis.Add(mini);
			row.MinisShown.Add(int.MinValue);
			row.MinisDrawn.Add(false);
			row.MinisPlaced = -1;
		}

		int placed = (picks.Count * 16) + active + 1;
		if (row.MinisPlaced == placed)
		{
			return;
		}

		row.MinisPlaced = placed;
		float scale = 1f;
		foreach (Engine_UiSpellPicks.Pick pick in picks)
		{
			scale = Mathf.Min(scale, pick.Scale);
		}

		Vector3 size = icon.Mesh.GetAabb().Size;
		(float X, float Y, float Scale)[] at = Engine_UiSpellPicks.Layout(picks.Count, scale);
		for (int i = 0; i < picks.Count; ++i)
		{
			MeshInstance3D mini = row.Minis[i];
			if (!GodotObject.IsInstanceValid(mini))
			{
				continue;
			}

			float s = at[i].Scale * (i == active ? ActiveEmphasis : 1f);
			mini.Position = new Vector3(at[i].X * size.X, at[i].Y * size.Y, i == active ? 2f : 1f);
			mini.Scale = new Vector3(s, s, 1f);
		}
	}
}
