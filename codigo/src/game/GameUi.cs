using System.Linq;
using Godot;
using Refii.Core;
using Refii.Ui;
using Refii.Host;
using Refii.Gdb;

namespace Refii.Game;

/// <summary>
/// Renders Fable 2's own front end, in Godot, over the world. The guest owns which screen is up
/// and what state every widget is in; Godot owns the pixels. The screens are the title's real
/// <c>.bgf</c> data, stood up through <see cref="Engine_UiPresentationHost"/>.
///
/// It renders into its OWN SubViewport with its own World3D. A Gameface screen is a 3D scene
/// carrying cameras, lights and quads on a ~720-unit canvas, and Godot renders a viewport
/// through exactly one current camera — sharing the world's viewport means the last camera to
/// declare itself wins, and the front end re-declares its camera on every widget state change
/// (<see cref="Conceal"/> hides meshes but leaves the camera current). The two then compose UI
/// over scene on a transparent background.
///
/// Built at boot, hidden until the startup movies are done: standing up the slide graph costs a
/// visible fraction of a second and would hitch at the logo-to-title cut if deferred.
/// </summary>
public partial class GameUi : Node
{
	/// <summary>
	/// Screen to show before the guest says otherwise — the title, which is what the guest puts
	/// up first. <see cref="FollowGuest"/> takes over once it reports a front end.
	/// </summary>
	[Export] public string Screen { get; set; } = "frontendstartupscreen";

	private Engine_UiPresentationHost? _presentation;
	private SubViewport? _viewport;
	private SubViewportContainer? _container;

	/// <summary>
	/// The main menu's model and its view — Godot's, not the guest's. The guest's menu has no C++
	/// item list and no C++ highlight to read: items, order, nesting and the selection all live
	/// in shipped Lua. So the list, the highlight and the navigation timing are ours and the
	/// guest is told the outcome by name; the art stays the game's.
	/// </summary>
	private Engine_FrontEndMenu? _menu;

	private Engine_FrontEndMenuView? _menuView;

	/// <summary>Debug stand-in for the main menu; replaces the Godot menu view while visible.</summary>
	private Refii.Launcher.Tools.DebugMenu? _debugMenu;

	/// <summary>The front end is up (Title/startup or MainMenu) per the last screen change.
	/// Combined with <see cref="IsRevealed"/> per-frame — the reveal is not a screen change,
	/// so the debug menu's visibility cannot be applied only inside the change handler.</summary>
	private bool _frontendUp;

	/// <summary>
	/// The gender card "New Game" opens. The guest models it as a SUBSTATE of the same screen
	/// object — `screen+24` goes to 2 and the menu's button handler stops answering the list's
	/// codes — so only the input changes hands.
	///
	/// Hiding the rows is ours: the file puts every `ExpandableMenu` element at z 0 and the
	/// card's active pose at z -134, so the rows cut across the raised card, and all six root
	/// slides start the menu off-canvas at x -800 rather than parking it. Whatever the shipped
	/// Lua does here is not in the file.
	/// </summary>
	private Engine_FrontEndCardChoice? _cardView;

	/// <summary>
	/// The Load Game card fan. The guest enumerates the saves and builds each card's text — calls
	/// only it can make — and Godot draws and moves the fan from that.
	/// </summary>
	private Engine_FrontEndSaveCards? _saveCardView;

	/// <summary>
	/// The pause menu's model — the guest's own list (<see cref="Engine_PauseMenu.BuildHero"/>),
	/// our highlight. Outlives the view for the same reason <see cref="_menu"/> does: the
	/// player's place in the menu survives the screen coming and going.
	/// </summary>
	private Engine_FrontEndMenu? _pauseMenu;

	/// <summary>Which side the standing pause menu was built for.</summary>
	private bool _pauseMenuJoiner;

	/// <summary>
	/// The pause menu's view — the same widget as the front end's, drawing the same authored
	/// art (`pausemainmenu` is the same ExpandableMenu component), bound to the pause model.
	/// The guest's own button pool and highlight are hidden on every rebuild of the mirrored
	/// pause tree; dispatch is the tab lever (<see cref="HostBridge.PauseTabSelect"/>).
	/// </summary>
	private Engine_FrontEndMenuView? _pauseView;

	/// <summary>
	/// The `GUI.*` native router. Held here rather than statically because it carries this
	/// session's census, which must not outlive the node.
	/// </summary>
	private readonly Engine_UiGuiNatives _guiNatives = new();

	/// <summary>
	/// The display boxes and counters as the guest's `GUI.*` calls stated them. Owned state, not
	/// a mirror: nothing is read back out of the guest's queues and lifetimes age on our clock.
	/// </summary>
	private readonly Engine_UiGuiBoxes _boxes = new();

	/// <summary>
	/// The menu model the Gameface Lua would have held, built from the C++'s own statements —
	/// see <see cref="Engine_UiMenuModel"/>.
	/// </summary>
	private readonly Engine_UiMenuModel _menuModel = new();

	/// <summary>Presentation addresses the registry walk has listed; see TracePresentations.</summary>
	private readonly System.Collections.Generic.HashSet<uint> _menuModelSeenLive = new();

	/// <summary>The movie the guest asked for, from its own `Play*MovieInternal` call.</summary>
	private readonly Engine_UiGuiMovie _movies = new();

	/// <summary>The epilogue, next-episode and loading-screen channels.</summary>
	private readonly Engine_UiGuiScreens _screens = new();

	/// <summary>The expression meter, the golf swing meter and the minigame tally.</summary>
	private readonly Engine_UiGuiWidgets _widgets = new();

	private int _menuModelReported;

	/// <summary>The menu model's last logged shape (its description minus the statement count).</summary>
	private string? _menuModelShapeReported;

	/// <summary>What the `g_Menu` capture had recorded/dropped when it was last reported.</summary>
	private ulong _menuChunksRecorded = ulong.MaxValue;

	private ulong _menuChunksDropped;

	private ulong _boxesReported;

	private bool _guiRoutesReady;

	private Engine_UiOwnedBook? _pauseBook;

	/// <summary>The guest's pausemainmenu presentation address, for the live sample.</summary>
	private uint _pauseAddress;

	/// <summary>The mirrored `pausemainmenu` tree is standing — set by its build, cleared by
	/// its retirement. The owned view only shows over the guest's own backdrop.</summary>
	private bool _pauseTreeUp;

	/// <summary>Last applied tab-availability mask, so the model rebuild and the report fire
	/// on the change rather than every frame. Starts at all-available — the mask's own
	/// fail-open value — because that is what BuildHero constructs.</summary>
	private uint _pauseTabsMask = HostBridge.AllPauseTabs;

	/// <summary>
	/// The pause list's origin: the authored `Buttons`-group canvas position of `pausemainmenu`,
	/// read from the pattern book. The front end's constant is only the front end's.
	///
	/// THREE AXES, NOT TWO. `Buttons` does not sit on the presentation root's plane — the
	/// composed chain is `ExpandableMenu` then `Buttons` and both carry a Z, with `LeftFrame` ten
	/// units in front of `Buttons` inside that group. Using the root's Z is a different parent,
	/// not an offset.
	/// </summary>
	private Vector3 _pauseAnchor = new(
		Engine_FrontEndMenuView.CanvasAnchor.X, Engine_FrontEndMenuView.CanvasAnchor.Y, 0f);

	/// <summary>
	/// The owned surfaces the guest can raise over any screen: the three modal boxes, the
	/// received-item card, the MenuBox, and the two display-only panels — one shape, one object;
	/// see <see cref="Engine_UiSurfaceSet"/>.
	///
	/// The three input masks are supplied rather than asserted by the surfaces: everything under
	/// `Engine/` is shared with the editor, which has no pad and no <see cref="GameInput"/>.
	/// </summary>
	private readonly Engine_UiSurfaceSet _surfaces = new(
		standing => GameInput.ModalBoxOwnsInput = standing,
		standing => GameInput.ReceiveItemsOwnsInput = standing,
		standing => GameInput.MenuBoxOwnsInput = standing);

	/// <summary>
	/// Whether a stated display MessageBox is still one: the class's vtable at +0 and its open
	/// byte at +12 (`lbz r11, 0xC(r3)`, which every handler gates on). The one address the class
	/// stated, re-checked — not a walk of the box queue.
	/// </summary>
	private static bool GuestMessageBoxOpen(uint box)
	{
		const uint MessageBoxVtable = 0x820AE668u;
		System.Span<uint> words = stackalloc uint[4];
		return HostBridge.Peek(box, words) == 4 &&
			HostBridge.GuestAddressToTu1(words[0]) == MessageBoxVtable &&
			(words[3] >> 24) != 0u;
	}

	/// <summary>This frame's press, in the form an owned surface takes it.</summary>
	private static Engine_UiSurfaceInput SurfaceInput(double delta) => new()
	{
		Accept = GameInput.MenuAcceptPressed,
		Back = GameInput.MenuBackPressed,
		Flip = GameInput.MenuFlipPressed,
		Equip = GameInput.MenuEquipPressed,
		Direction = GameInput.MenuDirection,
		Horizontal = GameInput.MenuHorizontal,
		TabPrev = GameInput.MenuTabPrevPressed,
		TabNext = GameInput.MenuTabNextPressed,
		Scroll = GameInput.RightStickY,
		Delta = delta,
	};

	/// <summary>
	/// The loading screen, on its own layer above this one. Polled every frame rather than driven
	/// from the level-change contract: the guest owns both whether one is up and how far it has
	/// faded — see <see cref="GameLoadingScreen"/>.
	/// </summary>
	private GameLoadingScreen? _loading;

	/// <summary>
	/// Whether the display takes the game's WIDE layout. Sampled from the viewport and re-read as
	/// the window changes, so a resize moves the HUD.
	/// </summary>
	private bool _isWidescreen = true;

	/// <summary>
	/// True while the host applies the shipped wide layout to a still-mirrored screen. The guest
	/// is deliberately left believing 4:3 — its authored canvas is the one canonical space every
	/// number it states arrives in, and Godot owns how that reaches any display. Never tell the
	/// guest it is on 16:9. Lives only as long as mirrored screens do.
	/// </summary>
	private bool HostAppliesWidescreen => _isWidescreen;

	/// <summary>
	/// True while anything modal is over the menu — the gender card, the load fan, or the
	/// warning box. One place: "is the row menu allowed on screen" is asked from several.
	/// </summary>
	private bool ModalPanelUp =>
		_cardView is { Visible: true } ||
		_saveCardView is { Visible: true } ||
		_warningView is { Visible: true };

	/// <summary>
	/// The refusal box, cloned from the shipped `dialog` page. Modal in the guest's sense — it
	/// parks the screen in substate 1 and only an A press clears it; while it is up the menu
	/// takes no input.
	/// </summary>
	private Engine_FrontEndWarning? _warningView;

	/// <summary>Whether the guest was in game last frame — the multiplayer close is edge-triggered.</summary>
	private bool _multiplayerSawInGame;

	/// <summary>The in-game flag as read last frame, for the falling edge <see cref="LeaveGame"/> runs on.</summary>
	private bool _sawInGame;

	/// <summary>
	/// Drops what the game's own presentations fed, because the guest has left the game.
	///
	/// Quit to Title is not a level change and not a screen change. The game holder's
	/// shutdown (`sub_823117A8`) clears `byte_83496A5E` at 0x82311860, frees the world, then
	/// deletes the in-game GUI (`sub_823716D0` zeroes `dword_83496B08` at 0x82371700); the
	/// Start holder's Init constructs a NEW front-end GUI and runs the same Init as first boot.
	/// From that point `FollowInGameUi` never runs again, so nothing else retires what the HUD
	/// and the owned surfaces were reading — the element cache kept the HUD's last state under
	/// an address the guest had freed, and the rebuilt HUD stood over the title screen on it.
	///
	/// Address-keyed state goes because the next game reuses the addresses; the views stay,
	/// they are rebuilt from the banks and re-fed when the game names them again.
	/// </summary>
	private void LeaveGame()
	{
		GD.Print("[game-ui] the guest has left the game — dropping the HUD feed, the owned " +
			"boxes, the menu capture and the element cache");

		RetireDrawn("the guest left the game");

		// The HUD's feed is its address; with it at 0 the icons and the piece gate stop, and a
		// null gate hides every stated piece. Hidden outright as well: a piece with no ordinal
		// is not gated and would otherwise stand. Shown again when the game names hud.bgf.
		_hudAddress = 0;
		_cyclerAddress = 0;
		if (_hud is not null && GodotObject.IsInstanceValid(_hud))
		{
			_hud.ElementActive = null;
			_hud.Visible = false;
			_hud.Values.Reset();
		}

		if (_hudTwo is not null && GodotObject.IsInstanceValid(_hudTwo))
		{
			_hudTwo.ElementActive = null;
			_hudTwo.Visible = false;
		}

		if (_worldIcons is not null && GodotObject.IsInstanceValid(_worldIcons))
		{
			_worldIcons.Clear();
		}

		if (_worldIconsTwo is not null && GodotObject.IsInstanceValid(_worldIconsTwo))
		{
			_worldIconsTwo.Clear();
		}

		_boxes.Clear();
		_menuModel.Reset();
		_menuModelSeenLive.Clear();
		Engine_UiElementState.Current.Reset();
		HasInGameUi = false;
	}

	/// <summary>The key over the last co-op mannequin line; null before the first one.</summary>

	/// <summary>Joiner only: whether the live hero has become the kit's source.</summary>
	private bool _coopLiveSampling;

	/// <summary>The name a menu entry asked us to dispatch, latched until something takes it.</summary>
	public string? PendingAction { get; private set; }

	/// <summary>True once <see cref="Reveal"/> has put the UI on screen.</summary>
	public bool IsRevealed { get; private set; }

	/// <summary>Whether the screen stood up at all.</summary>
	public bool IsBuilt => _presentation is not null;

	public override void _Ready()
	{
		string dataRoot = Engine_GameData.Resolve();
		if (dataRoot.Length == 0)
		{
			GD.PushWarning("[game-ui] no game data; UI not shown.");
			return;
		}

		// THE ONE MODS ROOT — the folder the launcher's panel writes and the mount reads. Walking up
		// from the data root guessed its depth: the installed data root is `assets/game`, so two
		// levels up was the app root and the list read a load order that does not exist.
		Engine_UiNetworkSource.ModsRoot = Refii.Assets.Engine_GameAssets.ModsRoot;

		// Layer 5: over the world, under the videos on layer 10. A movie covers the front
		// end, which is what the boot sequence and every later cutscene want.
		var layer = new CanvasLayer { Name = "UiLayer", Layer = 5 };
		AddChild(layer);

		_container = new SubViewportContainer
		{
			Name = "UiView",
			// The container drives the SubViewport's size, so the front end is rendered at
			// the window's real resolution rather than upscaled from a fixed buffer.
			Stretch = true,
			// The front end is driven by the GUEST's input, read through GameInput. Letting
			// the sub-viewport take Godot's GUI input as well would give a second, host-side
			// route into it that the guest knows nothing about.
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Visible = false,
		};
		_container.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		layer.AddChild(_container);

		_viewport = new SubViewport
		{
			Name = "UiViewport",
			// Its own world, so the front end's cameras and lights cannot reach the guest's, and
			// a transparent background so the world shows through where the screen does not draw.
			OwnWorld3D = true,
			TransparentBg = true,
			RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
			GuiDisableInput = true,
		};
		_container.AddChild(_viewport);

		// The viewport's one true camera, owned here and never freed. Presentation trees carry
		// their own cameras (the host's canvas camera, every mirrored screen's authored
		// perspective one), and freeing the current camera with its tree makes Godot promote
		// whichever it finds next. A camera parented OUTSIDE every presentation cannot be freed
		// by one; DriveUiCamera re-asserts it every frame.
		_uiCameraMount = new Node3D { Name = "UiCameraMount" };
		_viewport.AddChild(_uiCameraMount);
		_uiCamera = Engine_UiSceneBuilder.ActivateCamera(_uiCameraMount);

		_presentation = new Engine_UiPresentationHost
		{
			Name = "Presentation",
			GameDataRoot = dataRoot,
			StartScreen = Screen,

			// Framing camera OFF: it would fit the geometry to the viewport and hide the
			// placement errors the title's own camera aiming has to be checked against.
			UseFramingCamera = false,
		};

		// The host's own _Ready runs on AddChild, so the screen is already standing up by the
		// time this returns and the theme has a presentation to catalogue.
		_viewport.AddChild(_presentation);

		// When asset mounting completes or modded text overlays arrive, update all UI references:
		_presentation.StringsChanged += newStrings =>
		{
			if (_loading is not null)
			{
				_loading.Strings = newStrings;
			}
			if (_hud is not null)
			{
				_hud.Values.Strings = newStrings;
			}
			GameWhereabouts.Strings = newStrings;
			Engine_UiInventorySource.Strings = newStrings;

			// A new string book means the mount changed, which also dropped the text banks the
			// item labels resolve through (`Engine_BnkTextBankLoader.SetGameDirectory`); read
			// them again now rather than on the next list that needs them.
			if (HostBridge.IsInGame)
			{
				WarmTextBanks();
			}
		};

		_presentation.PatternBooksRetired += OnPatternBooksRetired;

		_books.Host = _presentation;
		_infoPanel.Host = _presentation;
		_spells.Host = _presentation;
		// The screen names the row; this turns it into the same action the front-end fold uses,
		// so there is one dispatch for the whole feature.
		foreach (Engine_UiListBookScreen listBook in new[] { _multiplayer, _modsBook })
		{
			listBook.Host = _presentation;
			listBook.Books = _books;
			listBook.Action = command =>
			{
				if (command.StartsWith(Engine_UiNetworkSource.CommandPrefix, System.StringComparison.Ordinal))
				{
					DispatchNetwork(command[Engine_UiNetworkSource.CommandPrefix.Length..]);
				}
			};
		}

		// The host page book's two rows ARE the question's two answers.
		_hostPageBook.Host = _presentation;
		_hostPageBook.Books = _books;
		_hostPageBook.Action = command =>
			Engine_UiHostBoxes.Answer(command == Engine_UiHostBoxPage.AcceptCommand);
		RegisterCommands();
		_showing = Screen;

		BuildRebuiltHud();

		// If Godot is STARTING on the menu, own it immediately rather than waiting to be told.
		// FollowGuest is the other way in and the one a real boot takes; this covers a harness
		// with no guest, so menu ownership is not gated on a guest report.
		if (string.Equals(Screen, Engine_UiFrontEndScreens.PresentationFor(Engine_UiModel.FrontEndScreen.MainMenu),
				System.StringComparison.OrdinalIgnoreCase))
		{
			AdoptMenu();
		}

		// The loading screen, above this layer and below the videos — see GameLoadingScreen.
		// Owned here because this class owns the layer ordering and the texture loader it needs.
		_loading = new GameLoadingScreen
		{
			Name = "LoadingScreen",
			Resolver = path => _presentation?.LoadTexture(path),
			// The game's own font and strings, from the library every Gameface screen uses. The
			// loading screen is not a presentation, but its tip arrives as a Babel KEY.
			Fonts = _presentation?.Fonts,
			Strings = _presentation?.Strings,
		};

		AddChild(_loading);

		// The screen fade. Owned here because a fade sits above the UI and below the loading art,
		// and this class owns the layer ordering. See Engine_UiScreenFade.
		_fade = new Engine_UiScreenFade { Name = "ScreenFade" };
		AddChild(_fade);

		GD.Print($"[game-ui] built '{Screen}' from {dataRoot} " +
			"in its own viewport (hidden until the logos end)");
	}

	/// <summary>
	/// Shows whichever presentation the guest currently has active. Idempotent — rebuilding a
	/// screen graph is not free, so a repeated report of the same screen does nothing.
	/// </summary>
	public void FollowGuest(Engine_UiModel.FrontEndScreen screen, int sceneSlide)
	{
		if (_presentation is null)
		{
			return;
		}

		string? presentation = Engine_UiFrontEndScreens.PresentationFor(screen);
		if (presentation is null)
		{
			// The front end is gone — take it down; nothing else frees the built screen, and
			// GameWindow conceals the layer only while the guest has NO live UI.
			//
			// Gated on the VTABLE, not on `screen`: once the game has taken over, `0x83496B08`
			// holds a base-only object whose +180/+184/+188 are not screen pointers, so `screen`
			// reads None from meaningless fields — and None also occurs for a beat between
			// front-end screens. FrontEndGuiIsUp separates the two.
			if (!HostBridge.FrontEndGuiIsUp)
			{
				LeaveFrontEnd();
			}

			return;
		}

		// The guest's own per-element slides — a button on GetFocus, a card on Selected. Compared
		// as a whole: one widget changing state means a rebuild either way. Keyed by ORDINAL —
		// the guest's depth-first walk is byte-for-byte the .bgf element order, while the name
		// hash is ambiguous (77 elements share 47 names on the title screen).
		System.Collections.Generic.List<Engine_UiModel.UiElement>? live = HostBridge.GetUiElements();
		int[]? slides = null;
		string?[]? texts = null;
		bool[]? visible = null;
		if (live is { Count: > 0 })
		{
			slides = new int[live.Count];
			texts = new string?[live.Count];
			visible = new bool[live.Count];
			for (int i = 0; i < live.Count; ++i)
			{
				slides[i] = live[i].Slide;
				texts[i] = live[i].Text;
				visible[i] = live[i].IsGloballyActive;
			}
		}

		// Live opacity, keyed the same way. NOT a visibility lever: sub_8229E358 uses opacity only
		// to choose the transparent bucket, and a fully transparent element is still traversed and
		// submitted — hiding is globalactive's job, above.
		//
		// Read every frame, AHEAD of the unchanged test: the owned book below is a standing tree
		// that follows the guest sample by sample, not a mirror rebuilt on change.
		float?[]? opacity = null;
		Engine_UiScreen.LiveTint?[]? tint = null;
		if (slides is not null)
		{
			Engine_UiModel.LiveAttribute[,]? attributes = HostBridge.GetUiAttributes(Engine_UiLiveAttributes.OpacityIds);
			if (attributes is not null)
			{
				int count = System.Math.Min(attributes.GetLength(0), slides.Length);
				opacity = new float?[count];
				tint = new Engine_UiScreen.LiveTint?[count];
				for (int i = 0; i < count; ++i)
				{
					opacity[i] = attributes[i, 0].Present ? attributes[i, 0].AsFloat : null;
					tint[i] = ReadTint(attributes, i);
				}
			}
		}

		// The front end needs this as much as the HUD: its own screens lay elements out by
		// writing position attributes, so without it they stack where the file put them.
		float?[]?[]? transform = slides is null ? null : Engine_UiLiveAttributes.Transform(0u, slides.Length);

		// The guest's chosen art: the front end decides pictures at runtime — a save card's
		// portrait, a storage device's icon — and the file carries only a placeholder.
		uint[]? textures = live is { Count: > 0 }
			? HostBridge.GetUiTextureHashes(0u, live.Count)
			: null;

		// The book stands whether or not the mirror needs rebuilding, and the mirror's visibility
		// is restated from the answer every frame rather than toggled once.
		bool owned = _books.DriveFrontEnd(
			presentation, slides, texts, visible, opacity, transform, textures);

		// GODOT OWNS EVERY SCREEN IT HAS A TEMPLATE FOR. The guest's tree is SAMPLED for its
		// data and never drawn once a template names the screen — a book that is not standing
		// yet (its art still reading) is a blank frame, not a frame of the guest's screen.
		// Only a presentation Godot has no template for at all is drawn as the guest's.
		// NEVER DRAWN. GODOT OWNS EVERY SCREEN. The guest's tree is a data source; a screen
		// with no template yet is a blank frame and a line in the log, not the guest's picture.
		ShowMirror(false);
		if (!owned && Engine_UiTemplates.ScreenFor(presentation) is null && _untemplated.Add(presentation))
		{
			GD.Print($"[game-ui] Godot has NO TEMPLATE for '{presentation}' — nothing is drawn " +
				"for it (the guest's tree is never shown)");
		}

		// Visibility counts as a change too: the guest hides and reveals widgets without any slide
		// moving, which is most of what a screen's Lua does. Two nulls compare EQUAL — a starved
		// presentation publishes no elements, and requiring non-null rebuilds it every frame.
		bool same = presentation == _showing &&
			SameLive(slides, _showingSlides) && SameLive(visible, _showingVisible);
		if (same)
		{
			return;
		}

		_presentation.LiveOpacity = opacity;
		_presentation.LiveTint = tint;
		_presentation.LiveVisible = visible;
		_presentation.LiveSlides = slides;
		_presentation.LiveText = texts;
		_presentation.LiveTransform = transform;
		_presentation.LiveTextures = textures;
		if (!_presentation.ShowByName(presentation, sceneSlide))
		{
			GD.PushWarning($"[game-ui] the guest is on '{screen}' but '{presentation}' " +
				"is not among the shipped presentations");
			return;
		}

		// The tree that was just built is a NEW node and comes back visible. Restated here as
		// well as above, because between the two the mirror would flash for one frame on every
		// state change — which on the front end is several times a second.
		ShowMirror(false);

		// GODOT OWNS THE MENU from here on.
		//
		// This runs after EVERY ShowByName, not once, because ShowByName rebuilds the whole
		// node tree — the guest's twelve row components come back with it each time, and a
		// screen carrying both their menu and ours is the failure this prevents.
		//
		// Only the STATE is set here; the debug menu's visibility is applied per-frame in
		// _Process, because the reveal after the boot movies is not a screen change and
		// would otherwise never reach a gated ShowDebugMenu.
		_frontendUp = screen != Engine_UiModel.FrontEndScreen.None;
		if (screen == Engine_UiModel.FrontEndScreen.MainMenu)
		{
			AdoptMenu();
		}
		else
		{
			if (_menuView is not null)
			{
				// Off the main menu — the title, or a screen we do not draw. The view is kept
				// rather than freed so the highlight survives a trip out to the title and back,
				// which is what the original does: leaving the menu does not reset the selection.
				_menuView.Visible = false;

				// The card goes with it and does NOT survive: leaving it armed would put it back
				// over whatever comes next.
				if (_cardView is not null)
				{
					_cardView.Visible = false;
				}
			}
		}

		if (presentation != _showing)
		{
			GD.Print($"[game-ui] following the guest onto '{presentation}' " +
				$"({slides?.Length ?? 0} live elements, " +
				$"{(_presentation.CurrentScreen?.IsLive == true ? "applied" : "NOT applied")})");
		}

		// The ordinal join, checked rather than believed. Slide, text and opacity are applied by
		// position, so a divergence gives every later element another element's state — a mostly
		// correct screen with stray widgets. Element TYPE is the cheap invariant: the guest holds
		// a kind byte numbered as BgfElementType and it carries no live state, so any mismatch is
		// a join failure.
		if (presentation != _showing && live is { Count: > 0 } &&
			_presentation.CurrentScreen is { } built)
		{
			int compared = System.Math.Min(built.Nodes.Length, live.Count);
			int diverged = -1;
			int mismatches = 0;
			for (int i = 0; i < compared; ++i)
			{
				if ((int)built.Nodes[i].Type == live[i].Type)
				{
					continue;
				}

				++mismatches;
				if (diverged < 0)
				{
					diverged = i;
				}
			}

			GD.Print(diverged < 0
				? $"[game-ui] ordinal join OK: {compared} elements, types agree end to end"
				: $"[game-ui] ORDINAL JOIN BROKEN: {mismatches} of {compared} element types " +
				  $"disagree, first at index {diverged} " +
				  $"(file says {built.Nodes[diverged].Type}, guest says {live[diverged].Type}) " +
				  "— every element past that point is wearing another element's state");

			if (built.Nodes.Length != live.Count)
			{
				GD.Print($"[game-ui] element COUNT differs: file {built.Nodes.Length}, " +
					$"guest {live.Count} — the join cannot be positional at all");
			}
		}

		// How much of the screen the guest told us about, once per screen. Opacity is the lever
		// the screens' Lua hides things with, and an unreported element keeps the file's value —
		// the shipped .bgf is full of placeholder labels that stay off screen only because
		// something set them transparent.
		if (presentation != _showing && opacity is not null)
		{
			int reported = 0;
			int hidden = 0;
			foreach (float? value in opacity)
			{
				if (value is not { } alpha)
				{
					continue;
				}

				++reported;
				if (alpha <= 0.001f)
				{
					++hidden;
				}
			}

			GD.Print($"[game-ui] opacity: {reported} of {slides?.Length ?? 0} elements " +
				$"reported one, {hidden} of those are fully transparent");

			// Did following globalactive change anything? The file-derived visibility and the
			// guest's answer should DISAGREE; matching element for element means the runtime
			// never moved from what the .bgf authored.
			if (visible is not null && _presentation?.CurrentScreen is { } current)
			{
				int guestHides = 0;
				int fileHides = 0;
				int disagree = 0;
				int compared = System.Math.Min(current.Nodes.Length, visible.Length);
				for (int i = 0; i < compared; ++i)
				{
					// Nodes[i].Visible already carries the live value, so the file's answer comes
					// from the document's own slide state instead.
					bool fromFile = current.FileVisible(i);
					if (!visible[i])
					{
						++guestHides;
					}

					if (!fromFile)
					{
						++fileHides;
					}

					if (visible[i] != fromFile)
					{
						++disagree;
					}
				}

				GD.Print($"[game-ui] visibility: guest hides {guestHides} of {compared}, " +
					$"the file would hide {fileHides}, they disagree on {disagree}");
			}

			ReportPages(slides, visible);
		}

		// Does the guest's submission gate agree with the by-name pick? The front end picks its
		// screen by name from the guest's reported active screen while the in-game path asks the
		// gate; the two should be one mechanism, so the disagreement is measured before the
		// by-name path can be deleted.
		if (presentation != _showing)
		{
			foreach (Engine_UiModel.Presentation p in HostBridge.GetPresentations())
			{
				if (!p.Is(presentation + ".bgf"))
				{
					continue;
				}

				GD.Print(p.IsShowing
					? $"[game-ui] gate agrees: '{presentation}' is SHOWING"
					: $"[game-ui] GATE DISAGREES: drawing '{presentation}' by name, but the " +
					  $"guest has it hidden (active={p.ActiveElements}" +
					  $"{(p.IsFrozen ? " FROZEN" : string.Empty)}" +
					  $"{(p.IsSuppressed ? " PARKED" : string.Empty)}) — the by-name path " +
					  "cannot be replaced by the gate until this stops happening");
			}
		}

		_showing = presentation;
		_showingSlides = slides;
		_showingVisible = visible;
	}

	/// <summary>
	/// Shows or hides the mirrored front-end tree. It is never freed and never stops being BUILT —
	/// the live art catalogue comes off it and the owned views place themselves against it — so
	/// ownership means only that it stops being drawn.
	/// </summary>
	private readonly System.Collections.Generic.HashSet<string> _untemplated =
		new(System.StringComparer.Ordinal);

	private void ShowMirror(bool show)
	{
		if (_presentation?.MirrorTree is { } tree && IsInstanceValid(tree) && tree.Visible != show)
		{
			tree.Visible = show;
			GD.Print($"[game-ui] the mirrored '{_showing}' tree is now " +
				(show ? "DRAWN (no owned book stands)" : "hidden — Godot's own book is the screen"));
		}
	}

	/// <summary>
	/// Whether two live arrays state the same thing, counting absent and empty as equal. Absent
	/// is a real reading — the guest publishing no elements — and must compare equal to itself or
	/// the caller rebuilds for ever, while still comparing UNEQUAL to a populated array.
	/// </summary>
	private static bool SameLive<T>(T[]? live, T[]? shown)
	{
		if (live is null || live.Length == 0)
		{
			return shown is null || shown.Length == 0;
		}

		return shown is not null && live.Length == shown.Length && live.SequenceEqual(shown);
	}

	/// <summary>
	/// Reports the state of each PAGE of the front-end screen, once per screen change.
	/// `frontendmainmenu` is seven pages plus a dialog in one file under a single `Layer`
	/// element, reached by the Lua as `Scene.Layer.newgame`, `.loadgame`, `.options`,
	/// `.multiplayer`, `.debug`; only one should be on screen at a time. Prints globalactive,
	/// the live slide (`loadgame` slide 1 parks its cards at y -598, slide 2 fans them, and 0
	/// means never set, falling back to the authored fan) and whether a live position was stated.
	/// Direct children of `Layer` rather than a hardcoded page list.
	/// </summary>
	private void ReportPages(int[]? slides, bool[]? visible)
	{
		if (_presentation?.CurrentScreen is not { } screen || slides is null || visible is null)
		{
			return;
		}

		float?[]?[]? transform = Engine_UiLiveAttributes.Transform(0u, slides.Length);

		// Node to ordinal, once: a page's subtree is walked by Children, and scanning Nodes for
		// each would be quadratic over 3812 elements.
		var ordinals = new System.Collections.Generic.Dictionary<UiNode, int>(screen.Nodes.Length);
		for (int i = 0; i < screen.Nodes.Length; ++i)
		{
			ordinals[screen.Nodes[i]] = i;
		}

		// How much of a page the guest has switched on. A page that is off should be off all the
		// way down — the engine skips an inactive element AND its whole subtree.
		(int Live, int Total) Count(UiNode node)
		{
			int live = 0;
			int total = 0;

			if (ordinals.TryGetValue(node, out int ordinal) && ordinal < visible.Length)
			{
				++total;
				if (visible[ordinal])
				{
					++live;
				}
			}

			foreach (UiNode child in node.Children)
			{
				(int childLive, int childTotal) = Count(child);
				live += childLive;
				total += childTotal;
			}

			return (live, total);
		}

		var report = new System.Text.StringBuilder("[game-ui] pages under 'Layer':");
		int pages = 0;

		for (int i = 0; i < screen.Nodes.Length && i < slides.Length; ++i)
		{
			if (screen.Nodes[i].Parent is not { } parent ||
				!string.Equals(parent.Name, "Layer", System.StringComparison.Ordinal))
			{
				continue;
			}

			++pages;
			(int live, int total) = Count(screen.Nodes[i]);

			string moved = transform is not null && i < transform.Length && transform[i] is not null
				? "stated"
				: "-";

			report.Append($"\n    {screen.Nodes[i].Name,-20} slide={slides[i],-3} " +
				$"active={(visible[i] ? "on " : "OFF")} live-transform={moved,-6} " +
				$"subtree {live}/{total} on");
		}

		if (pages > 0)
		{
			GD.Print(report.ToString());
		}
	}

	/// <summary>
	/// Takes the whole front end down, because the guest no longer has one.
	///
	/// Idempotent, and it has to be: this is reached from a per-frame path, and the guest sits
	/// with no front end for the entire game.
	///
	/// The MENU MODEL survives. Only the view goes — the highlight is the player's place in a
	/// list, and the original does not reset it either. The card is different and is reset on
	/// the way in rather than kept; see <see cref="OpenScreen"/>.
	/// </summary>
	private void LeaveFrontEnd()
	{
		if (_showing is null)
		{
			return;
		}

		GD.Print($"[game-ui] the guest has left the front end — taking '{_showing}' down");

		// Freed, not hidden: both views hold references INTO the tree about to go away (the row
		// view keeps the shipped `MenuItemButton` it duplicates slots from). AdoptMenu re-sources
		// them from the next build; the model they draw is untouched here.
		if (_menuView is not null)
		{
			_menuView.QueueFree();
			_menuView = null;
		}

		if (_cardView is not null)
		{
			_cardView.QueueFree();
			_cardView = null;
		}

		// The load fan goes with them. Choosing a save takes the guest out of the front end
		// immediately, so `DriveLoadCarousel`'s own tidy-up never runs again — it sits behind an
		// `IsRevealed` gate that is now false. Freed, not hidden, for the reason above;
		// `AdoptSaveCards` rebuilds it from the next front end.
		if (_saveCardView is not null)
		{
			_saveCardView.QueueFree();
			_saveCardView = null;
		}

		if (_warningView is not null)
		{
			_warningView.QueueFree();
			_warningView = null;
		}

		// The extras go too: `scrollingbackground` is a front-end presentation drawn behind the
		// by-name screen, otherwise retired only by the in-game sampler up to half a second later.
		RetireDrawn("the guest left the front end");

		// The owned book is hidden, not freed — its pattern book is cached for the session and
		// rebuilding costs a bank read — but nothing drawing the front end may still draw once
		// the guest has left it.
		if (_books.Book(_books.FrontEndBookId) is { } standing)
		{
			standing.Visible = false;
		}

		_books.ReleaseFrontEnd();

		_presentation?.ClearScreen();

		_showing = null;
		_showingSlides = null;
		_showingVisible = null;
	}

	private bool? _developerMenu;

	/// <summary>Whether the launcher's `developer_menu_enabled` setting asks for the overlay.</summary>
	private bool DeveloperMenuEnabled
	{
		get
		{
			if (_developerMenu is null)
			{
				try
				{
					_developerMenu = Refii.Core.Engine_RefiiConfig.Bool(
						Refii.Core.Engine_RefiiConfig.Read(out _), "developer_menu_enabled", false);
				}
				catch (System.Exception)
				{
					_developerMenu = false;
				}
			}

			return _developerMenu.Value;
		}
	}

	/// <summary>Debug stand-in for the main menu: shows the clickable DebugMenu instead of the
	/// Godot menu view. Created on first use so a build without the debug fixture costs nothing.</summary>
	private void ShowDebugMenu()
	{
		_debugMenu ??= new Refii.Launcher.Tools.DebugMenu { Name = "DebugMenu" };
		if (!IsAncestorOf(_debugMenu))
		{
			AddChild(_debugMenu);
		}
		_debugMenu.Visible = true;
		if (_menuView is not null)
		{
			_menuView.Visible = false;
		}
	}

	/// <summary>
	/// Takes the menu over from the presentation: the guest's rows go away, ours go up. Clone the
	/// shipped row components for their ART first, hide their group second — reversed, every row
	/// inherits the invisibility. The group may be ABSENT rather than hidden: the builder drops
	/// an inactive element's whole subtree, so on a slide where the menu is not up there is no
	/// "Buttons" node; the view then falls back to text-only rows.
	/// </summary>
	private void AdoptMenu()
	{
		if (_presentation?.Theme is not { } theme)
		{
			return;
		}

		// Two trees, two jobs: the group to HIDE is the live one (the copy on screen); the row to
		// CLONE is the file's, because the guest keeps its own components faded out.
		Node3D? group = theme.ArtSourceNode(Engine_FrontEndMenuView.RowGroupName);
		Node3D? pattern = _presentation?.Templates?.ArtSourceNode(Engine_FrontEndMenuView.RowGroupName);
		Node3D? template = pattern is null
			? null
			: Engine_FrontEndMenuView.RowTemplateIn(pattern);

		if (_menuView is null)
		{
			// The MODEL outlives the view: the view holds nodes from the tree freed when the
			// guest leaves the front end, while the model holds the list and the highlight —
			// the player's place in a menu, which survives a trip to the title and back.
			_menu ??= Engine_FrontEndMenu.BuildRoot();

			// The authored anchor and cursor pose, read from the book — see
			// Engine_FrontEndMenuView.Anchor for why the constant is 25 low. The cursor assembly
			// carries its visuals in a wrapper authored 210 up from its root, so it is rebased
			// against the same anchor rather than zero-rooted.
			Vector3 anchor = Engine_FrontEndMenuView.CanvasAnchor;
			if (pattern is not null)
			{
				Vector3 origin = Engine_UiTheme.CanvasTransform(pattern).Origin;
				anchor = new Vector3(origin.X, origin.Y, 0f);
			}

			Engine_UiTheme cursorBook = _presentation?.Templates ?? theme;
			Transform3D? cursorPose = null;
			if (cursorBook.ArtSourceNode("MenuHighlight") is { } highlightSource)
			{
				Transform3D authored = Engine_UiTheme.CanvasTransform(highlightSource);
				authored.Origin -= anchor;
				cursorPose = authored;
			}

			// Every time the menu comes up, not once: the pump's first sample may land after
			// the first build, and the player can change the setting and come back.
			ApplySubtitlesEmphasis();
			_menuView = new Engine_FrontEndMenuView
			{
				Name = "FrontEndMenu",
				Theme = theme,
				RowTemplate = template,
				Anchor = anchor,
				// The shipped highlight art, if this slide carries it. UpdateCursorColor only
				// changes its opacity and the scripts never move it, so a clone at its authored
				// spot is what the original does. From the pattern book, like the row.
				CursorTemplate = cursorBook.Art("MenuHighlight"),
				CursorPose = cursorPose,

				// The star and pip slides out of THIS file's button pool — every row sits on
				// one, the command rows on the empty slide. See Engine_FrontEndMenuView.ApplyPoses.
				Ratings = Engine_UiRowRating.Resolve(_presentation?.CurrentScreen),
			};

			// Parented to the HOST, not the built screen: that screen is destroyed and recreated
			// on every guest state change. Both have identity transforms, so they share the
			// authored canvas space. Non-null — the theme test above only passes with a live
			// presentation; the `?.` reads are for TEMPLATES, which a half-loaded host may lack.
			_presentation!.AddChild(_menuView);
			_menuView.Bind(_menu);

			GD.Print($"[game-ui] Godot owns the main menu now — " +
				$"{_menu.Visible.Count} entries, " +
				$"row art {(template is null ? "NOT sourced (text-only rows)" : "cloned from the presentation")}, " +
				$"cursor {(_menuView.CursorTemplate is null ? "stand-in" : "shipped")}");
		}

		AdoptCardChoice(theme);
		AdoptSaveCards(theme);
		AdoptWarning(theme);

		// The guest's own rows, out of the way. Hidden rather than freed: the tree is rebuilt
		// from the file on the next state change anyway, and freeing a node the theme's
		// catalogue still points at would leave a dangling entry behind.
		if (group is not null)
		{
			group.Visible = false;
		}

		// And its highlight: `MenuHighlight` is a SIBLING of the row group, not a child, so
		// hiding the group leaves it painting at its authored spot. Cloned first, hidden second.
		// EVERY instance — the screen ships its furniture twice, the authored 4:3 layout and the
		// 16:9 copy on a marker child — and the count goes in the takeover line.
		int highlightsHidden = 0;
		foreach (Engine_UiTheme? catalogue in new[] { _presentation?.Templates, theme })
		{
			if (catalogue is null)
			{
				continue;
			}

			foreach (Node3D instance in catalogue.ArtSourceNodes("MenuHighlight"))
			{
				if (instance.Visible)
				{
					instance.Visible = false;
					++highlightsHidden;
				}
			}
		}

		if (highlightsHidden != 0)
		{
			GD.Print($"[game-ui] hid {highlightsHidden} guest MenuHighlight instance(s) — " +
				"our cloned cursor is the only highlight now");
		}

		// Registered, so the hide survives the per-state-change rebuild: the hides above cover
		// the tree standing now, the registration every tree after it.
		_presentation!.SuppressedElementNames.Add("MenuHighlight");
		_presentation.SuppressedElementNames.Add(Engine_FrontEndMenuView.RowGroupName);

		// Not while any modal panel is up, and `ModalPanelUp` must list all of them: this runs
		// after EVERY rebuild, so an unconditional show puts the rows back over whatever is in
		// front of them a fraction of a second after they were hidden.
		_menuView.Visible = !ModalPanelUp;
		ApplyMenuAvailability();
	}

	/// <summary>
	/// Takes the gender card over from the presentation, as <see cref="AdoptMenu"/> takes the
	/// rows. Built HERE rather than on New Game: the art can only be cloned while
	/// `frontendmainmenu` is standing, and by then the screen may have been rebuilt under the
	/// catalogue. The shipped group is hidden on every rebuild — its `Intro` slide only parks it
	/// at x -831/883, and a live slide brings it back into the canvas.
	/// </summary>
	private void AdoptCardChoice(Engine_UiTheme theme)
	{
		Node3D? group = theme.ArtSourceNode(Engine_FrontEndCardChoice.GroupName);
		Node3D? pattern = _presentation?.Templates?.ArtSourceNode(Engine_FrontEndCardChoice.GroupName);

		if (_cardView is null)
		{
			// Cloned from the FILE, placed by the LIVE group's transform: the pattern book has the
			// art, the live tree is the authority on where on the canvas the group sits.
			_cardView = Engine_FrontEndCardChoice.FromPresentation(pattern ?? group);
			if (group is not null)
			{
				_cardView.Transform = group.GlobalTransform;
			}

			_presentation!.AddChild(_cardView);

			GD.Print($"[game-ui] gender card ready — art " +
				$"{(_cardView.HasShippedArt ? "cloned from 'choosecard'" : "NOT sourced (stand-ins)")}" +
				$" ({(pattern is null ? "live tree" : "pattern book")})" +
				$"{(group is null ? ", group ABSENT from this build" : string.Empty)}");
		}

		// The LIVE group, re-hidden on every rebuild including the first: its tree is rebuilt
		// from the file on every guest state change and comes back visible.
		if (group is not null)
		{
			group.Visible = false;
		}
	}

	/// <summary>
	/// Takes the Load Game fan over from the presentation, as <see cref="AdoptCardChoice"/> takes
	/// the gender card. Built HERE, not when the guest opens its carousel: the card art can only
	/// be cloned while `frontendmainmenu` is standing, and by substate 5 the screen has been
	/// rebuilt several times. The shipped `loadgame` page is hidden on every rebuild — its
	/// parking at y -598 is only the closed slide, and a live slide brings it back.
	/// </summary>
	private void AdoptSaveCards(Engine_UiTheme theme)
	{
		Node3D? page = theme.ArtSourceNode(Engine_FrontEndSaveCards.PageName);
		Node3D? pattern = _presentation?.Templates?.ArtSourceNode(Engine_FrontEndSaveCards.PageName);

		if (_saveCardView is null)
		{
			// Cloned from the FILE, placed by the LIVE page's transform — same split as the
			// gender card: the guest leaves its own copy in whatever state its last slide put it.
			_saveCardView = Engine_FrontEndSaveCards.FromPresentation(pattern ?? page);
			_saveCardView.Theme = theme;
			if (page is not null)
			{
				_saveCardView.Transform = page.GlobalTransform;
			}

			_presentation!.AddChild(_saveCardView);

			GD.Print("[game-ui] load fan ready — art " +
				$"{(_saveCardView.HasShippedArt ? "cloned from 'loadgame'" : "NOT sourced (stand-ins)")}" +
				$" ({(pattern is null ? "live tree" : "pattern book")})" +
				$"{(page is null ? ", page ABSENT from this build" : string.Empty)}");
		}

		if (page is not null)
		{
			page.Visible = false;
		}
	}

	/// <summary>True while the warning box shows the GUEST's dialog rather than a host refusal.</summary>
	private bool _guestDialogShown;

	private bool _menuHiddenForDialog;

	private string? _guestDialogShownText;

	/// <summary>
	/// THE FRONT END'S OWN DIALOG. The guest raises it itself — Load Game with no saves or a
	/// refused save, a co-op or network failure arriving on the menu or the title, Downloadable
	/// Content with nobody signed in — through `sub_826E40E0`, which writes the three
	/// `dialog.Group` texts and waits in its dialog mode for the owner's own accept. The host's
	/// warning box shows the guest's strings, and A/B go back through `FrontEndDialog:36/37`.
	/// The host's own refusals never raise it, so the two cannot stand together.
	/// </summary>
	private bool DriveGuestDialog()
	{
		Engine_UiSource.FrontEndDialogState? up = IsRevealed && !HostBridge.IsInGame
			? Engine_UiSource.Current.GetFrontEndDialog()
			: null;
		if (up is not { } dialog)
		{
			if (_guestDialogShown)
			{
				_guestDialogShown = false;
				_guestDialogShownText = null;
				if (_warningView is not null)
				{
					_warningView.Visible = false;
				}

				if (_menuHiddenForDialog && _menuView is not null)
				{
					_menuView.Visible = true;
				}

				_menuHiddenForDialog = false;
				GD.Print("[game-ui] front-end dialog closed by the guest");
			}

			return false;
		}

		if (_warningView is null && _presentation?.Theme is { } theme)
		{
			AdoptWarning(theme);
		}

		if (_warningView is null)
		{
			return false;
		}

		string body = FrontEndDialogText(dialog.Presentation, "yousure");
		string accept = FrontEndDialogText(dialog.Presentation, "OK");
		string cancel = FrontEndDialogText(dialog.Presentation, "cancel");
		string signature = $"{body}\u0001{accept}\u0001{cancel}\u0001{dialog.AllowCancel}";
		if (signature != _guestDialogShownText)
		{
			_guestDialogShownText = signature;
			_warningView.Show(_presentation?.Theme, body, accept.Length > 0 ? accept : "GUI_OK",
				twoButton: dialog.AllowCancel, cancelTag: dialog.AllowCancel && cancel.Length > 0 ? cancel : null);
			_warningView.AcceptOpens = null;
			GD.Print($"[game-ui] front-end dialog raised by the guest — '{body}' " +
				$"({(dialog.AllowCancel ? "two" : "one")}-button) — {_warningView.LastReport}");
		}

		if (!_guestDialogShown && _menuView is { Visible: true })
		{
			_menuView.Visible = false;
			_menuHiddenForDialog = true;
		}

		_guestDialogShown = true;
		int clicked = _warningView.Click();
		if (dialog.Accepting)
		{
			if (GameInput.MenuAcceptPressed || clicked > 0)
			{
				Engine_UiSource.Current.PostFrontEndAction("FrontEndDialog:36");
			}
			else if ((GameInput.MenuBackPressed || clicked < 0) && dialog.AllowCancel)
			{
				Engine_UiSource.Current.PostFrontEndAction("FrontEndDialog:37");
			}
		}

		return true;
	}

	/// <summary>One of the dialog's three texts as the guest wrote it, `dialog.Group.{leaf}` in
	/// the owner's presentation; empty when it cannot be read.</summary>
	private string FrontEndDialogText(uint presentation, string leaf)
	{
		foreach (Engine_UiModel.Presentation p in Engine_UiSource.Current.GetPresentations())
		{
			if (p.Address != presentation)
			{
				continue;
			}

			string name = System.IO.Path.GetFileNameWithoutExtension(p.Name.Replace('\\', '/'));
			if (_presentation?.DocumentFor(name) is not { } document ||
				Engine_UiElementState.Current.Elements(presentation) is not { } elements)
			{
				return string.Empty;
			}

			UiNode? node = Engine_UiStatedPanel.Child(
				Engine_UiStatedPanel.Child(Engine_UiStatedPanel.FindByName(document.Roots, "dialog"), "Group"),
				leaf);
			return node is not null && node.Ordinal < elements.Count
				? elements[node.Ordinal].Text ?? string.Empty
				: string.Empty;
		}

		return string.Empty;
	}

	/// <summary>
	/// Adopts the shipped `dialog` group — the box every refusing guard puts up.
	///
	/// Same shape as the gender card above, and hidden the same way: with Godot owning the
	/// front end, leaving the guest's own group live in the tree would draw a second box under
	/// ours. It is a PAGE of `frontendmainmenu` rather than a presentation of its own, which is
	/// why it is adopted here rather than loaded.
	/// </summary>
	private void AdoptWarning(Engine_UiTheme theme)
	{
		Node3D? group = theme.ArtSourceNode(Engine_FrontEndWarning.GroupName);
		Node3D? pattern = _presentation?.Templates?.ArtSourceNode(Engine_FrontEndWarning.GroupName);

		// Off the main menu — the title, where a host box can be raised too — the shown screen's
		// catalogue does not carry the page: the front end's own pattern book does, at the place
		// the file authors it. A catalogue build, not the live tree, so the paper's parked middle
		// column is there to grow a long host box (Engine_UiPaperBox).
		pattern ??= _presentation?.PatternBookFor(FrontEndMenuPresentation)?.ArtSourceNode(Engine_FrontEndWarning.GroupName);

		if (_warningView is null)
		{
			_warningView = Engine_FrontEndWarning.FromPresentation(pattern ?? group);
			if (group is not null)
			{
				_warningView.Transform = group.GlobalTransform;
			}
			else if (pattern is not null)
			{
				_warningView.Transform = Engine_UiTheme.CanvasTransform(pattern);
			}

			_presentation!.AddChild(_warningView);

			GD.Print("[game-ui] warning box ready — art " +
				$"{(_warningView.HasShippedArt ? "cloned from 'dialog'" : "NOT sourced")}" +
				$" ({(pattern is null ? "live tree" : "pattern book")})" +
				$"{(group is null ? ", group ABSENT from this build" : string.Empty)}");
		}

		if (group is not null)
		{
			group.Visible = false;
		}
	}

	/// <summary>How far this screen's owned row view stands in front of the mirrored tree.</summary>
	private static float ListLift(uint screenId) =>
		Engine_UiTemplates.Screen(screenId)?.ListLift ??
		(Engine_UiPresentationDepth.Step / 4f);

	/// <summary>Every row in a menu's tree, at any depth — for reporting what a resync moved.</summary>
	private static int CountRows(Engine_FrontEndMenu menu)
	{
		int count = 0;
		foreach (Engine_UiExpandableMenu.Item root in menu.Model.SpawnedItems)
		{
			_ = root;
			++count;
		}

		return count;
	}

	/// <summary>
	/// Takes the pause menu over from the mirrored `pausemainmenu` tree — the guest's list,
	/// values and art; our layout, highlight and navigation. Called on EVERY rebuild of that
	/// tree, which comes back with the guest's own button pool visible; the view itself is built
	/// once, from the FILE-ONLY pattern book.
	/// </summary>
	private void AdoptPauseMenu(Node3D built, uint address)
	{
		if (_presentation is null)
		{
			return;
		}

		_pauseAddress = address;

		// The owned book — chrome, right page, minimap, money — built once from the pattern book.
		// On the pause ROOT it draws instead of the mirrored tree; tab screens fall back to it.
		if (_pauseBook is null || !IsInstanceValid(_pauseBook))
		{
			// The one the loading screen prebuilt, when it got to it; otherwise built here.
			_pauseBook = _books.ClaimPrebuilt(0, PausePresentation) ?? _books.Ensure(0, PausePresentation);
		}

		// The banner, above the book — its own small owned tree, driven by the cycler's text.
		if (_pauseBanner is null || !IsInstanceValid(_pauseBanner))
		{
			_pauseBanner = _presentation.CloneTemplateTree(CyclerPresentation);
			if (_pauseBanner is not null)
			{
				_pauseBanner.Name = "PauseBanner";
				_pauseBanner.Position = new Vector3(0f, 0f, 600f);
				_pauseBanner.Visible = false;
				_presentation.MenuLayer.AddChild(_pauseBanner);
				GD.Print("[game-ui] the pause banner is owned — cloned from 'menucycler'");
			}
		}

		// The guest's own rows and highlight, out of THIS build. A throwaway catalogue rather than
		// FindByName: the screen ships its furniture twice (4:3 and 16:9), so a single lookup
		// hides one layout's copy and leaves the other painting.
		var liveArt = new Engine_UiTheme();
		liveArt.Catalogue(built, PausePresentation);
		int hidden = 0;
		foreach (string name in new[] { Engine_FrontEndMenuView.RowGroupName, "MenuHighlight" })
		{
			foreach (Node3D instance in liveArt.ArtSourceNodes(name))
			{
				if (instance.Visible)
				{
					instance.Visible = false;
					++hidden;
				}
			}
		}

		// THE SIDE CHANGED UNDER A STANDING VIEW: a join, a disconnect or a quit moves the
		// player between the hero's list and the joiner's, and the view is rebuilt for it.
		bool playerTwosPause = PauseIsPlayerTwos();
		if (_pauseView is not null && _pauseMenu is not null &&
			_pauseMenuJoiner != playerTwosPause)
		{
			GD.Print($"[game-ui] pause menu rebuilt — now " +
				(playerTwosPause ? "the joiner's variant" : "the hero's variant"));
			if (GodotObject.IsInstanceValid(_pauseView))
			{
				_pauseView.QueueFree();
			}

			_pauseView = null;
			_pauseMenu = null;
		}

		if (_pauseView is null)
		{
			Engine_UiTheme? book = _presentation.PatternBookFor(PausePresentation);
			Node3D? pattern = book is null ? null : Engine_UiButtonPool.FindButtonPool(book);
			Node3D? template = pattern is null
				? null
				: Engine_FrontEndMenuView.RowTemplateIn(pattern);

			// The anchor is the SCREEN's, not a constant: the slot table is authored in the
			// Buttons group's own space and each screen parks that group where it wants — this
			// one composes ExpandableMenu(-400,0) + Buttons(63,210) = (-337,210), while
			// CanvasAnchor is the front end's (-345,185).
			if (pattern is not null)
			{
				Vector3 origin = Engine_UiTheme.CanvasTransform(pattern).Origin;
				_pauseAnchor = origin;
			}

			// The cursor at the artist's spot: the MenuHighlight assembly carries its visuals in
			// a wrapper authored 210 up from its root, so the authored chain rebased into view
			// space is the pose — a zero-rooted clone paints the A badge at the top of the screen.
			Transform3D? cursorPose = null;
			if (book?.ArtSourceNode("MenuHighlight") is { } highlightSource)
			{
				Transform3D authored = Engine_UiTheme.CanvasTransform(highlightSource);
				authored.Origin -= _pauseAnchor;
				cursorPose = authored;
			}

			// REBUILT WHEN THE SIDE CHANGES. The two variants carry different rows (the joiner's
			// Weapons/Clothing are host tabs over the henchman's list); a menu built once and kept
			// across a join, a disconnect or a quit would keep the wrong variant for the rest of
			// the process.
			bool joiner = playerTwosPause;
			if (_pauseMenu is null || _pauseMenuJoiner != joiner)
			{
				_pauseMenu = Engine_PauseMenu.BuildHero(joiner);
				_pauseMenuJoiner = joiner;
			}

			_pauseView = new Engine_FrontEndMenuView
			{
				Name = "PauseMenu",
				// The pause root fades BOTH end slots: `sub_826A19E0` runs FadeOutBottomItemEarly
				// and FadeOutTopItemEarly (0x826a1a38 / 0x826a1a60) on open.
				ScreenId = 0,
				Theme = book,
				RowTemplate = template,
				CursorTemplate = book?.Art("MenuHighlight"),
				CursorPose = cursorPose,
				Ratings = Engine_UiRowRating.Resolve(_presentation.DocumentFor(PausePresentation)),
				Visible = false,
			};

			// Parented to the HOST rather than to the built tree, which is freed on every
			// guest state change — same lifetime split as the front-end view.
			_presentation.MenuLayer.AddChild(_pauseView);
			_pauseView.Bind(_pauseMenu);
			_pauseView.ClipRect = Engine_UiButtonPool.ListClip(_presentation.DocumentFor(PausePresentation));

			GD.Print($"[game-ui] Godot owns the pause menu now — {_pauseMenu.Visible.Count} " +
				$"entries, row art {(template is null ? "NOT sourced (text-only rows)" : "cloned from the pattern book")}, " +
				$"cursor {(_pauseView.CursorTemplate is null ? "stand-in" : "shipped")}" +
				$"{(cursorPose is null ? " (pinned-slot fallback)" : " at its authored spot")}, " +
				$"anchor ({_pauseAnchor.X}, {_pauseAnchor.Y}, z {_pauseAnchor.Z})");
		}

		// The pool's own plane — no lift. In `pausemainmenu.bgf` `Buttons` is authored at z 0,
		// `LeftFrame` at -10 and `MenuHighlight` at -11, which the builder's Z flip turns into
		// 0, +10, +11 toward the camera: cover over rows, badge over cover. `LeftFrame` lives
		// INSIDE `ExpandableMenu`, so our stand-in rows belong where `Buttons` is — not on the
		// presentation root's plane. The anchor's Z is part of the same composed chain as its X
		// and Y; see `_pauseAnchor`.
		_pauseView.Position = new Vector3(
			_pauseAnchor.X - Engine_UiTemplates.WideListShift(0), _pauseAnchor.Y,
			built.Position.Z + _pauseAnchor.Z +
				(Engine_UiTemplates.Screen(0)?.ListLift ?? 0f));

		// THE OWNED BOOK SHARES THE MIRROR'S SLAB. Its cover (+10), badge (+11) and Back bar
		// (+15) are authored relative to `Buttons`, so the book must sit where the rows' base is
		// — left at z 0 while the rows rode the mirror's slab, every row painted over the leather.
		if (_pauseBook is not null && IsInstanceValid(_pauseBook))
		{
			_pauseBook.Position = new Vector3(0f, 0f, built.Position.Z);
		}

		_pauseTreeUp = true;

		if (hidden != 0)
		{
			GD.Print($"[game-ui] hid {hidden} guest pause row/highlight instance(s) in this build");
		}
	}

	/// <summary>The bank entry name of the guest's pause presentation.</summary>
	private const string PausePresentation = "pausemainmenu";

	/// <summary>The pause banner's presentation — owned as a whole-tree clone.</summary>
	private const string CyclerPresentation = "menucycler";

	private Node3D? _pauseBanner;
	private uint _cyclerAddress;
	private string _bannerShown = string.Empty;

	/// <summary>
	/// Keeps the owned banner standing and its title current — the cycler's single live text
	/// is the current screen's NAME ("Paused", "Abilities", …), sampled off the presentation
	/// the mirror no longer draws.
	/// </summary>
	private void DrivePauseBanner(Engine_UiModel.PauseState? pause)
	{
		if (_pauseBanner is null || !IsInstanceValid(_pauseBanner))
		{
			return;
		}

		bool up = _pauseTreeUp && IsRevealed && pause is not null && pause.Value.ScreenId != 31;
		_pauseBanner.Visible = up;
		if (!up || _cyclerAddress == 0 ||
			Engine_UiElementState.Current.Elements(_cyclerAddress) is not { Count: > 0 } elements)
		{
			return;
		}

		// The cycler's one meaningful text — first non-empty in the walk.
		string title = string.Empty;
		foreach (Engine_UiModel.UiElement element in elements)
		{
			if (!string.IsNullOrEmpty(element.Text))
			{
				title = element.Text;
				break;
			}
		}

		if (title.Length == 0 || title == _bannerShown ||
			_presentation?.Theme is not { } theme)
		{
			return;
		}

		_bannerShown = title;
		if (Engine_UiTheme.FindByName(_pauseBanner, "Text_15") is MeshInstance3D label)
		{
			theme.SetMeshText(label, title);
		}
	}

	/// <summary>
	/// The in-game screens whose list Godot owns, and the presentation-independent fact that
	/// each of them has one.
	///
	/// The ids are the guest's own: 14 abilities, 15 items/weapons, 16 clothing, 17 logbook,
	/// 18 options, 21 crime, 22 shop, 23 world map, 25 gift, 26 selection, 27 decoration.
	/// Screen 0 is the pause ROOT, which <see cref="DrivePauseMenu"/> owns through the tab
	/// lever instead — a different dispatch, so a different driver.
	/// </summary>
	private static bool ScreenOwnsList(uint screenId) =>
		Engine_UiTemplates.Screen(screenId) is { OwnsList: true };

	/// <summary>
	/// True while a screen's second input LEVEL is up — the options category page — read off the
	/// element and slide value its TEMPLATE names.
	///
	/// The options screen has two input levels and only the first is a list: A on a category sets
	/// `this+24`, runs `g_Menu:SetExpandableMenuDisabled(true)` and opens a PAGE, where 28/29 move
	/// the row, 30/31 change its VALUE and 36 commits the whole page — nothing is written while
	/// scrolling. `sub_826D9FC8` puts `Foreground.ExpandableMenu` on slide 2 when the page opens
	/// and slide 3 when it closes, which the host already mirrors per ordinal.
	///
	/// Fails to FALSE: no template, an unresolved element or an unread slide all leave the list
	/// level standing, which the owned view can drive.
	/// </summary>
	private static bool ScreenPageOpen(uint screenId, Engine_UiDrawnPresentation drawn) =>
		Engine_UiTemplates.Screen(screenId) is { PageElement.Length: > 0 } template &&
		drawn.Screen is { } screen && drawn.Slides is { } slides &&
		Engine_UiScreenPanel.Resolve(screen, template.PageElement) is { } node &&
		node.Ordinal >= 0 && node.Ordinal < slides.Length &&
		slides[node.Ordinal] == template.PageSlide;

	/// <summary>
	/// One frame of the owned in-game list. True when it consumed the input.
	///
	/// The guest supplies the rows and HOLDS THE CURSOR — see <see cref="Engine_UiScreenMenu"/>.
	/// Directional input becomes a press and a release on the guest's stick latch, A and B become
	/// action codes 36 and 37 on the screen's handler, and the highlight drawn is read back out
	/// of the layout the guest produced. Yields the frame outright while a modal box is pending.
	/// </summary>
	/// <summary>The world map's region minimap markers, standing under the live `Page2.Contents`.</summary>
	private Engine_UiMinimapMarkers? _minimapMarkers;

	private const uint WorldMapScreenId = 23;

	/// <summary>
	/// The world map's minimap page: the stick's page turn (host-owned, the guest's own page
	/// index never sees our stick) and the markers the guest would have put on its GUI list —
	/// see <see cref="Engine_UiMinimapPage"/>. Every other screen drops the turn, so the map
	/// opens on the readout next time.
	/// </summary>
	private void DriveMinimap(uint screenId, Engine_UiOwnedBook book,
		HostBridge.WorldMapRegion worldMap, Engine_UiExpandableMenu.Item? row, double delta)
	{
		if (screenId != WorldMapScreenId)
		{
			Engine_UiMinimapPage.Reset();
			_minimapMarkers?.Feed(System.Array.Empty<Engine_UiMinimapPage.Marker>());
			return;
		}

		// A click on the right-hand page turns between the region and its map.
		int flip = GameInput.MenuHorizontal;
		if (flip == 0 && Engine_UiWorldMapPage.RegionOf(row) >= 0 && PageClicked(book))
		{
			flip = Engine_UiMinimapPage.Shown ? -1 : 1;
		}

		Engine_UiMinimapPage.DriveFlip(
			Engine_UiWorldMapPage.RegionOf(row) >= 0, flip, (float)delta);

		Node3D? contents = Engine_UiMinimapPage.Standing(row)
			? book.NodeFor(Engine_UiMinimapPage.Contents)
			: null;
		if (contents is null)
		{
			_minimapMarkers?.Feed(System.Array.Empty<Engine_UiMinimapPage.Marker>());
			return;
		}

		if (_minimapMarkers is null || !GodotObject.IsInstanceValid(_minimapMarkers))
		{
			_minimapMarkers = new Engine_UiMinimapMarkers
			{
				Name = "MinimapMarkers",
				ResolveTexture = hash => book.ResolveTexture?.Invoke(hash),
			};
		}

		if (_minimapMarkers.GetParent() != contents)
		{
			_minimapMarkers.GetParent()?.RemoveChild(_minimapMarkers);
			contents.AddChild(_minimapMarkers);
		}

		_minimapMarkers.Feed(Engine_UiMinimapPage.Markers(
			row, worldMap, book.AuthoredPosition("Foreground.Book.Pages." + Engine_UiMinimapPage.MapImage)));
	}

	private bool DriveScreenMenu(double delta, Engine_UiModel.PauseState? pause)
	{
		// Re-validate the held node: a Godot node freed elsewhere throws on EVERY access, and an
		// exception once a frame out of _Process reads as a hang.
		if (_screenMenuView is not null && !GodotObject.IsInstanceValid(_screenMenuView))
		{
			_screenMenuView = null;
			_screenMenuAddress = 0;
			_screenMenuScreen = uint.MaxValue;
		}

		if (!IsRevealed || _presentation is null || pause is not { } state ||
			state.ModalUp || !ScreenOwnsList(state.ScreenId) ||
			FindListPresentation(state.ScreenId) is not (uint address, Engine_UiDrawnPresentation drawn))
		{
			if (_screenMenuView is not null)
			{
				_screenMenuView.Visible = false;
			}

			// The guest's stick latch does not survive us walking away from it — see
			// HoldScreenStick.
			HoldScreenStick(0);
			GameInput.ScreenMenuOwnsInput = false;
			_screenMenuAddress = 0;
			if (pause is not { } stillOpen || stillOpen.ScreenId != Engine_UiScreenInput.AbilitiesScreenId)
			{
				_abilityHovered = -1;
			}

			// The panel follows its SCREEN, not this drive: every early return here is a frame
			// the owned text would keep painting over whatever replaced it, and the guest's own
			// copies are hidden for good. A MODAL is not a screen change — the guest dims the
			// screen behind its boxes and leaves the text there — so the test is whether the
			// panel's own screen is still up.
			if (pause is not { } behind || behind.ScreenId != _infoPanel.Screen)
			{
				_infoPanel.Withdraw();
			}

			// SAY WHY WE DECLINED. A screen that comes up mirrored looks identical to one we
			// never reached, and telling them apart used to cost a whole run — these screens
			// are several menu levels deep, so a narrowing step is expensive. Printed once per
			// distinct reason so a screen the host simply does not own stays quiet after the
			// first time.
			// 31 IS "NOTHING IS UP", not a screen we declined. Reporting it printed
			// "screen 31 left MIRRORED — this screen is not in the owned set" for the whole time
			// the player was simply walking around, which reads as a menu we failed to own.
			if (pause is { } declined && declined.ScreenId != 0 &&
				declined.ScreenId != (uint)Engine_UiModel.InGameScreen.None)
			{
				// THREE DECLINES, NOT TWO. "No populated pool" was printed for a screen whose
				// presentation the guest had not built yet, which is a different failure with a
				// different fix — one is a pool we read too early, the other is a sample we
				// took too late, and the report is what tells them apart.
				// Key first, string only on a change. The modal mode rides in the key only on the
				// branch that prints it, so a mode changing behind a different reason cannot
				// re-fire the line.
				int reason = declined.ModalUp
					? 0
					: !ScreenOwnsList(declined.ScreenId)
						? 1
						: _followedScreen != declined.ScreenId
							? 2
							: 3;

				long note = ((long)declined.ScreenId << 40) | ((long)reason << 32) |
					(reason == 0 ? declined.ModalMode : 0u);
				if (_screenMenuDeclined != note)
				{
					_screenMenuDeclined = note;
					GD.Print($"[game-ui] screen {declined.ScreenId} left MIRRORED — " +
						(reason switch
						{
							0 => $"a modal is up (mode {declined.ModalMode})",
							1 => "this screen is not in the owned set",
							2 => "the guest has not built its presentation yet",
							_ => "no drawn presentation has a populated ExpandableMenu pool",
						}));
				}

				// No screen is ever input-dead: the guest's action chain does not fire here, so a
				// screen with no owned list would take no input at all. The base vocabulary goes
				// through regardless — directions, A, B and the tab cycle land on the screen's
				// own handler, which ignores what it does not answer.
				if (IsRevealed && !declined.ModalUp && ScreenOwnsList(declined.ScreenId))
				{
					GameInput.ScreenMenuOwnsInput = true;
					_screenLevel.Drive(SurfaceInput(delta));
					if (GameInput.MenuTabPrevPressed)
					{
						HostBridge.ScreenAction(Engine_UiScreenInput.TabPrev);
					}
					else if (GameInput.MenuTabNextPressed)
					{
						HostBridge.ScreenAction(Engine_UiScreenInput.TabNext);
					}

					return true;
				}

				// The one-off panels answer a different vocabulary and are reached only through the
				// GameAction chain, which is dead here: the query panel (10) closes on 37,
				// minigame-completed (11) and quest-completion (12) on 6 (`sub_823CDC08` /
				// `sub_823D3F98`). Each screen STATES which button does what — see
				// `Engine_UiScreenActions` — so this is one path rather than four branches.
				if (IsRevealed && !declined.ModalUp &&
					Engine_UiScreenActions.OwnsRevealedInput(declined.ScreenId))
				{
					GameInput.ScreenMenuOwnsInput = true;
					Engine_UiScreenActions.RunRevealed(
						declined.ScreenId, GameInput.MenuAcceptPressed, GameInput.MenuBackPressed);
					return true;
				}
			}

			return false;
		}

		_screenMenuDeclined = null;
		if (state.ScreenId != Engine_UiDetailPages.OptionsScreenId && Engine_UiHostOptionsPage.Open)
			Engine_UiOptionsPending.Leave();

		// The options page level is not a list and must not be drawn as one: the owned view comes
		// down, the guest's page stays mirrored, and input becomes the page's own vocabulary.
		if (ScreenPageOpen(state.ScreenId, drawn) || Engine_UiHostOptionsPage.Open)
		{
			bool hostPage = Engine_UiHostOptionsPage.Open;
			if (_screenMenuView is not null)
			{
				_screenMenuView.Visible = hostPage;
			}

			// The captions are the guest's while the page is open — every LEFT/RIGHT rewrites
			// `.grp.text` and A commits — so the seven pickbox values are dropped from the host
			// set and the slider labels kept. Must be set on this path too, or the committed
			// captions written on the list level stay up.
			// Any screen whose provider fills a page gets the same treatment; only a screen that
			// declared a page element reaches here at all, and only one that needs the sliders
			// pays for reading them.
			// THE PAGE'S PENDING CHOICES START FROM THE COMMITTED ONES the moment it opens.
			if (!hostPage)
				Engine_UiOptionsPending.Enter(
					_screenMenu?.Script.Highlighted?.Name ?? string.Empty,
					HostBridge.GetOptions(), HostBridge.GetOptionSliders());

			if (Engine_UiDetailPages.Owns(state.ScreenId) &&
				_books.Book(state.ScreenId) is { } pageBook)
			{
				HostBridge.OptionSliders pageSliders =
					Engine_UiDetailPages.NeedsOptionSliders(state.ScreenId)
						? HostBridge.GetOptionSliders()
						: default;
				Engine_UiExpandableMenu.Item? pageRow = _screenMenu?.Script.Highlighted;
				pageBook.HostValues = Engine_UiDetailPages.Values(
					state.ScreenId, pageRow,
					default, default, new(), default, default,
					HostBridge.GetOptions(), pageSliders,
					pageBook.Book?.Strings, optionsPageOpen: true);
				// The page's placements, switches and slides follow the same row while the
				// list is dormant — values alone left the picked page's contents where the
				// list level last put them.
				pageBook.HostPositions =
					Engine_UiDetailPages.Positions(state.ScreenId, pageSliders, pageRow);
				pageBook.HostActive = Engine_UiDetailPages.Active(state.ScreenId, pageRow);
				pageBook.HostOpacity = Engine_UiDetailPages.Opacities(state.ScreenId, pageRow);
				pageBook.HostRotations = Engine_UiDetailPages.Rotations(state.ScreenId, pageRow);
				if (state.ScreenId == Engine_UiDetailPages.OptionsScreenId)
					Engine_UiHostOptionsPage.Apply(pageBook, pageRow);
				_providerFilledThisFrame = true;
			}

			HoldScreenStick(0);
			GameInput.ScreenMenuOwnsInput = true;
			if (_books.Book(state.ScreenId) is { } optionsBook)
			{
				_screenLevel.Pointer(optionsBook, delta);
			}

			_screenLevel.Drive(SurfaceInput(delta));
			return true;
		}

		Engine_UiOptionsPending.Leave();

		// The spell-assignment blade is owned: the list comes down and Godot's widget takes the
		// input — up/down move the d-pad slot, left/right move the carousel AND COMMIT
		// (`sub_826B5330` writes as it moves), A copies the slot's will down into every later
		// slot that offers it, B closes the level through the guest's own mode byte. The guest
		// enters it off an A-press on a spell row (Lua UI event 23), so this branch only follows.
		bool blade =
			(Engine_UiTemplates.Screen(state.ScreenId)?.HasSpellBlade ?? false) &&
			AbilitiesBladeOpen();

		// `SetExpandableMenuDisabled` — the flag the blade raises while its carousel is up.
		// `g_Menu:onUpdate` checks it BEFORE reading the stick, so a disabled menu does not
		// scroll. Carried on the model so the rule holds wherever the model is driven from.
		_screenMenu?.Script.SetDisabled(blade);

		if (blade)
		{
			if (_screenMenuView is not null)
			{
				_screenMenuView.Visible = false;
			}

			// The blade page's switches still have to be stated: the early return below
			// starves the book's channels, so they froze at the pre-open state — the
			// spell-selector row hides every page, and the blade stood on bare paper. The
			// provider's blade arm (`AbilitiesAssignmentOpen`) answers Page4 alone, and this
			// is the only frame it can run on.
			if (Engine_UiDetailPages.Owns(state.ScreenId) &&
				_books.Book(state.ScreenId) is { } bladeBook)
			{
				bladeBook.HostActive = Engine_UiDetailPages.Active(
					state.ScreenId, _screenMenu?.Script.Highlighted);
				bladeBook.HostPositions = Engine_UiDetailPages.Positions(
					state.ScreenId, default, _screenMenu?.Script.Highlighted);
			}

			HoldScreenStick(0);
			GameInput.ScreenMenuOwnsInput = true;
			if (_books.Book(state.ScreenId) is { } pointedBlade)
			{
				_spells.FollowBlade(pointedBlade);
				_spells.BladePointer(pointedBlade, delta);
			}

			_spells.DriveBlade(SurfaceInput(delta));
			return true;
		}

		using (Refii.Core.Engine_FramePhases.Measure("ui.list.view"))
		{
			AdoptScreenMenu(address, drawn, state.ScreenId);
		}

		// THE LIST FOLLOWS THE GAME'S DATA. The bridge's generation moves whenever a level,
		// experience, an item, gold or a drawn weapon changes (mod_events.cpp's entry points);
		// the open list re-reads its sources in place then — a purchase, a use or a sale shows at
		// once, as the guest's own handler restates its rows, instead of on the next visit.
		uint dataGeneration = HostBridge.UiDataGeneration();
		if (dataGeneration != _uiDataSeen)
		{
			_uiDataSeen = dataGeneration;
			if (_screenMenu?.Resource() == true)
			{
				_screenMenuView?.Refresh();
			}
		}

		// A ROW SEEN IS VIEWED. The guest's highlight ticks clear an item's or a quest's "!" on
		// the row their cursor stands on (`*(item+20) = 1` in `sub_829B5160`, `Quest+78 = 1` in
		// `sub_829AF638`/`sub_829AFE58`); that cursor no longer moves, so ours states it. The
		// pump writes the flag and moves the data generation, and the re-read above restates
		// the row, its header and (on the next pause build) the tab.
		if (_screenMenu?.Script.Highlighted is { Unviewed: true } seen)
		{
			uint id = ((uint)seen.IdHigh << 16) | ((uint)seen.IdLow & 0xFFFFu);
			if (id != 0u && id != _viewedPosted)
			{
				_viewedPosted = id;
				HostBridge.PostFrontEndAction(seen.EventType is 42 or 43
					? $"{Engine_UiQuestSource.QuestAction}viewed:0x{id:X8}"
					: $"{Engine_UiInventorySource.InventoryAction}viewed:{id}");
			}
		}

		// THE ABILITY SCREEN'S VOICE-OVER. The guest arms its tutorial narration on its own
		// highlight edge, which our cursor no longer moves; the pump runs that arm for the row
		// we state (guest_tick_pump.cpp ArmAbilityTutorial). 0 is "not an ability", which the
		// guest answers by disarming.
		int hovered = -1;
		if (state.ScreenId == Engine_UiScreenInput.AbilitiesScreenId)
		{
			hovered = _screenMenu?.Script.Highlighted is { } row &&
				!Engine_UiModAbilities.TryGet(row.Name, out _) &&
				Refii.Gdb.Engine_AbilityRecords.TryGet(row.Name,
					out Refii.Gdb.Engine_AbilityRecords.Ability ability) &&
				ability.Type is >= 1 and <= 14
					? ability.Type
					: 0;
		}

		if (hovered != _abilityHovered)
		{
			_abilityHovered = hovered;
			if (hovered >= 0)
			{
				HostBridge.PostFrontEndAction($"{Engine_UiAbilitySource.AbilityAction}hover:{hovered}");
			}
		}

		// A screen cannot have both: once its book is standing, the panel's text is painted into
		// the book's clones and running both draws every field twice a few units apart.
		if (_books.Book(state.ScreenId) is { Visible: true })
		{
			_infoPanel.Withdraw();
		}
		else
		{
			_infoPanel.Drive(state.ScreenId, address, drawn);
		}

		// The detail page follows our cursor because the guest's cannot: screen 14 fills its
		// right-hand page on a highlight-change edge keyed off the GUEST's Lua highlight, which
		// stops moving once Godot owns the cursor — see Engine_UiAbilityPage.
		//
		// Set EVERY frame rather than on a move edge: `HostValues` is read in the book's normal
		// text pass, which already skips a field whose display has not changed.
		if (Engine_UiDetailPages.Owns(state.ScreenId) &&
			_books.Book(state.ScreenId) is { } abilityBook)
		{
			// The levels are the page's one live input: hero component 57 at
			// `stats + 0x110 + 4*id`, the guest's own `4 * (Type + 68)` at `0x82896ad0`.
			// Everything else on the page is shipped GDB and Babel.
			// The region request goes FIRST — the pump samples on its own thread and answers a
			// tick later, so naming the region now makes the next frame's answer this cursor's.
			Engine_UiDetailPages.Prepare(state.ScreenId, _screenMenu?.Script.Highlighted);

			// Only the options screen asks: this walks the guest's LIVE screen map from Godot's
			// thread, and on any other screen it walks the whole tree to answer "not present" —
			// a per-frame read of a structure the guest mutates on every screen push.
			HostBridge.OptionSliders sliders =
				Engine_UiDetailPages.NeedsOptionSliders(state.ScreenId)
					? HostBridge.GetOptionSliders()
					: default;
			HostBridge.WorldMapRegion worldMap = HostBridge.GetWorldMapRegion();
			HostBridge.HeroVitals vitals = HostBridge.GetHeroVitals();
			HostBridge.HeroDerived hero = HostBridge.GetHeroDerived();
			abilityBook.HostValues = Engine_UiDetailPages.Values(
				state.ScreenId,
				_screenMenu?.Script.Highlighted,
				vitals,
				hero,
				HostBridge.GetCompletedQuests(),
				HostBridge.GetCrimeScreen(),
				worldMap,
				HostBridge.GetOptions(),
				sliders,
				abilityBook.Book?.Strings);
			abilityBook.HostTextures =
				Engine_UiDetailPages.Textures(state.ScreenId, _screenMenu?.Script.Highlighted);
			abilityBook.HostPositions =
				Engine_UiDetailPages.Positions(state.ScreenId, sliders, _screenMenu?.Script.Highlighted);
			abilityBook.HostActive =
				Engine_UiDetailPages.Active(state.ScreenId, _screenMenu?.Script.Highlighted);
			// The opacity claims ride here too: this path marks the frame filled, so the owned
			// screen driver that also sets them stands down, and without this line no item
			// book ever received one (the clothing footer, the shop's front-page parchment).
			abilityBook.HostOpacity =
				Engine_UiDetailPages.Opacities(state.ScreenId, _screenMenu?.Script.Highlighted);
			abilityBook.HostRotations =
				Engine_UiDetailPages.Rotations(state.ScreenId, _screenMenu?.Script.Highlighted);
			if (state.ScreenId == Engine_UiDetailPages.OptionsScreenId)
				Engine_UiHostOptionsPage.Apply(abilityBook, _screenMenu?.Script.Highlighted);
			DriveMinimap(state.ScreenId, abilityBook, worldMap, _screenMenu?.Script.Highlighted, delta);

			// THIS PATH FILLS THE BOOK TOO. DriveOwnedScreenProvider stands down on this flag,
			// and it was only ever set by the page path above — so for the ability book it ran
			// afterwards with `default` vitals and blanked the experience it had just written.
			_providerFilledThisFrame = true;
			_providerFilledScreen = state.ScreenId;
			abilityBook.ScrollKey = _screenMenu?.Script.Highlighted?.Name ?? string.Empty;
			abilityBook.HostSlides = Engine_UiDetailPages.Slides(
				state.ScreenId, _screenMenu?.Script.Highlighted, worldMap,
				_screenMenu?.HostExpanded ?? false, vitals, hero);

			// THE ABILITY PREVIEW MOVIE, on the quad the guest names. The abilities tick writes
			// the .bik into the tarot sprite's Movie string (`sub_829A5A18`, sprite+564) and the
			// sprite opens it; `GetUiMovie` reads that statement back. The decoded frame is a
			// viewport texture, registered once and painted through the book's own texture
			// channel over the still the provider put there.
			if (Video is { } video)
			{
				// THE HOST NAMES THE ABILITY MOVIE. The guest's own choice is frozen on the row
				// its highlight last reached, which is the first one — see
				// Engine_UiAbilityPage.MovieFor. Asked every frame; an unchanged name is free.
				string wanted = Engine_UiDetailPages.Movie(
					state.ScreenId, _screenMenu?.Script.Highlighted, vitals);

				// THE QUAD IS OURS TO KNOW. The guest's movie sprite names it when one exists,
				// but the sprite is only created by the guest's own highlight-change edge — a
				// boot where that edge never fires has no sprite, and gating playback on it
				// silenced every preview for the whole session. The still and the movie share
				// the tarot frame, so that element is the fallback quad.
				(int Ordinal, string Path)? hosted = HostBridge.GetUiMovie(address);
				int quad = hosted?.Ordinal ??
					abilityBook.OrdinalOf(Engine_UiAbilityPage.TarotFrame) ?? -1;

				// A mod's ability shows its still card: no movie, and the last one taken down.
				bool noMovie = Engine_UiAbilityPage.WantsNoMovie(_screenMenu?.Script.Highlighted);
				if (noMovie)
				{
					wanted = string.Empty;
					hosted = null;
				}

				if (wanted.Length != 0 && quad >= 0)
				{
					// The slot is registered BEFORE the open: `MovieStarted` fires inside
					// `PlayEmbedded`, and the window's conceal handler reads `HasEmbeddedMovie`
					// there — registering after lost the race once and concealed the whole UI
					// viewport for the rest of the session ("a full-screen movie started over
					// it", with no front end ever coming to reveal it again).
					_movieSlots[address] = quad;
					_embeddedMovieAsked = true;
					video.PlayEmbedded(wanted, quad);
				}

				if (quad >= 0 && (wanted.Length != 0 || hosted is not null))
				{
					video.EmbedOrdinal = quad;
					_movieSlots[address] = quad;
					if (video.FrameTexture is { } frame &&
						abilityBook.PathOfOrdinal(quad) is { } hosting)
					{
						var textures = new System.Collections.Generic.Dictionary<string, uint>();
						if (abilityBook.HostTextures is { } stated)
						{
							foreach (var pair in stated)
							{
								textures[pair.Key] = pair.Value;
							}
						}

						textures[hosting] = Engine_UiHostTextures.Register(frame);
						abilityBook.HostTextures = textures;
					}
				}
				else
				{
					if (_movieSlots.Remove(address) && !HasEmbeddedMovie)
					{
						video.EmbedOrdinal = -1;
					}

					// A row with no movie — a category — takes the preview down rather than
					// letting it run out as an orphan with no quad.
					video.StopEmbedded();
				}
			}
		}

		if (_screenMenuView is null || _screenMenu is null)
		{
			GameInput.ScreenMenuOwnsInput = false;
			return false;
		}

		// The cursor, every frame, from the transform the host already reads for this screen.
		// The guest's own row components are hidden, so our refresh is the animation on screen.
		float?[]?[]? liveTransform = Engine_UiLiveAttributes.Transform(address, drawn.ElementCount);

		// The slide and the dim, from the guest's element state — see `_screenMenuFollowOrdinal`.
		// A frame with no stated x keeps the anchor.
		if (_screenMenuFollowOrdinal >= 0 && liveTransform is not null &&
			_screenMenuFollowOrdinal < liveTransform.Length &&
			liveTransform[_screenMenuFollowOrdinal] is { Length: > 0 } menuRow &&
			menuRow[0] is { } liveX && float.IsFinite(liveX))
		{
			Vector3 at = _screenMenuView.Position;
			_screenMenuView.Position = new Vector3(
				_screenMenuAnchor.X + (liveX - _screenMenuFollowAuthoredX) -
					Engine_UiTemplates.WideListShift(state.ScreenId),
				at.Y, at.Z);
		}

		if (_screenMenuButtonsOrdinal >= 0 && drawn.Opacity is { } liveOpacity &&
			_screenMenuButtonsOrdinal < liveOpacity.Length &&
			liveOpacity[_screenMenuButtonsOrdinal] is { } buttonsOpacity)
		{
			_screenMenuView.FocusOpacity = Mathf.Clamp(buttonsOpacity / 100f, 0f, 1f);
		}

		if (_screenMenu.Track(liveTransform))
		{
			_screenMenuView.Refresh();

			// One line per actual move: the adoption line prints the highlight only once, so a
			// scroll the guest refused and one it made but we failed to draw look identical.
			GD.Print($"[game-ui] screen {state.ScreenId} highlight → " +
				$"{_screenMenu.GuestHighlight} of {_screenMenu.Count}");
		}

		_screenMenuView.Visible = true;
		GameInput.ScreenMenuOwnsInput = true;
		GameInput.ScreenMenuHoldsFlip = _screenMenu.HighlightedIsModAbility;
		Engine_UiModMenus.Focus(_screenMenuView, _screenMenu.PopulationScript, Engine_UiCommandList.Screen);

		// Whose cursor the pointer steers: the guest's while its rows are on screen (the stick
		// goes to its latch below), ours inside a host-only level or a mod's menu.
		_screenMenuView.GuestCursor = _screenMenuView.OpenedDepth == 0 && !_screenMenu.HostExpanded;

		// A MOD'S MENU opened over this list is the host's alone: the guest's pool does not carry
		// its rows, so the view is navigated directly and B returns to the screen's own list.
		if (_screenMenuView.OpenedDepth > 0)
		{
			DriveOpenedModMenu(_screenMenuView, _screenMenu.PopulationScript, delta);
			return true;
		}

		// Navigation goes to the GUEST while the rows on screen are the guest's: its cursor is
		// the authority for pool rows and the stick is a LATCH its Lua polls with the shipped
		// repeat (0.25s then 0.14s). Inside a HOST-ONLY level the roles swap — the guest's pool
		// does not carry those rows, so the latch is released and the view is navigated directly.
		if (_screenMenu.HostExpanded)
		{
			HoldScreenStick(0);
			if (_screenMenuView.Navigate(GameInput.MenuDirection, delta))
			{
				Cue(frontEnd: false, GameInput.MenuDirection < 0 ? "slideup" : "slidedown");
			}

			// Change-keyed row report: one line when the list settles, one more per press.
			ReportMenuView(_screenMenuView);
		}
		else
		{
			HoldScreenStick(GameInput.MenuDirection);
		}

		// Y on the abilities screen is UNLEARN: `sub_826C2958` code 39 clears `+198`
		// (0x826c2b2c) and raises `OnPressA` through Lua, whose push is deferred here and never
		// lands, so the identity commit is that push taken directly. `sub_826C2D40` @0x826c2d78
		// then reads `+198` — set runs `sub_8246C970`, the PURCHASE; clear opens the discard
		// confirm.
		//
		// `discard: true` is what makes this a discard rather than a very expensive way to buy
		// the ability again. It does TWO things, and this used to do neither: it skips the row's
		// own `Ability:learn:` command, which calls the purchase directly and beat the 39 to it,
		// and it posts `MenuDiscard` so the pump clears `+198` instead of arming it.
		// Y ON A MOD'S ABILITY is the mod's unlearn, never the guest's flip.
		if (GameInput.MenuFlipPressed && _screenMenu is { HighlightedIsModAbility: true } modRow &&
			modRow.Script.Highlighted is { } modItem &&
			Engine_UiModAbilities.TryGet(modItem.Name, out Engine_UiModAbilities.Ability modAbility))
		{
			Engine_UiModAbilities.AskRefund(modAbility);
			return true;
		}

		if (GameInput.MenuFlipPressed &&
			Engine_UiTemplates.Screen(state.ScreenId) is { FlipAction.Length: > 0 } flipScreen &&
			_screenMenu is { } unlearn && !unlearn.HighlightedIsModAbility)
		{
			Engine_UiScreenActions.Run(flipScreen.FlipAction, Engine_UiCommandList.Revealed);
			if (unlearn.Commit(discard: true))
			{
				_screenMenuView?.Refresh();
				ReportMenuView(_screenMenuView);
			}

			return true;
		}

		// THE RIGHT PAGE'S OTHER HALF. The item books author Page1 and Page2 on the same
		// right-hand leaf, and the guest turns between them on stick left/right through the page
		// widget (`sub_82990E48`, codes 26 and 27). Nothing forwarded that lever, so Page2 was
		// unreachable and had to be forced off to stop the two overlapping.
		//
		// Edge-detected, not held: `GameInput.MenuHorizontal` repeats while the stick is over,
		// and a page that turns every repeat is unusable. Once a turn has been sent the guest's
		// own page state is authoritative — see `Engine_UiPageProviders.ItemBookTurned`.
		if (Engine_UiTemplates.Screen(state.ScreenId)?.Provider is
				Engine_UiPageProviders.ItemBook or Engine_UiPageProviders.Abilities)
		{
			int sideways = GameInput.MenuHorizontal;

			// A click on the page's own arrows turns it.
			if (sideways == 0 && _books.Book(state.ScreenId) is { } turnedBook &&
				PageArrowClicked(turnedBook) is var arrow && arrow != 0)
			{
				sideways = arrow;
				_screenPageHeld = 0;
			}

			if (sideways != 0 && sideways != _screenPageHeld)
			{
				uint code = sideways > 0
					? Engine_UiScreenInput.PageNext
					: Engine_UiScreenInput.PagePrev;
				uint depth = HostBridge.ScreenAction(code);
				Engine_UiPageProviders.BookPage = sideways > 0 ? 1 : 0;
				GD.Print($"[game-ui] screen {state.ScreenId}: page turn forwarded as " +
					$"ScreenAction({code}) — now on Page" +
					$"{Engine_UiPageProviders.BookPage + 1}, queue depth {depth}");
			}

			_screenPageHeld = sideways;
		}

		// The tab cycle through the screen's own handler, which keeps the guest's gates in the
		// path: an open submenu eats it, unavailable tabs are skipped. NOT masked from the guest —
		// `WithoutQueryInput` takes the d-pad, A and B and deliberately leaves LB/RB — so the
		// press reaches the guest's chain as well as ours, and the log says so.
		if (GameInput.MenuTabPrevPressed || GameInput.MenuTabNextPressed)
		{
			bool next = GameInput.MenuTabNextPressed;
			uint code = next ? Engine_UiScreenInput.TabNext : Engine_UiScreenInput.TabPrev;
			uint depth = HostBridge.ScreenAction(code);
			GD.Print($"[game-ui] screen {state.ScreenId}: {(next ? "RB" : "LB")} forwarded as " +
				$"ScreenAction({code}) — queue depth {depth}; the guest also saw the shoulder");
			return true;
		}

		// The right-hand page first: the wheel over it scrolls it, its bar drags it. The list
		// takes only what lands on its own rows or bar, so the two sides never share a notch.
		PointerPage(_books.Book(state.ScreenId));
		SwallowOverPages(_books.Book(state.ScreenId));

		if (GameInput.MenuAcceptPressed || PointerList(_screenMenuView))
		{
			// A mod's row first: an `opens` menu, an Opens:/Mod:lua: command, or a row a mod
			// patched in (which raises its event and then commits like any other).
			if (_screenMenuView is { } modView && Engine_UiModMenus.Activated(modView,
					_screenMenu.PopulationScript, _screenMenu.Script.Highlighted, _screenMenu.Script.Highlighted?.Command,
					Engine_UiCommandList.Screen))
			{
				modView.Refresh();
				ReportMenuView(modView);
				return true;
			}

			// By IDENTITY where the row can be named, by A press where it cannot.
			// `ScreenAction(36)` posts `OnPressA` into the presentation's logic queue and the
			// screen asks Lua which row that meant, so it commits `CurrentHighlightIdx`, not our
			// cursor; naming the row pushes the same `GUI:PushButtonEvent` the shipped
			// `SelectMenuItem` would. Keep the fallback — a list populated before the spawn
			// capture was watching has labels with no identity, and inventing an
			// `(EventType, id)` pair activates a different row.
			//
			// Refresh on the PRESS, not the next sample: a host-owned fold changes the visible
			// list immediately and the sampling path runs only a few times a second.
			bool dyeHeld = Engine_UiDyeFlow.Active;

			// The shared A arm (`sub_826D58F0`) plays the select cue below the root
			// (`SE_GUI_MENU_BOX_SELECT` 0x826D59B0, skipped when `IsHighlightedItemAtRoot`);
			// a commit by identity never runs that arm.
			bool belowRoot = _screenMenu?.Script.Highlighted is { Parent: { Parent: not null } };
			if (_screenMenu is { } pressed && pressed.Commit())
			{
				if (belowRoot)
				{
					Cue(frontEnd: false, "select");
				}

				if (Engine_UiDyeFlow.Active != dyeHeld)
				{
					pressed.FollowDyeFlow();
				}

				_screenMenuView?.Refresh();
				ReportMenuView(_screenMenuView);
			}
			else
			{
				uint depth = HostBridge.ScreenAction(Engine_UiScreenInput.Select);
				GD.Print($"[game-ui] screen {state.ScreenId}: A press forwarded as " +
					$"ScreenAction({Engine_UiScreenInput.Select}) — queue depth {depth}");
			}

			return true;
		}

		if (GameInput.MenuBackPressed)
		{
			// B collapses one level; at the root it goes back to the pause menu. One guest
			// decision, `sub_826D5AF0`, branching on `IsHighlightedItemAtRoot()`:
			//
			//   FALSE — `OnPressB` into the presentation's UIContract, where `CloseMenuItem`
			//           closes the highlighted row's PARENT and puts the cursor on it.
			//   TRUE  — `sub_826A2C88(screenMap[0], …)` = `SetScreen(mgr, 0)` (`826a2cc4 li r4,
			//           0`, `826a2cd0 bl sub_8236E630`, id landing at `8236eb20 stw r28, 4(r9)`)
			//           plus `sub_8298DF98` rebuilding the pause root's rows — screen id 0.
			//
			// The `31` that path also writes goes to `*(*(GUI+56)+152)`, the dynamic presentation
			// SLOT cache `sub_823DC9D8` early-outs on, not the screen id. Leaving the GUI is a
			// different pair (`sub_8236ED98` / `sub_82370178`) no book screen's B reaches.
			//
			// The fold is ours, so `Collapse()` while there is something to close and forward
			// once there is not. `atRoot` carries the predicate's answer because the guest cannot
			// compute its own — `sub_826C1D98` reads `Float1` back ten instructions after posting
			// the chunk that would set it. The pump writes it first; see DispatchScreenAction.
			// ONE FOLD. A level the GUEST laid out (pool rows) closes through the guest — its
			// `sub_826C2560` case 37 folds it and re-lays the pool, which the model follows —
			// or two folds drift apart: ours closed, the guest's open, the highlight solved
			// against the guest's sixteen rows on our eight. Only a host-only level is ours to
			// collapse.
			if (_screenMenu is { } menu && menu.HostExpanded && menu.Collapse())
			{
				// The guest's own collapse below the root cancels (`SE_GUI_MENU_BOX_CANCEL`,
				// 0x826D5BBC); this one is ours.
				Cue(frontEnd: false, "cancel");
				_screenMenuView?.Refresh();
				GD.Print($"[game-ui] screen {state.ScreenId}: B collapsed one level — " +
					$"cursor {menu.Script.CurrentHighlightIdx}/{menu.Script.Visible.Count}");
				return true;
			}

			// B at the top of the dye list puts the dye away and returns to the items; the
			// guest's screen never left them.
			if (Engine_UiDyeFlow.Active)
			{
				Engine_UiDyeFlow.End();
				_screenMenu?.FollowDyeFlow();
				_screenMenuView?.Refresh();
				return true;
			}

			// The queue depth separates a press the guest acted on from one still queued. A
			// CLIMBING depth means the drain is stuck inside an action, not that the pump was
			// never asked: `BeginDrain` swaps the whole queue out and `NoteRan` runs only after a
			// dispatch returns, so an action that never returns leaves the queue at 0 and the
			// next press starting again from 1.
			//
			// `atRoot` is true because reaching here means `Collapse()` found nothing to close,
			// and the fold and the cursor are both ours.
			uint depth = HostBridge.ScreenAction(Engine_UiScreenInput.Back, atRoot: true);
			GD.Print($"[game-ui] screen {state.ScreenId}: B press forwarded as " +
				$"ScreenAction({Engine_UiScreenInput.Back}, atRoot) — queue depth {depth}");
		}

		return true;
	}

	/// <summary>
	/// Holds the guest's menu stick in one direction, sending the edges as they change. Every
	/// press must get its release — the guest's latch does not time out — so call this with 0
	/// from every path that stops owning the input, not only when the player lets go.
	/// </summary>
	private void HoldScreenStick(int direction)
	{
		_screenStickWanted = direction;
		if (direction != _screenStickHeld)
		{
			LatchScreenStick(direction);
		}
	}

	/// <summary>
	/// Releases the latch when no drive asked for a direction on the previous pass. Called once
	/// at the top of the input chain — the drives below each return out of <c>_Process</c>.
	/// </summary>
	private void PumpScreenStick()
	{
		if (_screenStickWanted == 0 && _screenStickHeld != 0)
		{
			LatchScreenStick(0);
		}

		_screenStickWanted = 0;
	}

	/// <summary>Sends the edges that take the guest's latch from where it is to where we want it.</summary>
	private void LatchScreenStick(int direction)
	{
		if (_screenStickHeld != 0)
		{
			HostBridge.MenuScroll(
				_screenStickHeld < 0 ? Engine_UiModel.MenuStick.Up : Engine_UiModel.MenuStick.Down,
				held: false);
		}

		if (direction != 0)
		{
			HostBridge.MenuScroll(
				direction < 0 ? Engine_UiModel.MenuStick.Up : Engine_UiModel.MenuStick.Down,
				held: true);
		}

		_screenStickHeld = direction;
	}

	/// <summary>Which way the guest's menu stick is currently latched: -1 up, +1 down, 0 free.</summary>
	private int _screenStickHeld;

	/// <summary>
	/// Which way the stick was last seen on an item-book screen, so a page turn fires on the
	/// EDGE. Reset with the screen, not with the press.
	/// </summary>
	private int _screenPageHeld;

	/// <summary>Which way a drive asked for it this pass. Cleared by <see cref="PumpScreenStick"/>.</summary>
	private int _screenStickWanted;

	/// <summary>
	/// True while the abilities screen's spell-assignment blade is up — the guest's own mode byte
	/// (`screen14+620`), read through the bridge. Never key on element state: `abilitiesscreen.bgf`
	/// ships its `Adjuster` components authored ACTIVE. The mode byte has exactly two writers
	/// (enter `sub_826C2480`, exit `sub_826C25C0`) and IS the state.
	/// </summary>
	private static bool AbilitiesBladeOpen() => HostBridge.AbilitiesAssignmentOpen();

	/// <summary>
	/// The presentation address the guest's screen manager holds for a screen id, or 0 when it
	/// states none — the only reliable join between a screen and its presentation. Never search
	/// the drawn set for the first tree that contains what you want: the previous screen's
	/// presentation is still drawn when the new one arrives.
	/// </summary>
	private uint PresentationFor(uint screenId)
	{
		if (HostBridge.GetInGameScreens()?.Owned is not { } owned)
		{
			return 0;
		}

		foreach ((uint address, int id) in owned)
		{
			if (id == (int)screenId)
			{
				return address;
			}
		}

		return 0;
	}

	/// <summary>
	/// The drawn presentation carrying an <c>ExpandableMenu</c> pool with rows in it. Found, not
	/// named — most of these screens live inside `pausemainmenu`, but the gift screen and the
	/// shop are their own. A pool with no filled button has not populated yet and is skipped.
	/// </summary>
	private (uint Address, Engine_UiDrawnPresentation Drawn)? FindListPresentation(uint screenId)
	{
		// Which presentation belongs to this screen is the guest's own statement: its screen
		// manager holds one per id. Fails OPEN — when the guest states nothing for this id the
		// first-match walk stands rather than leaving a screen with no list.
		uint stated = PresentationFor(screenId);

		foreach ((uint address, Engine_UiDrawnPresentation drawn) in _drawn)
		{
			if (drawn.Screen is null || drawn.Visible is null ||
				Engine_ExpandableMenuItems.FindGroup(drawn.Screen) is null)
			{
				continue;
			}

			if (stated != 0 && address != stated)
			{
				continue;
			}

			// The pause presentation owns screen 0's list and nothing else's: `pausemainmenu`
			// keeps its root entries in its pool even while a tab screen is up in another
			// presentation, and it is the first entry this walk reaches.
			if (screenId != 0 && string.Equals(drawn.Name, PausePresentation,
					System.StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (_screenLists.TryGetValue(address, out Engine_UiScreenMenu? list) && list.Count > 0)
			{
				return (address, drawn);
			}
		}

		return null;
	}

	/// <summary>
	/// Stands the owned view over one presentation's list and hides the guest's own rows.
	/// Rebuilt when the presentation changes — the view holds nodes cloned from that build.
	/// </summary>
	/// <summary>
	/// The in-game screen that is up, read the way the preview gate reads it — the pause
	/// state answers a sample before the manager's `Current` does. 0 when nothing is up.
	/// </summary>
	private uint UpScreenId()
	{
		if (HostBridge.GetPauseState() is { ScreenId: not 0 and not 31 } pause)
		{
			return pause.ScreenId;
		}

		return _inGameScreen is { } up && (uint)up != 0u && (uint)up != 31u ? (uint)up : 0u;
	}

	private void AdoptScreenMenu(uint address, Engine_UiDrawnPresentation drawn, uint screenId)
	{
		if (_screenLists.TryGetValue(address, out Engine_UiScreenMenu? list))
		{
			_screenMenu = list;
		}

		if (_screenMenu is null)
		{
			return;
		}

		if (_screenMenuAddress == address && _screenMenuView is not null &&
			_screenMenuScreen == screenId)
		{
			// The pool's own plane — the cover overlaps the tabs by authored z; see the pause
			// view's placement note.
			_screenMenuView.Position = new Vector3(
				_screenMenuAnchor.X - Engine_UiTemplates.WideListShift(screenId), _screenMenuAnchor.Y,
				drawn.Node.Position.Z + ListLift(screenId));
			return;
		}

		// The guest's own rows and highlight, out of THIS build and in EVERY instance — the
		// screens ship their furniture twice, 4:3 and 16:9, and a single lookup leaves one
		// layout's copy painting under ours. ONLY REAL POOLS ARE HIDDEN: `abilitiesscreen`
		// alone carries three unrelated groups also named `Buttons` (the blade's, the prompt
		// bars'), and hiding by bare name took other widgets' art down with the rows.
		// ONLY A MIRRORED TREE HAS GUEST ROWS TO HIDE. This exists to switch off the guest's
		// own button pool and highlight where they would paint under ours; with nothing
		// rebuilt there is nothing there, and cataloguing an empty stand-in finds no art.
		var liveArt = new Engine_UiTheme();
		if (drawn.Mirrored)
		{
			liveArt.Catalogue(drawn.Node, drawn.Name);
		}
		foreach (Node3D instance in liveArt.ArtSourceNodes(Engine_FrontEndMenuView.RowGroupName))
		{
			if (Engine_UiButtonPool.IsButtonPool(instance))
			{
				instance.Visible = false;
			}
		}

		foreach (Node3D instance in liveArt.ArtSourceNodes("MenuHighlight"))
		{
			instance.Visible = false;
		}

		Engine_UiTheme? book = _presentation!.PatternBookFor(drawn.Name);
		Node3D? pattern = book is null ? null : Engine_UiButtonPool.FindButtonPool(book);

		// THE ANCHOR IS THE SCREEN'S OWN. The slot table is authored in the Buttons group's
		// space and each screen parks that group where it wants; a constant put every row 25
		// units low on the pause screen and there is no reason to expect these to agree either.
		if (pattern is not null)
		{
			Vector3 origin = Engine_UiTheme.CanvasTransform(pattern).Origin;
			_screenMenuAnchor = origin;
		}

		// THE ROWS RIDE THE GUEST'S SLIDE. `Foreground.ExpandableMenu` is authored at x −400
		// and every book's root `Intro` runs it in from −800 (`Outro` back out) while the
		// guest still plays the presentation we draw from; `ExpandableMenu.LoseFocus` dims
		// `Buttons` to 10. The two ordinals are resolved once here and read per frame below.
		_screenMenuFollowOrdinal = -1;
		_screenMenuButtonsOrdinal = -1;
		if (drawn.Screen is { } screenDoc)
		{
			if (Engine_UiStatedPanel.FindByName(screenDoc.Roots, "ExpandableMenu") is { } menuNode)
			{
				_screenMenuFollowOrdinal = menuNode.Ordinal;
				_screenMenuFollowAuthoredX = menuNode.PositionX;
				if (Engine_UiStatedPanel.FindByName(new[] { menuNode }, "Buttons") is { } buttons)
				{
					_screenMenuButtonsOrdinal = buttons.Ordinal;
				}
			}
		}

		Transform3D? cursorPose = ScreenMenuCursorPose(book);
		_screenMenuPresentation = drawn.Name;

		_screenMenuView?.QueueFree();
		_screenMenuView = new Engine_FrontEndMenuView
		{
			Name = "ScreenMenu",
			Theme = book,
			RowTemplate = pattern is null
				? null
				: Engine_FrontEndMenuView.RowTemplateIn(pattern),
			CursorTemplate = book?.Art("MenuHighlight"),
			CursorPose = cursorPose,

			// WHICH SCREEN, for the end-slot fade only. The two functions that retarget slots 2
			// and 11 are run per screen from C++ and two screens ask for only one end — see
			// Engine_FrontEndMenuView.FadesTopEnd.
			ScreenId = screenId,

			// The star and pip states, read out of THIS screen's document — every screen
			// authors its own button pool, so the slides differ per file.
			Ratings = Engine_UiRowRating.Resolve(drawn.Screen),
			Visible = false,
		};

		// Parented to the HOST's menu layer, not the built tree: that tree is freed whenever the
		// guest changes the screen's state, and a view inside it would go with it.
		_presentation.MenuLayer.AddChild(_screenMenuView);
		_screenMenuView.Bind(_screenMenu.Menu);
		_screenMenuView.Position = new Vector3(
			_screenMenuAnchor.X - Engine_UiTemplates.WideListShift(screenId), _screenMenuAnchor.Y,
			// THE ANCHOR'S Z IS READ AND REPORTED BUT NOT YET APPLIED HERE. The pause view is
			// having the same correction proved on it first; these screens draw correctly today
			// and moving both planes in one change would leave no way to tell which one the
			// next run is showing. See `_pauseAnchor`.
			drawn.Node.Position.Z + ListLift(screenId));
		_screenMenuView.ClipRect = Engine_UiButtonPool.ListClip(drawn.Screen);

		_screenMenuAddress = address;
		_screenMenuScreen = screenId;

		GD.Print($"[game-ui] Godot owns screen {screenId}'s list in '{drawn.Name}' — " +
			$"{_screenMenu.Describe()}, row art " +
			$"{(_screenMenuView.RowTemplate is null ? "NOT sourced (text-only rows)" : "cloned from the pattern book")}, " +
			$"anchor ({_screenMenuAnchor.X}, {_screenMenuAnchor.Y}, z {_screenMenuAnchor.Z}), clip " +
			$"{(_screenMenuView.ClipRect is { } band ? $"y {band.Position.Y:0}..{band.End.Y:0}" : "NONE (pool unmasked)")}");
	}

	/// <summary>
	/// Every owned book — see <see cref="Engine_UiBookSet"/>. The host is set once the
	/// presentation host exists.
	/// </summary>
	private readonly Engine_UiBookSet _books = new();

	/// <summary>The multiplayer book — friends, requests, invitations and games.</summary>
	private readonly Engine_UiListBookScreen _multiplayer =
		new(Engine_UiMultiplayerPage.ScreenId, Engine_UiMultiplayerPage.MenuName, "MultiplayerMenu");

	/// <summary>The Active Mods book — the enabled mods, each one's page its description.</summary>
	private readonly Engine_UiListBookScreen _modsBook =
		new(Engine_UiMultiplayerPage.ModsScreenId, Engine_UiMultiplayerPage.ModsMenuName, "ModsMenu");

	/// <summary>The list book standing, if any; at most one is open at a time.</summary>
	private Engine_UiListBookScreen? OpenListBook =>
		_multiplayer.IsOpen ? _multiplayer : _modsBook.IsOpen ? _modsBook : null;

	/// <summary>
	/// The host page book — a host question asked on a page (<see cref="Engine_UiHostBoxPage"/>):
	/// the question's title and body in the scroll region, Continue and Back as the rows. Driven
	/// with the front end's host box (GameUiHostSurfaces), never through <see cref="OpenListBook"/>.
	/// </summary>
	private readonly Engine_UiListBookScreen _hostPageBook =
		new(Engine_UiHostBoxPage.ScreenId, Engine_UiHostBoxPage.MenuName, "HostBoxMenu");

	/// <summary>The rebuilt, Godot-authored HUD. There is no mirrored one.</summary>
	private Engine_HudView? _hud;

	/// <summary>
	/// Player two's HUD on split screen: the same view, sharing <see cref="_hud"/>'s values and
	/// element feed, holding player two's pieces only.
	/// </summary>
	private Engine_HudView? _hudTwo;

	/// <summary>The presentation the rebuilt HUD replaces. Never mirrored.</summary>
	private const string HudPresentation = "hud";

	/// <summary>
	/// Stands the rebuilt HUD up. Unconditional — there is no mirrored HUD to fall back to.
	///
	/// THE LAYOUT IS DERIVED, NOT TYPED. `hud.bgf` binds 52 meters and 18 text fields and says
	/// per element which value it answers to and where the artist put it; the only thing this
	/// adds is the ANCHOR. Hand-listing seventy pieces would be a host-side copy that drifts
	/// against the banks, and one wrong name loses a piece silently.
	///
	/// Mounted under the PRESENTATION so it shares the canvas camera — which is what makes
	/// `Engine_HudLayout.CanvasExtent` the right frame of reference for its anchors.
	/// </summary>
	private void BuildRebuiltHud()
	{
		if (_presentation is null)
		{
			return;
		}

		Engine_UiScreen? screen = _presentation.DocumentFor(HudPresentation);
		if (screen is null)
		{
			GD.PushWarning($"[hud] '{HudPresentation}' is not in the banks; no HUD.");
			return;
		}

		_hudScreen = screen;
		Engine_UiBindings bindings = Engine_UiBindings.Read(screen.Document);
		_hud = new Engine_HudView
		{
			Name = "Hud",
			Theme = _presentation.Theme,
			Screen = screen,
			Art = new Engine_HudArt { Shipped = _presentation.PatternBookFor(HudPresentation) },
			ResolveTexture = hash =>
				_presentation.TextureNames is { } names &&
				names.TryResolve(hash, out string path)
					? _presentation.LoadTexture(path)
					: null,
			Layout = Engine_HudLayout.FromBindings(
				screen, bindings,
				HostAppliesWidescreen ? Engine_UiWidescreen.Read(screen.Document) : null),
		};

		// Every later build re-derives from the files as they stand (G.23); this first one has
		// just been derived below.
		_hudLayoutFresh = true;
		_hud.Derive = DeriveHudLayout;
		_hud.Rebuilt += OnHudRebuilt;

		// THE STRING BOOK, so the Lua-driven half of the text channel can be read at all: that
		// route states a Babel TAG ID rather than a string, and without the book those names
		// simply never resolve. Same database every screen already uses.
		_hud.Values.Strings = _presentation.Strings;
		GameWhereabouts.Strings = _presentation.Strings;

		_presentation.AddChild(_hud);

		// Mod overrides, one file per piece id — see Engine_HudArt.LoadOverrides. Under
		// `user://` because it is the PLAYER's folder, outside the export and outside the
		// repository; nothing of Fable 2's is written there by us.
		var ids = new System.Collections.Generic.List<string>();
		foreach (Engine_HudLayout.Piece piece in _hud.Layout.Pieces)
		{
			ids.Add(piece.Id);
		}

		// THE TRANSLATED UI, WRITTEN OUT AS EDITABLE SCENES. One `.tscn` per chrome unit under
		// `user://ui/components/<screen>/`, beside the template snapshot — the pair is the
		// authoring set: instance a component in the editor, place it, name it in a template,
		// and it stands through the same code every shipped screen uses.
		//
		// Done at HUD build time because that is where the pattern books are already open and it
		// is once per session. Under `user://` because these are converted Fable 2 art and are
		// not ours to ship.
		Engine_UiComponentLibrary.Result exported =
			Engine_UiComponentLibrary.ExportAll(name => _presentation.PatternBookFor(name));
		GD.Print($"[ui-components] {exported} -> {Engine_UiComponentLibrary.Root}");

		// The mods' arrangement, then the player's, over the derived one — shipped < mods < the
		// player's file. Both only move pieces, so the banks stay the source of what a piece IS.
		DirAccess.MakeDirRecursiveAbsolute("user://hud");
		int modArranged = _hud.Layout.ApplyModLayout();

		// The derived arrangement, written out for the player's layout tool and for modders: the
		// piece ids are the contract for BOTH override files. It is the arrangement BENEATH the
		// player's file — shipped, then the mods' layout in load order — so the tool's "back to
		// the place beneath" returns a piece there, not to where the player's own file put it.
		_hud.Layout.Save(HudLayoutSnapshotPath, Engine_HudLayout.SnapshotLayer);
		int arranged = _hud.Layout.ApplyOverrides(HudLayoutPath);

		int replaced = _hud.Art.LoadOverrides("user://hud", ids);
		_hud.Build();
		GD.Print($"[hud] {bindings.Describe()}; {replaced} piece(s) replaced from user://hud" +
			(modArranged == 0 ? string.Empty : $"; {modArranged} piece(s) moved by mods") +
			(arranged == 0 ? string.Empty : $"; {arranged} piece(s) moved by {HudLayoutPath}"));

		// PLAYER TWO'S HALF, for split screen. The same view over the same layout, art and
		// published values — `hud.bgf` is one presentation carrying both players' pieces — built
		// with player one's pieces keyed to player two's (Engine_HudView.ForPlayerTwo) and laid
		// out in player two's half. Down until the screen splits; see SeatPlayerUi.
		_hudTwo = new Engine_HudView
		{
			Name = "HudPlayerTwo",
			Theme = _presentation.Theme,
			Screen = screen,
			Art = _hud.Art,
			ResolveTexture = _hud.ResolveTexture,
			Layout = _hud.Layout,
			// Player one's feed, with player two's own numbers stated over it where the guest's
			// one HUD states only the local player's (DrivePlayerTwoHud).
			Values = new Engine_HudValues { Under = _hud.Values },
			ReadsValues = false,
			Seat = Engine_UiSeats.Seat.PlayerTwo,
			Suppressed = true,
			Visible = false,
		};
		_presentation.AddChild(_hudTwo);
		_hudTwo.Build();
		ApplyHudWidgetCopies();

		// THE WORLD ICONS, beside the layout-driven pieces. Their positions are the guest's —
		// a projection of world space, restated every frame — so they are a reader, not a
		// layout entry. A step in front of the HUD pieces so a marker is never under a bar.
		_worldIcons = new Engine_HudWorldIcons
		{
			Name = "WorldIcons",
			Theme = _presentation.Theme,
			Art = _hud.Art.Shipped,
			Values = _hud.Values,
			Position = new Vector3(0f, 0f, 50f),
			ResolveTexture = hash =>
				_presentation.TextureNames is { } names &&
				names.TryResolve(hash, out string path)
					? _presentation.LoadTexture(path)
					: null,
		};
		_worldIcons.Bind(screen);
		MountHudWidget(Engine_HudWidgets.WorldIcons, false, _worldIcons);

		// PLAYER TWO'S WORLD ICONS on split screen: the same pooled prompts, seated in their half,
		// each drawn when player two sees its thing (both players' in-range tests run,
		// coop_hero_hooks.cpp), with player two's own hold meter.
		_worldIconsTwo = new Engine_HudWorldIcons
		{
			Name = "WorldIconsTwo",
			Theme = _presentation.Theme,
			Art = _hud.Art.Shipped,
			Values = _hudTwo?.Values ?? _hud.Values,
			Position = new Vector3(0f, 0f, 50f),
			ResolveTexture = _worldIcons.ResolveTexture,
			Visible = false,
		};
		_worldIconsTwo.Bind(screen);
		MountHudWidget(Engine_HudWidgets.WorldIcons, true, _worldIconsTwo);

		// THE WORLD-SPACE ICON CLASS the guest drew through its renderer — property signs,
		// income, opinions, quest markers. Same reader shape, projected by Godot's own camera.
		_worldSprites = new Engine_HudWorldSprites
		{
			Name = "WorldSprites",
			Theme = _presentation.Theme,
			Position = new Vector3(0f, 0f, 52f),
			ResolveTexture = hash =>
				_presentation.TextureNames is { } names &&
				names.TryResolve(hash, out string path)
					? _presentation.LoadTexture(path)
					: null,
		};
		MountHudWidget(Engine_HudWidgets.WorldSprites, false, _worldSprites);

		// The same sprites through player two's camera, in player two's half, on split screen.
		_worldSpritesTwo = new Engine_HudWorldSprites
		{
			Name = "WorldSpritesPlayerTwo",
			Theme = _presentation.Theme,
			Position = new Vector3(0f, 0f, 52f),
			ResolveTexture = _worldSprites.ResolveTexture,
			SightView = 1,
			Visible = false,
		};
		MountHudWidget(Engine_HudWidgets.WorldSprites, true, _worldSpritesTwo);

		// THE AIM RETICULE, which the guest pushed as 2D sprites from a record, never as an
		// element — see Engine_HudReticule. In front of the world sprites.
		_reticule = new Engine_HudReticule
		{
			Name = "Reticule",
			Position = new Vector3(0f, 0f, 54f),
			ResolveTexture = hash =>
				_presentation.TextureNames is { } names &&
				names.TryResolve(hash, out string path)
					? _presentation.LoadTexture(path)
					: null,
		};
		MountHudWidget(Engine_HudWidgets.Reticule, false, _reticule);

		// Player two's petals in player two's half, on split screen.
		_reticuleTwo = new Engine_HudReticule
		{
			Name = "ReticulePlayerTwo",
			Position = new Vector3(0f, 0f, 54f),
			ResolveTexture = _reticule.ResolveTexture,
			OnlyPlayer = 1,
			Visible = false,
		};
		MountHudWidget(Engine_HudWidgets.Reticule, true, _reticuleTwo);

		// TIME CONTROL'S DIAL, which the guest keeps as a sprite record on the hero's component
		// 118 and never as an element — see Engine_HudSlowTimeDial. Behind the reticule.
		_slowTimeDial = new Engine_HudSlowTimeDial
		{
			Name = "SlowTimeDial",
			Position = new Vector3(0f, 0f, 50f),
			ResolveTexture = hash =>
				_presentation.TextureNames is { } names &&
				names.TryResolve(hash, out string path)
					? _presentation.LoadTexture(path)
					: null,
		};
		MountHudWidget(Engine_HudWidgets.SlowTimeDial, false, _slowTimeDial);

		// Player two's, in their half on split screen: their own component 118's record.
		_slowTimeDialTwo = new Engine_HudSlowTimeDial
		{
			Name = "SlowTimeDialTwo",
			Position = new Vector3(0f, 0f, 50f),
			ResolveTexture = _slowTimeDial.ResolveTexture,
			Visible = false,
		};
		MountHudWidget(Engine_HudWidgets.SlowTimeDial, true, _slowTimeDialTwo);

		BuildAchievementToast();
	}

	/// <summary>
	/// SPLIT SCREEN, PER PLAYER. While the world is drawn through two views
	/// (<see cref="Engine_Views.Split"/>), each local player's UI stands in their own half: player
	/// one's HUD in their half (Engine_UiSeats: left or top), laid out as on a screen that size, and
	/// player two's view of the same HUD in theirs (right or bottom); the spells wheel in the half of whoever opened it; the menu
	/// input and masks on the pad of whoever owns the open menu. A shared screen and solo play take
	/// none of this — every seat is the whole window and the menu owner is not stated. The menus
	/// stand in the half of whoever opened them (<see cref="Engine_UiPresentationHost.MenuLayer"/>).
	/// </summary>
	private void SeatPlayerUi(Vector2 window)
	{
		bool split = Engine_Views.Split;
		if (_hud is not null && GodotObject.IsInstanceValid(_hud))
		{
			_hud.Seat = split ? Engine_UiSeats.Seat.PlayerOne : Engine_UiSeats.Seat.Whole;
			_hud.HidesPartner = CoopSession.Side != CoopSession.Role.Solo && !CoopSession.LocalCoop;
			_hud.LocalPlayerTwo = CoopSession.IsClient && !CoopSession.LocalCoop;
		}

		if (_hudTwo is not null && GodotObject.IsInstanceValid(_hudTwo))
		{
			_hudTwo.Window = window;
			_hudTwo.Suppressed = !split || _fullCutscene || _hudAddress == 0;
		}

		_spells.Window = window;

		GameInput.PlayerTwoPad = CoopSession.LocalCoop ? CoopSession.LocalPadTwo : -1;
		// The owner's pad drives the host menu in all local co-op: on a shared screen player two's
		// pause is player two's too.
		GameInput.MenuOwner = CoopSession.LocalCoop ? StatedMenuOwner() : 0;

		// THE MENUS IN THE OPENER'S HALF. The book is laid out for the whole window; on split
		// screen it is drawn whole inside the half of the player who opened it, scaled to fit,
		// so the other player keeps their view of the world.
		if (_presentation is not null && GodotObject.IsInstanceValid(_presentation))
		{
			int owner = split ? GameInput.MenuOwner : 0;
			if (owner != 0)
			{
				Engine_UiSeats.Frame half = Engine_UiSeats.Of(window, Engine_UiSeats.For(owner, true));
				Vector2 full = Engine_HudLayout.CanvasExtent(window);
				Vector2 room = half.Extent * half.Scale;
				float fit = full.X > 0f && full.Y > 0f ? Mathf.Min(room.X / full.X, room.Y / full.Y) : 1f;
				_presentation.SeatMenus(fit, half.Centre);
			}
			else
			{
				_presentation.SeatMenus(1f, Vector2.Zero);
			}
		}
		if (GameInput.MenuOwner != _menuOwnerSaid)
		{
			_menuOwnerSaid = GameInput.MenuOwner;
			GD.Print(_menuOwnerSaid == 0
				? "[split-ui] no menu owner stated — host menus read pad 0, masks hold every pad"
				: $"[split-ui] the open menu is player {_menuOwnerSaid}'s — host menus read " +
					$"pad {(_menuOwnerSaid == 2 ? GameInput.PlayerTwoPad : 0)}, only that pad is masked");
		}
	}

	private int _menuOwnerSaid;

	/// <summary>
	/// The expression wheel's owner as the guest states it: `sub_826A9A60` stores the opener's
	/// player id at wheel+24, the wheel being `*(*(GUI+32)+60)` with GUI at 0x83496B08. 1 when
	/// unread.
	/// </summary>
	private static readonly uint[] WheelOwnerChain = { 32u, 60u, 24u };

	private int _wheelSectorTwo = -1;

	/// <summary>
	/// The wheel sector a stick points at, as `sub_826AA060` computes it: dead zone |v|² < 0.1,
	/// then the nearest of eight unit vectors at 3π/4 − iπ/4 (0 = NE, clockwise). −1 at rest.
	/// </summary>
	private static int WheelSector(Vector2 stick)
	{
		if (stick.LengthSquared() < 0.1f)
		{
			return -1;
		}

		int best = -1;
		float bestDistance = float.MaxValue;
		for (int i = 0; i < 8; ++i)
		{
			float angle = 2.3561945f - (i * 0.7853982f);
			float dx = stick.X - Mathf.Sin(angle);
			float dy = -stick.Y - Mathf.Cos(angle);
			float distance = (dx * dx) + (dy * dy);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = i;
			}
		}

		return best;
	}

	private static int ExpressionWheelOwner()
	{
		System.Span<uint> word = stackalloc uint[1];
		uint at = HostBridge.GuestAddress(0x83496B08u);
		foreach (uint offset in WheelOwnerChain)
		{
			if (at == 0u || HostBridge.Peek(at, word) != 1 || word[0] < 0x10000u)
			{
				return 1;
			}

			at = word[0] + offset;
		}

		return HostBridge.Peek(at, word) == 1 && word[0] < 0x10000u ? (int)word[0] == 2 ? 2 : 1 : 1;
	}

	/// <summary>
	/// Whether the pause standing is player two's: the online client's own, or local player two's
	/// — the guest opens player two's reduced pause root for whoever is player two (the henchman
	/// receiver's pause, `sub_826A2C88(…, 0)`) and states the owner (<see cref="HostBridge.GetMenuOwner"/>).
	/// </summary>
	/// <summary>
	/// The co-op travel rule's notice: a region exit waits until both players are at it, and the
	/// one who walked in is told once per approach (coop_hero_hooks.cpp; Fable 3's
	/// TEXT_TUTORIAL_EXIT_LEVEL_BLOCKED_BY_PLAYER_DISTANCE, which Fable 2's text banks do not
	/// carry), for five seconds as Fable 3 shows it.
	/// </summary>
	private void TellExitRefused()
	{
		int refused = HostBridge.TakeExitRefused();
		if (refused == 0)
		{
			return;
		}

		string text = !CoopSession.LocalCoop
			? "Your partner must be with you to travel."
			: (refused & 1) != 0
				? "Player two must be with you to travel."
				: "Player one must be with you to travel.";
		_boxes.InfoBoxEx(ExitRefusedBox, 5f, text);
	}

	private const string ExitRefusedBox = "RefiiCoopExitRefused";

	/// <summary>Refii's own notice boxes: no guest manager holds them, so the HUD box shows them.</summary>
	private static readonly System.Collections.Generic.HashSet<string> RefiiNoticeBoxes =
		new() { Engine_UiCoopNotices.BoxName, ExitRefusedBox, Engine_UiNotices.ModBoxName };

	private static bool PauseIsPlayerTwos() =>
		CoopSession.IsClient || (CoopSession.LocalCoop && HostBridge.GetMenuOwner() == 2);

	/// <summary>
	/// Which player owns the open menu, as the guest states it: 0 when no menu is open. The spells
	/// wheel's open flags are per player (`QM+36` one, `QM+37` two). The pause and every screen
	/// opened from it carry their owner in the guest (`sub_8236E400`, "player two owns the menu",
	/// sampled each guest tick: <see cref="HostBridge.GetMenuOwner"/>), read while the guest says a
	/// pause screen is up — the root, or a screen opened from it.
	/// </summary>
	private int StatedMenuOwner()
	{
		// A consent question is the partner's to answer, on their pad and in their half.
		if (_consentOwner != 0)
		{
			return _consentOwner;
		}

		// A mod's menu box is its player's to answer, on their pad.
		if (Engine_UiModMenuBox.Active is { } modBox)
		{
			return modBox.Player;
		}

		// A box built for one player (the core co-op Lua's @Box.Seat): the showing box's player.
		int boxed = _boxes.SeatOf(_boxes.GuestMessageBox);
		if (boxed == 0 && HostBridge.GetDisplayBox() is { } display)
		{
			boxed = _boxes.SeatOf(display.Box);
		}

		if (boxed != 0)
		{
			return boxed;
		}

		int wheel = _spells.WheelPlayer;
		if (wheel != 0)
		{
			return wheel;
		}

		return HostBridge.GetPauseState() is { } pause && (pause.RootActive || pause.ModalMode == 1)
			? HostBridge.GetMenuOwner()
			: 0;
	}

	/// <summary>
	/// Hands player two's HUD the element feed player one's was just given: one `hud.bgf`
	/// presentation, one read, two views — resolved again with player two's own slides folded in
	/// whenever they state any (<see cref="ResolvePlayerTwo"/>).
	/// </summary>
	private void ShareHudFeed()
	{
		if (_hud is null || _hudTwo is null || !GodotObject.IsInstanceValid(_hudTwo))
		{
			return;
		}

		if (ResolvePlayerTwo())
		{
			_hudTwo.Resolve = _hudTwoResolve;
			_hudTwo.LiveSlides = _hudTwoSlideBuffer;
			_hudTwo.ElementActive = _hudTwoActive;
		}
		else
		{
			_hudTwo.Resolve = _hud.Resolve;
			_hudTwo.LiveSlides = SlidesForPlayerTwo(_hud.LiveSlides);
			_hudTwo.ElementActive = _hud.ElementActive;
			_hudTwo.ActiveInputs = _hud.ActiveInputs;
			_hudTwo.StatedOpacity = _hud.StatedOpacity;
		}

		_hudTwo.LiveTextures = TexturesForPlayerTwo(_hud.LiveTextures);
		_hudTwo.LiveTransform = _hud.LiveTransform;
		_hudTwo.LiveTexts = _hud.LiveTexts;
	}

	/// <summary>Player two's own element slides over the shared ones (ordinal → slide).</summary>
	private readonly System.Collections.Generic.Dictionary<int, int> _hudTwoSlides = new();

	private int[]? _hudTwoSlideBuffer;

	private System.Collections.Generic.IReadOnlyList<int>? SlidesForPlayerTwo(
		System.Collections.Generic.IReadOnlyList<int>? shared)
	{
		if (shared is null || (_hudTwoSlides.Count == 0 && _hudTwoQuerySlides.Count == 0 && _hudTwoDpadSlides.Count == 0))
		{
			return shared;
		}

		if (_hudTwoSlideBuffer is null || _hudTwoSlideBuffer.Length != shared.Count)
		{
			_hudTwoSlideBuffer = new int[shared.Count];
		}

		for (int i = 0; i < shared.Count; ++i)
		{
			_hudTwoSlideBuffer[i] = shared[i];
		}

		foreach ((int ordinal, int slide) in _hudTwoSlides)
		{
			if (ordinal >= 0 && ordinal < _hudTwoSlideBuffer.Length)
			{
				_hudTwoSlideBuffer[ordinal] = slide;
			}
		}

		foreach ((int ordinal, int slide) in _hudTwoQuerySlides)
		{
			if (ordinal >= 0 && ordinal < _hudTwoSlideBuffer.Length)
			{
				_hudTwoSlideBuffer[ordinal] = slide;
			}
		}

		foreach ((int ordinal, int slide) in _hudTwoDpadSlides)
		{
			if (ordinal >= 0 && ordinal < _hudTwoSlideBuffer.Length)
			{
				_hudTwoSlideBuffer[ordinal] = slide;
			}
		}

		return _hudTwoSlideBuffer;
	}

	/// <summary>Player two's own element textures over the shared ones (ordinal → texture hash).</summary>
	private readonly System.Collections.Generic.Dictionary<int, uint> _hudTwoTextures = new();

	private uint[]? _hudTwoTextureBuffer;

	private uint[]? TexturesForPlayerTwo(uint[]? shared)
	{
		if (shared is null || _hudTwoTextures.Count == 0)
		{
			return shared;
		}

		if (_hudTwoTextureBuffer is null || _hudTwoTextureBuffer.Length != shared.Length)
		{
			_hudTwoTextureBuffer = new uint[shared.Length];
		}

		System.Array.Copy(shared, _hudTwoTextureBuffer, shared.Length);
		foreach ((int ordinal, uint texture) in _hudTwoTextures)
		{
			if (ordinal >= 0 && ordinal < _hudTwoTextureBuffer.Length)
			{
				_hudTwoTextureBuffer[ordinal] = texture;
			}
		}

		return _hudTwoTextureBuffer;
	}

	/// <summary>One guest word, or 0 when the address does not read.</summary>
	private static uint PeekWord(uint address)
	{
		System.Span<uint> word = stackalloc uint[1];
		return address != 0u && HostBridge.Peek(address, word) == 1 ? word[0] : 0u;
	}

	/// <summary>The HUD object `*(GUI+32)` (GUI at 0x83496B08), or 0.</summary>
	private static uint GuestHudObject()
	{
		uint gui = PeekWord(HostBridge.GuestAddress(0x83496B08u));
		return gui >= 0x10000u ? PeekWord(gui + 32u) : 0u;
	}

	private static readonly Engine_HudValues.Handle[] ExperienceTexts =
	{
		Engine_HudValues.Handle.For("GenericExpText"), Engine_HudValues.Handle.For("StrengthExpText"),
		Engine_HudValues.Handle.For("SkillExpText"), Engine_HudValues.Handle.For("WillExpText"),
	};

	private readonly int[] _experienceShownTwo = new int[4];
	private bool _experienceUpTwo;
	private double _experienceLingerTwo;
	private double _experienceStepClock;
	private ulong _experienceClockUsec;

	/// <summary>
	/// PLAYER TWO'S EXPERIENCE COUNTER, in their half. The guest keeps one counter (HUD+0x68,
	/// `sub_82185718`) for the local player, but decides visibility for BOTH heroes: hero one's
	/// at counter+64, hero two's at +65 (`sub_82539F38`: authority and an expression of type 17).
	/// Its numbers are each pool's spendable balance, component 164 +16 float[4], snapped when the
	/// counter appears (`sub_82539318`) and stepped toward the balance once per guest frame
	/// (`sub_82539648`: +1 within ten, else up to nineteen); it lingers five seconds
	/// (counter+8). Player two's half shows the same from player two's +65 and balances.
	/// </summary>
	private void DrivePlayerTwoExperience()
	{
		ulong now = Time.GetTicksUsec();
		double delta = _experienceClockUsec == 0ul ? 0.0 : System.Math.Min((now - _experienceClockUsec) * 1e-6, 0.25);
		_experienceClockUsec = now;

		var overlay = _hudTwo?.Values;
		bool split = Engine_Views.Split && overlay is not null && overlay.Under is not null;
		uint hero = split && CoopSession.LocalCoop && HostBridge.CoopSessionRaw() is { } coop ? coop.PlayerTwo : 0u;
		uint pools = hero != 0u ? HostBridge.EntityComponentAddress(hero, 164u) : 0u;
		uint hud = pools != 0u ? GuestHudObject() : 0u;
		uint counter = hud >= 0x10000u ? PeekWord(hud + 0x68u) : 0u;
		if (counter < 0x10000u || _hud?.Bindings is not { } bindings)
		{
			foreach (Engine_HudValues.Handle handle in ExperienceTexts)
			{
				overlay?.Override(handle, null);
			}

			_hudTwoSlides.Clear();
			_experienceUpTwo = false;
			return;
		}

		// +64 and +65 are the two bytes after the word at +64's high byte, big-endian.
		bool visible = ((PeekWord(counter + 64u) >> 16) & 0xFFu) != 0u;
		System.Span<uint> balance = stackalloc uint[4];
		bool read = HostBridge.Peek(pools + 16u, balance) == 4;

		if (visible && !_experienceUpTwo && read)
		{
			for (int i = 0; i < 4; ++i)
			{
				_experienceShownTwo[i] = (int)System.BitConverter.Int32BitsToSingle(unchecked((int)balance[i]));
			}
		}

		_experienceUpTwo = visible;
		_experienceLingerTwo = visible ? 5.0 : System.Math.Max(0.0, _experienceLingerTwo - delta);
		bool shown = visible || _experienceLingerTwo > 0.0;

		// The guest's own step, once per guest frame (30 a second).
		_experienceStepClock += delta;
		while (read && _experienceStepClock >= 1.0 / 30.0)
		{
			_experienceStepClock -= 1.0 / 30.0;
			for (int i = 0; i < 4; ++i)
			{
				int target = (int)System.BitConverter.Int32BitsToSingle(unchecked((int)balance[i]));
				int diff = target - _experienceShownTwo[i];
				if (diff > 0)
				{
					_experienceShownTwo[i] += diff <= 10 ? 1 : System.Math.Min(diff, 19);
				}
			}
		}

		for (int i = 0; i < 4; ++i)
		{
			overlay!.Override(ExperienceTexts[i], ExperienceText(_experienceShownTwo[i]));
		}

		// SlideFader2 `ExpAmounts`: 2 in, 3 out fast.
		foreach (int ordinal in bindings.Elements(Engine_UiBindings.Kind.SlideFader2, Refii.Core.Engine_GameHash.Fnv1("ExpAmounts")))
		{
			_hudTwoSlides[ordinal] = shown ? 2 : 3;
		}
	}

	/// <summary>An experience pool's value as the counter writes it (`sub_8253A028`).</summary>
	private static string ExperienceText(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>Where the edited arrangement lives — the editor writes it, every boot reads it.</summary>
	public const string HudLayoutPath = Engine_HudLayout.LayoutPath;

	/// <summary>The derived layout as it ran, written every boot for the editor and modders.</summary>
	public const string HudLayoutSnapshotPath = Engine_HudLayout.SnapshotPath;

	private Engine_HudWorldIcons? _worldIcons;

	/// <summary>Player two's copy of the pooled world icons, in their half while split.</summary>
	private Engine_HudWorldIcons? _worldIconsTwo;

	/// <summary>Per half, the pooled slots' canvas positions this frame (index 1: player two's seat).</summary>
	private System.Collections.Generic.Dictionary<int, Vector2>? _pooledCanvasTwo;
	private Engine_HudWorldSprites? _worldSprites;

	/// <summary>Player two's copy of the world sprites, drawn only while the screen is split.</summary>
	private Engine_HudWorldSprites? _worldSpritesTwo;

	private Engine_HudReticule? _reticule;

	/// <summary>Player two's reticule layer, drawn only while the screen is split.</summary>
	private Engine_HudReticule? _reticuleTwo;

	private Engine_HudSlowTimeDial? _slowTimeDial;
	private Engine_HudSlowTimeDial? _slowTimeDialTwo;

	/// <summary>`GUI.PlayFullCutscene`'s game-flow state (`sub_82305FE8` builds it; its type
	/// slot is `li r3,7`).</summary>
	private const uint FullCutsceneStateVtable = 0x820AED30u;

	/// <summary>The guest's letterbox bars: rows 0..100 and H−100..H of its 720-line backbuffer
	/// (`sub_82296368` 0x822964a4), stated here as a fraction of the height.</summary>
	private const float LetterboxFraction = 100f / 720f;

	private CanvasLayer? _letterbox;

	private Engine_UiCreditsRoll? _creditsRoll;

	private readonly System.Collections.Generic.List<Engine_UiCreditsRoll.Line> _creditLines = new();

	/// <summary>
	/// The end credits: every Text element of the live `creditsscreen`, at the string and
	/// position the guest's own roll gives it this frame — see <see cref="Engine_UiCreditsRoll"/>.
	/// </summary>
	private void DriveCreditsRoll()
	{
		_creditLines.Clear();
		uint address = 0u;
		if (HostBridge.IsInGame)
		{
			foreach (Engine_UiModel.Presentation p in Engine_UiSource.Current.GetPresentations())
			{
				if (p.IsShowing && p.Is($"{Engine_UiCreditsRoll.Presentation}.bgf"))
				{
					address = p.Address;
					break;
				}
			}
		}

		if (address != 0u && _presentation?.DocumentFor(Engine_UiCreditsRoll.Presentation) is { } document &&
			Engine_UiElementState.Current.Elements(address) is { } elements &&
			elements.Count == document.Nodes.Length)
		{
			float?[]?[]? transform = Engine_UiLiveAttributes.Transform(address, elements.Count);
			foreach (UiNode node in document.Nodes)
			{
				if (node.Type != BgfElementType.Text || node.Text is not { } authored)
				{
					continue;
				}

				Engine_UiModel.UiElement element = elements[node.Ordinal];
				if (!element.IsGloballyActive || string.IsNullOrEmpty(element.Text))
				{
					continue;
				}

				float?[]? stated = transform is not null && node.Ordinal < transform.Length
					? transform[node.Ordinal]
					: null;
				_creditLines.Add(new Engine_UiCreditsRoll.Line(
					element.Text,
					new Vector2(stated?[0] ?? node.PositionX, stated?[1] ?? node.PositionY),
					authored.Size > 0 ? authored.Size : 18,
					new Color(authored.R, authored.G, authored.B)));
			}
		}

		if (_creditsRoll is null)
		{
			if (_creditLines.Count == 0 || _presentation?.Fonts is not { } fonts)
			{
				return;
			}

			// Above the video (layer 10): the roll plays over `Credits.bik`.
			var layer = new CanvasLayer { Name = "Credits", Layer = 11 };
			_creditsRoll = new Engine_UiCreditsRoll { Name = "CreditsRoll", Fonts = fonts };
			layer.AddChild(_creditsRoll);
			AddChild(layer);
		}

		_creditsRoll.Show(_creditLines);
	}

	private bool _fullCutscene;

	/// <summary>`sub_82296368`: list = *(*(*(*(game+12)+128)+4)+4), node = *(list+4),
	/// state = *(node+8).</summary>
	private static readonly uint[] FlowListChain = { 0u, 12u, 128u, 4u, 4u };

	/// <summary>True while the top game-flow state is the full cutscene.</summary>
	private static bool FullCutscenePlaying()
	{
		uint at = HostBridge.GuestAddress(0x83496AB8u);
		foreach (uint offset in FlowListChain)
		{
			if (!HostBridge.TryPeekWord(at + offset, out uint word) || word < 0x10000u)
			{
				return false;
			}

			at = word;
		}

		if (!HostBridge.TryPeekWord(at + 4u, out uint node) || node == at || node < 0x10000u)
		{
			return false;
		}

		return HostBridge.TryPeekWord(node + 8u, out uint state) &&
			state >= 0x10000u &&
			HostBridge.TryPeekWord(state, out uint vtable) &&
			HostBridge.GuestAddressToTu1(vtable) == FullCutsceneStateVtable;
	}

	/// <summary>
	/// THE FULL CUTSCENE REPLACES THE WHOLE GUI. `sub_82296368` tests the top game-flow state's
	/// type and, for 7, draws two opaque black bars INSTEAD of the GUI — no HUD, no markers, no
	/// reticule. The in-game layers go down with it and come back on the falling edge.
	/// </summary>
	private void DriveFullCutscene()
	{
		bool up = HostBridge.IsInGame && FullCutscenePlaying();
		if (up == _fullCutscene)
		{
			return;
		}

		_fullCutscene = up;
		GD.Print($"[game-ui] full cutscene {(up ? "UP — letterbox, in-game UI hidden" : "down")}");

		if (_letterbox is null)
		{
			_letterbox = new CanvasLayer { Name = "Letterbox", Layer = 4 };
			foreach ((float top, float bottom) in new[] { (0f, LetterboxFraction), (1f - LetterboxFraction, 1f) })
			{
				_letterbox.AddChild(new ColorRect
				{
					Color = Colors.Black,
					AnchorLeft = 0f, AnchorRight = 1f, AnchorTop = top, AnchorBottom = bottom,
					MouseFilter = Control.MouseFilterEnum.Ignore,
				});
			}

			AddChild(_letterbox);
		}

		_letterbox.Visible = up;
		if (_hud is not null && GodotObject.IsInstanceValid(_hud))
		{
			_hud.Suppressed = up;
		}

		foreach (Node3D? layer in new Node3D?[] { _hud, _worldIcons, _worldSprites, _reticule })
		{
			if (layer is not null && GodotObject.IsInstanceValid(layer))
			{
				layer.Visible = !up && (layer != _hud || _hudAddress != 0);
			}
		}
	}

	/// <summary>The aiming object each player's reticule setter last stated (`@Reticule`,
	/// hooks/reticule_hooks.cpp); 0 before any. Its `+0x90` is the aim point.</summary>
	private readonly uint[] _reticuleObject = new uint[2];

	/// <summary>Each player's reticule anchor last read whole inside one mouse account, and that account's sequence.</summary>
	private readonly Vector3?[] _aimAnchor = new Vector3?[2];
	private readonly uint[] _aimAnchorSequence = new uint[2];

	/// <summary>
	/// Feeds the reticule each frame: the record at `*(*(GUI+32)+72)` (+64 for player two) for
	/// active, progress, colour, mode and the intro counter, and the stated object's `+0x90` for
	/// the aim point. Runs after <see cref="DriveWorldSprites"/>, which captured the camera.
	/// </summary>
	private void DriveReticule()
	{
		if (_reticule is null || !GodotObject.IsInstanceValid(_reticule))
		{
			return;
		}

		System.Span<uint> root = stackalloc uint[9];
		uint hud = HostBridge.IsInGame &&
			HostBridge.TryPeekWord(HostBridge.GuestAddress(0x83496B08u), out uint gui) &&
			HostBridge.Peek(gui, root) == 9 &&
			HostBridge.GuestAddressToTu1(root[0]) == 0x82001610u &&
			HostBridge.TryPeekWord(root[8] + 72u, out uint hudAddr)
			? hudAddr
			: 0u;
		if (hud == 0u || _spriteWindow.X <= 0f || _spriteWindow.Y <= 0f)
		{
			_reticule.Feed(0, null);
			_reticule.Feed(1, null);
			_reticuleTwo?.Feed(0, null);
			_reticuleTwo?.Feed(1, null);
			return;
		}

		// The guest's backbuffer, `qword_834A5FE8` — the pixels its petal sizes are stated in.
		System.Span<uint> backbuffer = stackalloc uint[2];
		int bbCount = HostBridge.Peek(HostBridge.GuestAddress(0x834A5FE8u), backbuffer);
		float guestHeight = bbCount == 2 && backbuffer[1] != 0u ? backbuffer[1] : 720f;
		// Each layer in its own seat: player one's (the whole window's) and, while split, player
		// two's. The petals clamp inside their own half, and an aim outside it is clamped rather
		// than dropped — so the projection is unclipped.
		Engine_UiSeats.Frame seat = _spriteViews[0].Frame;
		_reticule.CanvasHalfExtent = seat.Extent * 0.5f;
		_reticule.UnitsPerGuestPixel = seat.UnitsPerGuestPixel(guestHeight);
		_reticule.Place ??= guest => PlaceIn(_spriteViews[0], guest, 0u, clip: false)?.Canvas;
		if (_reticuleTwo is not null && GodotObject.IsInstanceValid(_reticuleTwo))
		{
			Engine_UiSeats.Frame theirs = _spriteViews[1].Frame;
			_reticuleTwo.CanvasHalfExtent = theirs.Extent * 0.5f;
			_reticuleTwo.UnitsPerGuestPixel = theirs.UnitsPerGuestPixel(guestHeight);
			_reticuleTwo.Place ??= guest => PlaceIn(_spriteViews[1], guest, 0u, clip: false)?.Canvas;
		}

		static float F(uint word) => System.BitConverter.Int32BitsToSingle(unchecked((int)word));

		System.Span<uint> record = stackalloc uint[14];
		System.Span<uint> anchor = stackalloc uint[3];
		for (int player = 0; player < 2; ++player)
		{
			int recCount = HostBridge.Peek(hud + (64u * (uint)player), record);
			bool turnsWithView = _reticuleObject[player] != 0u &&
				_reticuleObject[player] == Refii.Game.GameWorld.MouseLookAim;
			uint? sequenceBefore = turnsWithView ? HostBridge.GetMouseLook()?.Sequence : null;
			int ancCount = _reticuleObject[player] != 0u
				? HostBridge.Peek(_reticuleObject[player] + 0x90u, anchor)
				: 0;
			uint? sequenceAfter = turnsWithView ? HostBridge.GetMouseLook()?.Sequence : null;
			if (recCount != 14 || (record[0] >> 24) == 0u || ancCount != 3)
			{
				_reticule.Feed(player, null);
				_reticuleTwo?.Feed(player, null);
				continue;
			}

			// The mouse's aim turns with the view: the anchor is turned by the turn drawn ahead of
			// the guest's camera, as the guest's next update turns the aim (GameWorld.TurnAimAhead).
			// The anchor must be the one stated with the account the camera was drawn from: one read
			// across a camera update already carries a turn still drawn ahead, so the last anchor read
			// inside that account stands in for it.
			var stated = new Vector3(F(anchor[0]), F(anchor[1]), F(anchor[2]));
			if (turnsWithView)
			{
				if (sequenceBefore is { } before && before == sequenceAfter &&
					before == Refii.Game.GameWorld.MouseLookSequence)
				{
					_aimAnchor[player] = stated;
					_aimAnchorSequence[player] = before;
				}

				if (_aimAnchor[player] is { } kept &&
					_aimAnchorSequence[player] == Refii.Game.GameWorld.MouseLookSequence)
				{
					stated = kept;
				}

				stated = Refii.Game.GameWorld.TurnAimAhead(stated);
			}

			var aim = new Engine_HudReticule.Aim(
				stated,
				F(record[3]),
				new Color(F(record[4]), F(record[5]), F(record[6]), F(record[7])),
				record[8],
				unchecked((int)record[13]));
			_reticule.Feed(player, aim);
			_reticuleTwo?.Feed(player, aim);
		}
	}

	/// <summary>
	/// Feeds Time Control's dial each frame from the record at `comp118 + 484` on the hero:
	/// active byte, alpha, and per segment the rectangle, texture hash and angle. The guest ticks
	/// and paces the record itself on the sim path (`sub_821DFDD0` -> `sub_822B5150`), so this
	/// reads its statement and draws nothing of its own.
	/// </summary>
	private void DriveSlowTimeDial()
	{
		// Local co-op: each dial is its own player's (the local hero follows the menu owner,
		// which is no answer for whose dial this is). Online: the local hero's.
		uint one = 0u, two = 0u;
		if (HostBridge.IsInGame)
		{
			HostBridge.CoopSessionWords? coop = CoopSession.LocalCoop ? HostBridge.CoopSessionRaw() : null;
			one = coop is { } words ? words.PlayerOne : HostBridge.HeroEntityAddress();
			two = coop is { } both && Engine_Views.Split ? both.PlayerTwo : 0u;
		}

		DriveSlowTimeDial(_slowTimeDial, one, 0);
		DriveSlowTimeDial(_slowTimeDialTwo, two, 1);
	}

	private void DriveSlowTimeDial(Engine_HudSlowTimeDial? view, uint hero, int seat)
	{
		if (view is null || !GodotObject.IsInstanceValid(view))
		{
			return;
		}

		uint spells = hero != 0u ? HostBridge.EntityComponentAddress(hero, 118u) : 0u;
		if (spells == 0u || _spriteWindow.X <= 0f || _spriteWindow.Y <= 0f)
		{
			view.Feed(null);
			return;
		}

		// Dial +0..+108: words 1 (active byte high), 3 (alpha), then per segment i at word
		// 8 + 10i: prim, top, left, bottom, right, texture, angle.
		System.Span<uint> dial = stackalloc uint[28];
		if (HostBridge.Peek(spells + 484u, dial) != 28 || (dial[1] >> 24) == 0u)
		{
			view.Feed(null);
			return;
		}

		System.Span<uint> backbuffer = stackalloc uint[2];
		int bbCount = HostBridge.Peek(HostBridge.GuestAddress(0x834A5FE8u), backbuffer);
		float guestWidth = bbCount == 2 && backbuffer[0] != 0u ? backbuffer[0] : 1280f;
		float guestHeight = bbCount == 2 && backbuffer[1] != 0u ? backbuffer[1] : 720f;
		view.UnitsPerGuestPixel = _spriteViews[seat].Frame.UnitsPerGuestPixel(guestHeight);

		static float F(uint word) => System.BitConverter.Int32BitsToSingle(unchecked((int)word));

		Engine_HudSlowTimeDial.Segment? Segment(System.ReadOnlySpan<uint> d, int i)
		{
			int at = 8 + (10 * i);
			if (d[at] == 0xFFFFFFFFu)
			{
				return null;
			}

			float top = unchecked((int)d[at + 1]);
			float left = unchecked((int)d[at + 2]);
			float bottom = unchecked((int)d[at + 3]);
			float right = unchecked((int)d[at + 4]);
			return new Engine_HudSlowTimeDial.Segment(
				new Vector2(((left + right) * 0.5f) - (guestWidth * 0.5f), ((top + bottom) * 0.5f) - (guestHeight * 0.5f)),
				(right - left) * 0.5f,
				d[at + 5],
				F(d[at + 6]));
		}

		view.Feed(new Engine_HudSlowTimeDial.Dial(F(dial[3]), Segment(dial, 0), Segment(dial, 1)));
	}

	private bool _worldSpritesCleared;
	private System.Collections.Generic.List<HostBridge.WorldSprite>? _lastRawWorldSprites;

	/// <summary>This frame's projected canvas point per pooled icon slot.</summary>
	private System.Collections.Generic.Dictionary<int, Vector2>? _pooledCanvas;

	/// <summary>
	/// Feeds the world-space icons from the guest's live vector, projected through the world
	/// camera onto the HUD canvas. The anchor is guest space; entities go through the same
	/// handedness flip `Engine_MdlPlacement.FromGuest` applies to their transforms.
	/// </summary>
	private void DriveWorldSprites(bool guestFrame = true)
	{
		if (_worldSprites is null || !GodotObject.IsInstanceValid(_worldSprites))
		{
			return;
		}

		// CLEARED, not merely left alone, when the game is down: the last game's icons
		// ("9 Gold") stayed painted over the title screen after Quit to Title.
		if (!HostBridge.IsInGame)
		{
			if (!_worldSpritesCleared)
			{
				_worldSpritesCleared = true;
				_lastRawWorldSprites = null;
				// The mods' HUD state goes with the game: markers, meters and fades are a session's.
				Engine_HudModMarkers.Clear();
				Engine_HudModMeters.Clear();
				Engine_UiModFade.Clear();
				Refii.Lighting.Engine_ModLights.Clear();
				_worldSprites.Feed(
					new System.Collections.Generic.List<Engine_HudWorldSprites.Sprite>());
				if (_worldSpritesTwoFed && _worldSpritesTwo is not null &&
					GodotObject.IsInstanceValid(_worldSpritesTwo))
				{
					_worldSpritesTwo.Feed(System.Array.Empty<Engine_HudWorldSprites.Sprite>());
					_worldSpritesTwoFed = false;
				}
			}

			return;
		}

		_worldSpritesCleared = false;

		if (GetWindow().GetCamera3D() is not { } camera)
		{
			return;
		}

		Vector2 window = GetWindow().GetVisibleRect().Size;
		if (window.X <= 0f || window.Y <= 0f)
		{
			return;
		}

		// The projection's inputs live in fields and the projector is one cached delegate, so
		// the per-frame feed allocates no closure (GC pauses stall the guest's sim thread).
		bool split = SeatSpriteViews(camera, window);
		_spriteProject ??= ProjectWorldSprite;

		// Reused, not reallocated — and cleared here rather than on entry, because the early
		// returns above deliberately leave the last frame's pooled placements standing. Neither
		// `Feed` nor `TrackProjected` keeps a reference past its call, and `_pooledCanvas` is
		// read later in this same frame by the icon drive.
		System.Collections.Generic.List<Engine_HudWorldSprites.Sprite> sprites =
			_worldSpriteList ??= new System.Collections.Generic.List<Engine_HudWorldSprites.Sprite>();
		System.Collections.Generic.Dictionary<int, Vector2> pooled =
			_pooledCanvas ??= new System.Collections.Generic.Dictionary<int, Vector2>();
		pooled.Clear();
		System.Collections.Generic.List<HostBridge.WorldSprite> rawSprites =
			guestFrame || _lastRawWorldSprites is null
				? (_lastRawWorldSprites = HostBridge.GetWorldSprites())
				: _lastRawWorldSprites;
		bool rebuildSprites = guestFrame || sprites.Count == 0;
		System.Collections.Generic.List<Engine_HudWorldSprites.Sprite> spritesTwo =
			_worldSpriteListTwo ??= new System.Collections.Generic.List<Engine_HudWorldSprites.Sprite>();
		if (rebuildSprites)
		{
			sprites.Clear();
			spritesTwo.Clear();
		}

		System.Collections.Generic.Dictionary<int, Vector2> pooledTwo =
			_pooledCanvasTwo ??= new System.Collections.Generic.Dictionary<int, Vector2>();
		pooledTwo.Clear();
		if (guestFrame)
		{
			_holdPromptTwo = CoopSession.LocalCoop ? HostBridge.GetPlayerTwoHoldPrompt() : null;
			_promptsTwo.Clear();
			_promptsOneHidden.Clear();
			_titlesOne.Clear();
			_titlesTwo.Clear();
			if (split)
			{
				HostBridge.GetPromptTitles(_promptTitles);
			}
			else
			{
				_promptTitles.Clear();
			}
			if (split)
			{
				int prompts = HostBridge.GetPlayerTwoPrompts(_promptsTwoBuffer);
				for (int i = 0; i < prompts; ++i)
				{
					_promptsTwo.Add(_promptsTwoBuffer[i]);
				}

				int hidden = HostBridge.GetPlayerOneHiddenPrompts(_promptsTwoBuffer);
				for (int i = 0; i < hidden; ++i)
				{
					_promptsOneHidden.Add(_promptsTwoBuffer[i]);
				}
			}
		}

		foreach (HostBridge.WorldSprite sprite in rawSprites)
		{
			if (sprite.Pooled)
			{
				// A prompt on a pooled HUD element: the element draws it, Godot places it. The
				// off-screen hero and henchman markers (types 52/53, the guest's own test in
				// `sub_82824D60`) are pinned inside the edge. On split screen the hero's marker
				// is for player two, so it is pinned inside player two's half through player
				// two's camera; the icons layer stands in player one's seat, so the point is
				// carried into that seat's canvas. On split screen a use prompt is drawn in each
				// half whose player sees its thing (HostBridge.GetPlayerTwoPrompts /
				// GetPlayerOneHiddenPrompts): player one's by their layer, player two's by theirs.
				bool prompt = sprite.Type is not (52u or 53u);
				Vector2? placed = split && sprite.Type == 52u
					? ProjectOffScreenMarker(_spriteViews[1], sprite.Anchor) is { } theirs
						? (Vector2?)_spriteViews[0].Frame.FromCanvas(_spriteViews[1].Frame.ToCanvas(theirs))
						: null
					: sprite.Type is 52u or 53u
						? ProjectOffScreenMarker(_spriteViews[0], sprite.Anchor)
						: split && _promptsOneHidden.Contains(sprite.Entity)
							? null
							: ProjectWorldSprite(sprite.Anchor);
				if (sprite.Slot >= 0 && placed is { } canvas)
				{
					pooled[sprite.Slot] = canvas;
				}

				if (split && prompt && sprite.Slot >= 0 && _promptsTwo.Contains(sprite.Entity) &&
					ProjectWorldSpriteIn(_spriteViews[1], sprite.Anchor) is { } theirsAt)
				{
					pooledTwo[sprite.Slot] = theirsAt;
				}

				// EACH HALF ITS OWN PLAYER'S TEXT: the guest's prompt for a thing is the nearer
				// player's; the other player's own text (who owns it, who may open it) comes from
				// the guest's update run for them (HostBridge.GetPromptTitles).
				if (guestFrame && split && prompt && sprite.Slot >= 0 &&
					_promptTitles.TryGetValue(sprite.Entity, out (int Player, string Title) other))
				{
					(other.Player == 2 ? _titlesTwo : _titlesOne)[sprite.Slot] = other.Title;
				}

				continue;
			}

			// A world icon of the sprite layer: composed below, with the mods' markers, by the one
			// composition (Engine_HudModMarkers.Compose).
		}

		// THE WORLD ICONS — the game's own and the mods' markers — through ONE composition
		// (Engine_HudModMarkers.Compose) into each view's list: the game's exactly as the guest
		// states them (kind, texture hash, fade, anchor, tweened scale, text), shown by the one
		// per-player rule (each half its own player's hearts and splashes; one shared screen both
		// players'; an online host its own, the joiner is sent player two's); the markers after
		// them, sized by that view's camera distance through the game's own curve. Fed on the same
		// frames, through the same layers and projectors.
		if (rebuildSprites)
		{
			Engine_HudModMarkers.LoadIcon ??= LoadMarkerIcon;
			bool hostOnline = CoopSession.PeerStanding && !CoopSession.LocalCoop && !CoopSession.IsClient;
			double clock = Godot.Time.GetTicksMsec() / 1000.0;
			Engine_HudModMarkers.Compose(sprites, rawSprites, 1, split, CoopSession.LocalCoop, hostOnline,
				_markerDistanceOne ??= guest => MarkerDistanceSquared(_spriteViews[0], guest), clock);
			if (split)
			{
				Engine_HudModMarkers.Compose(spritesTwo, rawSprites, 2, split, CoopSession.LocalCoop, hostOnline,
					_markerDistanceTwo ??= guest => MarkerDistanceSquared(_spriteViews[1], guest), clock);
			}
		}

		// PLAYER TWO'S OWN HOLD PROMPT (the guest's one hold slot is player one's): drawn by their
		// layer's hold icon, in their half or on the shared screen.
		if (_holdPromptTwo is { } hold &&
			(split ? ProjectWorldSpriteIn(_spriteViews[1], hold.Anchor) : ProjectWorldSprite(hold.Anchor)) is { } holdAt)
		{
			pooledTwo[16] = holdAt;
		}

		if (_worldIconsTwo is not null && GodotObject.IsInstanceValid(_worldIconsTwo))
		{
			_worldIconsTwo.StatedFill = _holdPromptTwo?.Title;
			_worldIconsTwo.StatedTitles = split ? _titlesTwo : null;
		}

		_worldIcons.StatedTitles = split ? _titlesOne : null;

		_worldSprites.Place ??= PlaceWorldSprite;
		if (rebuildSprites)
		{
			_worldSprites.Feed(sprites);
		}

		// Player two's copy is fed only while it is drawn, and emptied once when the split ends.
		if (_worldSpritesTwo is not null && GodotObject.IsInstanceValid(_worldSpritesTwo))
		{
			_worldSpritesTwo.Place ??= PlaceWorldSpriteTwo;
			if (split && rebuildSprites)
			{
				_worldSpritesTwo.Feed(spritesTwo);
				_worldSpritesTwoFed = true;
			}
			else if (!split && _worldSpritesTwoFed)
			{
				_worldSpritesTwo.Feed(System.Array.Empty<Engine_HudWorldSprites.Sprite>());
				_worldSpritesTwoFed = false;
			}
		}
	}

	/// <summary>Whether player two's sprite layer holds this split's sprites.</summary>
	private bool _worldSpritesTwoFed;

	private System.Func<Vector3, float?>? _markerDistanceOne;
	private System.Func<Vector3, float?>? _markerDistanceTwo;

	/// <summary>A view camera's squared distance to a guest point, for a marker's size; null with no camera.</summary>
	private static float? MarkerDistanceSquared(SpriteView view, Vector3 guest) =>
		view.Camera is { } camera && GodotObject.IsInstanceValid(camera)
			? camera.GlobalPosition.DistanceSquaredTo(GameWorld.GuestToGodot(guest))
			: null;

	/// <summary>
	/// A marker's icon by what the call named: a PNG of an enabled mod (<c>art/x.png</c>, through
	/// the mount or the package itself), else a game texture path through the presentation's loader.
	/// </summary>
	private Texture2D? LoadMarkerIcon(string name)
	{
		string path = name.Replace('\\', '/').TrimStart('/');
		if (path.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase))
		{
			byte[]? bytes = Refii.Textures.Engine_BnkTextureLoader.ReadBytes(path) ?? Refii.Mods.ModEntries.ReadEnabledBytes(path);
			if (bytes is null)
			{
				return null;
			}

			var image = new Image();
			return image.LoadPngFromBuffer(bytes) == Error.Ok ? ImageTexture.CreateFromImage(image) : null;
		}

		return _presentation?.LoadTexture(path);
	}

	/// <summary>The other player's prompt text per thing, and each half's titles by pool slot.</summary>
	private readonly System.Collections.Generic.Dictionary<uint, (int Player, string Title)> _promptTitles = new();
	private readonly System.Collections.Generic.Dictionary<int, string> _titlesOne = new();
	private readonly System.Collections.Generic.Dictionary<int, string> _titlesTwo = new();

	/// <summary>Player two's own hold prompt, read once a guest frame (local co-op).</summary>
	private (Vector3 Anchor, string Title)? _holdPromptTwo;

	/// <summary>Player two's half's sprites on split screen, reused like <c>_worldSpriteList</c>.</summary>
	private System.Collections.Generic.List<Engine_HudWorldSprites.Sprite>? _worldSpriteListTwo;

	/// <summary>The things whose use prompt answers player two, read once a guest frame.</summary>
	private readonly System.Collections.Generic.HashSet<uint> _promptsTwo = new();

	/// <summary>The things whose use prompt only player two sees, read once a guest frame.</summary>
	private readonly System.Collections.Generic.HashSet<uint> _promptsOneHidden = new();
	private readonly uint[] _promptsTwoBuffer = new uint[64];

	/// <summary>
	/// One camera the world sprites are projected through, and the seat they land in: the
	/// window's camera on the whole window, or one player's view camera and half on split screen.
	/// </summary>
	private sealed class SpriteView
	{
		public Camera3D? Camera;

		/// <summary>The camera's own viewport, in pixels — what <c>UnprojectPosition</c> answers in.</summary>
		public Vector2 Viewport;

		public Engine_UiSeats.Frame Frame;

		/// <summary>
		/// Whether the scene-depth line of sight applies: a view whose depth is captured (each
		/// view's own, Engine_FxDepthCaptureEffect.View).
		/// </summary>
		public bool TestSight = true;

		/// <summary>The frame <see cref="View"/> and <see cref="Projection"/> were taken on.</summary>
		public ulong ViewFrame = ulong.MaxValue;

		public Transform3D View;

		public Projection Projection;

		/// <summary>A pixel on the camera's viewport, in the seat's own pixels.</summary>
		public Vector2 SeatPixel(Vector2 pixel) =>
			Viewport == Frame.Window || Viewport.X <= 0f || Viewport.Y <= 0f
				? pixel
				: pixel * (Frame.Window / Viewport);

		/// <summary>Whether a viewport pixel lies inside the view.</summary>
		public bool Shows(Vector2 pixel) =>
			pixel.X >= 0f && pixel.Y >= 0f && pixel.X <= Viewport.X && pixel.Y <= Viewport.Y;
	}

	/// <summary>[0] player one's (or the whole window's), [1] player two's while split.</summary>
	private readonly SpriteView[] _spriteViews = { new(), new() };

	/// <summary>Whether the sprite layers carry seat transforms a shared screen must undo.</summary>
	private bool _spriteLayersSeated;

	/// <summary>
	/// Points both sprite views at this frame's cameras, and seats the layers drawn through them:
	/// on the whole window everything is exactly as it always was; split, player one's layers
	/// (sprites, pooled icons, their reticule, the slow-time dial) take player one's half and
	/// player two's (their sprites and reticule) player two's. Returns whether the screen is split.
	/// </summary>
	private bool SeatSpriteViews(Camera3D windowCamera, Vector2 window)
	{
		Camera3D? one = Engine_Views.FirstViewport is { } first && GodotObject.IsInstanceValid(first)
			? first.GetCamera3D()
			: null;
		bool split = Engine_Views.Split && one is not null &&
			Engine_Views.SecondViewport is { } second && GodotObject.IsInstanceValid(second);

		SpriteView mine = _spriteViews[0];
		mine.Camera = split ? one : windowCamera;
		mine.Viewport = split ? (Vector2)Engine_Views.FirstViewport!.Size : window;
		mine.Frame = Engine_UiSeats.Of(window, split ? Engine_UiSeats.Seat.PlayerOne : Engine_UiSeats.Seat.Whole);
		mine.TestSight = true;

		SpriteView theirs = _spriteViews[1];
		theirs.Camera = split ? Engine_Views.Second : null;
		theirs.Viewport = split ? (Vector2)Engine_Views.SecondViewport!.Size : Vector2.Zero;
		theirs.Frame = Engine_UiSeats.Of(window, Engine_UiSeats.Seat.PlayerTwo);
		theirs.TestSight = split;

		_spriteWindow = mine.Frame.Window;

		if (split || _spriteLayersSeated)
		{
			_spriteLayersSeated = split;
			Seat(_worldSprites, mine.Frame, 52f);
			Seat(_worldIcons, mine.Frame, 50f);
			Seat(_worldIconsTwo, split ? theirs.Frame : mine.Frame, 50f);
			Seat(_reticule, mine.Frame, 54f);
			Seat(_slowTimeDial, mine.Frame, 50f);
			Seat(_worldSpritesTwo, theirs.Frame, 52f);
			Seat(_reticuleTwo, theirs.Frame, 54f);
			Seat(_slowTimeDialTwo, theirs.Frame, 50f);
			if (_reticule is not null && GodotObject.IsInstanceValid(_reticule))
			{
				_reticule.OnlyPlayer = split ? 0 : -1;
			}
		}

		bool theirsShown = split && !_fullCutscene;
		ShowLayer(_worldSpritesTwo, theirsShown);
		ShowLayer(_worldIconsTwo, (split || _holdPromptTwo is not null) && !_fullCutscene);
		ShowLayer(_reticuleTwo, theirsShown);
		ShowLayer(_slowTimeDialTwo, theirsShown);
		return split;

		static void Seat(Node3D? layer, Engine_UiSeats.Frame frame, float z)
		{
			if (layer is not null && GodotObject.IsInstanceValid(layer))
			{
				layer.Transform = frame.Transform(z);
			}
		}

		static void ShowLayer(Node3D? layer, bool visible)
		{
			if (layer is not null && GodotObject.IsInstanceValid(layer) && layer.Visible != visible)
			{
				layer.Visible = visible;
			}
		}
	}

	/// <summary>
	/// One world sprite's place on the HUD canvas and its line of sight, read off the camera AS
	/// IT STANDS WHEN ASKED — the sprite layer asks from its own late process, after GameWorld
	/// has moved the camera. The anchor rides its entity's drawn body (`DrawnLead`).
	/// </summary>
	private Engine_HudWorldSprites.Placed? PlaceWorldSprite(Vector3 guest, uint entity) =>
		PlaceIn(_spriteViews[0], guest, entity, clip: true);

	/// <summary><see cref="PlaceWorldSprite"/> through player two's camera, into their half.</summary>
	private Engine_HudWorldSprites.Placed? PlaceWorldSpriteTwo(Vector3 guest, uint entity) =>
		PlaceIn(_spriteViews[1], guest, entity, clip: true);

	/// <summary>
	/// A sprite's place in <paramref name="view"/>'s seat. A half has a neighbour, so with
	/// <paramref name="clip"/> an anchor outside the half's view is not drawn rather than drawn
	/// over the other player's picture; the whole window keeps no such test, as before.
	/// </summary>
	private Engine_HudWorldSprites.Placed? PlaceIn(SpriteView view, Vector3 guest, uint entity, bool clip)
	{
		if (view.Camera is not { } camera || !GodotObject.IsInstanceValid(camera) || !camera.IsInsideTree() ||
			view.Viewport.X <= 0f || view.Viewport.Y <= 0f)
		{
			return null;
		}

		Vector3 world = entity != 0u && GameEntities.Current is { } entities
			? entities.WorldAnchorFor(entity, guest)
			: GameWorld.GuestToGodot(guest);

		if (camera.IsPositionBehind(world))
		{
			return null;
		}

		// Once a frame: the view and projection the depth buffer was written with.
		ulong frame = Engine.GetProcessFrames();
		if (frame != view.ViewFrame)
		{
			view.ViewFrame = frame;
			view.View = camera.GlobalTransform.AffineInverse();
			view.Projection = camera.GetCameraProjection();
		}

		Vector3 eye = view.View * world;
		Vector4 clipSpace = view.Projection * new Vector4(eye.X, eye.Y, eye.Z, 1f);
		if (clipSpace.W <= 0.0001f)
		{
			return null;
		}

		Vector2 pixel = camera.UnprojectPosition(world);
		if (clip && !view.Frame.IsWhole && !view.Shows(pixel))
		{
			return null;
		}

		// Godot's depth buffer is REVERSED: 1 at the near plane, 0 at the far one.
		float depth = 0.5f - (0.5f * (clipSpace.Z / clipSpace.W));
		return new Engine_HudWorldSprites.Placed(
			view.Frame.FromPixel(view.SeatPixel(pixel)),
			view.TestSight
				? new Vector3(pixel.X / view.Viewport.X, pixel.Y / view.Viewport.Y, depth)
				: new Vector3(-1f, 0f, 0f));
	}

	/// <summary>
	/// A guest world anchor in canvas units, or null when it is behind the camera — the
	/// projector <see cref="DriveWorldSprites"/> hands to the sprite feed. On a half, an anchor
	/// outside player one's view is parked far off the canvas: the pooled icon it places would
	/// otherwise fall back to the guest's own projection, which is for the whole screen.
	/// </summary>
	private Vector2? ProjectWorldSprite(Vector3 guest) => ProjectWorldSpriteIn(_spriteViews[0], guest);

	/// <summary><see cref="ProjectWorldSprite"/> through one view's camera, into that view's seat.</summary>
	private static Vector2? ProjectWorldSpriteIn(SpriteView view, Vector3 guest)
	{
		if (view.Camera is not { } camera || !GodotObject.IsInstanceValid(camera))
		{
			return null;
		}

		// The WORLD's conversion, not the model handedness: the anchor is a guest world
		// point with its up-lift on guest Z, and `(x, y, -z)` put that lift into depth.
		Vector3 world = GameWorld.GuestToGodot(guest);
		if (camera.IsPositionBehind(world))
		{
			return null;
		}

		Vector2 pixel = camera.UnprojectPosition(world);
		if (!view.Frame.IsWhole && !view.Shows(pixel))
		{
			return new Vector2(100000f, 100000f);
		}

		return view.Frame.FromPixel(view.SeatPixel(pixel));
	}

	/// <summary>
	/// The off-screen partner markers (`HeroOffScreenWorldIcon` / `HenchmanOffScreenWorldIcon`,
	/// types 52/53) are ALWAYS clamped into the screen — `sub_82824D60` sends both through
	/// `sub_82824BD0`, which pins the point inside the viewport less 10% (plus 10 px) at the
	/// left, 10% plus 40 px at the top, and 9% (plus 10 px) right / 9% bottom, in its 720-line
	/// pixels. Behind the camera the guest pushes the point outward before clamping; its exact
	/// vector math is not reproduced — the reflected point is extended to the edge instead. On
	/// a half, "the screen" is that half.
	/// </summary>
	private Vector2? ProjectOffScreenMarker(SpriteView view, Vector3 guest)
	{
		if (view.Camera is not { } camera || !GodotObject.IsInstanceValid(camera) ||
			view.Viewport.X <= 0f || view.Viewport.Y <= 0f)
		{
			return null;
		}

		Vector3 world = GameWorld.GuestToGodot(guest);
		Transform3D eye = camera.GlobalTransform;
		Vector3 local = eye.AffineInverse() * world;
		bool behind = local.Z > -0.01f;
		if (behind)
		{
			local.Z = -Mathf.Max(Mathf.Abs(local.Z), 0.01f);
			world = eye * local;
		}

		Vector2 pixel = camera.UnprojectPosition(world);
		Vector2 canvas = view.Frame.FromPixel(view.SeatPixel(pixel));
		if (behind && canvas.LengthSquared() > 0.0001f)
		{
			canvas = canvas.Normalized() * 100000f;
		}

		Vector2 extent = view.Frame.Extent;
		float unit = extent.Y / 720f;
		float left = (-extent.X * 0.5f) + (extent.X * 0.1f) + (10f * unit);
		float right = (extent.X * 0.5f) - (extent.X * 0.09f) - (10f * unit);
		float top = (extent.Y * 0.5f) - (extent.Y * 0.1f) - (40f * unit);
		float bottom = (-extent.Y * 0.5f) + (extent.Y * 0.09f);
		return new Vector2(Mathf.Clamp(canvas.X, left, right), Mathf.Clamp(canvas.Y, bottom, top));
	}

	/// <summary>Player one's seat window — the whole window, or their half while split — for
	/// the layers that size themselves off it. See <see cref="SeatSpriteViews"/>.</summary>
	private Vector2 _spriteWindow;

	private System.Func<Vector3, Vector2?>? _spriteProject;

	/// <summary>The world sprites handed to the feed, reused frame to frame.</summary>
	private System.Collections.Generic.List<Engine_HudWorldSprites.Sprite>? _worldSpriteList;

	/// <summary>The HUD's element pipeline — one resolve, shared with every owned surface.</summary>
	private Engine_UiElementResolve? _hudResolve;


	private int _hudRootSlideSaid = -1;

	/// <summary>
	/// The scene root's current slide, applied over the guest's stale statements: its entries'
	/// actives replace the guest's, and every element it commands an opacity for drops the
	/// guest's stated opacity so the authored track decides. Nothing outside the root's own
	/// slides is touched.
	/// </summary>
	private int _hudStatedFolded = -1;

	private void FoldStatedHudSlides(Engine_UiScreen doc, int[] slides, bool[] active, float?[] stated)
	{
		if (_hud is null || _hud.Bindings is not { } bindings)
		{
			return;
		}

		int folded = 0;
		void Fold(int ordinal, int slide)
		{
			if (ordinal < 0 || ordinal >= slides.Length)
			{
				return;
			}

			slides[ordinal] = slide;
			++folded;

			// THE SLIDE OUTRANKS THE STATED OPACITY, as for the root: the guest's own animation
			// of a slide it did not play never ran, so the elements it commands still carry the
			// authored opacity — 0 on every box that opens by fading in. Same release as the
			// root's, on this owner's chosen slide.
			ReleaseHudOwnerSlide(doc, ordinal, slide, active, stated);
		}

		foreach ((uint hash, bool visible) in _hud.Values.Toggles)
		{
			foreach (int ordinal in bindings.Elements(Engine_UiBindings.Kind.SlideToggle, hash))
			{
				Fold(ordinal, visible ? 2 : 1);
			}
		}

		foreach ((uint hash, int slide) in _hud.Values.Fades)
		{
			foreach (int ordinal in bindings.Elements(Engine_UiBindings.Kind.SlideFader, hash))
			{
				Fold(ordinal, slide);
			}

			foreach (int ordinal in bindings.Elements(Engine_UiBindings.Kind.SlideFader2, hash))
			{
				Fold(ordinal, slide + 1);
			}
		}

		// REGISTRY 10 IS VERBATIM — no shift. `sub_828421F8` hands the published integer
		// straight to the slide player, and registry 9 reaches that same call through
		// `sub_82426CA8` with 1 or 2, so the toggle's +1 is the toggle's and not the channel's.
		// Nothing clamps: an index the owner has no slide for simply finds nothing.
		foreach ((uint hash, int slide) in _hud.Values.SlideSets)
		{
			foreach (int ordinal in bindings.Elements(Engine_UiBindings.Kind.SlideSet, hash))
			{
				Fold(ordinal, slide);
			}
		}

		if (folded != _hudStatedFolded)
		{
			_hudStatedFolded = folded;
			GD.Print($"[hud] {folded} host-stated toggle/fader slide(s) folded over the guest's");
		}
	}

	/// <summary>
	/// Releases the stated opacity of every element an owner's chosen slide commands or
	/// animates, and takes the slide's active bits — so the slide's end pose is what draws.
	/// </summary>
	private static int ReleaseHudOwnerSlide(
		Engine_UiScreen doc, int owner, int wanted, bool[] active, float?[] stated)
	{
		if (doc.Document is not { } file || wanted <= 0)
		{
			return 0;
		}

		int seen = 0;
		foreach (BgfSlide slide in file.Slides)
		{
			if (slide.OwnerElement != owner || slide.IsMaster || ++seen != wanted)
			{
				continue;
			}

			return ReleaseHudSlideEntries(file, slide, active, stated);
		}

		return 0;
	}

	private System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<BgfSlide>>? _hudSlidesByOwner;

	private Engine_BgfDocument? _hudSlidesDocument;

	private void ReleaseHudPlayedSlides(Engine_UiScreen doc, int[] slides, bool[] active, float?[] stated)
	{
		if (doc.Document is not { } file || doc.Roots.Length == 0)
		{
			return;
		}

		if (!ReferenceEquals(_hudSlidesDocument, file) || _hudSlidesByOwner is null)
		{
			_hudSlidesDocument = file;
			_hudSlidesByOwner = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<BgfSlide>>();
			foreach (BgfSlide slide in file.Slides)
			{
				if (slide.IsMaster)
				{
					continue;
				}

				if (!_hudSlidesByOwner.TryGetValue(slide.OwnerElement, out System.Collections.Generic.List<BgfSlide>? list))
				{
					_hudSlidesByOwner[slide.OwnerElement] = list = new System.Collections.Generic.List<BgfSlide>();
				}

				list.Add(slide);
			}
		}

		int root = doc.Roots[0].Ordinal;
		for (int owner = 0; owner < slides.Length; ++owner)
		{
			int wanted = slides[owner];
			if (wanted <= 0 || owner == root ||
				!_hudSlidesByOwner.TryGetValue(owner, out System.Collections.Generic.List<BgfSlide>? owned) ||
				wanted > owned.Count)
			{
				continue;
			}

			ReleaseHudSlideEntries(file, owned[wanted - 1], active, stated);
		}
	}

	/// <summary>The one release of a slide's entries, shared with a copy's own slide (Engine_UiElementResolve.ReleaseSlideEntries).</summary>
	private static int ReleaseHudSlideEntries(
		Engine_BgfDocument file, BgfSlide slide, bool[] active, float?[] stated) =>
		Engine_UiElementResolve.ReleaseSlideEntries(file, slide, active, stated);

	private void ReleaseHudRootSlide(Engine_UiScreen doc, int[] slides, bool[] active, float?[] stated)
	{
		if (doc.Document is not { } file || doc.Roots.Length == 0)
		{
			return;
		}

		int root = doc.Roots[0].Ordinal;
		if (root < 0 || root >= slides.Length)
		{
			return;
		}

		int wanted = slides[root];
		if (wanted <= 0)
		{
			if (_hudRootSlideSaid != 0)
			{
				_hudRootSlideSaid = 0;
				GD.Print($"[hud] root '{doc.Roots[0].Name}'#{root} states slide 0 (master) — nothing released");
			}

			return;
		}

		int seen = 0;
		foreach (BgfSlide slide in file.Slides)
		{
			if (slide.OwnerElement != root || slide.IsMaster)
			{
				continue;
			}

			if (++seen != wanted)
			{
				continue;
			}

			int released = 0;
			foreach (BgfSlideEntry entry in slide.Entries)
			{
				int ordinal = entry.ElementOrdinal;
				if (ordinal < 0 || ordinal >= stated.Length)
				{
					continue;
				}

				active[ordinal] = entry.Active;
				foreach (BgfAttribute command in entry.Commands)
				{
					if (command.Id == Engine_UiElementResolve.OpacityAttribute)
					{
						stated[ordinal] = null;
						++released;
						break;
					}
				}
			}

			if (_hudRootSlideSaid != wanted)
			{
				_hudRootSlideSaid = wanted;
				GD.Print($"[hud] root '{doc.Roots[0].Name}'#{root} on slide {wanted} ('{slide.Name}') — " +
					$"{slide.Entries.Length} entry(ies), {released} guest opacity(ies) released to the slide");
			}

			return;
		}

		if (_hudRootSlideSaid != -2)
		{
			_hudRootSlideSaid = -2;
			GD.Print($"[hud] root '{doc.Roots[0].Name}'#{root} states slide {wanted} but owns no such slide");
		}
	}

	/// <summary>The hud document, kept for the opacity chain the piece gate walks.</summary>
	private Engine_UiScreen? _hudScreen;

	/// <summary>The guest's hud presentation address, taken as Follow declines to mirror it.</summary>
	private uint _hudAddress;

	private int _worldIconTick;

	/// <summary>
	/// A scratch buffer at least <paramref name="length"/> long, grown in place.
	///
	/// The feed's arrays are sized by the hud document's element count, which does not change
	/// while a document is up; reallocating them each pass only makes garbage.
	/// </summary>
	private static T[] Buffer<T>(ref T[]? held, int length)
	{
		if (held is null || held.Length < length)
		{
			held = new T[length];
		}

		return held;
	}

	private bool[]? _hudActive;
	private int[]? _hudSlides;
	private float?[]? _hudStated;

	private long _hudOpacityTicks;
	private long _hudTransformTicks;
	private long _hudResolveTicks;

	private long _hudFeedTicks;

	private int _hudFeedFrames;

	/// <summary>
	/// Feeds the world icons: positions every frame (the guest restates them every frame),
	/// the lit set, titles and art at the registry sampling cadence.
	///
	/// AND THE REBUILT HUD'S GATE, WHICH IS PER FRAME AND WAS NOT. Whether a HUD piece is dark is
	/// a per-frame fact — the guest lights a spell charge, parks a quick slot, fades the whole
	/// layer for a cutscene — and its VALUES are already sampled every frame in
	/// `Engine_HudView._Process`. Only the gate rode the world icons' registry cadence, because
	/// it was written into this method to share one element read, so the HUD drew fresh numbers
	/// through visibility up to thirty frames old: a bar that pops in half a second after the
	/// menu closes, and a charge that lights and dies between two samples never drawn at all.
	///
	/// THE SPLIT IS BY HOW OFTEN THE ANSWER CHANGES, not by what is cheap. Active bits, slides
	/// and opacity move every frame and are read every frame; which TEXTURE a slot carries
	/// changes when the player equips something, and stays on the icons' cadence with the lit
	/// set and the titles.
	/// </summary>
	private void DriveWorldIcons(bool guestFrame = true)
	{
		DriveWorldSprites(guestFrame);
		DriveReticule();
		if (guestFrame)
		{
			DriveSlowTimeDial();
			DriveFullCutscene();
			DriveCreditsRoll();
		}

		if (_worldIcons is null || _hudAddress == 0 ||
			!GodotObject.IsInstanceValid(_worldIcons))
		{
			return;
		}

		// THE WHEEL IS A ONE-FRAME STATEMENT, so the ordinary cadence cannot serve it.
		// `sub_826AB1F8` writes all eight sectors' icons and titles in the single frame
		// `sub_826A9A60` opens the ring, sets the populated flag at `wheel+30` after the
		// populate returns, and never restates them — there is no per-frame update on that
		// class. Sampling on the 30-frame cadence therefore catches whatever fraction of the
		// ring existed at the last multiple of 30, which at the ~19fps one run measured is
		// most of a second of half-dressed wheel. Same argument as the spell blade's arm in
		// SampleIsOverdue: a widget the player is driving directly has to sample at the rate
		// they drive it.
		//
		// The increment stays on the left so it runs every frame regardless — this is an
		// EXTRA sampling window, not a replacement clock. It costs the texture-hash walk per
		// frame for as long as the ring is held, and only then.
		bool cadence = ++_worldIconTick % PresentationSampleFrames == 0 ||
			GameInput.QuickWheelHeld;

		// THE GATE RUNS EVERY FRAME AGAIN, because the read it was rationing is no longer a
		// read. It went 30-frame -> per-frame -> every third frame, and each move was the right
		// call for the cost at the time: per-frame cost 8.4ms, all of it marshalling 3,431
		// elements' worth of strings the gate never looks at, so a third of the frames bought a
		// third of the cost at a latency the old cadence could not touch anyway.
		//
		// That comment ended "the honest fix is a lean bridge export that returns only the gate
		// channels; until that ABI bump, this is the balance". The ABI bump happened, and it was
		// better than a lean export: the presentation is walked ONCE and the guest's own writers
		// keep it current (ABI 83/84, Engine_UiElementState). A cached read costs a dictionary
		// lookup, so rationing it buys nothing and costs up to two frames of latency on the HUD
		// appearing and disappearing.
		// Nothing rations it any more, so there is no gate variable to carry.

		// WHAT THE PER-FRAME GATE COSTS, reported rather than assumed — and kept now that the
		// reads are cached rather than retired with them, because "the cache made it free" is
		// exactly the kind of claim this project has been caught asserting. The number is what
		// says the seed is holding; a gate that climbs back towards its old cost means something
		// is marking the HUD stale every frame. Averaged over the cadence, so the line is one
		// number a second rather than noise.
		bool hudValid = _hud is not null && GodotObject.IsInstanceValid(_hud);
		bool needHudFeed = guestFrame || cadence || GameInput.QuickWheelHeld ||
			GameInput.SpellsMenuHeld || (hudValid && _hud!.ElementActive is null);
		long began = System.Diagnostics.Stopwatch.GetTimestamp();
		if (needHudFeed && Engine_UiElementState.Current.Elements(_hudAddress) is { Count: > 0 } elements)
		{
			if (cadence)
			{
				uint[]? iconTextures = SampleHudTextures(elements.Count);
				_worldIcons.Sample(elements, iconTextures);
				if (_worldIconsTwo is not null && GodotObject.IsInstanceValid(_worldIconsTwo))
				{
					_worldIconsTwo.Sample(elements, iconTextures);
				}
			}

			// THE SAME ELEMENT READ, EVERY FRAME, gates the rebuilt HUD's pieces — it shares
			// the read above but not the icons' cadence; see the method comment for why the
			// two diverge. TWO statements, both required:
			// the ACTIVE bit, and the OPACITY CHAIN — because opacity is how the guest
			// actually hides HUD furniture. The wheel's elements stay globalactive while it
			// is closed; `SpellsMenu`'s show/hide is an opacity write on a group, exactly
			// like hud.lua's whole-HUD Enable/Disable. Gating on active alone drew the closed
			// wheel as permanent scenery.
			//
			// The chain is the engine's own accumulation — parent product times own, live
			// value where stated, authored where not — and document order is parents-first,
			// so one forward pass computes every element.
			if (hudValid)
			{
				// REUSED, NOT REALLOCATED. This walks the WHOLE hud document — 3431 elements —
				// and three fresh arrays a pass (bool, int and a nullable float each per
				// element) is tens of kilobytes of garbage per gate frame for buffers whose
				// size never changes. The feed measured 11-14ms per frame averaged across the
				// gate; the allocation and the collection it provokes are part of that.
				bool[] active = Buffer(ref _hudActive, elements.Count);
				for (int i = 0; i < elements.Count; ++i)
				{
					active[i] = elements[i].IsGloballyActive;
				}

				if (_hudScreen is { } doc && doc.Nodes.Length == elements.Count)
				{
					// ONE RESOLVE, the same one the owned books use. This block used to carry its own
					// copy of the slide fold and the opacity chain; those are terms of one function
					// and they live in Engine_UiElementResolve now.
					_hudResolve ??= new Engine_UiElementResolve(doc);

					int[] slides = Buffer(ref _hudSlides, elements.Count);
					for (int i = 0; i < elements.Count; ++i)
					{
						slides[i] = elements[i].Slide;
					}

					// CLEARED, because a slot the guest stops stating must fall back to the
					// authored value rather than keep last frame's answer.
					float?[] stated = Buffer(ref _hudStated, elements.Count);
					System.Array.Clear(stated, 0, elements.Count);
					long opacityBegan = System.Diagnostics.Stopwatch.GetTimestamp();
					Engine_UiModel.LiveAttribute[,]? opacityRead =
						Engine_UiElementState.Current.Attributes(
							Engine_UiLiveAttributes.OpacityIds, _hudAddress);
					_hudOpacityTicks += System.Diagnostics.Stopwatch.GetTimestamp() - opacityBegan;
					if (opacityRead is { } read)
					{
						int rows = System.Math.Min(read.GetLength(0), elements.Count);
						for (int i = 0; i < rows; ++i)
						{
							if (read[i, 0].Present && float.IsFinite(read[i, 0].AsFloat))
							{
								stated[i] = read[i, 0].AsFloat;
							}
						}
					}

					// THE ROOT SLIDE OUTRANKS THE GUEST'S STALE ATTRIBUTES. The HUD's show/hide is a
					// slide on the scene root (1 shown, 2 hidden — `sub_821764D8` / `sub_82405678`)
					// whose tracks move `UpperElements` and fade it; the guest's own animation of
					// that slide does not run here, so the elements it commands keep the opacity
					// they had on the LAST slide the guest did play — dark, for the rest of the
					// session. The owned books already give a slide's commands precedence over the
					// guest's stated opacity; the HUD gets the same for its root's current slide.
					// THE STATED TOGGLES AND FADES. Registries 9 (SlideToggleName) and 3
					// (SlideFader2Name) cannot be read out of the guest, so what its publishers
					// toggle — the TopBox's visibility, the meter's — is re-derived host-side and
					// folded here as the slides those registries would play: toggle true → 2,
					// false → 1 (`sub_823FE848`); fader index as stated, plus one on the second channel.
					FoldStatedHudSlides(doc, slides, active, stated);

					ReleaseHudRootSlide(doc, slides, active, stated);

					// EVERY PLAYED SLIDE OUTRANKS THE STALE ATTRIBUTES, not only the root's. The
					// guest's tweens do not advance here, so an element whose owner sits on a
					// slide it did play (the vignette border's `Off`, opacity 0 over 1444 ms)
					// still carries the loaded value the tween was meant to leave — 90, a black
					// band across the top of every region. Slide 0 is the master, never played,
					// and its elements keep their stated values.
					ReleaseHudPlayedSlides(doc, slides, active, stated);

					long resolveBegan = System.Diagnostics.Stopwatch.GetTimestamp();
					_hudResolve.Apply(slides, active, stated);
					_hudResolveTicks += System.Diagnostics.Stopwatch.GetTimestamp() - resolveBegan;
					_hud!.Resolve = _hudResolve;
					_hud.LiveSlides = slides;
					// The resolve's inputs beside it: a mod copy that states a slide of its own
					// resolves itself from these (Engine_HudView.Built.SlideOverrides).
					_hud.ActiveInputs = active;
					_hud.StatedOpacity = stated;

					// THE ART, ON THE ICONS' CADENCE. A slot's texture changes when the player
					// equips something, not per frame, and this is the one read here that walks a
					// hash per element. Held across frames rather than re-read, so the gate above
					// can be per frame without the whole feed becoming so.
					if (cadence || _hud.LiveTextures is null)
					{
						_hud.LiveTextures = SampleHudTextures(elements.Count);
					}

					// AND THE TRANSFORMS. The bars are authored empty, so the binding needs the
					// guest's stated scale or it writes the authored zero back over the scrub.
					long transformBegan = System.Diagnostics.Stopwatch.GetTimestamp();
					_hud.LiveTransform =
						Engine_UiLiveAttributes.Transform(_hudAddress, elements.Count);
					_hudTransformTicks += System.Diagnostics.Stopwatch.GetTimestamp() - transformBegan;

					// AND THE ELEMENT TEXT, for the expression wheel's titles — the guest writes
					// them onto the elements (`sub_826AB1F8`), not into the text registry.
					if (_hudTexts is null || _hudTexts.Length != elements.Count)
					{
						_hudTexts = new string?[elements.Count];
					}

					for (int i = 0; i < elements.Count; ++i)
					{
						_hudTexts[i] = elements[i].Text;
					}

					_hud.LiveTexts = _hudTexts;

					for (int i = 0; i < elements.Count; ++i)
					{
						// A piece is dark when the guest says so, when its SLIDE says so, or when the
						// accumulated opacity has faded it out — three channels, one answer.
						active[i] = _hudResolve.Drawn[i] && _hudResolve.Opacity[i] > 0.01f;
					}
				}

				_hud!.ElementActive = active;
				DrivePlayerTwoExperience();
				DrivePlayerTwoHud();
				ShareHudFeed();
			}

			_hudFeedTicks += System.Diagnostics.Stopwatch.GetTimestamp() - began;
			++_hudFeedFrames;
			if (cadence && _hudFeedFrames >= 1800)
			{
				double ms = _hudFeedTicks * 1000.0
					/ System.Diagnostics.Stopwatch.Frequency / _hudFeedFrames;
				double per = 1000.0 / System.Diagnostics.Stopwatch.Frequency / _hudFeedFrames;
				GD.Print($"[hud] feed split/frame: " +
					$"opacity-read {_hudOpacityTicks * per:0.00}ms, " +
					$"transform-read {_hudTransformTicks * per:0.00}ms, " +
					$"resolve {_hudResolveTicks * per:0.00}ms — the rest is the element read " +
					"and the three passes");
				_hudOpacityTicks = 0;
				_hudTransformTicks = 0;
				_hudResolveTicks = 0;
				GD.Print($"[hud] feed {ms:0.00}ms/frame over {_hudFeedFrames} frame(s), " +
					$"{elements.Count} element(s)");
				_hudFeedTicks = 0;
				_hudFeedFrames = 0;
			}
		}

		// U09: a latched wheel with no HUD to show it (a cutscene, a loading screen) is let go,
		// so the cursor never stays freed from the camera.
		if (GameInput.WheelToggle && GameInput.QuickWheelHeld && !(hudValid && _hudScreen is not null))
		{
			GameInput.WheelClosedByGame();
		}

		if (hudValid && _hudScreen is not null)
		{
			// THE EXPRESSION WHEEL, through the guest's own open/close/select — the
			// GameAction chain that would run them from the pad does not fire here.
			// Hold to open, release to close (the shipped default; the toggle option
			// `GUIQuickAccessMenuOnToggle` is not read yet). The pump gates every call
			// on the guest's own tests, so a press it cannot honour is refused there.
			if (GameInput.QuickWheelPressed)
			{
				HostBridge.PostFrontEndAction("QuickWheel:open");
				_wheelSector = -1;
				_wheelSeenOpen = false;
				_wheelHiddenFrames = 0;
				_wheelPointerLogged = false;
			}
			else if (GameInput.QuickWheelReleased)
			{
				HostBridge.PostFrontEndAction("QuickWheel:close");
			}
			else if (GameInput.QuickWheelHeld)
			{
				// A accepts (`sub_826AA230`: enter a group / start the item), its release
				// ends a held expression (`sub_826AA5D8`), B backs out (`sub_826AA510`).
				if (GameInput.MenuAcceptPressed)
				{
					HostBridge.PostFrontEndAction("QuickWheel:select");
				}
				else if (GameInput.MenuAcceptReleased)
				{
					HostBridge.PostFrontEndAction("QuickWheel:release");
				}
				else if (GameInput.MenuBackPressed)
				{
					HostBridge.PostFrontEndAction("QuickWheel:back");
				}

				// THE CURSOR, as `sub_826AA060` computes it from the stick: dead zone
				// |v|² < 0.1, then the nearest of eight unit vectors at 3π/4 − iπ/4 (0 =
				// NE, clockwise). The guest's own raw-stick route feeds the same routine
				// when it is alive; this posts only on a sector change, so the two agree.
				// THE POINTER AS THE STICK: its offset from the wheel's centre, inside a
				// dead ring of a third of the wheel, is the same direction the stick
				// would hold. Left press selects, its release ends a held expression.
				Vector2 stick = GameInput.LeftStick;

				// U09: with the toggle, nothing releases the key when the game itself closes
				// the wheel (an expression started), so the latch follows the wheel's art: once
				// it has been shown, a wheel hidden for a few frames running is a closed one.
				Rect2? wheelBounds = WheelCanvasBounds(out int wheelPieces);
				if (GameInput.WheelToggle)
				{
					if (wheelPieces > 0)
					{
						_wheelSeenOpen = true;
						_wheelHiddenFrames = 0;
					}
					else if (_wheelSeenOpen && ++_wheelHiddenFrames >= 15)
					{
						_wheelSeenOpen = false;
						GD.Print("[ui-u09] the game closed the wheel: latch released");
						GameInput.WheelClosedByGame();
					}
				}

				if (!_wheelPointerLogged && wheelPieces > 0)
				{
					_wheelPointerLogged = true;
					GD.Print($"[ui-u09] wheel open: pointer={(Engine_UiPointer.Canvas is { } at ? at.ToString() : "none")} " +
						$"free={Engine_UiPointer.Free} pieces={wheelPieces} box={(wheelBounds is { } bx ? bx.ToString() : "none")}");
				}

				// U09c: the wheel is drawn at the window's centre; the pointer's offset from it in
				// window pixels (Y down, as the stick) is the direction. The pieces' canvas box is
				// off-centre and was the miscalibration.
				if (Engine_UiPointer.Canvas is not null && wheelBounds is not null && GetViewport() is { } wheelPort)
				{
					Vector2 portSize = wheelPort.GetVisibleRect().Size;
					Vector2 offset = wheelPort.GetMousePosition() - (portSize * 0.5f);
					float dead = Mathf.Min(portSize.X, portSize.Y) * 0.06f;
					if (dead > 0f && offset.Length() > dead)
					{
						stick = offset.Normalized();
					}

					if (Engine_UiPointer.TakeLeft())
					{
						_wheelMouseDown = true;
						HostBridge.PostFrontEndAction("QuickWheel:select");
					}
					else if (_wheelMouseDown && !Input.IsMouseButtonPressed(MouseButton.Left))
					{
						_wheelMouseDown = false;
						HostBridge.PostFrontEndAction("QuickWheel:release");
					}
				}

				if (stick.LengthSquared() >= 0.1f)
				{
					int best = -1;
					float bestDistance = float.MaxValue;
					for (int i = 0; i < 8; ++i)
					{
						float angle = 2.3561945f - (i * 0.7853982f);
						float dx = stick.X - Mathf.Sin(angle);
						float dy = -stick.Y - Mathf.Cos(angle);
						float distance = (dx * dx) + (dy * dy);
						if (distance < bestDistance)
						{
							bestDistance = distance;
							best = i;
						}
					}

					if (best != _wheelSector)
					{
						_wheelSector = best;
						if (GameInput.WheelToggle)
						{
							GD.Print($"[ui-u09] sector {best} stick={stick}");
						}

						HostBridge.PostFrontEndAction($"QuickWheel:point:{best}");
					}
				}
			}

			// LOCAL PLAYER TWO'S WHEEL: the same guest routines, opened for player two
			// (`QuickWheel:2:` — the wheel stores its owner, lists the owner's expressions and,
			// accepted with the owner's flag, performs on player two), off player two's pad.
			if (CoopSession.LocalCoop && GameInput.PlayerTwoPad >= 1)
			{
				if (GameInput.QuickWheelTwoPressed)
				{
					HostBridge.PostFrontEndAction("QuickWheel:2:open");
					_wheelSectorTwo = -1;
				}
				else if (GameInput.QuickWheelTwoReleased)
				{
					HostBridge.PostFrontEndAction("QuickWheel:2:close");
				}
				else if (GameInput.QuickWheelTwoHeld)
				{
					if (GameInput.WheelTwoAcceptPressed)
					{
						HostBridge.PostFrontEndAction("QuickWheel:2:select");
					}
					else if (GameInput.WheelTwoAcceptReleased)
					{
						HostBridge.PostFrontEndAction("QuickWheel:2:release");
					}
					else if (GameInput.WheelTwoBackPressed)
					{
						HostBridge.PostFrontEndAction("QuickWheel:2:back");
					}

					int sector = WheelSector(GameInput.LeftStickTwo);
					if (sector >= 0 && sector != _wheelSectorTwo)
					{
						_wheelSectorTwo = sector;
						HostBridge.PostFrontEndAction($"QuickWheel:2:point:{sector}");
					}
				}
			}

			// THE SPELLS MENU. The comment here used to say "no GameAction reaches the
			// consumer that would run 42 and 43, so the guest never sees the press" —
			// THAT IS FALSE. `sub_8236F788` has one code caller, the guest's own
			// GameAction dispatcher, and it is what sets the open flag in a live run
			// while the host lever logs only refusals. The guest sees the trigger.
			//
			// These posts are kept as a debug lever, and the pump now refuses to CLOSE a
			// blade it did not open — closing the guest's own clears the flag at
			// 0x8236f9b8 before the id-5 GuiBlock pop at 0x8236fa00 can run, and an
			// orphaned GuiBlock costs movement and camera for the rest of the session.
			if (GameInput.SpellsMenuPressed)
			{
				HostBridge.PostFrontEndAction("SpellsMenu:open");
			}
			else if (GameInput.SpellsMenuReleased)
			{
				HostBridge.PostFrontEndAction("SpellsMenu:close");
			}
			else if (GameInput.SpellsMenuHeld)
			{
				// THE HELD DIRECTION, AS THE GUEST'S OWN IDS, EVERY FRAME. The widget
				// moves on GameAction 28 UP / 29 DOWN / 30 LEFT / 31 RIGHT and nothing
				// else: its stick resolution is compiled off (`widget+165` is left 0 by
				// `sub_826B4358` at 0x826b4410, and `sub_828818E8` drops a 26 while it is
				// clear), which is why the old `stick` post never moved the box. The
				// cadence is the widget's own latch — first press at once, then 0.3 s,
				// then 0.1 s — so no clock here; `release` resets it when the pad lets go.
				//
				// 28 is UP = `++row` = the higher power level, drawn higher on screen; the
				// host's menu direction is a LIST direction (down = +1), so up is < 0.
				string? direction = GameInput.MenuDirection < 0 ? "up"
					: GameInput.MenuDirection > 0 ? "down"
					: GameInput.MenuHorizontal < 0 ? "left"
					: GameInput.MenuHorizontal > 0 ? "right"
					: null;
				if (direction is not null)
				{
					HostBridge.PostFrontEndAction("SpellBlade:" + direction);
					_spellBladeHeld = true;
				}
				else if (_spellBladeHeld)
				{
					HostBridge.PostFrontEndAction("SpellBlade:release");
					_spellBladeHeld = false;
				}

				// A copies this power level's will down into every later level that also
				// offers it (`sub_826B4EF8`). B is the guest's own GameAction 43 and is
				// never ours — see DispatchSpellsMenu on the orphaned GuiBlock.
				if (GameInput.MenuAcceptPressed)
				{
					HostBridge.PostFrontEndAction("SpellBlade:copydown");
				}

				// A mod's secondary pick (Engine_UiSpellAssignSecondary): Y or X on the
				// highlighted will, written to the mod's setting.
				Engine_UiSpellAssign.SecondaryAction partnerAction =
					Engine_UiSpellAssign.SecondaryPressed(GameInput.MenuFlipPressed, GameInput.MenuEquipPressed);
				if (partnerAction != Engine_UiSpellAssign.SecondaryAction.None)
				{
					_spells.WheelSecondary(partnerAction);
				}

				_spells.WheelPointer();
			}
		}

		bool splitIcons = Engine_Views.Split && _worldIconsTwo is not null && GodotObject.IsInstanceValid(_worldIconsTwo);
		bool iconsTwo = (Engine_Views.Split || _holdPromptTwo is not null) && _worldIconsTwo is not null &&
			GodotObject.IsInstanceValid(_worldIconsTwo);
		if (_worldIcons.Lit > 0)
		{
			_worldIcons.Drive(GameInput.AHeld);
			if (_pooledCanvas is { } pooled)
			{
				_worldIcons.TrackProjected(pooled, splitIcons);
			}
		}

		if (iconsTwo && _worldIconsTwo!.Lit > 0)
		{
			(float fraction, bool pressed) = HostBridge.GetPlayerTwoHold();
			_worldIconsTwo.Drive(pressed, fraction * 100f);
			_worldIconsTwo.TrackProjected(_pooledCanvasTwo ??
				(System.Collections.Generic.IReadOnlyDictionary<int, Vector2>)new System.Collections.Generic.Dictionary<int, Vector2>(), true);
		}
	}

	private Engine_AchievementToast? _toast;
	private Node3D? _toastMount;

	/// <summary>
	/// Stands the achievement toast up, topmost on the UI canvas.
	///
	/// THE MOUNT CONVERTS PIXELS TO CANVAS UNITS. The toast lays itself out in display PIXELS
	/// (it is handed the window as its `Canvas`) so its text is laid out at the size it is
	/// drawn, and the canvas camera draws 720 UNITS across the window height — so the wrapper's
	/// per-frame scale is the visible-extent-per-pixel ratio. The toast's own numbers are HUD
	/// canvas units, carried into those pixels by its `HudScale`.
	///
	/// Z 9000: above every presentation slab (depth × 1000) and still inside the canvas
	/// camera's near plane at 10000. The toast hides itself when nothing is queued.
	/// </summary>
	private void BuildAchievementToast()
	{
		if (_presentation is null)
		{
			return;
		}

		_toast = new Engine_AchievementToast
		{
			Name = "AchievementToast",
			Theme = _presentation.Theme,
		};

		_toastMount = new Node3D { Name = "ToastMount", Position = new Vector3(0f, 0f, 9000f) };
		_toastMount.AddChild(_toast);
		MountHudWidget(Engine_HudWidgets.AchievementToast, false, _toastMount);
	}

	/// <summary>
	/// The achievement system's frame. The guest's own answer to its logbook `Achievements` row
	/// is `XamShowAchievementsUI` (`sub_826D9620` case 150), the console's guide blade, which on
	/// this platform opens nothing.
	///
	/// The request is LOGGED, NOT ACTED ON. The achievements have their own screen now
	/// (<see cref="Engine_UiHostScreen"/>, 1013) on its own pause tab, and the logbook row that
	/// raised this is dropped from the list by its own template record — so a request reaching
	/// here at all means something OTHER than that row asked, which is worth seeing.
	/// </summary>
	private static void DriveAchievements()
	{
		Refii.Achievements.Engine_Achievements.Drive();
		int requests = Refii.Achievements.Engine_Achievements.TakeUiRequests();
		if (requests > 0)
		{
			GD.Print($"[achievements] the guest asked for the achievements UI ({requests}x) — " +
				"the list is the pause menu's Achievements tab");
		}
	}

	/// <summary>The owned info panel — see <see cref="Engine_UiInfoPanel"/>.</summary>
	private readonly Engine_UiInfoPanel _infoPanel = new();

	/// <summary>The owned spell-assignment blade and quick wheel — see
	/// <see cref="Engine_UiSpellAssignSet"/>.</summary>
	private readonly Engine_UiSpellAssignSet _spells = new();

	/// <summary>Whether a blade direction was posted last frame, so one `release` follows it.</summary>
	private bool _spellBladeHeld;

	/// <summary>The options page's second input level — see <see cref="Engine_UiScreenLevel"/>.</summary>
	private readonly Engine_UiScreenLevel _screenLevel = new();

	/// <summary>One list model per drawn presentation that has a pool.</summary>
	private readonly System.Collections.Generic.Dictionary<uint, Engine_UiScreenMenu>
		_screenLists = new();

	/// <summary>The last "left mirrored" reason printed, so it says so once rather than per frame.</summary>
	private long? _screenMenuDeclined;

	private Engine_UiScreenMenu? _screenMenu;

	/// <summary>The bridge's UI data generation the open list last re-read at (<see cref="HostBridge.UiDataGeneration"/>).</summary>
	private uint _uiDataSeen;

	/// <summary>The last row this cursor marked viewed, so a resting cursor posts it once.</summary>
	private uint _viewedPosted;

	/// <summary>The ability row last stated to the pump; -1 off the abilities screen.</summary>
	private int _abilityHovered = -1;
	private Engine_FrontEndMenuView? _screenMenuView;

	/// <summary>The guest's screen fade, drawn over the UI. See <see cref="Engine_UiScreenFade"/>.</summary>
	private Engine_UiScreenFade? _fade;

	private uint _pumpQueued = uint.MaxValue;

	private double _pumpPoll;

	private ulong _pumpTicksAtReport;

	/// <summary>
	/// The tick pump's own three counters, whenever the queue moves.
	///
	/// THE EXPORT HAS EXISTED SINCE ABI 35 AND NOTHING EVER READ IT, which cost this project
	/// several sessions: a press the pump never drained and a press the guest received and
	/// ignored are indistinguishable from Godot's side, and both were guessed at more than once.
	/// The three numbers fail differently and that is the point — ticks not climbing is a hook
	/// that stopped; ticks climbing while `drained` does not is a drain that is refusing;
	/// a queue that only grows is neither of those, it is work being posted into a void.
	///
	/// Keyed on the QUEUE moving, so it is one line per post and per drain rather than per
	/// frame, and it carries the tick delta since the last line so a stalled hook is visible
	/// without needing a second report to compare against.
	/// </summary>
	private void ReportPumpHealth(double delta)
	{
		// NOT EVERY FRAME. Reading the counters takes the pump's OWN mutex — the same one
		// `PostGuestTickAction` and the drain take — so polling it at the display rate puts a
		// hundred-plus needless lock acquisitions a second on the guest tick thread's path. The
		// queue only moves when a button is pressed or a drain runs, so ten times a second
		// misses nothing and costs nothing.
		_pumpPoll += delta;
		if (_pumpPoll < 0.1)
		{
			return;
		}

		_pumpPoll = 0.0;

		if (HostBridge.TryGetAbiVersion() is null)
		{
			return;
		}

		(ulong ticks, ulong drained, uint queued) = HostBridge.GuestTickPump();
		if (queued == _pumpQueued)
		{
			return;
		}

		ulong since = ticks - _pumpTicksAtReport;
		_pumpQueued = queued;
		_pumpTicksAtReport = ticks;

		GD.Print($"[guest-pump] queued {queued}, drained {drained}, ticks {ticks} " +
			$"(+{since} since the last change)");
	}
	private uint _screenMenuAddress;
	private uint _screenMenuScreen = uint.MaxValue;
	/// <summary>The owned in-game list's origin — see <see cref="_pauseAnchor"/> for why the
	/// Z belongs with the X and the Y.</summary>
	/// <summary>The HUD's live element text, reused across frames.</summary>
	private string?[]? _hudTexts;

	/// <summary>The expression-wheel sector last posted, so the cursor lever fires on change.</summary>
	private int _wheelSector = -1;

	/// <summary>`Foreground.ExpandableMenu`'s ordinal and authored x in the adopted screen.</summary>
	private int _screenMenuFollowOrdinal = -1;

	private float _screenMenuFollowAuthoredX;

	/// <summary>`ExpandableMenu.Buttons`'s ordinal — the focus dim's element.</summary>
	private int _screenMenuButtonsOrdinal = -1;

	private Vector3 _screenMenuAnchor = new(
		Engine_FrontEndMenuView.CanvasAnchor.X, Engine_FrontEndMenuView.CanvasAnchor.Y, 0f);

	/// <summary>
	/// One frame of the owned pause menu. True when it consumed the input.
	///
	/// STANDS IN ONLY ON THE PAUSE ROOT — screen id 0 with no modal pending, the same window
	/// the bridge accepts a tab select in and the same gate the input mask uses. A tab screen,
	/// a query box, or the resume all leave that window, and the view yields the frame the
	/// guest says so.
	/// </summary>
	private bool DrivePauseMenu(double delta)
	{
		if (_pauseView is null || _pauseMenu is null)
		{
			return false;
		}

		Engine_UiModel.PauseState? pause = HostBridge.GetPauseState();
		bool standing = _pauseTreeUp && IsRevealed && pause is { RootActive: true };

		// The LIST stays on screen under a query box — the original dims it behind the paper,
		// it does not vanish — so visibility follows the SCREEN and only input follows the
		// modal. ScreenId stays 0 under a box (the opener restores it).
		bool onRoot = _pauseTreeUp && IsRevealed && pause is { ScreenId: 0 };
		_pauseView.Visible = onRoot;

		// A mod's menu stands over the pause ROOT only: a tab push, a resume or a quit drops it.
		if (!onRoot)
		{
			_pauseView.CloseMenus();
		}

		DrivePauseBanner(pause);

		// THE OWNED BOOK DRAWS THE ROOT; THE MIRROR DRAWS THE REST. One presentation carries
		// every in-game screen, so the swap is per SCREEN, not per presentation: on the pause
		// root our book stands and the guest's whole tree goes dark; on a tab screen — not
		// owned yet — the mirror comes back. The mirror keeps being SAMPLED either way, which
		// is where the book's text, region map and token positions come from.
		if (_pauseBook is not null && IsInstanceValid(_pauseBook))
		{
			_pauseBook.Visible = onRoot;
			if (_drawn.TryGetValue(_pauseAddress, out Engine_UiDrawnPresentation? pauseDrawn))
			{
				if (pauseDrawn.Node is { } mirrored && IsInstanceValid(mirrored))
				{
					mirrored.Visible = false;  // never the guest's tree — see Engine_UiBookSet
				}

				if (onRoot)
				{
					// THE WHOLE SAMPLE, not three of its six channels. Dropping the draw
						// bit, the opacity and the slides left the pause book with no page
						// fold at all — every page of the stack drawn at once, every ghosted
						// row at full strength — which is the identical defect the world map
						// had before the slide pass landed. Every other owned book passes six.
						// THE READOUTS ARE OURS, not the mirror's — see Engine_UiPausePage.
						// Renown, the purse, the four orbs and the clock were the last
						// guest-fed numbers on this page, correct only for as long as the
						// guest kept writing them.
						_pauseBook.HostValues = Engine_UiPausePage.Values(
							HostBridge.GetHeroVitals(), _pauseBook.Book?.Strings);
						_pauseBook.SetLive(
							pauseDrawn.Texts, pauseDrawn.Textures, pauseDrawn.LastTransform,
							pauseDrawn.Visible, pauseDrawn.Opacity, pauseDrawn.Slides);
					_pauseBook.SetMarkers(HostBridge.GetMapMarkers());
				}
			}
		}

		// THE MULTIPLAYER BOOK STANDS OVER THE PAUSE LIST and takes the frame whole, as a host
		// screen does (Engine_UiHostScreen, below). Driven here, before the list, so its B closes
		// the screen rather than collapsing a fold behind it.
		//
		// It has no guest state to survive on, so it is dropped the moment the pause root is
		// not what the guest says is up: a tab push, a query box or the resume all close it,
		// the same window the list itself yields in.
		// IN GAME ONLY. After a game this drive keeps running on the FRONT END (the pause view
		// exists), and "the pause root is not standing" was closing the multiplayer book the
		// frame the title's own Multiplayer row opened it — leaving the front-end list hidden
		// and the screen blank. On the front end the book belongs to the front-end drive.
		if (OpenListBook is { } standingBook && HostBridge.IsInGame)
		{
			if (!standing)
			{
				standingBook.Close();
			}
			else
			{
				_pauseView.Visible = false;
				GameInput.PauseMenuSwallowsBack = true;

				float slab =
					_drawn.TryGetValue(_pauseAddress, out Engine_UiDrawnPresentation? behind) &&
					IsInstanceValid(behind.Node)
						? behind.Node.Position.Z
						: 0f;

				// Y REFRESHES the open page — MenuFlipPressed is Y, already plumbed.
				PointerPage(standingBook.Book);
				if (standingBook.Drive(
						GameInput.MenuDirection,
						GameInput.MenuAcceptPressed || PointerList(standingBook.View),
						GameInput.MenuBackPressed,
						GameInput.MenuFlipPressed, delta, slab))
				{
					ReportMenuView(standingBook.View);
				}

				return true;
			}
		}

		// A HOST SCREEN STANDS OVER THE PAUSE LIST and takes the frame whole — the achievements
		// screen, a mod's screen (Engine_UiHostScreen, opened by `Screen:open:<id>`). Driven here,
		// before the list, so its B closes the screen rather than collapsing a fold behind it.
		if (DriveHostScreen(standing, delta))
		{
			return true;
		}

		// The nav mask keys on the guest's own pause state inside GameInput; only the B
		// swallow is ours to declare, and it is re-derived every frame so it can never
		// outlive the expansion that justified it.
		GameInput.PauseMenuSwallowsBack = standing &&
			(_pauseView.Menu?.CanCollapse == true || _pauseView.OpenedDepth > 0);

		if (!standing)
		{
			// Said once per change, with the three parts: which of them fell is the whole
			// question when a press on this list goes nowhere.
			//
			// Key first, string only on a change: the three parts pack into three bits, and
			// ordinary play takes this branch every frame.
			int why = (_pauseTreeUp ? 1 : 0) | (IsRevealed ? 2 : 0) |
				(pause is { RootActive: true } ? 4 : 0);
			if (_pauseStandingSaid != why)
			{
				_pauseStandingSaid = why;
				GD.Print($"[game-ui] pause list not standing — tree {_pauseTreeUp} " +
					$"revealed {IsRevealed} root {(pause is { RootActive: true })}");
			}

			return false;
		}

		_pauseStandingSaid = -1;

		// THE GUEST'S OWN AVAILABILITY, followed while the list is up. Its LB/RB nav skips an
		// unavailable tab per frame (`sub_826B83D0` — the childhood restriction on the hero),
		// and its push lever never re-checks, so the ghosting is ours to keep current.
		uint tabs = HostBridge.PauseTabsAvailable();

		// THE HENCHMAN MASK DOES NOT APPLY TO A FULL HERO. On a joiner the guest's mask is the
		// henchman root's — it ghosts Abilities, Weapons and Clothing — and this list is the
		// co-op variant that owns those rows outright, so every tab is offered.
		if (CoopSession.IsClient)
		{
			tabs = 0xFFFFFFFFu;
		}

		// Fable 3's Kick: the online host's, while a partner stands (game+0x11C == 1 in the guest).
		bool kick = CoopSession.Side == CoopSession.Role.Host && CoopSession.PeerStanding &&
			!CoopSession.LocalCoop;
		if (_pauseMenu.SetAvailable(Engine_PauseMenu.KickRow, kick))
		{
			_pauseView.Refresh();
		}

		if (tabs != _pauseTabsMask)
		{
			_pauseTabsMask = tabs;
			if (Engine_PauseMenu.ApplyTabAvailability(_pauseMenu, tabs))
			{
				_pauseView.Refresh();
				GD.Print($"[game-ui] pause tabs available: 0x{tabs:X} " +
					$"({_pauseMenu.Visible.Count} entries offered)");
			}
		}

		// The slide sound every in-game screen's own 140/141 arm plays on the in-game GUI
		// (`sub_826A26E8` 0x826A2734/0x826A2760 on the pause root), for a move that happened.
		if (_pauseView.Navigate(GameInput.MenuDirection, delta))
		{
			Cue(frontEnd: false, GameInput.MenuDirection < 0 ? "slideup" : "slidedown");
		}
		bool pauseClicked = PointerList(_pauseView);

		// Change-keyed, so this is one line when the list settles and one more per press —
		// not a line per frame. It is the only report that walks the ROWS.
		ReportMenuView(_pauseView);
		string pauseRoot = Engine_PauseMenu.RootTemplate;
		Engine_UiModMenus.Focus(_pauseView, pauseRoot, Engine_UiCommandList.Pause);

		if (GameInput.MenuBackPressed)
		{
			// Collapse, then close a mod's menu. With neither B is left through to the guest,
			// whose own B on the pause root resumes the game — that path is entirely the guest's.
			// A level closed below the root is the shared cancel (`SE_GUI_MENU_BOX_CANCEL`,
			// 0x826D5BBC).
			if (_pauseView.Back())
			{
				Cue(frontEnd: false, "cancel");
			}
			else
			{
				Engine_UiModMenus.Back(_pauseView);
			}
		}

		if (!GameInput.MenuAcceptPressed && !pauseClicked)
		{
			return true;
		}

		Engine_UiExpandableMenu.Item? pauseChosen = _pauseView.Menu?.Highlighted;
		string? action = _pauseView.Activate();
		if (Engine_UiModMenus.Activated(_pauseView, pauseRoot, pauseChosen, action, Engine_UiCommandList.Pause))
		{
			return true;
		}

		// EVERY PRESS SAYS WHAT IT DID. A run pressed A on 'Multiplayer' on both instances and
		// the log held one line for the whole session — there was no way to tell a press the
		// gate above swallowed from one Activate answered null on.
		GD.Print($"[game-ui] pause A on '{_pauseMenu.Highlighted?.Name ?? "(none)"}' → " +
			(action is null ? "null (group toggle / opens / no highlight)" : $"'{action}'"));
		if (action is null)
		{
			// Expanded or collapsed a group — complete here.
			return true;
		}

		// ANY OTHER CHOICE CANCELS A PENDING QUIT. The guest returns a refused box to this list,
		// so the next row picked IS the refusal — otherwise the latch would outlive it and turn
		// a later "quit to title" into an exit. A quit row re-arms it as it runs.
		if (_quitToDesktopPending)
		{
			_quitToDesktopPending = false;
			GD.Print("[game-ui] a pending quit to the desktop was cancelled — the pause list " +
				"took another choice, so the guest's box was answered 'no'");
		}

		// THE ONE DISPATCHER: the pause levers (GameUiCommands), a host screen (`Screen:open:`), a
		// host action or a mod's verb — whatever the row names. A host screen opened here stands
		// over this list; nothing is posted to the guest, whose pause root stays where it is.
		string pauseRow = _pauseMenu.Highlighted?.Name ?? string.Empty;
		Engine_UiCommandResult ran = Engine_UiCommands.Run(action, new Engine_UiCommandContext(
			Engine_UiCommandList.Pause, pauseRoot, pauseRow, Engine_UiModMenus.MenuPlayer,
			Engine_UiTemplates.MenuOwner(pauseRoot, pauseRow)) { View = _pauseView });
		if (ran.Outcome == Engine_UiCommandOutcome.NotOurs)
		{
			GD.Print($"[game-ui] pause entry '{action}' names no verb; nothing runs it");
		}

		return true;
	}

	/// <summary>
	/// Reports the world-icon channel — property income, gift gold, and every scripted
	/// `GUI.AddWorldIcon` — whenever its state changes.
	///
	/// THE CHANNEL EXISTS AND THE HOST HAS NEVER READ IT, which is why property income shows
	/// nothing however much gold is paid. Two gates decide whether the guest even queues a
	/// request (see <see cref="HostBridge.WorldIcons"/>), and which one is dark cannot be
	/// settled from the binary — it is a live fact. This names it once per change so the next
	/// run answers it: an enabled channel with a filling queue means the host simply has to
	/// draw them; a disabled one means the guest's own HUD binding never armed it.
	/// </summary>
	private void TraceWorldIcons()
	{
		if (!HostBridge.IsInGame || HostBridge.GetWorldIcons() is not { } icons)
		{
			return;
		}

		// Key first, string only on a change: two bits and two counts, packed. Runs on every
		// guest frame in game.
		long state = ((long)icons.Queued << 34) | ((long)icons.Showing << 2) |
			(icons.Enabled ? 2L : 0L) | (icons.IncomeIcons ? 1L : 0L);
		if (state == _worldIconState)
		{
			return;
		}

		_worldIconState = state;
		GD.Print($"[game-ui] world icons: channel {(icons.Enabled ? "ENABLED" : "DISABLED — the guest's HUD never bound registry 7")}, " +
			$"income icons {(icons.IncomeIcons ? "on" : "OFF — ShowIncomeIcon is clear, so a payout is never labelled income")}, " +
			$"{icons.Queued} queued, {icons.Showing} LIT on the guest's own elements" +
			(icons.Queued > 0
				? $" (first type {icons.Types[0]}{(icons.Types[0] == 37 ? " = property income" : icons.Types[0] == 36 ? " = gold change" : string.Empty)})"
				: string.Empty));
	}

	private long? _worldIconState;

	/// <summary>
	/// The anchor each preview screen hangs its object on — the guest's own dotted paths.
	/// </summary>
	private static string PreviewAnchor(uint screenId) =>
		Engine_UiTemplates.Screen(screenId) is { PreviewAnchor.Length: > 0 } template
			? template.PreviewAnchor
			: "Foreground.Book.Pages.Page1.Contents.ModelHider.model_proxy.model";

	/// <summary>
	/// Draws the 3D object a menu panel shows — the item in the ring that has been drawing
	/// empty.
	///
	/// THE GUEST NAMES THE MODEL AND WE DRAW IT: `RefiiHostGetUiPreviewObject` returns the
	/// resource hash it resolved plus the artist's offsets, and the anchor is a real element
	/// of the drawn screen, so parenting the mesh there puts it in the panel's own space with
	/// the panel's own camera — no world placement to reproduce and no render target.
	///
	/// The SPIN rides in for free: the guest writes it as `rotation.x/y/z` on that anchor
	/// element every frame, so the live-transform read has already applied it to the node we
	/// parent to.
	/// </summary>
	/// <summary>The key over the last preview-gate line; null before the first one.</summary>
	private long? _previewGate;

	/// <summary>The clothing book (`equipmentscreen.bgf`), the one item book that dresses a mannequin.</summary>
	private const uint EquipmentScreenId = 16;

	/// <summary>
	/// AN OUTFIT IS SHOWN ON THE MANNEQUIN. Screen 16 spawns a `CreatureHeroMannequin*` at open,
	/// cloned from the hero in the hero's clothes, and the original's ring never shows anything
	/// else; an outfit has no model of its own (most state none, the DLC ones a single piece).
	/// The highlighted outfit is tried on it through the pump (`Inventory:tryon`, the mechanism
	/// the guest's own dye mode uses on highlight), and put back on leaving the outfit rows.
	/// Single garments keep the item-alone view. Null when the row is not an outfit.
	/// </summary>
	private HostBridge.UiPreviewObject? OutfitTryOn(Engine_UiExpandableMenu.Item? row)
	{
		const string outfit = Engine_UiInventorySource.InventoryAction + "outfit:";
		uint record = row?.Command is { } command &&
			command.StartsWith(outfit, System.StringComparison.Ordinal) &&
			uint.TryParse(command[outfit.Length..], out uint parsed)
				? parsed
				: 0u;

		// Keyed on the BODY too: the screen spawns a fresh mannequin, in the hero's clothes, every
		// time it opens, and the first outfit highlighted after a reopen must still be tried on.
		uint body = HostBridge.ClothingMannequin() ?? 0u;
		if (body != 0u && (record != _triedOn || body != _triedOnBody))
		{
			HostBridge.PostFrontEndAction($"{Engine_UiInventorySource.InventoryAction}tryon:{record}");
		}

		_triedOn = record;
		_triedOnBody = body;
		if (record == 0u || body == 0u)
		{
			return null;
		}

		uint mannequin = body;

		// Turned a quarter to face the viewer, as the co-op body is: the composed body's
		// forward is +X. The scale is the panel's fit of a standing figure.
		return new HostBridge.UiPreviewObject(
			EquipmentScreenId, 0u, mannequin, Vector3.Zero,
			new Vector3(0f, Mathf.Pi / 2f, 0f), MannequinScale, 0);
	}

	/// <summary>The outfit record last tried on the mannequin; 0 = the hero's own clothes.</summary>
	private uint _triedOn;

	/// <summary>The mannequin that try-on was posted for.</summary>
	private uint _triedOnBody;

	/// <summary>The co-op page's figure scale (`(2.1 / body) * 350` canvas units, fitted).</summary>
	private const float MannequinScale = 121f;

	private void DrivePreviewObject()
	{
		// THIS ONE LIVES INSIDE THE BUILT TREE ON PURPOSE — it hangs off the anchor element so
		// the guest's per-frame `rotation.x/y/z` on that anchor spins it for free. The cost is
		// that a rebuild frees it under us, so the reference must be re-validated rather than
		// trusted; the owned panel threw once a frame for exactly this reason before it was
		// moved to the host. Dropping it here simply rebuilds it on the next frame.
		if (_previewObject is not null && !GodotObject.IsInstanceValid(_previewObject))
		{
			_previewObject = null;
		}

		// THE GUEST'S SPAWNED OBJECT FIRST, THEN THE ITEM'S OWN RECORD.
		//
		// The bridge reads a struct the guest fills on ITS highlight-change edge, and that edge
		// stops firing once Godot owns the cursor — so on the item books it answered "nothing
		// spawned" and the ring drew empty. The placement is shipped GDB
		// (`GUIScreenMesh` and its offsets), so the host resolves it for the row the player is
		// actually on; the guest's answer still wins where it has one, because a screen that
		// spawns a real entity knows things the record does not.
		HostBridge.UiPreviewObject? spinning =
			IsRevealed ? HostBridge.GetUiPreviewObject() : null;
		if (spinning is null && IsRevealed && _screenMenu is { } menu)
		{
			// THE SCREEN THAT IS UP, not the list's own id: the item books' rows ride in under
			// `capture:Root`, whose list answers screen 0, so a gate on the list's id never saw
			// a book. The pause state names the screen; the list only supplies the cursor.
			uint up = HostBridge.GetPauseState() is { ScreenId: not 0 } state
				? state.ScreenId
				: menu.ScreenId;
			// Key first, string only on a change; runs every frame a screen list stands. The
			// provider is looked up once and reused below. See Engine_HudValues.DescribeKey.
			string? provider = Engine_UiTemplates.Screen(up)?.Provider;
			string? row = menu.Script.Highlighted?.Name;
			long gate = 17L;
			gate = (gate * 1000003L) ^ up;
			gate = (gate * 1000003L) ^ menu.ScreenId;
			gate = (gate * 1000003L) ^ (provider?.GetHashCode() ?? 0);
			gate = (gate * 1000003L) ^ (row?.GetHashCode() ?? 0);
			if (_previewGate != gate)
			{
				_previewGate = gate;
				GD.Print($"[game-ui] preview gate: screen {up} (list {menu.ScreenId}) " +
					$"provider '{provider}' row '{row}'");
			}

			if (up == EquipmentScreenId)
			{
				spinning = OutfitTryOn(menu.Script.Highlighted);
			}

			if (spinning is null && provider == Engine_UiPageProviders.ItemBook)
			{
				spinning = Engine_UiItemPage.Preview(up, menu.Script.Highlighted);
			}
		}

		if (spinning is not { } preview)
		{
			if (_previewObject is not null)
			{
				_previewObject.Visible = false;
			}

			// THE SILENT CASE, said out loud once. The bridge answers nothing both when no
			// screen with a preview is up (the normal state) and when it failed to reach the
			// handler, and until this line the two were the same empty log.
			if (IsRevealed && HostBridge.IsInGame &&
				HostBridge.GetPauseState() is { ScreenId: not 0 } up && _previewNone != up.ScreenId)
			{
				_previewNone = up.ScreenId;
				GD.Print($"[game-ui] screen {up.ScreenId} is up and the bridge reports no " +
					"preview object — either the screen has none or its handler was not reached");
			}

			return;
		}

		_previewNone = null;

		// The anchor lives in whichever mirrored screen is drawn; without it there is nowhere
		// to put the object, so the panel stays empty rather than the model floating.
		//
		// RESOLVED THE ENGINE'S WAY, not as a Godot node path. `getElement('Foreground.Book…')`
		// searches for the first segment and walks the rest as children, and a built
		// presentation wraps the artist's tree in the component and layer nodes the file
		// declares — `Component_0/Layer/Foreground/…`. A rigid relative path from the
		// presentation root therefore matched NOTHING, which is why this returned silently on
		// every screen that has a preview and the ring drew empty.
		// THE GUEST'S OWN PRESENTATION FOR THIS SCREEN FIRST. Every in-game book screen has an
		// anchor at the same authored path, so a search over the drawn set finds the previous
		// screen's — and hangs the model in a panel the player is not looking at.
		string path = PreviewAnchor(preview.ScreenId);
		Node3D? anchor = null;
		uint anchorAddress = 0;
		Engine_UiScreen? anchorScreen = null;
		uint owner = PresentationFor(preview.ScreenId);

		// THE SCREEN'S OWN BOOK FIRST, when it has one. A book that is standing has taken its
		// mirrored tree off screen, and hanging the model in a hidden tree hides the model with
		// it — the panel would be empty for no visible reason. The book's clones carry the same
		// authored paths, so the anchor is the same name in a different tree.
		if (_books.Book(preview.ScreenId) is { } book &&
			IsInstanceValid(book) && book.Visible &&
			FindAnchor(book, path) is { } bookAnchor)
		{
			anchor = bookAnchor;
			anchorAddress = owner;
			anchorScreen = owner != 0 && _drawn.TryGetValue(owner, out Engine_UiDrawnPresentation? bookDrawn)
				? bookDrawn.Screen
				: null;
		}

		if (anchor is null && owner != 0 && _drawn.TryGetValue(owner, out Engine_UiDrawnPresentation? owned) &&
			Engine_UiTheme.FindByPath(owned.Node, path) is { } ownedAnchor)
		{
			anchor = ownedAnchor;
			anchorAddress = owner;
			anchorScreen = owned.Screen;
		}

		foreach (var (address, drawn) in _drawn)
		{
			if (anchor is not null)
			{
				break;
			}

			if (FindAnchor(drawn.Node, path) is { } found)
			{
				anchor = found;
				anchorAddress = address;
				anchorScreen = drawn.Screen;
				break;
			}
		}

		// AN OWNED SCREEN HAS NO DRAWN PRESENTATION, which is why screen 8's preview reported
		// its anchor "in none of the drawn presentation(s)" every time a gift arrived. The
		// received-item card is built from the PATTERN BOOK rather than mirrored, so
		// `Foreground.SpinningCube.Cube` lives inside the owned view and the search over
		// `_drawn` can never reach it. Every screen we take over moves its anchors out of that
		// collection, so the owned views have to be searched too.
		if (anchor is null && _surfaces.ReceiveItems.View is { } card &&
			GodotObject.IsInstanceValid(card))
		{
			anchor = FindAnchor(card, path);
		}

		if (anchor is null)
		{
			if (_previewObject is not null)
			{
				_previewObject.Visible = false;
			}

			// Named once per change: the guest states a model and we have nowhere to hang it,
			// which is a different failure from the guest naming none — and the two are
			// indistinguishable on screen.
			// The pair keyed rather than spelled: a screen with a preview and no anchor takes
			// this branch every frame it stands.
			long missing = ((long)preview.ScreenId << 32) | preview.ModelHash;
			if (_previewAnchorMissing != missing)
			{
				_previewAnchorMissing = missing;
				GD.Print($"[game-ui] preview object on screen {preview.ScreenId} names " +
					$"0x{preview.ModelHash:X8} but '{path}' is in none of the " +
					$"{_drawn.Count} drawn presentation(s) nor the owned views — " +
					"panel left empty");
			}

			return;
		}

		_previewAnchorMissing = null;

		_previewObject ??= new Engine_UiPreviewObject { Name = "PreviewObject" };
		if (_previewObject.GetParent() != anchor)
		{
			_previewObject.GetParent()?.RemoveChild(_previewObject);
			anchor.AddChild(_previewObject);
		}

		// THE ARTIST'S PANEL, so the object cannot paint over the screen it sits in. The ring
		// is a real quad in the anchor's own ancestry — `ModelHider` on the book screens,
		// `SpinningCube` on the card — and its canvas extent is what the model is drawn to fit.
		_previewObject.PanelSize = PanelExtent(anchor);

		// THE EQUIPMENT SCREEN'S OFFSET IS A MANNEQUIN OFFSET. On screen 16 the guest shows the
		// hero WEARING the item (`TypeSpecific`'s Hero slides, `sub_826D6C90`), and a garment's
		// `GUIScreenTranslationOffset` is where it sits on that standing body — the soldier
		// helmet states (0, -9.6, -559.7), 560 canvas units below the anchor. The remaster
		// shows the item alone in the ring (the provider states the Item slide), so the
		// offset is dropped there and the item hangs at the anchor.
		Vector3 translation = preview.ScreenId == EquipmentScreenId
			? Vector3.Zero
			: preview.Translation;

		uint was = _previewObject.ModelHash;
		bool live = preview.ScreenId == EquipmentScreenId && preview.ModelHash == 0u && preview.Entity != 0u;
		// Hung by its origin wherever the guest's own placement stands (`sub_828C3C78` reads no
		// bounds); centred where the placement is ours — the live body, and screen 16's
		// item-alone view, whose stated offset was dropped above.
		var placement = new Engine_UiPreviewObject.Placement(
			translation, preview.Rotation, preview.Scale, preview.RotationAxis,
			Hung: !live && preview.ScreenId != EquipmentScreenId);
		bool changed = live ? _previewLiveShown != preview.Entity : was != preview.ModelHash;
		bool shown;
		if (live)
		{
			// The body comes from the entity feed's node for that address — GameEntities lives
			// under GameWorld, a sibling of this UI under GameWindow. Its mesh is read, never
			// re-parented: the feed still owns and drives the node it built.
			MeshInstance3D? feedNode = GetParent()?.GetNodeOrNull<GameWorld>("GameWorld")?.Entities?
				.NodeFor(preview.Entity);
			Mesh? body = feedNode?.Mesh;
			Skeleton3D? rig = feedNode is not null && !feedNode.Skeleton.IsEmpty
				? feedNode.GetNodeOrNull<Skeleton3D>(feedNode.Skeleton)
				: null;
			shown = _previewObject.ShowLive(body, preview.Entity, placement, rig, feedNode?.Skin);
			_previewLiveShown = shown ? preview.Entity : 0u;
		}
		else
		{
			_previewLiveShown = 0u;
			// A dyed garment shows dyed: the guest's own dye builder states the item's layers.
			shown = _previewObject.Show(
				preview.ModelHash, placement,
				preview.Item != 0u ? HostBridge.GetItemDyeLayers(preview.Item, out _) : null);
		}

		if (changed)
		{
			GD.Print($"[game-ui] preview object on screen {preview.ScreenId}: " +
				(live ? $"live entity 0x{preview.Entity:X8}" : $"0x{preview.ModelHash:X8}") +
				$" — {_previewObject.LastReport}" +
				$"{(shown ? $"; {_previewObject.Measurement}" : " (panel left empty)")}");
		}

		// The turn is handed to the preview as the spinner's `rotation.y`; how that lands —
		// the axis swizzle and the order against the record's offset — is the preview's
		// (`Engine_UiPreviewObject.Stated`, from `sub_828C3C78`).
		//
		// THE PLAYER TURNS IT. The guest's spinner (`sub_828C37C8`) free-runs 0.01 rad per
		// GUI tick and, on the stick (event 27, `sub_828C3910`), moves its target by
		// -0.08·x³ and pauses the free spin for 3 s, easing at 4·dt. Here the free spin is
		// dropped — the stick alone turns the model, with the guest's own cubic response and
		// easing — and a new model starts from its record's pose. Read only while the model is
		// on screen, so the page-scroll stick on the other page is untouched.
		if (changed)
		{
			// From the record's own pose: `GUIScreenRotationOffset` is the artist's facing, and
			// the guest's spinner starts from zero over it.
			_previewYaw = PreviewStartYaw;
			_previewYawTarget = PreviewStartYaw;
		}

		if (shown && _previewObject.IsVisibleInTree())
		{
			float step = (float)GetProcessDeltaTime();
			float x = GameInput.RightStickX;
			_previewYawTarget += x * x * x * PreviewStickDegreesPerSecond * step;

			// THE MOUSE TURNS IT TOO: a press anywhere in its ring grabs it and the drag turns
			// it, a degree per canvas unit, for as long as the button is held.
			if (Engine_UiPointer.Canvas is { } turnAt)
			{
				if (_previewDragX is { } fromX)
				{
					if (Input.IsMouseButtonPressed(MouseButton.Left))
					{
						_previewYawTarget += turnAt.X - fromX;
						_previewDragX = turnAt.X;
					}
					else
					{
						_previewDragX = null;
					}
				}
				else if (Engine_UiPointer.LeftPressed &&
					(PreviewRingHas(turnAt) ||
						(Engine_UiPointer.CanvasBounds(_previewObject) is { } model &&
							model.Grow(20f).HasPoint(turnAt))))
				{
					Engine_UiPointer.TakeLeft();
					_previewDragX = turnAt.X;
				}
			}
			_previewYaw += (_previewYawTarget - _previewYaw) * Mathf.Min(1f, PreviewEaseRate * step);
			_previewObject.Spin(_previewYaw);

			if (x != 0f && Time.GetTicksMsec() - _previewYawSaid > 1000)
			{
				_previewYawSaid = Time.GetTicksMsec();
				GD.Print($"[game-ui] preview spin: stick {x:F2} target {_previewYawTarget:F0}° " +
					$"yaw {_previewYaw:F0}° step {step * 1000f:F0}ms");
			}
		}
	}

	private ulong _previewYawSaid;

	/// <summary>Where the drag turning the preview was last frame, in canvas X; null when none.</summary>
	private float? _previewDragX;

	private const float PreviewStartYaw = 0f;

	private float _previewYaw;

	private float _previewYawTarget;

	/// <summary>0.08 rad per 30 Hz GUI tick at full deflection, in degrees per second.</summary>
	private const float PreviewStickDegreesPerSecond = 0.08f * 30f * 180f / Mathf.Pi;

	/// <summary>`flt_8209B46C` = 4.0, the spinner's easing rate per second.</summary>
	private const float PreviewEaseRate = 4f;

	/// <summary>
	/// The canvas extent of the authored frame an anchor hangs inside, or 0 when it has none.
	///
	/// Walks UP from the anchor and takes the largest quad found beside it on the way — the
	/// ring, the window, the card back. A quad's canvas size is its accumulated basis scale
	/// times the builder's quad base size, which is the same arithmetic the shader's own
	/// detail mapping does. Stops at the first ancestor that has one, so a screen-wide
	/// backdrop further up can never become the "panel".
	/// </summary>
	private static float PanelExtent(Node3D anchor)
	{
		for (Node? at = anchor.GetParent(); at is Node3D; at = at.GetParent())
		{
			float largest = 0f;
			Measure(at, ref largest);
			if (largest > 0f)
			{
				return largest;
			}
		}

		return 0f;

		static void Measure(Node node, ref float largest)
		{
			if (node is MeshInstance3D mesh && Engine_UiSceneBuilder.DescribeQuad(mesh).Painted)
			{
				Vector3 scale = mesh.GlobalTransform.Basis.Scale;
				float size = Mathf.Max(scale.X, scale.Y) * Engine_UiSceneBuilder.QuadBaseSize;
				if (size > largest)
				{
					largest = size;
				}
			}

			foreach (Node child in node.GetChildren())
			{
				Measure(child, ref largest);
			}
		}
	}

	private Engine_UiPreviewObject? _previewObject;

	/// <summary>The screen and model whose anchor was last reported missing, to print once.</summary>
	private long? _previewAnchorMissing;

	/// <summary>The screen last reported as answering no preview object, to print once.</summary>
	private uint? _previewNone;

	/// <summary>
	/// Applies the guest's OWN spawn conditions to our list.
	///
	/// The one thing a host-authored menu genuinely cannot invent. "Load Game" exists only when
	/// signed in with at least one save and "Language" only above one installed language — the
	/// guest decides both, and an entry we offer that it will not honour is worse than a
	/// missing one, because it looks like the game is broken rather than like the option is
	/// unavailable.
	/// </summary>
	private void ApplyMenuAvailability()
	{
		if (_menu is null || HostBridge.GetFrontEndModel() is not { } model)
		{
			return;
		}

		// THE GUEST'S OWN COUNT, and the second attempt at getting it.
		//
		// The first passed a hardcoded 0; the second passed `model.EnumeratedSaves`, the vector
		// at the front-end screen's +84/+88. Both kept "Load Game" off the menu unconditionally,
		// and the second only looked right — that vector is written in exactly one place,
		// `sub_82443048` at 0x826E03CC, INSIDE the dispatcher's own "Load Game" branch. It is
		// empty until the entry has already been chosen, so it can never be the thing that
		// decides whether to offer it. The guest's spawn guard calls `sub_82443308` instead, and
		// so now do we, through the tick pump.
		//
		// The languages come from HERE, not from the model, because reading them reads guest
		// memory — and this is the one place that already knows a guest is up, having just got a
		// front-end model out of it.
		_menu.ApplyAvailability(model, HostBridge.GetSaveState(),
			languages: HostBridge.GetLanguages());

		// HERE, not only at view creation: the pump's first subtitles sample lands on its own
		// schedule, and a marker applied once from an unsampled bridge stays blank for the
		// whole session. This path already runs on every front-end state change for exactly
		// this class of late answer.
		ApplySubtitlesEmphasis();
		_menuView?.Refresh();
	}

	/// <summary>
	/// Drives the Godot-owned menu from the Godot-owned input.
	///
	/// Per frame rather than event-driven because the auto-repeat curve is a TIMING model —
	/// the model wants the held direction and a delta and owns the rest of it. Reading discrete
	/// key events here instead would move the repeat timing into this class, which is the one
	/// piece of the feel that was read out of shipped Lua rather than chosen.
	/// </summary>
	/// <summary>
	/// How long an armed quit waits for the guest to reach the front end before disarming.
	/// </summary>
	private const double QuitToDesktopWindowSeconds = 120.0;

	/// <summary>
	/// A pause row asked to leave for the desktop and the guest is still saving and tearing the
	/// world down. See the latch at the top of <see cref="ui_processBody"/>.
	/// </summary>
	private bool _quitToDesktopPending;

	/// <summary>Seconds left on that latch.</summary>
	private double _quitToDesktopWindow;

	/// <summary>Set when the page path above already filled a book this frame.</summary>
	private bool _providerFilledThisFrame;

	/// <summary>The screen whose book the list path last filled, 0 when none.</summary>
	private uint _providerFilledScreen;

	/// <summary>
	/// EVERY OWNED SCREEN WITH A PROVIDER GETS ITS CHANNELS, not only one whose provider fills a
	/// PAGE.
	///
	/// The page path is gated on `ScreenPageOpen`, so a screen that declares a provider and has
	/// no page element never reached it. The co-op screen is exactly that: it named
	/// `Engine_UiPageProviders.Coop` and was never once asked for a value, which is why its
	/// profile block stayed dark and its X prompt kept the shipped caption while the sliders
	/// beside them looked right — those were the GUEST still writing them, not us.
	///
	/// Stands down when the page path already filled a book this frame, so nothing here can
	/// clobber the item, options or logbook pages.
	/// </summary>
	private void DriveOwnedScreenProvider()
	{
		bool filled = _providerFilledThisFrame;
		_providerFilledThisFrame = false;
		if (filled)
		{
			return;
		}

		uint upId = OwnedScreenId();
		if (upId != _providerFilledScreen)
		{
			_providerFilledScreen = 0u;
		}

		if (upId == 0u || !Engine_UiDetailPages.Owns(upId) || _books.Book(upId) is not { } book)
		{
			return;
		}

		// A screen the list path fills keeps its last stated channels while that path is
		// starved (a modal or the spell blade over it): refilling here from `default` samples
		// blanks the page behind the box.
		if (upId == _providerFilledScreen)
		{
			return;
		}

		Engine_UiExpandableMenu.Item? row = _screenMenu?.Script.Highlighted;
		book.HostValues = Engine_UiDetailPages.Values(
			upId, row,
			default, default, new(), default, default,
			HostBridge.GetOptions(), default,
			book.Book?.Strings, optionsPageOpen: false);
		book.HostActive = Engine_UiDetailPages.Active(upId, row);
		book.HostOpacity = Engine_UiDetailPages.Opacities(upId, row);
		book.HostPositions = Engine_UiDetailPages.Positions(upId, default, row);

		// X ON THE CO-OP SCREEN PUTS PLAYER TWO IN THEIR OWN HERO'S BODY — player two's X, as the
		// prompt stands on their page: the joiner's own pad online, player two's pad on the couch.
		// `MenuEquipPressed` is pad 0's X.
		//
		// The body's gender is the kit's where one describes player two, else the appearance
		// pickbox's (`CoopHeroBody:auto`). `sub_82389FB8` is the guest's own
		// `Player::ChangeEntityType`, proven live — the body swaps and the session survives.
		// The outfit, weapons, inventory and stats follow from the kit on the channel drive.
		bool playerTwoX = CoopSession.LocalCoop
			? GameInput.XTwoPressed
			: CoopDrivesRightPage && GameInput.MenuEquipPressed;
		if (upId == CoopScreenId && playerTwoX)
		{
			RequestCoopHeroBody(!Engine_UiPageProviders.CoopOwnHero);
		}
	}

	/// <summary>
	/// The screen whose page provider runs. The in-game pause state names it while the in-game
	/// GUI is up; a joiner arriving from the TITLE has the co-op screen under the front-end GUI
	/// instead, where the pause state is null — there the follower's presentation name says it.
	/// Without this, that joiner's page got no names, no X prompt and no mannequin.
	/// </summary>
	private uint OwnedScreenId() =>
		HostBridge.GetPauseState() is { ScreenId: not 0 and not 0xFFFFFFFFu } up
			? up.ScreenId
			: _showing == "coopscreen" ? CoopScreenId : 0u;

	/// <summary>
	/// Player two's side of the toggle: a DESIRED body,
	/// applied only when it differs from the one player two is in, so a repeated request is
	/// not a flip. ONLY AFTER A SAVE IS CHOSEN: the hero comes from the save the player picked
	/// with Y on this screen; until one is chosen there is no hero to switch to, and the prompt
	/// says so.
	/// </summary>
	private void RequestCoopHeroBody(bool ownHero)
	{
		if (ownHero == Engine_UiPageProviders.CoopOwnHero)
		{
			return;
		}

		if (ownHero && !Engine_UiPageProviders.CoopHeroAvailable)
		{
			GD.Print("[game-ui] co-op X — no save chosen yet; the hero comes from the chosen save");
			return;
		}

		SetCoopHeroBody(ownHero);
	}

	/// <summary>
	/// Puts player two in their own hero's body, or back in the pickbox henchman, on THIS
	/// instance. The restatement to the peer rides <see cref="DriveCoopHeroChannel"/>, which
	/// also dresses, arms and stats the new body from the kit once the guest has built it.
	///
	/// `pickbox` goes back to the exact henchman the pickbox is showing, out of `sub_8233C508`'s
	/// own table — a fixed name would quietly change their henchman on the way back.
	/// `sub_82389FB8` is the guest's own `Player::ChangeEntityType` and the swap is proven live.
	/// </summary>
	private void SetCoopHeroBody(bool ownHero)
	{
		Engine_UiPageProviders.CoopOwnHero = ownHero;
		_coopHeroApplied = ownHero;

		// A new body comes up armed with the henchman's defaults, in either direction: the
		// joiner's live sample waits for the kit's own weapons to be drawn again.
		_coopLiveSampling = false;

		// A new body is a new apply: player two's record goes to the pump again for it.
		_coopStatePostedTwo = 0u;

		string body = CoopHeroBodyAction(ownHero);
		_coopBodyPosted = body;
		_coopHeroPickedAt = Time.GetTicksMsec();

		// Told to the peer on this pass, not the next half-second restatement.
		_coopHeroRestate = 0.0;
		GD.Print($"[game-ui] co-op — player two to their {(ownHero ? "own hero" : "henchman")} ({body})");
		HostBridge.PostFrontEndAction(body);
	}

	/// <summary>
	/// The body action for a state. THE KIT'S GENDER WHERE A KIT DESCRIBES PLAYER TWO — Local on
	/// the joiner, Remote on the host — because the two pickboxes did not agree in one run
	/// ("Good Female" on the host, "Good Male" on the joiner). `auto` otherwise: the guest sets
	/// that pickbox from the chosen save's real Gender and Morality (`sub_8243AEF8`), so it is
	/// the authority until a kit arrives.
	/// </summary>
	private static string CoopHeroBodyAction(bool ownHero)
	{
		CoopHeroKit.Kit two = SessionRole == 2u ? CoopHeroKit.Local : CoopHeroKit.Remote;
		return !ownHero ? "CoopHeroBody:pickbox"
			: two.Known ? (two.Female ? "CoopHeroBody:female" : "CoopHeroBody:male")
			: "CoopHeroBody:auto";
	}

	/// <summary>
	/// What one page says about its player's hero while it crosses the wire — the same words on
	/// both machines. Each hero record travels from the machine that plays it (player one's from
	/// the host, player two's from the joiner) as acknowledged fragments (<see cref="CoopHeroState"/>),
	/// so the sending side reports the acknowledgement and the receiving side the fragments
	/// landed. Empty once the record is across, and always on one machine: local co-op reads player
	/// two's save in place.
	/// </summary>
	private static string CoopHeroStatus(bool playerTwo)
	{
		uint role = SessionRole;
		if (role == 0u || CoopSession.LocalCoop)
		{
			return string.Empty;
		}

		bool ours = playerTwo == (role == 2u);
		if (ours)
		{
			return !CoopHeroKit.Local.Known
				? (playerTwo ? "Select a save to bring your hero" : string.Empty)
				: CoopHeroState.Delivered ? "Hero ready"
				: $"Sending hero to {(playerTwo ? "player one" : "player two")}…";
		}

		(int have, int total) = CoopHeroState.Receiving;
		return total > 0 ? $"Receiving hero… {have}/{total}"
			: CoopHeroKit.Remote.Known ? "Hero ready"
			: "Waiting for hero…";
	}

	/// <summary>The session's role as the pump's numbers: 0 solo, 1 host, 2 client.</summary>
	private static uint SessionRole => CoopSession.Side switch
	{
		CoopSession.Role.Host => 1u,
		CoopSession.Role.Client => 2u,
		_ => 0u,
	};

	private CoopSession.Phase _coopPhaseSeen = CoopSession.Phase.Solo;

	private CoopSession.Role _coopSideSeen = CoopSession.Role.Solo;

	/// <summary>Whether local co-op was up last frame, so its end resets player two's hero.</summary>
	private bool _coopLocalHeroSeen;

	/// <summary>Local player two's record, read from their save: the version and its base64.</summary>
	private uint _localTwoVersion;

	private string? _localTwoBase64;

	/// <summary>
	/// Drops every piece of co-op state on both sides of the bridge: the pump's stored state
	/// (`CoopHeroReset`), the own-hero bit, the applied body, the live-sampling latch, the
	/// restate clock, the peer's record, the chosen save, both names, the HeroState versions,
	/// acks, fragment buffers and post latches. Everything restarts from the next poll, so a
	/// later join starts clean.
	/// </summary>
	private void ResetCoopHero(string why)
	{
		HostBridge.PostFrontEndAction("CoopHeroReset");
		_coopPartnerWaiting = false;
		_coopExitUseTaken = 0u;
		_coopExitRequestRestates = 0;
		Engine_UiPageProviders.CoopOwnHero = false;
		Engine_UiPageProviders.CoopHeroAvailable = false;
		Engine_UiPageProviders.CoopPlayerOneName = string.Empty;
		Engine_UiPageProviders.CoopPlayerTwoName = string.Empty;
		_coopHeroApplied = false;
		_coopLiveSampling = false;
		_coopBodyPosted = string.Empty;
		_localTwoBase64 = null;
		_coopHeroRestate = 0.0;
		_coopStatePostedOne = 0u;
		_coopStatePostedTwo = 0u;
		CoopHeroKit.ResetSession();
		CoopHeroState.Reset();
		ResetCoopMods();
		GD.Print($"[game-ui] co-op — {why}; remote hero, chosen save, own-hero bit, versions, acks and the peer's mods dropped");
	}

	/// <summary>
	/// Refii's own co-op channel, drained and restated once a frame.
	///
	/// THE MESSAGES ARE STATE, NOT EVENTS. The joiner restates "player two's body is X" and the
	/// host its held pairs on a timer for as long as the session lasts, so a dropped datagram
	/// costs one restatement rather than leaving the two instances rendering different
	/// creatures. The hero record itself is the one message too large for that: it crosses as
	/// a versioned, fragmented, acknowledged HeroState (<see cref="CoopHeroState"/>), resent
	/// only until the peer's pump reports the version applied.
	/// </summary>
	private void DriveCoopHeroChannel(double delta)
	{
		// THE SESSION MODEL FIRST — one read of the authoritative manager per frame; every
		// role decision below comes off it. On the frame the peer goes, the session drops to
		// solo or the side changes, everything learned about the peer goes too, so nothing
		// restates a torn-down body and a later join is clean.
		CoopSession.Poll();
		string? reset =
			_coopPhaseSeen == CoopSession.Phase.PeerJoined && CoopSession.State != CoopSession.Phase.PeerJoined
				? "the peer is gone"
			: _coopPhaseSeen != CoopSession.Phase.Solo && CoopSession.State == CoopSession.Phase.Solo
				? "the session ended"
			: _coopSideSeen != CoopSession.Side
				? $"the side changed ({_coopSideSeen} → {CoopSession.Side})"
			: null;

		// The same transitions, told to the player (GameUiCoopNotices) — before the reset drops
		// what they need.
		TellCoopTransitions(_coopPhaseSeen);
		reset ??= _coopLocalHeroSeen && !CoopSession.LocalCoop ? "local co-op ended" : null;
		_coopLocalHeroSeen = CoopSession.LocalCoop;
		if (reset is not null)
		{
			ResetCoopHero(reset);
		}

		_coopPhaseSeen = CoopSession.State;
		_coopSideSeen = CoopSession.Side;

		// SAMPLED EVEN OUTSIDE A SESSION, and deliberately first. Joining tears the joiner's own
		// world down, so their hero has to have been captured BEFORE the co-op screen ever asks
		// for it. See CoopHeroKit.
		_coopKitSample -= delta;
		if (_coopKitSample <= 0.0)
		{
			_coopKitSample = CoopKitSampleSeconds;

			// THE JOINER STOPS SAMPLING ONCE IN A SESSION. After the join, the "hero" slot the
			// capture reads is the HOST's hero — the copy the sync created, armed and naked —
			// not the joiner's own. Sampling on would overwrite the outfit captured before the
			// join with an empty one and ship that to the host as player two's clothes. The
			// host keeps sampling: its hero is player one, and that is what the joiner needs.
			// Record id → def name, off the host's own globals.gdb view — the same lookup the
			// inventory rows use. `Engine_` is reached from here, never from the host layer.
			// Where the saves are, by convention — `assets/save` under the app, the same
			// `user_data_root` the launcher writes into refii.toml.
			CoopHeroKit.UserDataRoot ??= System.IO.Path.Combine(Engine_AppRoot.AssetsDirectory, "save");

			// The shared database directly, not the inventory source's copy of it: that copy is
			// set on a screen-menu path the host may not have walked yet when the kit is built,
			// and both weapon ids resolved to '' on the host for exactly that reason.
			CoopHeroKit.RecordName ??= id =>
				(Engine_UiInventorySource.Globals ?? Refii.Gdb.Engine_GameGdb.Database) is { } globals &&
				globals.TryLookup(id, out GdbCursor cursor)
					? cursor.View.RecordName(cursor.Record)
					: string.Empty;

			// THE JOINER'S HERO IS THE ONE THEY WERE PLAYING. A joiner who loads a save and joins
			// from the pause menu has a live capture of that hero (`Sample` ran while the role was
			// still 0 or host); that capture is the kit, and nothing replaces it. The save picked
			// on the co-op screen is read only when there is NO capture — a join from the title,
			// where no hero was ever loaded. Reading the pick whenever its name changed shipped a
			// different hero with every cursor move on the save list (run 2026-10-07 22:07: Hero000
			// then Hero002, the host built the second), and the pick's weapon is the SAVE FILE's,
			// not the one the live hero holds.
			//
			// Once the body is player two's, the live sample is appearance only and starts at
			// once: Worn and Inventory keep their previous values while the fresh body reads
			// empty, and a weapon slot is taken only when it holds a record, so an early sample
			// cannot unarm or undress the kit.
			if (SessionRole != 2u)
			{
				CoopHeroKit.Sample();
			}
			else if (HostBridge.IsInGame && Engine_UiPageProviders.CoopOwnHero &&
				CoopHeroKit.Local.Known && HostBridge.HeroEntityAddress() != 0u)
			{
				if (!_coopLiveSampling)
				{
					_coopLiveSampling = true;
					GD.Print("[game-ui] co-op — the joiner's body is their own hero; sampling it " +
						"live from here (appearance only)");
				}

				CoopHeroKit.Sample(appearanceOnly: true);
			}

			// LOCAL CO-OP: player two's kit is the save they picked on the co-op screen, read the
			// joiner's way; a new pick is a new version of their record. Fable 3's couch rule: with a
			// save they play as their own hero (the body goes to it once the kit is read), without
			// one as the henchman.
			if (CoopSession.LocalCoop && CoopHeroKit.SampleLocalPlayerTwo())
			{
				_localTwoBase64 = System.Convert.ToBase64String(
					CoopHeroState.Serialize(CoopHeroKit.Remote, ++_localTwoVersion));
				GD.Print($"[game-ui] local co-op — player two's hero read from their save (v{_localTwoVersion})");
				if (!Engine_UiPageProviders.CoopOwnHero)
				{
					SetCoopHeroBody(true);
				}
			}

			if (SessionRole == 2u && (!CoopHeroKit.Local.Known || CoopHeroKit.ChosenSaveRead))
			{
				// No capture, or the kit came from a pick: the pick names the hero, and a
				// different pick replaces it. The read skips an unchanged name.
				CoopHeroKit.SampleChosenSave();
			}

			// PLAYER TWO'S OWN HERO IS OFFERED ONCE BOTH MACHINES HOLD IT: on the host when the
			// joiner's record has landed, on the joiner when the host has acknowledged it. Offering
			// it earlier swapped the body on one screen while the other still drew the henchman.
			// On the couch player two's record is read from their save in place (Remote, by
			// SampleLocalPlayerTwo) and nothing crosses a wire.
			Engine_UiPageProviders.CoopHeroAvailable = CoopSession.LocalCoop
				? CoopHeroKit.Remote.Known
				: SessionRole switch
				{
					1u => CoopHeroKit.Remote.Known,
					2u => CoopHeroKit.Local.Known && CoopHeroState.Delivered,
					_ => false,
				};
			Engine_UiPageProviders.CoopPlayerOneStatus = CoopHeroStatus(playerTwo: false);
			Engine_UiPageProviders.CoopPlayerTwoStatus = CoopHeroStatus(playerTwo: true);

			// Player one is the host, player two the joiner; each side knows its own name from
			// the node and the other's from RexNet's view of them (PartnerName).
			string own = CoopHeroKit.LocalName;
			string peer = PartnerName();
			bool joiner = SessionRole == 2u;
			Engine_UiPageProviders.CoopPlayerOneName = joiner ? peer : own;
			Engine_UiPageProviders.CoopPlayerTwoName = joiner ? own : peer;
		}

		// NOT GATED ON A ROLE. The role bytes come off the co-op manager, and the name kit has
		// to travel while the co-op screen is up, whatever those bytes say at that moment; the
		// channel answers to whichever peer's game traffic reached us. Only the APPLY steps
		// below need a side.
		uint role = SessionRole;

		// THE INBOX IS DRAINED EVERY FRAME AND READ ONLY WHILE A SESSION STANDS: a kit, a chunk
		// or a held pair that arrives after the peer has gone, or before a session exists,
		// belongs to no one and is dropped rather than stored against the next join.
		bool listening = CoopSession.State is CoopSession.Phase.Hosting or CoopSession.Phase.PeerJoined;
		while (HostBridge.CoopReceive() is { } message)
		{
			if (!listening)
			{
				continue;
			}

			// The players' mod values (core/player_state.h): the peer's statement of the player it
			// plays, or its writes to the one this machine plays.
			if (HostBridge.PlayerStateReceive(message))
			{
				continue;
			}

			// A bed's question for this machine's player, or the answer to this machine's (GameUiCoopConsent).
			if (TakeConsentMessage(message))
			{
				continue;
			}

			// The joiner's question about a villager's opinion of them, or the host's answer (GameUiCoopOpinion).
			if (TakeOpinionMessage(message))
			{
				continue;
			}

			// The joiner's real-estate action for its hero two, or the host's ack (GameUiCoopEstate).
			if (TakeEstateMessage(message))
			{
				continue;
			}

			// The host's dynamic weather, for this joiner to show (GameUiCoopWeather).
			if (TakeWeatherMessage(message))
			{
				continue;
			}

			// The host's notice about one of this joiner's buildings (GameUiPropertyTips).
			if (TakePropertyNoticeMessage(message))
			{
				continue;
			}

			// The joiner's Repair All in this host's world (Engine_UiPropertySource).
			if (message == Engine_UiPropertySource.RepairAllMessage)
			{
				if (!CoopSession.IsClient)
				{
					HostBridge.PostFrontEndAction("Property:repairall:2");
				}

				continue;
			}

			// The joiner's repair of one of its properties in this host's world (Engine_UiPropertySource).
			if (message.StartsWith(Engine_UiPropertySource.RepairMessage, System.StringComparison.Ordinal))
			{
				if (!CoopSession.IsClient)
				{
					HostBridge.PostFrontEndAction("Property:repair:2:" + message[Engine_UiPropertySource.RepairMessage.Length..]);
				}

				continue;
			}

			// The peer's mods record; compared below once the peer stands (GameUiCoopMods).
			if (TakeCoopMods(message))
			{
				continue;
			}


			if (message.StartsWith(CoopHeroState.FragmentPrefix, System.StringComparison.Ordinal))
			{
				// The peer's hero, one fragment of a version. Complete and decoded it is
				// `CoopHeroKit.Remote`; the pump post and the ack ride the tick below.
				CoopHeroState.Receive(message);
				continue;
			}

			if (message.StartsWith(CoopHeroState.AckPrefix, System.StringComparison.Ordinal))
			{
				CoopHeroState.Acknowledge(message);
				continue;
			}

			if (message.StartsWith(CoopExitUsePrefix, System.StringComparison.Ordinal))
			{
				// The joiner used an exit: `seq:x:y:z`, the exit's guest position. The host's exit
				// update fires the exit standing there, once per sequence.
				string[] parts = message[CoopExitUsePrefix.Length..].Split(':');
				if (role == 1u && parts.Length == 4 &&
					uint.TryParse(parts[0], System.Globalization.NumberStyles.Integer,
						System.Globalization.CultureInfo.InvariantCulture, out uint sequence) &&
					sequence != _coopExitUseTaken)
				{
					_coopExitUseTaken = sequence;
					HostBridge.PostFrontEndAction($"CoopExitUse:{parts[1]}:{parts[2]}:{parts[3]}");
				}

				continue;
			}

			if (message.StartsWith(CoopExitWaitingPrefix, System.StringComparison.Ordinal))
			{
				// The partner stands at an exit waiting for this machine's player: told once as
				// it begins (Fable 3's TEXT_..._BLOCKED_BY_PLAYER_DISTANCE_REMOTE).
				bool waiting = message[CoopExitWaitingPrefix.Length..] == "1";
				if (waiting && !_coopPartnerWaiting)
				{
					_boxes.InfoBoxEx(ExitRefusedBox, 5f, "Your partner is waiting for you at the exit.");
				}

				_coopPartnerWaiting = waiting;
				continue;
			}

			if (message.StartsWith("CoopHeroHeld:", System.StringComparison.Ordinal))
			{
				// The host's held weapons as net ids; the pump attaches the replicas here.
				if (role == 2u)
				{
					HostBridge.PostFrontEndAction(message);
				}

				continue;
			}

			if (!message.StartsWith(CoopHeroStatePrefix, System.StringComparison.Ordinal))
			{
				continue;
			}

			bool ownHero = message[CoopHeroStatePrefix.Length..] == "1";
			if (role == 1u && ownHero != _coopHeroApplied)
			{
				SetCoopHeroBody(ownHero);
			}
		}

		// THE MODS MUST AGREE. Checked every frame once a peer stands; a mismatch is refused on
		// this machine whichever side it is.
		CheckCoopMods(role);

		// THE PLAYERS' MOD VALUES, every frame: what changed goes at once (the store paces itself,
		// and restates every half second), but only to a standing peer.
		string[] playerState = HostBridge.PlayerStateOutgoing();
		if (CoopSession.PeerStanding)
		{
			foreach (string line in playerState)
			{
				HostBridge.CoopSend(line);
			}
		}

		if (role == 0u)
		{
			// Nothing to apply outside a session; the record goes out while the co-op screen is
			// up, so the peer's page can name this player before the role bytes land.
			_coopHeroRestate -= delta;
			if (_coopHeroRestate > 0.0 || OwnedScreenId() != CoopScreenId)
			{
				return;
			}

			_coopHeroRestate = CoopHeroRestateSeconds;
			SendCoopMods();
			CoopHeroState.Send(CoopHeroKit.Local, CoopHeroRestateSeconds);
			return;
		}

		// THE KIT'S GENDER REACHES THE BODY. A body posted as `auto` before the kit arrived, or
		// under a kit of the other gender, is rebuilt from the kit's: the pump re-types player
		// two and the restatement below dresses the new body.
		if (Engine_UiPageProviders.CoopOwnHero && CoopHeroBodyAction(true) != _coopBodyPosted)
		{
			GD.Print("[game-ui] co-op — player two's kit states a different body; restating it");
			SetCoopHeroBody(true);
		}

		// SENT ONLY INTO A SESSION. After the loss there is nobody to state anything to, and a kit
		// sent into that gap is one the next peer misreads as its own. The host states from the
		// moment its session is up — the joiner's co-op page names player one before the
		// joined byte lands — the joiner only once it stands.
		bool inSession = CoopSession.PeerStanding ||
			(role == 1u && CoopSession.State == CoopSession.Phase.Hosting);
		if (!inSession)
		{
			return;
		}

		// ONE TICK for the bit, the held pairs, the record and its ack.
		_coopHeroRestate -= delta;
		if (_coopHeroRestate > 0.0)
		{
			return;
		}

		_coopHeroRestate = CoopHeroRestateSeconds;

		// THE MODS RECORD FIRST, both ways: the peer compares it with its own the moment it stands.
		SendCoopMods();

		// Whether this machine's player waits at an exit for the partner (the travel rule).
		HostBridge.CoopSend(CoopExitWaitingPrefix + (HostBridge.ExitWaiting() ? "1" : "0"));

		// THE JOINER'S EXIT USE, restated for a few seconds so a dropped datagram costs nothing; the
		// host fires each sequence once.
		if (role == 2u)
		{
			(uint sequence, float x, float y, float z) = HostBridge.ExitRequest();
			if (sequence != 0u && sequence != _coopExitRequestSeq)
			{
				_coopExitRequestSeq = sequence;
				_coopExitRequestRestates = CoopExitRequestRestates;
			}

			if (_coopExitRequestRestates > 0)
			{
				--_coopExitRequestRestates;
				HostBridge.CoopSend(string.Create(System.Globalization.CultureInfo.InvariantCulture,
					$"{CoopExitUsePrefix}{sequence}:{x:R}:{y:R}:{z:R}"));
			}
		}

		// THE BIT IS THE JOINER'S TO STATE; THE KIT GOES BOTH WAYS. The joiner's kit dresses
		// player two on the host; the host's kit dresses player one on the joiner, whose copy of
		// the host's hero the sync creates without any clothes.
		if (role == 2u)
		{
			HostBridge.CoopSend(CoopHeroStatePrefix + (Engine_UiPageProviders.CoopOwnHero ? "1" : "0"));
		}

		// The host names the item entities both players hold, as net ids; the client attaches
		// the replicas (the pump's CoopAttachHeld). Idempotent state, restated — the zero pair
		// included, which says "nothing held" and the pump stores as such.
		if (role == 1u)
		{
			for (uint player = 1u; player <= 2u; ++player)
			{
				if (HostBridge.CoopHeldUids(player) is { } heldIds)
				{
					HostBridge.CoopSend($"CoopHeroHeld:{player}:{heldIds.Melee:X16},{heldIds.Ranged:X16}");
				}
				else if (player == 2u && Engine_UiPageProviders.CoopOwnHero &&
					(_coopHeldUnanswered += CoopHeroRestateSeconds) >= 5.0)
				{
					// Said while it lasts: a body the bridge cannot read is a joiner who never
					// gets their weapons (run 2026-10-07 22:07: no CoopHeroHeld:2 for a session).
					_coopHeldUnanswered = 0.0;
					(uint one, uint two) = HostBridge.CoopPlayerEntities();
					GD.Print($"[game-ui] co-op — player two's held ids are unanswered by the bridge " +
						$"(player entities one 0x{one:X8}, two 0x{two:X8})");
				}
			}
		}

		// THE RECORD GOES BOTH WAYS. The joiner's dresses player two on the host; the host's
		// dresses player one on the joiner, whose copy of the host's hero the sync creates
		// without any clothes. A changed record is a new version; an unacked one is resent.
		CoopHeroState.Send(CoopHeroKit.Local, CoopHeroRestateSeconds);
		PostHeroStates(role);
		AckHeroState();
	}

	/// <summary>
	/// Hands each player's HeroState to the pump as <c>HeroState:&lt;player&gt;:&lt;base64&gt;</c>, once
	/// per version per body. Player two's is the JOINER's record — Remote on the host, Local on
	/// the joiner, so the joiner's own copy of player two is dressed too — and goes only while
	/// player two is in their own hero's body: the apply dresses whatever body stands, and the
	/// pickbox henchman is not to be dressed. Player one's is the host's record, which only the
	/// joiner's copy needs; the pump refuses to re-give the host's own player one.
	/// </summary>
	private void PostHeroStates(uint role)
	{
		if (Engine_UiPageProviders.CoopOwnHero)
		{
			// Local co-op: player two's record is the one read from their save on this machine.
			(uint version, string? record, bool known) = CoopSession.LocalCoop
				? (_localTwoVersion, _localTwoBase64, CoopHeroKit.Remote.Known)
				: role == 1u
				? (CoopHeroState.ReceivedVersion, CoopHeroState.ReceivedBase64, CoopHeroKit.Remote.Known)
				: (CoopHeroState.Version, CoopHeroState.LocalBase64, CoopHeroKit.Local.Known);
			if (known && version != 0u && version != _coopStatePostedTwo && record is not null)
			{
				_coopStatePostedTwo = version;
				HostBridge.PostFrontEndAction("HeroState:2:" + record);
				GD.Print($"[game-ui] co-op — HeroState v{version} posted for player two " +
					$"({(role == 1u ? "the joiner's record" : "own record")})");
			}
		}

		if (role == 2u && CoopHeroKit.Remote.Known)
		{
			uint version = CoopHeroState.ReceivedVersion;
			if (version != 0u && version != _coopStatePostedOne && CoopHeroState.ReceivedBase64 is { } record)
			{
				_coopStatePostedOne = version;
				HostBridge.PostFrontEndAction("HeroState:1:" + record);
				GD.Print($"[game-ui] co-op — HeroState v{version} posted for player one (the host's record)");
			}
		}
	}

	/// <summary>
	/// Acknowledges the peer's record the moment it is complete and decoded. Acked again when the
	/// peer resends a version already acknowledged, which says the ack was lost.
	///
	/// ON RECEIPT, NOT ON APPLY. Waiting for the pump to apply the record to a body held the ack
	/// until player two was standing in their own hero — which the screen offers only once the
	/// record is across — so the sender resent a record the host already held, every second, and
	/// "Load Hero" could never appear. A resend could not have helped an apply anyway: a version
	/// already complete is not reassembled again. The record is kept (`ReceivedBase64`) and
	/// applied whenever its body stands (<see cref="PostHeroStates"/>).
	/// </summary>
	private static void AckHeroState()
	{
		uint received = CoopHeroState.ReceivedVersion;
		if (received != 0u && (CoopHeroState.AckedToPeer != received || CoopHeroState.AckDue))
		{
			CoopHeroState.SendAck();
		}
	}

	/// <summary>The joiner's statement of which body player two is in.</summary>
	private const string CoopHeroStatePrefix = "coop.hero=";


	/// <summary>`coop.exit.waiting=0|1`: the sender's player waits at a region exit for the partner.</summary>
	private const string CoopExitWaitingPrefix = "coop.exit.waiting=";

	/// <summary>The partner's last stated <see cref="CoopExitWaitingPrefix"/>.</summary>
	private bool _coopPartnerWaiting;

	/// <summary>`coop.exit.use=seq:x:y:z`: the joiner used the region exit at that guest position.</summary>
	private const string CoopExitUsePrefix = "coop.exit.use=";

	/// <summary>Restatements of one exit use (0.5 s apart).</summary>
	private const int CoopExitRequestRestates = 6;

	private uint _coopExitRequestSeq;
	private int _coopExitRequestRestates;
	private uint _coopExitUseTaken;

	/// <summary>How often the joiner restates it. Often enough that a drop is invisible.</summary>
	private const double CoopHeroRestateSeconds = 0.5;

	/// <summary>Seconds the bridge has answered nothing for player two's held items.</summary>
	private double _coopHeldUnanswered;

	/// <summary>What this instance last asked the guest for, so a restatement is not re-applied.</summary>
	private bool _coopHeroApplied;

	/// <summary>The last `CoopHeroBody:` action posted, so a kit stating another body re-posts it.</summary>
	private string _coopBodyPosted = string.Empty;

	/// <summary>Seconds until the next restatement.</summary>
	private double _coopHeroRestate;

	/// <summary>How often the local hero is captured, in a session or out of one.</summary>
	private const double CoopKitSampleSeconds = 2.0;

	/// <summary>Seconds until the next capture.</summary>
	private double _coopKitSample;

	/// <summary>The HeroState version last posted to the pump for player one (joiner only).</summary>
	private uint _coopStatePostedOne;

	/// <summary>The HeroState version last posted to the pump for player two, for the body posted.</summary>
	private uint _coopStatePostedTwo;


	/// <summary>
	/// An anchor by authored path, tolerant of WHERE the tree starts. `FindByPath` walks exact
	/// names from the first segment, and an owned book that clones its pages as separate chrome
	/// units (`LeftPage`, `RightPage` on the co-op screen) has no `Foreground` node above them —
	/// the full authored path then matches nothing while `RightPage.model_proxy.model` is right
	/// there. Shorter suffixes are tried in turn, so one path serves both tree shapes.
	/// </summary>
	private static Node3D? FindAnchor(Node root, string path)
	{
		string[] segments = path.Split('.');
		for (int from = 0; from < segments.Length; ++from)
		{
			if (Engine_UiTheme.FindByPath(root, string.Join('.', segments[from..])) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	/// <summary>
	/// Last reason the pause list was not standing, so it is said once: the three gate bits
	/// packed, or -1 while it stands.
	/// </summary>
	private int _pauseStandingSaid = -1;

	/// <summary>The co-op screen — id 24, `CoopScreen.bgf`.</summary>
	private const uint CoopScreenId = 24u;

	/// <summary>The live entity the preview panel last showed a body for; 0 on the hash path.</summary>
	private uint _previewLiveShown;

	public override void _Process(double delta)
	{
		using (Refii.Core.Engine_FramePhases.Measure("ui_process"))
		{
			// The pointer first, so every screen below reads the same position this frame.
			Engine_UiPointer.Resolve(_uiCamera, _viewport, GetViewport().GetVisibleRect().Size);
			Engine_UiCursor.Apply(DisplayServer.WindowGetSize().Y);

			GameInput.CoopScreenOwnsInput = OwnedScreenId() == CoopScreenId;
			PointerCoop();
			ui_processBody(delta);

			// What no screen took becomes the pad's: right click is always B and a wheel notch a
			// D-pad step. A left click acts only on what it lands on — a click on nothing is
			// nothing, never an A for whatever happens to be highlighted.
			GameInput.QueuePointerButtons(
				Engine_UiPointer.Free && Engine_UiPointer.RightPressed,
				Engine_UiPointer.Free ? Engine_UiPointer.WheelSteps : 0);
			Engine_UiPointer.EndFrame();
		}
	}

	/// <summary>The pause state as read on the last guest frame.</summary>
	private Engine_UiModel.PauseState? _modal;

	/// <summary>Seconds until the hero's text suffix is read again.</summary>
	private double _heroSuffixClock;

	/// <summary>
	/// The mouse on a list — the controller's carousel, driven by the mouse. Pointer MOVEMENT does
	/// nothing to it. A wheel notch over the list's column is a D-pad step. The scroll bar walks
	/// the highlight to the row it names. A click on a drawn row moves the highlight there and is
	/// the A press, once the carousel has let A through (`WaitForInput`). True on the frame that
	/// press is due — the caller treats it as accept.
	/// </summary>
	private static bool PointerList(Engine_FrontEndMenuView? view)
	{
		if (view is null)
		{
			return false;
		}

		// U08: Home / End jump to the first / last row — the scroll bar's own path (our cursor
		// walks there a step a frame; the guest's is sent the pad steps).
		if (Engine_UiInputMode.KeyboardMouse && view.Menu is not null && Engine_UiPointer.TakeJump() is int jump and not 0)
		{
			_clickView = null;
			_clickItem = null;
			BarTo(view, jump < 0 ? 0f : 1f, grab: true);
			GD.Print($"[ui-u11] {(jump < 0 ? "Home" : "End")} on list {view.Name}");
		}

		if (Engine_UiPointer.Canvas is not { } at)
		{
			// The pad took over: a click still waiting is dropped, never answered later.
			if (ReferenceEquals(_clickView, view))
			{
				_clickView = null;
				_clickItem = null;
			}

			view.Hover = null;
			BackPromptHover(null, false);
			return false;
		}

		// U11 / V4: the "Esc Back" prompt of the page lights under the mouse and a click on it is B.
		bool onBack = BackPromptAt(view, at);
		BackPromptHover(view, onBack);
		if (onBack)
		{
			view.Hover = null;
			if (Engine_UiPointer.TakeLeft())
			{
				_clickView = null;
				_clickItem = null;
				GameInput.QueuePointerButtons(true, 0);
				GD.Print("[ui-u11] click on the Back prompt -> B");
			}

			return false;
		}

		// THE SCROLL BAR: a press on it sends the highlight to the row under the pointer, and the
		// drag keeps sending it for as long as the button is held, wherever the pointer wanders.
		if (ReferenceEquals(_barDrag, view))
		{
			view.Hover = null;
			if (Input.IsMouseButtonPressed(MouseButton.Left))
			{
				BarTo(view, view.ScrollBarPositionAt(at), grab: false);
				return false;
			}

			_barDrag = null;
		}

		if (view.ScrollBarAt(at) is { } barPosition)
		{
			view.Hover = null;
			if (Engine_UiPointer.TakeLeft())
			{
				_barDrag = view;
				_clickView = null;
				_clickItem = null;
				BarTo(view, barPosition, grab: true);
			}

			WheelToPad();
			return false;
		}

		// A list on the left page never answers the pointer over the right one: that half is
		// the detail page's, to scroll and to turn the preview.
		if (view.GlobalPosition.X < 0f && at.X >= 0f)
		{
			view.Hover = null;
			return ClickDue(view);
		}

		Engine_UiExpandableMenu.Item? item = view.EntryAt(at);

		// U11 / V2: the hover mark follows the mouse only while the MOUSE is what moved last:
		// arrows or the wheel sliding the carousel under a resting pointer drop it until the
		// mouse moves again, so it never marks a row the player did not point at.
		int highlightIdx = view.Menu?.Model.CurrentHighlightIdx ?? 0;
		if (!ReferenceEquals(_hoverView, view))
		{
			_hoverView = view;
			_hoverArmed = Engine_UiPointer.Moved;
		}
		else if (Engine_UiPointer.Moved)
		{
			_hoverArmed = true;
		}
		else if (highlightIdx != _hoverLastIdx)
		{
			_hoverArmed = false;
		}

		_hoverLastIdx = highlightIdx;
		view.Hover = _hoverArmed ? item : null;

		// The wheel over the list's column — on a row or between them — is the pad's D-pad step,
		// handed to it here so nothing else this frame claims a notch that landed on the list.
		// The pad clamps at the ends exactly as it does for the stick.
		if (item is not null || view.ColumnHas(at))
		{
			WheelToPad();
		}

		if (item is not null && Engine_UiPointer.TakeLeft())
		{
			_clickView = view;
			_clickItem = item;
			_clickPointed = false;
			_clickAt = Time.GetTicksMsec();
		}

		return ClickDue(view);
	}

	/// <summary>Hands this frame's wheel notches to the pad as D-pad steps.</summary>
	private static void WheelToPad()
	{
		if (Engine_UiPointer.WheelSteps != 0)
		{
			GameInput.QueuePointerButtons(false, Engine_UiPointer.TakeWheel());
		}
	}

	/// <summary>
	/// The highlight to the row a 0..1 place on the bar names. Our cursor walks there a step a
	/// frame (<see cref="Engine_FrontEndMenuView.SeekTo"/>); the guest's is sent the D-pad steps
	/// that take it there, counted from where the grab found it.
	/// </summary>
	private static void BarTo(Engine_FrontEndMenuView view, float position, bool grab)
	{
		if (view.Menu is null)
		{
			return;
		}

		if (!view.GuestCursor)
		{
			view.ScrollTo(position);
			return;
		}

		if (grab)
		{
			GameInput.ClearPointerSteps();
			_barStart = view.Menu.Model.CurrentHighlightIdx;
			_barSent = 0;
		}

		int send = Engine_UiCarouselBar.StepsToSend(_barStart, view.RowAtBar(position), _barSent);
		if (send != 0)
		{
			GameInput.QueuePointerSteps(send);
			_barSent += send;
		}
	}

	/// <summary>
	/// Whether a click on <paramref name="view"/>'s row is due as A this frame. The click first
	/// moves the highlight onto its row; the press follows once the carousel's press gate is down,
	/// since A mid-cascade is swallowed whole. Dropped when its row is gone, when something else
	/// moved the highlight off it, or after <see cref="ClickPatienceMs"/>.
	/// </summary>
	private static bool ClickDue(Engine_FrontEndMenuView view)
	{
		if (!ReferenceEquals(_clickView, view) || _clickItem is not { } item)
		{
			return false;
		}

		if (view.Menu is null || view.Menu.Model.VisibleIndexOf(item) == 0 ||
			Time.GetTicksMsec() - _clickAt > ClickPatienceMs)
		{
			_clickView = null;
			_clickItem = null;
			return false;
		}

		if (view.PressGated)
		{
			return false;
		}

		if (!ReferenceEquals(view.Menu.Highlighted, item))
		{
			if (_clickPointed)
			{
				_clickView = null;
				_clickItem = null;
				return false;
			}

			view.PointAt(item);
			_clickPointed = true;
			if (view.PressGated || !ReferenceEquals(view.Menu.Highlighted, item))
			{
				return false;
			}
		}

		_clickView = null;
		_clickItem = null;
		return true;
	}

	/// <summary>The list a click is waiting on, and the row it landed on.</summary>
	private static Engine_FrontEndMenuView? _clickView;

	private static Engine_UiExpandableMenu.Item? _clickItem;

	/// <summary>The waiting click has already moved the highlight onto its row.</summary>
	private static bool _clickPointed;

	private static ulong _clickAt;

	/// <summary>How long a click waits on the carousel before it is dropped.</summary>
	private const ulong ClickPatienceMs = 1500;

	/// <summary>Where the bar's grab found the guest's cursor, and the steps sent since.</summary>
	private static int _barStart;

	private static int _barSent;

	/// <summary>The left button went down on the expression wheel and has not come up.</summary>
	private bool _wheelMouseDown;
	private bool _wheelSeenOpen;
	private int _wheelHiddenFrames;
	private bool _wheelPointerLogged;

	/// <summary>
	/// U09: the expression wheel on the canvas, the union of its drawn pieces. The HUD cuts the
	/// wheel (`Layer/wheelmenu`) into pieces named by id, `group.ExpressionWheel.*`; no node is
	/// named ExpressionWheel, which is why the pointer never reached the wheel before.
	/// </summary>
	private Rect2? WheelCanvasBounds(out int pieces)
	{
		pieces = 0;
		if (_hud is null || !GodotObject.IsInstanceValid(_hud))
		{
			return null;
		}

		Rect2? union = null;
		foreach ((string id, Node3D node) in _hud.DrawnPieces())
		{
			if (!id.StartsWith("group.ExpressionWheel", System.StringComparison.Ordinal))
			{
				continue;
			}

			if (Engine_UiPointer.CanvasBounds(node) is { } box)
			{
				pieces++;
				union = union is { } u ? u.Merge(box) : box;
			}
		}

		return union;
	}

	/// <summary>The list whose scroll bar the left button is dragging, or null.</summary>
	private static Engine_FrontEndMenuView? _barDrag;

	// ---- U11 / V2: the list hover mark only while the mouse moved last ----
	private static Engine_FrontEndMenuView? _hoverView;
	private static bool _hoverArmed;
	private static int _hoverLastIdx = -1;

	// ---- U11 / V4: the page's "Esc Back" prompt answers the mouse ----

	/// <summary>A view's back prompt: the B glyph and the word beside it.</summary>
	private sealed class BackPrompt
	{
		public MeshInstance3D? Glyph;
		public MeshInstance3D? Label;
		public ulong RetryFrame;
		public bool Logged;
	}

	private static readonly System.Collections.Generic.Dictionary<ulong, BackPrompt> _backPrompts = new();

	/// <summary>The prompt lit now, and what it looked like before.</summary>
	private static BackPrompt? _backLit;
	private static Color _backLitColour;
	private static Vector3 _backLitScale;

	private const float BackGlyphHoverScale = 1.12f;

	/// <summary>Whether the canvas point is on the view's drawn back prompt (glyph + word).</summary>
	private static bool BackPromptAt(Engine_FrontEndMenuView view, Vector2 at)
	{
		if (!Engine_FrontEndMenuView.HoverEnabled || BackPromptFor(view) is not { Glyph: { } glyph } prompt ||
			!glyph.IsVisibleInTree() || Engine_UiPointer.CanvasBounds(glyph) is not { } box)
		{
			return false;
		}

		if (prompt.Label is { } label && GodotObject.IsInstanceValid(label) && label.IsVisibleInTree() &&
			Engine_UiPointer.CanvasBounds(label) is { } words)
		{
			box = box.Merge(words);
		}

		return box.Grow(6f).HasPoint(at);
	}

	/// <summary>
	/// The view's back prompt, found once by climbing its ancestors to the nearest one that holds
	/// a drawn <c>icon_button_b</c>; retried every half second while the page has none drawn.
	/// </summary>
	private static BackPrompt? BackPromptFor(Engine_FrontEndMenuView view)
	{
		ulong id = view.GetInstanceId();
		if (!_backPrompts.TryGetValue(id, out BackPrompt? prompt))
		{
			prompt = new BackPrompt();
			_backPrompts[id] = prompt;
		}

		if (prompt.Glyph is { } held && GodotObject.IsInstanceValid(held) && held.IsInsideTree())
		{
			return prompt;
		}

		prompt.Glyph = null;
		prompt.Label = null;
		ulong frame = Engine.GetProcessFrames();
		if (frame < prompt.RetryFrame)
		{
			return null;
		}

		prompt.RetryFrame = frame + 30;
		Node? level = view;
		for (int up = 0; up < 7 && level is not null; ++up, level = level.GetParent())
		{
			if (FindDrawn(level, "icon_button_b") is not { } glyph)
			{
				continue;
			}

			prompt.Glyph = glyph;
			prompt.Label = NearestLabel(glyph);
			if (!prompt.Logged)
			{
				prompt.Logged = true;
				// The band's other pieces, to see whether a tabbed page draws LB/RB (V4 legend).
				var band = new System.Collections.Generic.List<string>();
				if (glyph.GetParent() is { } parent)
				{
					for (int i = 0, count = parent.GetChildCount(); i < count; ++i)
					{
						if (parent.GetChild(i) is Node3D { Visible: true } piece)
						{
							band.Add(piece.Name);
						}
					}
				}

				GD.Print($"[ui-u11] back prompt found for {view.Name}: {glyph.GetPath()} " +
					$"(word: {(prompt.Label is null ? "none" : prompt.Label.Name)}; band: {string.Join(", ", band)})");
			}

			return prompt;
		}

		if (!prompt.Logged)
		{
			prompt.Logged = true;
			GD.Print($"[ui-u11] back prompt not found for {view.Name} ({view.GetPath()})");
		}

		return null;
	}

	/// <summary>The first visible, drawn mesh of that name under the node.</summary>
	private static MeshInstance3D? FindDrawn(Node node, string name)
	{
		if (node is Node3D { Visible: false })
		{
			return null;
		}

		if (node is MeshInstance3D mesh && mesh.Name == name && Engine_UiPointer.CanvasBounds(mesh) is not null)
		{
			return mesh;
		}

		for (int i = 0, count = node.GetChildCount(); i < count; ++i)
		{
			if (FindDrawn(node.GetChild(i), name) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	/// <summary>The text label beside the glyph in its band: on its row, nearest on its right.</summary>
	private static MeshInstance3D? NearestLabel(MeshInstance3D glyph)
	{
		if (glyph.GetParent() is not { } band || Engine_UiPointer.CanvasBounds(glyph) is not { } at)
		{
			return null;
		}

		MeshInstance3D? best = null;
		float bestGap = float.MaxValue;
		for (int i = 0, count = band.GetChildCount(); i < count; ++i)
		{
			if (band.GetChild(i) is not MeshInstance3D label || ReferenceEquals(label, glyph) ||
				!label.Visible || !Engine_UiSceneBuilder.IsLabel(label) ||
				Engine_UiPointer.CanvasBounds(label) is not { } box)
			{
				continue;
			}

			float midY = box.GetCenter().Y;
			if (midY < at.Position.Y - at.Size.Y || midY > at.End.Y + at.Size.Y)
			{
				continue;
			}

			float gap = box.Position.X - at.End.X;
			if (gap < -at.Size.X || gap > 400f)
			{
				continue;
			}

			gap = Mathf.Abs(gap);
			if (gap < bestGap)
			{
				bestGap = gap;
				best = label;
			}
		}

		return best;
	}

	/// <summary>Lights the view's back prompt while the mouse is on it; puts it back when it leaves.</summary>
	private static void BackPromptHover(Engine_FrontEndMenuView? view, bool on)
	{
		BackPrompt? mine = view is not null && _backPrompts.TryGetValue(view.GetInstanceId(), out BackPrompt? p) ? p : null;
		BackPrompt? want = on ? mine : null;

		// Another list asked this frame with the mouse off its own prompt: the lit one is not its to put out.
		if (!on && view is not null && _backLit is not null && !ReferenceEquals(mine, _backLit))
		{
			return;
		}

		if (!ReferenceEquals(want, _backLit))
		{
			UnlightBack();
			if (want is { Glyph: { } glyph })
			{
				_backLit = want;
				_backLitScale = glyph.Scale;
				_backLitColour = want.Label is { } label ? Engine_UiSceneBuilder.TextColour(label) : Colors.White;
				glyph.Scale = _backLitScale * BackGlyphHoverScale;
			}
		}

		// Every frame while lit, so a fade the page runs on the word keeps its own alpha.
		if (_backLit is { Label: { } lit } && GodotObject.IsInstanceValid(lit))
		{
			Color now = Engine_UiSceneBuilder.TextColour(lit);
			Color hover = Engine_FrontEndMenuView.HoverLabelColour;
			Engine_UiSceneBuilder.SetTextColour(lit, new Color(hover.R, hover.G, hover.B, now.A));
		}
	}

	private static void UnlightBack()
	{
		if (_backLit is null)
		{
			return;
		}

		if (_backLit.Glyph is { } glyph && GodotObject.IsInstanceValid(glyph))
		{
			glyph.Scale = _backLitScale;
		}

		if (_backLit.Label is { } label && GodotObject.IsInstanceValid(label))
		{
			float alpha = Engine_UiSceneBuilder.TextColour(label).A;
			Engine_UiSceneBuilder.SetTextColour(label,
				new Color(_backLitColour.R, _backLitColour.G, _backLitColour.B, alpha));
		}

		_backLit = null;
	}

	/// <summary>
	/// The mouse on a book's right-hand page: the wheel over its scroll region scrolls it, and its
	/// authored scroll bar jumps on a press and drags while the button is held.
	/// </summary>
	private static void PointerPage(Engine_UiOwnedBook? book)
	{
		if (book is null || Engine_UiPointer.Canvas is not { } at)
		{
			return;
		}

		if (ReferenceEquals(_pageDrag, book))
		{
			if (Input.IsMouseButtonPressed(MouseButton.Left))
			{
				book.ScrollRegionTo(book.ScrollBarAt(at, anyHeight: true) ?? 0f);
				return;
			}

			_pageDrag = null;
		}

		if (book.ScrollBarAt(at) is { } fraction)
		{
			if (Engine_UiPointer.TakeLeft())
			{
				_pageDrag = book;
				book.ScrollRegionTo(fraction);
			}

			if (Engine_UiPointer.WheelSteps != 0)
			{
				book.AddScrollWheel(Engine_UiPointer.TakeWheel());
			}

			return;
		}

		if (book.ScrollRegionRect is { } page && page.HasPoint(at) && Engine_UiPointer.WheelSteps != 0)
		{
			book.AddScrollWheel(Engine_UiPointer.TakeWheel());
		}
	}

	/// <summary>The book whose scroll bar the left button is dragging, or null.</summary>
	private static Engine_UiOwnedBook? _pageDrag;

	/// <summary>
	/// The right-hand pages keep the pointer to themselves: a click or a wheel notch over them
	/// that nothing on the page took is dropped, never handed on to the list as A or a step.
	/// </summary>
	private void SwallowOverPages(Engine_UiOwnedBook? book)
	{
		if (book is null || Engine_UiPointer.Canvas is not { } at ||
			(!Engine_UiPointer.LeftPressed && Engine_UiPointer.WheelSteps == 0) ||
			_screenMenuView?.EntryAt(at) is not null || _screenMenuView?.ScrollBarAt(at) is not null ||
			(book.NodeFor("Foreground.Book.Pages") ?? book.Find("Pages")) is not { } pages ||
			Engine_UiPointer.CanvasBounds(pages) is not { } bounds || !bounds.HasPoint(at) ||
			Engine_UiSeats.PointToLayout(pages, at).X < 0f)
		{
			return;
		}

		// The preview's ring takes its own press, to grab and turn the model.
		if (!PreviewRingHas(at))
		{
			Engine_UiPointer.TakeLeft();
		}

		Engine_UiPointer.TakeWheel();
	}

	/// <summary>The page arrows each book screen authors: the equipment books' Page1 carries
	/// them in its footer, every other page directly (`equipmentscreen.bgf`,
	/// `abilitiesscreen.bgf`).</summary>
	private static readonly string[] PageNumberGroups =
	{
		"Foreground.Book.Pages.Page1.Contents.Footer.PageNumber",
		"Foreground.Book.Pages.Page1.Contents.PageNumber",
		"Foreground.Book.Pages.Page2.Contents.PageNumber",
	};

	/// <summary>
	/// -1 or +1 when this frame's click landed on a drawn page arrow (taking it), else 0.
	/// </summary>
	private static int PageArrowClicked(Engine_UiOwnedBook book)
	{
		if (!Engine_UiPointer.LeftPressed || Engine_UiPointer.Canvas is not { } at)
		{
			return 0;
		}

		foreach (string group in PageNumberGroups)
		{
			foreach ((string arrow, int way) in new[] { ("LeftArrow", -1), ("RightArrow", 1) })
			{
				if (book.NodeFor($"{group}.{arrow}") is { } node && node.IsVisibleInTree() &&
					Engine_UiPointer.CanvasBounds(node) is { } bounds && bounds.Grow(12f).HasPoint(at))
				{
					Engine_UiPointer.TakeLeft();
					return way;
				}
			}
		}

		return 0;
	}

	/// <summary>
	/// Whether a canvas point is inside the item preview's ring — the panel the model is fitted
	/// to, centred on the anchor it hangs from.
	/// </summary>
	private bool PreviewRingHas(Vector2 at) =>
		_previewObject is { } preview && preview.IsVisibleInTree() &&
		preview.GetParent() is Node3D anchor && preview.PanelSize > 0f &&
		new Vector2(anchor.GlobalPosition.X, anchor.GlobalPosition.Y).DistanceTo(at) <=
			preview.PanelSize * 0.5f;

	/// <summary>
	/// Whether this frame's click landed on the book's right-hand pages — not on its scroll bar,
	/// which the page scroll takes — and takes it if so.
	/// </summary>
	private bool PageClicked(Engine_UiOwnedBook book)
	{
		if (!Engine_UiPointer.LeftPressed || Engine_UiPointer.Canvas is not { } at ||
			book.ScrollBarAt(at) is not null || _screenMenuView?.EntryAt(at) is not null ||
			_screenMenuView?.ScrollBarAt(at) is not null ||
			(book.NodeFor("Foreground.Book.Pages") ?? book.Find("Pages")) is not { } pages ||
			Engine_UiPointer.CanvasBounds(pages) is not { } bounds || !bounds.HasPoint(at) ||
			Engine_UiSeats.PointToLayout(pages, at).X < 0f)
		{
			return false;
		}

		Engine_UiPointer.TakeLeft();
		return true;
	}

	public static bool WorldInteractionAllowed { get; private set; }

	/// <summary>
	/// Reads the VANILLA text bank — the one `Engine_ItemNames` asks for a curated record,
	/// after the mounted bank — on a worker, so the first Items list does not read 2.6 MB and
	/// inflate 5.5 MB of pages on the main thread. Only bytes, dictionaries and deflate streams
	/// are touched; the loader prints its own line. Not awaited, and only this bank: its lazy
	/// read is a plain `??=`, so a main-thread lookup landing mid-read sees null and reads the
	/// bank itself, exactly as before. The MOUNTED bank is left to the level build, which
	/// already reads it — its loader marks "tried" before "loaded", and a second reader would
	/// open a window where a lookup answers nothing.
	/// </summary>
	private static void WarmTextBanks()
	{
		_ = System.Threading.Tasks.Task.Run(() =>
		{
			try
			{
				_ = Engine_BnkTextBankLoader.BaseBank;
			}
			catch (System.Exception ex)
			{
				GD.PushWarning($"[babel] warming the text banks failed: {ex.Message}");
			}
		});
	}

	/// <summary>
	/// The language the guest runs in, followed. The title's Language entry switches the guest's
	/// own book and fonts (`sub_82382C68` -> `sub_823835F8`) and writes the choice to the profile;
	/// every string and glyph on screen is ours, so without this the switch changed nothing the
	/// player could see. Read on the same half-second clock as the hero suffix: a switch is a
	/// title-screen event, and the guest takes a second to reload behind its own banner anyway.
	/// </summary>
	private void FollowGuestCulture()
	{
		if (!Refii.Text.Engine_GameCulture.Set(HostBridge.GetCurrentCulture()))
		{
			return;
		}

		string culture = Refii.Text.Engine_GameCulture.Current;
		GD.Print($"[game-ui] the guest's language is now '{culture}' — reading its text and fonts");
		Engine_BnkTextBankLoader.InvalidateCulture();
		_presentation?.ReloadForCulture(culture);
		_menuView?.Refresh();
	}

	private void ui_processBody(double delta)
	{
		WorldInteractionAllowed = IsRevealed && !ModalPanelUp && !_fullCutscene;
		if (DriveGoldenCockMenu(delta)) return;
		// THE QUIT LATCH. The guest is still running its confirmation, save and teardown; the
		// front end coming up is it reporting all three done, and the only point at which
		// ending the process cannot interrupt a save in flight.
		if (_quitToDesktopPending)
		{
			_quitToDesktopWindow -= delta;
			if (HostBridge.GetFrontEndModel() is not null)
			{
				_quitToDesktopPending = false;
				GameExit.ToDesktop(GetTree());
				return;
			}

			// A BOUND on the latch, not a claim about the guest: a player can refuse the box,
			// leave the pause list and play on, and an armed latch would then fire on some
			// later return to the title.
			if (_quitToDesktopWindow <= 0.0)
			{
				_quitToDesktopPending = false;
				GD.Print("[game-ui] the pending quit to the desktop expired — the guest never " +
					"reached the front end; staying in the game");
			}
		}

		// The Video page's brightness, as the guest's camera holds it. Every frame, so the
		// slider's own live preview (`sub_829C0AB0`) shows while it is dragged.
		if (HostBridge.GuestBrightness() is { } brightness)
		{
			Refii.Lighting.Engine_PostFxParams.UserGamma = brightness;
		}

		// The hero's text suffix, which every guest string lookup tries first. It changes only
		// with the hero, so twice a second is plenty.
		_heroSuffixClock -= delta;
		if (_heroSuffixClock <= 0.0)
		{
			_heroSuffixClock = 0.5;
			Refii.Text.Engine_BabelDatabase.HeroTextSuffix = HostBridge.HeroTextSuffix();
			FollowGuestCulture();
		}

		// OUR MULTIPLAYER SCREEN COMES DOWN WHEN THE GAME STARTS, AND IT HAS TO BE CHECKED HERE.
		//
		// The guest has no multiplayer/friends presentation — the id->bgf table's only online
		// entry is 24 CoopScreen.bgf — so this screen is ours end to end and the guest's own
		// front-end teardown cannot dismiss it. On a join `sub_82319BE8` writes its run-loop exit
		// byte (0x82319E00) and destroys the guest's own presentations; ours would otherwise keep
		// standing over the running world, which is what left a joining player looking at the
		// friends list with the game already playing behind it.
		//
		// IT MUST RUN BEFORE THE FRONT-END GATES BELOW. The first attempt sat next to the screen's
		// own input drive, which is past `!IsRevealed` — the gate that stops running the moment
		// the game is up, so the close could never fire on the one transition it exists for.
		//
		// `IsInGame` is the guest stating it: byte_83496A5E, set by Game_InitDatabases and cleared
		// by the game mode's shutdown.
		//
		// ON THE TRANSITION ONLY. The pause menu's Multiplayer tab opens this same screen while
		// the game is already up; a level test closed it again the frame after it opened, which
		// is what made the tab look dead in a running co-op game.
		bool inGame = HostBridge.IsInGame;
		if (OpenListBook is { } frontEndBook && inGame && !_multiplayerSawInGame)
		{
			GD.Print("[game-ui] the game came up — closing the front end's list book");
			frontEndBook.Close();
		}

		_multiplayerSawInGame = inGame;

		// THE GUEST LEAVING THE GAME, on the falling edge of its own flag. Everything keyed on
		// the game's presentations is dropped here — see LeaveGame — rather than left to be
		// noticed, because the front end that follows never runs the in-game sampler.
		if (!inGame && _sawInGame)
		{
			LeaveGame();
		}

		// THE GAME COMING UP, on the rising edge: the text banks an item label resolves through
		// are read now, off the main thread, rather than on the frame the first Items screen
		// opens. The vanilla bank in particular is only ever asked for by a curated item name,
		// so it was reached 30 minutes into a session, in the middle of building the gift list.
		if (inGame && !_sawInGame)
		{
			WarmTextBanks();
		}

		_sawInGame = inGame;

		// THE OWNED BOOKS, BUILT WHILE THE LOADING SCREEN IS UP. A book is 100-500 cloned
		// nodes, their materials, bound text and nine-slice borders — 100-300 ms on the main
		// thread, which used to land on the frame the player first opened each screen. The
		// loading screen is a window where that frame is not seen, so one book is built per
		// frame of it; whatever is left when it comes down is built on demand as before.
		if (inGame && _loading is { IsShowing: true })
		{
			using (Refii.Core.Engine_FramePhases.Measure("ui.book.prebuild"))
			{
				_books.PrebuildOne();
			}
		}

		// THE HOST'S EMBEDDED PREVIEW dies with the page that asked for it. The ask is
		// per-frame (the abilities detail block sets the flag); a frame with no ask — the
		// blade, another screen, gameplay — takes the movie down.
		//
		// The DECODER is torn down by GameVideo's OWN _Process (the `EmbedOrdinal < 0` path):
		// GameVideo is the one owner of `_decoder.Dispose`. Disposing it from here too was a
		// second teardown site on the same object each frame — a double-free that corrupted the
		// host heap (`malloc_consolidate(): unaligned fastbin`). Here we only DETACH — drop the
		// ordinal and clear the latch — and let GameVideo notice and stop on its next tick.
		if (!_embeddedMovieAsked && Video is { } orphaned && orphaned.HasHostMovie)
		{
			orphaned.DetachHostMovie();

			// Only the host's own slots: a guest-hosted movie's slot is re-added every frame
			// by FollowMovie while its sprite stands, so a clear here cannot lose one.
			_movieSlots.Clear();
		}

		_embeddedMovieAsked = false;

		// THE DEBUG MENU'S VISIBILITY, applied every frame rather than on screen changes:
		// the reveal after the boot movies is not a screen change, and the menu must never
		// appear behind a bink or accept a click before the front end is ready. Frontend up
		// AND the movies done.
		// GATED ON A LAUNCHER SETTING. The overlay is a development fixture that draws over the
		// game's own UI, so it is off unless `developer_menu_enabled` says otherwise. Read once —
		// the file is not watched, and a per-frame read would open it every frame.
		bool showDebug = IsRevealed && DeveloperMenuEnabled;
		if (showDebug && _debugMenu is null)
		{
			ShowDebugMenu();
		}
		if (_debugMenu is not null)
		{
			_debugMenu.Visible = showDebug;
		}
		// Visible as well as revealed. IsRevealed is the whole UI layer being on screen — it
		// is false through the boot movies — and Visible is this menu being the screen that is
		// up. Without the second test the highlight would keep moving under the title screen,
		// so a player holding down on the title would arrive at the menu with the selection
		// already somewhere else.
		// Before the menu gate, because this has to keep reporting once the front end is gone
		// — that is exactly the window the in-game UI lives in.
		// THE LOADING SCREEN FIRST, and outside every gate below it. The others return early on
		// `IsRevealed` or on there being no menu, and a loading screen has to work in exactly
		// those windows — it is up while the front end is coming down and before the HUD exists.
		_loading?.Poll();

		// THE FADE, on the same terms and for the same reason — it runs across every one of
		// those windows, and it is a continuous ramp, so it is driven per frame rather than on
		// the sampling cadence.
		_fade?.Drive();

		ReportPumpHealth(delta);

		// WHICH LAYOUT THE DISPLAY WANTS, re-read as the window changes. Reported on the change
		// rather than every frame: a HUD that jumped between layouts and a HUD in the wrong one
		// look the same in a still, and only the transition says which.
		if (_viewport is not null)
		{
			bool wide = Engine_UiWidescreen.IsWidescreen(_viewport.Size);
			if (wide != _isWidescreen)
			{
				_isWidescreen = wide;
				GD.Print($"[game-ui] the display is now {(wide ? "WIDESCREEN" : "4:3")} " +
					$"({_viewport.Size.X}x{_viewport.Size.Y}) — the shipped " +
					$"{(wide ? "wide" : "standard")} layout applies");
			}
		}


		// The front-end list rides out with its book's left half — the template's wide layout.
		if (_menuView is not null && GodotObject.IsInstanceValid(_menuView))
		{
			_menuView.Position = _menuView.Position with
			{
				X = _menuView.Anchor.X - Engine_UiTemplates.WideListShift(1010),
			};
		}

		// The anchors resolve against the WINDOW, and it can change under us at any time.
		if (_hud is not null && GodotObject.IsInstanceValid(_hud))
		{
			_hud.Window = GetViewport().GetVisibleRect().Size;
		}

		SeatPlayerUi(GetViewport().GetVisibleRect().Size);

		// The toast works in display pixels; the mount maps them onto the canvas — see
		// BuildAchievementToast for why the two stay in different units.
		if (_toast is not null && _toastMount is not null &&
			GodotObject.IsInstanceValid(_toastMount))
		{
			Vector2 window = GetViewport().GetVisibleRect().Size;
			if (window.Y > 0f)
			{
				Vector2 extent = Engine_HudLayout.CanvasExtent(window);
				float unitsPerPixel = extent.Y / window.Y;
				_toastMount.Scale = new Vector3(unitsPerPixel, unitsPerPixel, 1f);
				_toast.Canvas = window;
				_toast.Held = !IsRevealed;
			}
		}

		ApplyHudWidgets(GetViewport().GetVisibleRect().Size);

		DriveUiCamera();
		// THE UI CUT, DRAINED FIRST. The `GUI.*` calls the game VM made since the last frame are
		// the guest STATING what it wants on screen, and every other drive below infers the same
		// thing from a result — so anything driven from a call has to see it before the frame
		// that would otherwise have inferred it. Drained even when nothing routes yet: the
		// report is what turns the plan's static census into a measured one.
		EnsureGuiNativeRoutes();

		// On the guest's frame: what reads guest state below reads what the guest wrote on its
		// last HF tick, and between two the read is identical. Input pumps, per-frame animation
		// and the surfaces' own drive stay on every frame.
		bool guestFrame = Refii.Core.Engine_GuestFrame.Changed;
		if (guestFrame)
		{
			_guiNatives.Drive();
		}

		// THE WIDGETS RUN THEIR OWN CLOCKS, and their predicates report from them — so both have
		// to happen every frame, in this order. A predicate published before the tick would
		// answer with last frame's fill percent.
		_widgets.Tick(delta);
		foreach (string sound in _widgets.TakeSounds())
		{
			_screens.PlaySound(sound);
		}

		if (guestFrame)
		{
			PublishPredicates();
		}

		// THE MENU MODEL, FED FROM THE C++'s OWN STATEMENTS. Every `g_Menu:` mutation the game
		// makes — spawn, ghost, emphasis, refresh, delete — arrives here with its staged
		// attributes, and applying them in order puts the list model in the host. This is what
		// `GetLuaMenu` retired into: the same information, taken where the C++ states it instead
		// of read back out of the Gameface VM afterwards.
		//
		// DRAINED BEFORE ANYTHING READS A MENU, because a statement made this frame should be
		// visible to this frame's drive rather than the next one.
		if (guestFrame)
		{
			_menuModel.Apply(Engine_UiSource.Current.TakeMenuChunks());
		}
		if (_menuModel.Applied != _menuModelReported)
		{
			_menuModelReported = _menuModel.Applied;

			// ONLY A CHANGE OF SHAPE IS NEWS. Every highlight step is a statement, and logging
			// the whole description on each one wrote a line per keypress (3,500 a session)
			// with only the counter moving. REFII_UI_TRACE=1 logs every one again.
			string described = _menuModel.Describe();
			int shapeAt = described.IndexOf(" | ", System.StringComparison.Ordinal);
			string shape = shapeAt < 0 ? string.Empty : described[(shapeAt + 3)..];
			if (Engine_UiTrace.Verbose || shape != _menuModelShapeReported)
			{
				_menuModelShapeReported = shape;
				GD.Print($"[menu-model] {described}");
			}
		}

		// WHAT THE CAPTURE ITSELF HAS SEEN, whenever it moves. Without this, "the guest states
		// no menus" and "our hook never fired" produce the identical silence — and a whole run
		// went by with no `[menu-model]` line at all, which is why every screen was inferring its
		// rows from the button pool's geometry instead of reading the guest's own statements.
		(ulong recorded, uint pending, ulong dropped) = guestFrame
			? HostBridge.MenuChunkCounters()
			: (_menuChunksRecorded, 0u, _menuChunksDropped);
		if (recorded != _menuChunksRecorded || dropped != _menuChunksDropped)
		{
			// The first statement proves the hook fires, and a drop or a restart is a failure:
			// those are always logged. The climb in between only with REFII_UI_TRACE=1.
			bool captureNews = Engine_UiTrace.Verbose || _menuChunksRecorded == 0 ||
				recorded < _menuChunksRecorded || dropped != _menuChunksDropped;
			_menuChunksRecorded = recorded;
			_menuChunksDropped = dropped;
			if (captureNews)
			{
				GD.Print($"[menu-model] the g_Menu capture has recorded {recorded} statement(s), " +
					$"{pending} pending, {dropped} dropped — applied {_menuModel.Applied}");
			}
		}
		TellExitRefused();
		_boxes.Tick(delta);

		// THE HUD'S TOPBOX AND TIMING METER, stated by name for the slide fold above: the
		// guest publishes their strings and meters into registries the host reads, and only
		// their visibility has to be re-derived here.
		if (_hud is not null && GodotObject.IsInstanceValid(_hud))
		{
			if (guestFrame)
			{
				Engine_UiHudTopBox.Publish(
					HostBridge.GetTopBoxFront(), GameInput.AHeld, GameInput.YHeld, _hud.Values,
					_presentation?.Strings, _boxes.FrontOf(RefiiNoticeBoxes));
				HostBridge.TargetedQuery targeted = HostBridge.GetTargetedQuery();
				Engine_UiHudTargetedQuery.Publish(targeted, _hud.Values);
				DriveJoinerOpinion(targeted, _hud.Values, delta);
			}

			// THE PEER'S EXPRESSION STAYS ON THE PEER'S SCREEN. The guest starts the local meter
			// for a remote expression too (net message 136); in a network session only the local
			// player's pip is drawn here — pip A is player one's, pip B player two's
			// (`sub_824143F8`: 1 when the expressing entity is `*(EM+108)`). Split screen and a
			// job's bar fill (mode 3) are one screen's business and draw as they are.
			// Run every display frame so Smoothed() extrapolates the timing meter at full monitor Hz.
			HostBridge.ExpressionMeter meter = HostBridge.GetExpressionMeter();
			bool network = CoopSession.PeerStanding && !CoopSession.LocalCoop;
			bool mine = !network || meter.Mode == 3 || (CoopSession.IsClient ? meter.ArmedB : meter.ArmedA);
			int ownPip = network && meter.Mode != 3 ? (CoopSession.IsClient ? 2 : 1) : 0;
			Engine_UiHudExpressionMeter.Publish(meter, _hud, mine, ownPip);

			// ON SPLIT SCREEN THE METER STANDS IN THE HALF OF WHOEVER ARMED IT: pip A is the
			// hero's, pip B the henchman's, both is both (`sub_823D0E10`). A meter with no pip
			// (a job's bar fill) is player one's screen, as the guest's one HUD is.
			int armed = (meter.ArmedA ? 1 : 0) | (meter.ArmedB ? 2 : 0);
			_hud.ExpressionPlayers = armed == 0 ? 1 : armed;

			// THE EXPRESSION WHEEL STANDS IN ITS OWNER'S HALF, as the guest states the owner.
			_hud.WheelPlayer = ExpressionWheelOwner();
			if (Engine_Views.Split && _hudTwo is not null && GodotObject.IsInstanceValid(_hudTwo))
			{
				_hudTwo.WheelPlayer = _hud.WheelPlayer;
				_hudTwo.ExpressionPlayers = _hud.ExpressionPlayers;
				Engine_UiHudExpressionMeter.Publish(meter, _hudTwo, mine);
			}
		}

		if (guestFrame && _boxes.GuestMessageBox is not 0u and uint stated &&
			!GuestMessageBoxOpen(stated))
		{
			// Usually a close the guest's thread made after this frame's drain, stated again on
			// the next; only a box freed with no close or destructor ever reaches here alone.
			GD.Print($"[game-ui] display message box {stated:X8} is no longer an open " +
				"MessageBox — dropped");
			_boxes.GuestMessageLost(stated);
		}
		_surfaces.Message.StatedDisplayBox = _boxes.GuestMessageBox;
		GameInput.DisplayMessageBoxUp = _boxes.GuestMessageBox != 0u;

		// Change-keyed, so it is one line per box rather than one per frame — and it is the only
		// place the owned box state is visible at all.
		if (_boxes.Generation != _boxesReported)
		{
			_boxesReported = _boxes.Generation;
			GD.Print($"[ui-cut] boxes: {_boxes.Describe()}");
		}

		if (guestFrame)
		{
			ReportCamera();
			TracePresentations();
			TraceWorldIcons();
		}
		DriveWorldIcons(guestFrame);
		DriveCoopHeroChannel(delta);
		DriveCoopNotices(delta);
		DriveCoopConsent(delta);
		DriveCoopEstate(delta);
		DriveCoopWeather(delta);
		DrivePropertyTips(delta);
		if (guestFrame) DriveOwnedScreenProvider();
		if (guestFrame) _books.Drive(_drawn, IsRevealed);
		if (guestFrame) _spells.Follow(IsRevealed);
		DrivePreviewObject();
		DriveCoopScreen(delta);
		if (guestFrame) DriveCoopFigures();

		// THE MOVING PARTS, every frame — see Engine_UiSceneBuilder.ApplyLiveTransform. This is
		// deliberately NOT inside TracePresentations, which samples a few times a second: the
		// guest animates these per frame, and a backdrop that scrolled at the sampling rate
		// would judder rather than move.
		AnimateDrawn();

		if (guestFrame) ScanWhenSettled();
		DriveTitle(delta);

		// THE SHARED MODAL FLAG BELONGS TO WHICHEVER SURFACE STANDS A VIEW THIS FRAME — false
		// here, re-set by the active one before this frame's value is ever read (GameInput
		// samples once per frame, after the value settles). This is the statement the surface
		// set cannot make for itself: "nobody raised a box at all" happens where none of them
		// runs.
		GameInput.ModalBoxOwnsInput = false;
		GameInput.ScreenMenuHoldsFlip = false;
		GameInput.HostUiOwnsPad = false;

		// THE GUEST'S STICK LATCH IS RELEASED BY DEFAULT. Every drive below can take the frame
		// with a `return`, so "release it when the list stops owning the input" cannot be written
		// at the point of use — a modal raised while the player held a direction would leave the
		// guest scrolling for ever, because its latch has no timeout. Asserting is immediate;
		// only the release waits for a pass in which nobody asked for a direction.
		PumpScreenStick();

		// THE OWNED SURFACES FIRST, and independent of the pause menu — the guest raises them
		// over ANY screen and one that is pending owns the frame outright. Their order is the
		// set's own; see Engine_UiSurfaceSet.
		if (guestFrame)
		{
			DriveAchievements();
			_modal = HostBridge.GetPauseState();
		}

		Engine_UiModel.PauseState? modal = _modal;
		if (_surfaces.Drive(IsRevealed ? _presentation : null, modal, SurfaceInput(delta)))
		{
			return;
		}

		// THE HOST'S OWN SURFACES with nothing of the guest's under them: a host box out of game
		// (the front end's dialog), then a mod's menu opened with no list up. See GameUiHostSurfaces.
		if (DriveFrontEndHostBox(delta) || DriveHostModMenu(delta))
		{
			return;
		}

		// A DISPLAY BOX DRAWN AS A SCREEN — the quest card (5). It is never the current screen,
		// so the revealed-screen path below never reaches it; it answers its own template's
		// vocabulary, and the modal mask holds the pad while it is up.
		if (IsRevealed && Engine_UiSource.Current.DisplayBoxScreen() is var boxScreen &&
			boxScreen != uint.MaxValue && Engine_UiScreenActions.OwnsRevealedInput(boxScreen))
		{
			GameInput.ModalBoxOwnsInput = true;
			Engine_UiScreenActions.RunRevealed(
				boxScreen, GameInput.MenuAcceptPressed, GameInput.MenuBackPressed);
			return;
		}

		// THE PAUSE MENU, before the front-end gate below — in game there is no front-end menu
		// at all, so the gate would starve it of input the way it starved the load fan once.
		if (DrivePauseMenu(delta))
		{
			return;
		}

		// THE IN-GAME LISTS — abilities, items, weapons, clothing, logbook, options, crime,
		// shop, world map, gift, selection, decoration. After the pause ROOT because they are
		// what the root opens INTO: the root is screen 0 and every one of these is not, so at
		// most one of the two ever stands.
		if (DriveScreenMenu(delta, modal))
		{
			return;
		}

		// WHETHER B IS OURS THIS FRAME — see GameInput.FrontEndSwallowsBack. Computed before
		// the guard so it is cleared the moment the front end is not the thing on screen.
		GameInput.FrontEndSwallowsBack =
			_menu is not null && _menuView is not null && IsRevealed &&
			(_warningView is { Visible: true } || _cardView is { Visible: true } ||
			(_menuView.Visible && (_menuView.Menu?.CanCollapse == true || _menuView.OpenedDepth > 0)));

		// AND WHAT IT IS SHOWING, for the unattended driver — see GameInput.FrontEndDrive. The
		// driver asked the GUEST which slide the menu was on and got a permanent 1, so it kept
		// pressing A at a list it could have read straight off this object. Same place, same
		// cadence, same reason: this class owns the front end, so it states its own condition.
		GameInput.FrontEndDrive = new GameInput.FrontEndDriveState(
			ListUp: _menu is not null && _menuView is { Visible: true } && IsRevealed &&
				_menu.Visible.Count > 0 &&
				_cardView is not { Visible: true } && _warningView is not { Visible: true },
			CardUp: _cardView is { Visible: true },
			WarningUp: _warningView is { Visible: true },
			Highlighted: _menu?.Highlighted?.Name ?? string.Empty);

		// THE GUEST'S OWN DIALOG, ahead of everything the host owns — see DriveGuestDialog.
		if (DriveGuestDialog())
		{
			return;
		}

		if (_menu is null || _menuView is null || !IsRevealed)
		{
			return;
		}

		// THE WARNING IS MODAL and therefore first of all, ahead of even the card. The guest's
		// box parks its screen in substate 1 and the only way out is the accept button — every
		// other input is dead while it stands. B clears it too, which is ours: the one-button
		// form has nothing else to press, and a box that could only be dismissed by a key we
		// might have mismapped is a deadlock.
		if (_warningView is { Visible: true })
		{
			int warningClick = _warningView.Click();

			// ACCEPT may PROCEED rather than merely dismiss. The two-button form is an offer,
			// not a dead end — "Continue without saving" goes on to the gender card — so the
			// box carries where accepting leads and null means it leads nowhere.
			if (GameInput.MenuAcceptPressed || warningClick > 0)
			{
				string? proceed = _warningView.AcceptOpens;
				_warningView.Visible = false;

				if (proceed is not null)
				{
					OpenScreen(proceed);
				}
				else
				{
					_menuView.Visible = true;
				}
			}
			else if ((GameInput.MenuBackPressed || warningClick < 0) && _warningView.AllowCancel)
			{
				// Cancel exists only on the two-button form, and the guest's B arm checks that
				// flag before doing anything at all — see AllowCancel. It returns the screen to
				// substate 0 without touching the presentation: back to the list, no card.
				_warningView.Visible = false;
				_menuView.Visible = true;
			}

			return;
		}

		// THE CARD OWNS THE INPUT WHILE IT IS UP, and it is tested BEFORE the menu's own
		// visibility. The card takes the rows off screen, so a gate that required a visible
		// menu first would hand the card no input at all and leave it stuck on the screen it
		// had just put up.
		if (_cardView is { Visible: true })
		{
			DriveCardChoice();
			return;
		}

		// THE LOAD FAN OWNS THE PRESS ONCE THE GUEST HAS OPENED IT, and — like the gender card
		// above — it is tested BEFORE the menu's own visibility rather than after.
		//
		// That ordering is the whole of a bug worth naming, because it is the same one twice. The
		// fan takes the rows off screen, so a gate requiring a visible menu first would hand it no
		// input at all and leave it stuck on the screen it had just put up. The earlier version of
		// this sat below that gate and was reachable only while the rows were still drawn.
		//
		// Choosing "Load Game" puts the guest's screen into substate 5 and it keeps receiving
		// input there, because our input mask covers only the root list. A run showed the
		// consequence exactly: its cursor walked 0..5 and back while every A press re-dispatched
		// "Load Game", because this class still believed the root list was up.
		if (DriveLoadCarousel(delta))
		{
			return;
		}

		// THE MULTIPLAYER BOOK OWNS THE INPUT WHILE IT IS UP, and — like the gender card and the
		// load fan above — it is tested BEFORE the menu's own visibility. Opening it takes the
		// rows off screen, so a gate requiring a visible menu first would hand it no input at
		// all and leave it standing with nothing able to close it.
		if (OpenListBook is { } listBook)
		{
			PointerPage(listBook.Book);
			if (listBook.Drive(
					GameInput.MenuDirection,
					GameInput.MenuAcceptPressed || PointerList(listBook.View),
					GameInput.MenuBackPressed,
					GameInput.MenuFlipPressed, delta, 0f))
			{
				ReportMenuView(listBook.View);
			}

			// B on its left list closes the screen; the rows come back with it.
			if (!listBook.IsOpen)
			{
				_menuView.Visible = true;
			}

			return;
		}

		if (!_menuView.Visible)
		{
			return;
		}

		// THE GAME'S OWN MOVEMENT SOUND, asked for the way its own menu asks.
		//
		// Godot owns the list now, so the guest never fires the slide sound it would have fired
		// for itself. Rather than reach into the audio system, the outcome is named: the
		// dispatcher's command ids 140 and 141 exist for exactly this and do nothing else but
		// fire "SE_GUI_SLIDE_MENU_UP" / "..._DOWN" on the front end's emitter. Only on a move
		// that actually happened — Navigate returns false while the auto-repeat is still
		// waiting, and a sound per frame held would be a buzz.
		if (_menuView.Navigate(GameInput.MenuDirection, delta))
		{
			HostBridge.PostFrontEndAction(
				GameInput.MenuDirection < 0 ? MenuSlideUpAction : MenuSlideDownAction);
		}

		bool menuClicked = PointerList(_menuView);

		// THE SOURCED ROWS, RE-READ EVERY FRAME. Friends come and go, a search answers over
		// seconds, and a fold built once shows none of it — the row list was static until the
		// menu was rebuilt, which is why a search only appeared after leaving Games and coming
		// back. `RefreshSources` compares the set and does nothing when it has not moved, so a
		// settled list costs one walk; `PollSearch` drains the native queue, which empties on
		// read and so has to be collected whether or not the fold is open.
		bool sourcesMoved = Engine_UiNetworkSource.PollSearch();
		if (_menu is not null &&
			Engine_UiTemplates.Menu("frontend-root") is { } frontEnd &&
			frontEnd.RefreshSources(_menu))
		{
			sourcesMoved = true;
		}

		if (sourcesMoved)
		{
			_menuView.Refresh();

			// WHAT THE FOLDS ACTUALLY HOLD. A sourced entry with no children does not fold at
			// all — it dispatches as a command row — and that is indistinguishable on screen
			// from a fold that opens onto nothing, so the counts are reported rather than
			// inferred. Only on a change, so a settled menu is silent.
			GD.Print("[game-ui] content rows: " + string.Join(" ", new[]
			{
				"Mods", "Friends", "Requests", "Invites", "Games",
			}.Select(name => $"{name}={_menu?.Find(name)?.Children.Count ?? -1}")));
		}

		// The front end's rows, reported the same way as the pause root's. Both views are the
		// same class on the same slot table, so the front end — which draws correctly — is the
		// control: a difference between the two lines localises a defect in one report.
		ReportMenuView(_menuView);
		Engine_UiModMenus.Focus(_menuView, Engine_FrontEndMenu.RootTemplate, Engine_UiCommandList.FrontEnd);

		if (GameInput.MenuBackPressed)
		{
			// A false return is "nothing left to back out of". Inside a mod's menu that closes
			// the menu; on the front end's root it is the guest's business, not ours — B there
			// leaves for the title screen, and the guest is fed the same B press through XInput.
			// A fold closed here is the guest's `OnPressB` arm, which slides the blade out
			// (`SE_GUI_BLADE_SLIDE_OUT`, 0x826DFC80 then 0x826DFBDC).
			if (_menuView.Back())
			{
				Cue(frontEnd: true, "bladeout");
			}
			else
			{
				Engine_UiModMenus.Back(_menuView);
			}
		}

		if (!GameInput.MenuAcceptPressed && !menuClicked)
		{
			return;
		}

		Engine_UiExpandableMenu.Item? frontChosen = _menuView.Menu?.Highlighted;
		string? action = _menuView.Activate();
		if (Engine_UiModMenus.Activated(_menuView, Engine_FrontEndMenu.RootTemplate, frontChosen, action,
				Engine_UiCommandList.FrontEnd))
		{
			return;
		}

		// A on any root row slides the blade in (`SE_GUI_BLADE_SLIDE_IN`, 0x826DF428) before the
		// press reaches Lua; A is ours in this substate, so the guest's arm never plays it.
		Cue(frontEnd: true, "bladein");

		// The guest confirms through the pump's next sample; flipping now keeps the marker on
		// the row the player just chose instead of one sample behind their press.
		if (action is "SubtitlesOn" or "SubtitlesOff")
		{
			ApplySubtitlesEmphasis(action == "SubtitlesOn");
		}

		// THE ONE DISPATCHER, AHEAD OF THE REFUSAL PIPE. `QuitToDesktop` and the multiplayer rows
		// (`Net:`) are host-owned verbs (GameUiCommands) — the guest's Marketplace branch is a
		// stubbed XAM surface that draws nothing here — and a mod's row may name any verb. HANDLED,
		// THEN FALL THROUGH: the refusal pipe below is what SHOWS a message a verb raised, so
		// returning here put the box one press late. A command no verb claims is the guest's row
		// name and is posted to it below.
		bool handledHere = false;
		if (action is not null)
		{
			string frontRow = frontChosen?.Name ?? string.Empty;
			handledHere = Engine_UiCommands.Run(action, new Engine_UiCommandContext(
				Engine_UiCommandList.FrontEnd, Engine_FrontEndMenu.RootTemplate, frontRow, Engine_UiModMenus.MenuPlayer,
				Engine_UiTemplates.MenuOwner(Engine_FrontEndMenu.RootTemplate, frontRow)) { View = _menuView })
				.Outcome != Engine_UiCommandOutcome.NotOurs;
		}

		// A REFUSAL, before either of the two outcomes below. The guest's New Game branch runs
		// its storage guards ahead of the gender card, so an entry can decline to open the very
		// screen it names — and the reason is a Babel tag, which is what the guest puts through
		// `dialog.Group.yousure`.
		if (_menu?.TakeWarning() is { } warning)
		{
			// The save-limit refusal is the guest's ONE two-button warning on this path
			// (`r6 = 1` at 0x826E01A4) and it labels its accept button GUI_DEVICE_REMOVED_CONTINUE
			// — a generic "Continue", reused. Every other storage refusal passes `r6 = 0` with
			// GUI_OK. Carried explicitly rather than derived, because it is the guest's flag.
			bool limit = warning == Engine_FrontEndMenu.SaveLimitWarning;
			string button = limit ? "GUI_DEVICE_REMOVED_CONTINUE" : "GUI_OK";

			if (_warningView is not null)
			{
				_warningView.Show(_presentation?.Theme, warning, button, twoButton: limit);

				// ONLY A NEW GAME REFUSAL PROCEEDS. Both of its forms go on to the card — each
				// parks `+116 = 0, +120 = 2` on the screen and the accept arm routes that pair
				// to "choosecard" whatever the button count — but this pipe now also carries
				// host-authored messages, and those must dismiss to the list. Setting it
				// unconditionally made A on the friend-code box start a new game.
				_warningView.AcceptOpens =
					warning == Engine_FrontEndMenu.NoProfileWarning ||
					warning == Engine_FrontEndMenu.SaveLimitWarning
						? Engine_FrontEndMenu.HeroCardScreen
						: null;
				_menuView.Visible = false;
			}

			GD.Print($"[game-ui] New Game refused — {warning} " +
				$"(saves {_menu.Saves.Count}/{_menu.Saves.Limit}, " +
				$"signed in {_menu.Saves.SignedIn}) — " +
				$"{(_warningView is null ? "NO BOX to show it in" : _warningView.LastReport)}");
			return;
		}

		// A SCREEN the entry opens, before the null test below — an entry that opens one also
		// returns null, and treating the two the same is what made "New Game" do nothing
		// visible while quietly starting a game.
		if (_menuView.TakeOpenedScreen() is { } screen)
		{
			OpenScreen(screen);
			return;
		}

		if (action is null || handledHere)
		{
			// Expanded a submenu, collapsed one, or a host-only entry the guest knows nothing
			// about. All three are complete here — there is nothing to tell the guest.
			return;
		}

		Post(action, "menu");
	}

	/// <summary>
	/// One frame of the Load Game card fan. True when it consumed the input.
	///
	/// THE GUEST OPENS IT; GODOT DRAWS IT. That split is not a compromise — it is the only
	/// division the enumeration allows. Listing a profile's saves is `sub_82443308`, a live
	/// `XamContentCreateEnumerator`, and building each card's text is `sub_826E14F8`, which
	/// mounts every container and parses its `ChapterSave.bin`. Both are guest CALLS, both run
	/// inside the dispatcher's own "Load Game" branch, and neither is something a host can do
	/// for itself. So "Load Game" still dispatches by name, the guest still does the work, and
	/// what Godot takes over is the part that was never the guest's to begin with once the front
	/// end changed hands: the cards on screen and the cursor moving between them.
	///
	/// SUBSTATE 5 IS THE SIGNAL — `screen+24`, set at 0x826E07E0 once the branch has enumerated
	/// more than one save. A single save never reaches it: that path commits directly at
	/// 0x826E0470, which is why this returning false during a one-save load is correct rather
	/// than a gap.
	///
	/// B IS LEFT TO THE GUEST, as it was. Its own handler maps B in this substate to UNFREEZE,
	/// which puts `screen+24` back to 0 — and it is already being fed the press, since the input
	/// mask covers only the root list. Posting a second back-out would drive one transition
	/// twice; the fan comes down on the next frame when the substate is no longer 5.
	/// </summary>
	private bool DriveLoadCarousel(double delta)
	{
		if (HostBridge.GetFrontEndModel() is not { } model || model.MenuState != LoadCarouselState)
		{
			// The guest has left the carousel — by its own B, or because a save was committed.
			// Taking the fan down here rather than on our own B press is what keeps the two in
			// step whichever of them ended it.
			CloseSaveCards();
			return false;
		}

		if (_saveCardView is null)
		{
			return false;
		}

		// POPULATED ONCE PER OPENING, not per frame. The containers are filled before the
		// substate is set — `sub_826E14F8` runs at 0x826E0448 and `screen+24 = 5` at 0x826E07E0
		// — so by the time this first sees state 5 the list is complete and re-reading it every
		// frame would rebuild the glyph mesh for a string that cannot change.
		if (!_saveCardView.Visible)
		{
			System.Collections.Generic.List<HostBridge.SaveCard> cards =
				HostBridge.GetSaveCards(out int guestCursor);
			if (cards.Count == 0)
			{
				// STATE 5 WITH NOTHING TO DRAW IS A CONTRADICTION, and saying so is the point.
				// The guest only reaches this substate having enumerated two or more saves —
				// zero warns and one commits directly — so an empty list means OUR read is
				// wrong, never that there are no saves.
				//
				// It reports the model's own count beside ours because that is the comparison
				// that localises the fault. `EnumeratedSaves` is read from the same
				// `screen+84/+88` vector by a different function: if it is non-zero while this
				// list is empty, the defect is in `GetSaveCards`; if both are zero, the vector
				// really is empty and the substate is being reached some way the dispatcher
				// does not explain. Reporting neither is what let the first version of this say
				// "empty" once a frame for a whole run without narrowing anything — the fault
				// was a host-side liveness gate returning early, and the log could not have
				// distinguished that from a bad offset.
				//
				// Rate-limited to once per opening: it fired every frame before, which buried
				// the build lines above it.
				if (_loadFanEmpty.Add(model.MenuState))
				{
					GD.PushWarning("[game-ui] the guest is in its save carousel but the " +
						$"enumerated list read back empty — model says {model.EnumeratedSaves} " +
						$"save(s) at screen+84/+88, cursor {model.LoadCardCursor}. The fan " +
						"cannot be drawn.");
				}

				return true;
			}

			_loadFanEmpty.Clear();

			// THE PORTRAITS, taken from the guest rather than from the file.
			//
			// Every card in `frontendmainmenu.bgf` is authored with the same placeholder back,
			// and the guest stamps the real screenshot's texture HASH into each card's
			// `screenshot` element as it builds the list. Without this the fan draws six
			// identical blank cards, which is what a run showed.
			System.Collections.Generic.List<Texture2D?> portraits = ReadCardPortraits();

			// Save i is stamped onto card `base + i`, not card i+1: the guest first resets all
			// six portraits to the card back (0x826E06AC..0x826E070C), then writes save i into
			// slot `i + screen+76` (`add r6, r31, r11` at 0x826E0744). Reading card i+1 gave a
			// two-save fan the two reset backs.
			int firstCard = Engine_FrontEndSaveCards.BaseSlotFor(cards.Count) - 1;
			var saves = new System.Collections.Generic.List<Engine_FrontEndSaveCards.Save>();
			// THE FILE NAMES, the guest's own vector the index commits by (ABI 218): the export
			// copy is named by them. A save's mods are told on the page that opens when it is
			// picked (ModSaveGate), never on the card.
			_saveCardNames = HostBridge.GetSaveCardNames();
			for (int i = 0; i < cards.Count; ++i)
			{
				int card = firstCard + i;
				saves.Add(new Engine_FrontEndSaveCards.Save(
					cards[i].Description,
					card < portraits.Count ? portraits[card] : null));
			}

			OpenSaveCards(saves);
			GD.Print($"[game-ui] the guest opened the load fan with {cards.Count} save(s), " +
				$"its cursor at {guestCursor} — {_saveCardView.Describe()}");
		}

		// WHAT THE FAN IS BEING TOLD, reported when it changes — the gender card's diagnostic,
		// for the same reason: a fan that does not move on left/right is either never handed the
		// direction or ignoring it, and only the input side is invisible from a screenshot.
		int sideways = GameInput.MenuHorizontal;
		if (sideways != _lastFanDirection)
		{
			_lastFanDirection = sideways;
			GD.Print($"[game-ui] load fan input {sideways:+#;-#;0} — {_saveCardView.Describe()}");
		}

		if (_saveCardView.Navigate(sideways, delta))
		{
			GD.Print($"[game-ui] load fan moved — {_saveCardView.Describe()}");
		}

		// The mouse: pointing at a card lifts it, clicking a card loads it. A click on no card
		// is taken here too, so it cannot fall through as A on whichever card is lifted.
		bool fanClicked = false;
		if (Engine_UiPointer.Canvas is { } fanAt)
		{
			int pointed = _saveCardView.SaveAt(fanAt);
			if (pointed >= 0 && Engine_UiPointer.Moved)
			{
				_saveCardView.Select(pointed);
			}

			if (Engine_UiPointer.TakeLeft() && pointed >= 0)
			{
				_saveCardView.Select(pointed);
				fanClicked = true;
			}
		}

		// X on a card: "Copy for Xbox / Xenia" — the save's guest files, vanilla-only, beside the
		// content root; the original is untouched (ModSaveGate.ExportForConsole).
		if (GameInput.MenuEquipPressed && _saveCardView.Activate() is var exported && exported >= 0 &&
			exported < _saveCardNames.Count)
		{
			Engine_UiHostBoxes.Tell(Refii.Mods.ModSaveGate.ExportForConsole(_saveCardNames[exported]));
			return true;
		}

		if ((GameInput.MenuAcceptPressed || fanClicked) &&
			_saveCardView.Activate() is var chosen && chosen >= 0)
		{
			// THE INDEX IS THE GUEST'S JOIN. It identifies a save by the address of its handle
			// slot — `Frontend_SetStartGameTarget` takes `*(screen+84) + 4*i` and
			// `*(screen+148) + 40*i + 12` — so the index is what both sides already agree on,
			// and our fan is populated in that same enumeration order.
			uint queued = HostBridge.LoadSave(chosen);
			GD.Print($"[game-ui] load fan: save {chosen} chosen — posted (queue depth {queued})");
		}

		// Consumed either way. A press that is not A still belongs to the fan, and letting it
		// fall through would move the root list's highlight underneath the cards.
		return true;
	}

	/// <summary>
	/// Each save card's portrait, in card order, as the guest currently has it.
	///
	/// THREE STEPS, and each is a place this can honestly come back empty. The guest publishes
	/// a texture HASH per element; `Engine_TextureNameTable` inverts it to a path; the texture
	/// cache loads that path. A card whose quest has no screenshot, a hash the table does not
	/// know, or a texture the banks do not carry all end with a null — and a null keeps the
	/// artist's placeholder, which is the right picture for "no screenshot" and an honest one
	/// for the other two.
	///
	/// Read from the LIVE front-end presentation by ordinal, so the join is the same one slides
	/// and text use. It declines wholesale if the guest's element count disagrees with ours.
	/// </summary>
	private System.Collections.Generic.List<Texture2D?> ReadCardPortraits()
	{
		var portraits = new System.Collections.Generic.List<Texture2D?>();

		if (_presentation?.TextureNames is not { } names ||
			HostBridge.GetUiElements() is not { } elements || elements.Count == 0)
		{
			return portraits;
		}

		if (HostBridge.GetUiTextureHashes(0u, elements.Count)
			is not { } hashes)
		{
			return portraits;
		}

		// THE SIX PORTRAIT ELEMENTS, FOUND BY NAME HASH IN WALK ORDER.
		//
		// `frontendmainmenu` carries exactly six elements named `screenshot` and all six are the
		// load cards' — checked against the file rather than assumed, because matching a leaf
		// name is only safe when the name is unique to the thing being matched. Walk order is
		// card order; the caller maps saves onto cards by the guest's base slot.
		uint screenshot = Engine_GameHash.Sdbm(Engine_FrontEndSaveCards.PortraitName);

		for (int i = 0; i < elements.Count && portraits.Count < Engine_FrontEndSaveCards.MaxCards; ++i)
		{
			if (elements[i].NameHash != screenshot)
			{
				continue;
			}

			Texture2D? texture = null;
			if (i < hashes.Length && hashes[i] != 0 &&
				names.TryResolve(hashes[i], out string path))
			{
				texture = _presentation.LoadTexture(path);
			}

			portraits.Add(texture);
		}

		return portraits;
	}

	/// <summary>
	/// Puts the fan up with the guest's own enumerated list, and takes the rows down.
	///
	/// The rows go for the reason the gender card's do — see <see cref="_cardView"/>: the file
	/// puts every menu element at z 0 and the cards reach z -100, so the list would cut straight
	/// across them, and no shipped slide parks the menu anywhere this could reuse.
	/// </summary>
	private void OpenSaveCards(
		System.Collections.Generic.List<Engine_FrontEndSaveCards.Save> saves)
	{
		if (_saveCardView is null)
		{
			return;
		}

		_saveCardView.SetSaves(saves);
		_saveCardView.Visible = true;

		if (_menuView is not null)
		{
			_menuView.Visible = false;
		}
	}

	/// <summary>
	/// Takes the fan down and gives the rows back. Idempotent — it runs on every frame the
	/// guest is not in substate 5, which is nearly all of them.
	/// </summary>
	private void CloseSaveCards()
	{
		if (_saveCardView is not { Visible: true })
		{
			return;
		}

		_saveCardView.Visible = false;

		// Not over the gender card, and not over the running game. The menu comes back only if
		// it is what the player would be looking at.
		if (_menuView is not null && _cardView is not { Visible: true } && !HasInGameUi)
		{
			_menuView.Visible = true;
		}

		GD.Print("[game-ui] the guest left its save carousel — the load fan is down");
	}

	/// <summary>
	/// `screen+24` while the guest's save carousel is up — set at 0x826E07E0 for two or more
	/// saves. The one-save case never reaches it; that path commits directly at 0x826E0470.
	/// </summary>
	private const uint LoadCarouselState = 5;

	/// <summary>
	/// Menu states already reported as "carousel up, list empty", so that warning fires once
	/// per opening rather than once per frame.
	///
	/// Cleared as soon as a non-empty list arrives, so a later opening that fails still speaks.
	/// </summary>
	private readonly System.Collections.Generic.HashSet<uint> _loadFanEmpty = new();

	/// <summary>
	/// Sends one named action to the guest.
	///
	/// POSTED, not called. The name goes onto the guest's tick-thread queue and the guest
	/// performs it inside its own tick. Nothing here touches guest state, which is what keeps
	/// this side as safe as it has always been — the bridge still only reads and writes memory
	/// from Godot's thread, and the one place that makes a guest CALL is the guest's own thread.
	///
	/// A name the guest has no action for is dropped by the pump with a log line rather than
	/// silently, so an entry that does nothing says so.
	/// </summary>
	/// <summary>
	/// Marks the current subtitles arm the way `sub_826E2220` does with SetItemEmphasis.
	/// </summary>
	/// <param name="known">The setting, or null to ask the bridge for the guest's answer.</param>
	private void ApplySubtitlesEmphasis(bool? known = null)
	{
		if (_menu is null)
		{
			return;
		}

		bool? on = known ?? HostBridge.GetSubtitles();
		if (on is null)
		{
			return;  // not sampled yet — leave both unmarked rather than guess
		}

		if (_menu.Find("SubtitlesOn") is { } onItem)
		{
			onItem.Emphasised = on.Value;
		}

		if (_menu.Find("SubtitlesOff") is { } offItem)
		{
			offItem.Emphasised = !on.Value;
		}
	}

	private void Post(string action, string from)
	{
		PendingAction = action;
		uint queued = HostBridge.PostFrontEndAction(action);
		GD.Print($"[game-ui] {from} activated '{action}' — posted to the guest tick pump " +
			$"(queue depth {queued})");
	}

	/// <summary>
	/// Opens a front-end screen a menu entry named.
	///
	/// An unknown name is reported rather than ignored: an entry that opens nothing looks
	/// exactly like an entry whose press was dropped, and those need different fixes.
	/// </summary>
	/// <summary>
	/// One command from the content fold — a mod row, a friend, a request, an invite or a
	/// session search. The suffix is `verb` or `verb:argument`.
	/// </summary>
	/// <remarks>
	/// EVERY ANSWER GOES ON THE ROW'S PAGE (<see cref="Engine_UiNetworkSource.Say"/>). The
	/// front end's warning box draws UNDER the multiplayer book and blocks it, so nothing on
	/// this path raises one.
	/// </remarks>
	private void DispatchNetwork(string command)
	{
		int colon = command.IndexOf(':');
		string verb = colon < 0 ? command : command[..colon];
		string argument = colon < 0 ? string.Empty : command[(colon + 1)..];

		switch (verb)
		{
			case "code":
				// A COPIES. The code is drawn on the page beside this row, so there is nothing
				// for a box to add — it would only be another press to dismiss.
				if (NetBridge.Self() is { FriendCode.Length: > 0 } self)
				{
					DisplayServer.ClipboardSet(self.FriendCode);
					Engine_UiNetworkSource.MarkCopied();
				}
				else
				{
					Engine_UiNetworkSource.Say("RexNet is not up, so there is no friend code yet.\n\n" +
						"Check the RexNet settings in the launcher.");
				}

				break;

			case "book":
				// THE FRONT END HOSTS THE SAME SCREEN as the pause tab. `_books` is a cache keyed
				// by screen id, not an in-game fixture, so the book stands here too.
				if (_multiplayer.Open())
				{
					// Null-conditional: this dispatch is shared with the pause book's own rows,
					// and there is no front-end list to hide on that path.
					if (_menuView is not null)
					{
						_menuView.Visible = false;
					}
				}
				else
				{
					// No page to answer on yet: the book is what failed to open.
					GD.PushWarning("[game-ui] multiplayer book not ready — art still loading");
				}

				break;

			case "paste":
				// EVERY ANSWER GOES ON THE PAGE. The clipboard is the input — a friend code is
				// ~100 characters that arrives by being pasted — and the confirm is a SECOND
				// press on this same row rather than a box, so nothing here opens a modal.
				if (Engine_UiNetworkSource.PendingAdd is { Length: > 0 } confirmed)
				{
					// The name rides the code; kept so a decline can say who it came from.
					if (NetBridge.PreviewCode(confirmed) is { } target)
					{
						Engine_UiNetworkSource.RememberSent(target.PeerId, target.Name);
					}

					Engine_UiNetworkSource.SetAddState(
						NetBridge.AddFriendByCodeResult(confirmed, string.Empty) switch
						{
							1 => "Friend request sent.\n\nThey appear under Friends once they accept.",
							2 => "That is your own friend code.\n\nIf this came from another install, " +
								"that install is a copy of this one and shares its identity: delete " +
								"its assets/save/rexnet/identity.key and restart it.",
							_ => "That request could not be sent — RexNet may be off.",
						});
					break;
				}

				string pasted = DisplayServer.ClipboardGet().Trim();
				if (NetBridge.PreviewCode(pasted) is { } who)
				{
					// The name is the code owner's own claim, so the peer id is shown with it.
					Engine_UiNetworkSource.SetAddState(
						$"Add {(who.Name.Length == 0 ? "this player" : who.Name)}?\n" +
						$"{who.PeerId}\n\nA sends the request.",
						pasted);
				}
				else
				{
					Engine_UiNetworkSource.SetAddState(pasted.Length == 0
						? "Your clipboard is empty.\n\nCopy a friend code and choose this again."
						: "That is not a friend code.\n\nCopy the whole REXN- code and try again.");
				}

				break;

			case "status":
				Engine_UiJoinRequests.StatusIndex = int.TryParse(argument, out int status) ? status : 0;
				Engine_UiNetworkSource.Say($"Your status is now: {Engine_UiJoinRequests.Statuses[Engine_UiJoinRequests.StatusIndex]}.");
				break;

			case "greet":
				{
					int greeting = int.TryParse(argument, out int id) ? id : 0;
					Answer(greeting >= 1 && greeting <= Engine_UiJoinRequests.Greetings.Length && NetBridge.SendPlayerSignal(greeting),
						"Sent to your friends.", "That could not be sent — RexNet is not up.");
					break;
				}

			case "joinmode":
				{
					var mode = (Engine_UiJoinRequests.JoinMode)System.Math.Clamp(
						int.TryParse(argument, out int wanted) ? wanted : 0, 0, 2);
					Engine_UiJoinRequests.Mode = mode;
					Answer(NetBridge.SetJoinable(mode == Engine_UiJoinRequests.JoinMode.Anyone),
						mode switch
						{
							Engine_UiJoinRequests.JoinMode.Anyone => "Friends can now join your game.",
							Engine_UiJoinRequests.JoinMode.InvitesOnly => "Only someone you invite can join now.",
							_ => "No one can join now; asks to join are declined.",
						},
						"That could not be changed — no game is hosted yet.");
					break;
				}

			case "joinable":
				// The row states the CURRENT setting, so its command carries the one it wants.
				Answer(NetBridge.SetJoinable(argument == "1"),
					argument == "1"
						? "Friends can now join your game."
						: "Your game is closed to joiners.",
					"That could not be changed — no game is hosted yet.");
				break;

			case "search":
				Engine_UiNetworkSource.BeginSearch();
				Engine_UiNetworkSource.Say(NetBridge.Available
					? "Searching.\n\nResults appear in this list as they answer."
					: "RexNet is not up, so there is nothing to search.");
				break;

			case "accept":
				Answer(NetBridge.PeerAction(argument, NetBridge.Act.AcceptRequest),
					"Friend request accepted.", "That friend request could not be accepted.");
				break;

			case "dismiss":
				Answer(NetBridge.PeerAction(argument, NetBridge.Act.DismissRequest),
					"Friend request dismissed.", "That friend request could not be dismissed.");
				break;

			case "join-allow":
				WithdrawJoinQuestion(argument);
				Answer(Engine_UiJoinRequests.Answer(argument, true),
					"Invite sent through Discord.\n\nThe game joins when they accept it.",
					"That ask could not be answered; it may have lapsed.");
				break;

			case "join-deny":
				WithdrawJoinQuestion(argument);
				Answer(Engine_UiJoinRequests.Answer(argument, false),
					"Declined.", "That ask could not be answered; it may have lapsed.");
				break;

			case "accept-invite":
				if (!ConfirmLeavingGame(command, argument))
				{
					break;
				}

				Answer(NetBridge.PeerAction(argument, NetBridge.Act.AcceptInvite),
					"Invitation accepted.\n\nThe game joins when the host is ready.",
					"That invitation could not be accepted.", hold: true);
				break;

			case "join":
				// THE SAME PATH AN INVITE TAKES. The guest joins from an accepted invite, and the
				// material that needs — host peer plus session id — is what presence carries for
				// a friend who is hosting. The node raises the accept for us.
				if (!ConfirmLeavingGame(command, argument))
				{
					break;
				}

				Answer(NetBridge.PeerAction(argument, NetBridge.Act.Join),
					"Joining.\n\nThe game loads when the host answers.",
					"They are not hosting a game you can join right now.", hold: true);
				break;

			case "about":
				// NOTHING TO RAISE. The placeholder row's page already explains the section
				// (Engine_UiNetworkSource.Detail for `none:`), so the press is a no-op.
				break;

			case "mods":
				// THE ACTIVE MODS BOOK: the enabled mods on the left page, the highlighted one's
				// description on the right.
				if (_modsBook.Open())
				{
					if (_menuView is not null)
					{
						_menuView.Visible = false;
					}
				}
				else
				{
					GD.PushWarning("[game-ui] Active Mods book not ready — art still loading");
				}

				break;

			case "mod":
				// READ-ONLY, answered on the page: the load order is the launcher's to write, and
				// editing it from two places would be two sources of truth.
				Engine_UiNetworkSource.Say("Mods are turned on and ordered in the launcher.");
				break;

			default:
				GD.PushWarning($"[game-ui] content command '{command}' has no dispatch path");
				break;
		}

		GD.Print($"[game-ui] content command '{command}' handled");
	}

	private static void Answer(bool ok, string good, string bad, bool hold = false) =>
		Engine_UiNetworkSource.Say(ok ? good : bad, hold: ok && hold);

	/// <summary>The join a first press armed, and when; a second press of the same row joins.</summary>
	private string _joinConfirmArmed = string.Empty;

	private ulong _joinConfirmAt;

	private const ulong JoinConfirmMsec = 10_000;

	/// <summary>
	/// FABLE 3'S JOIN WARNING (`GUI_JOIN_DESTRUCTIVE_ACTION_*`): joining someone leaves the game you
	/// are in, and what you have not saved goes with it. Said on the row's page — never a box over
	/// the book — and the join goes ahead on a second press of the same row within ten seconds. No
	/// warning from the title or the front end, where there is nothing to lose, or once already
	/// joined to someone.
	/// </summary>
	private bool ConfirmLeavingGame(string command, string argument)
	{
		if (!HostBridge.IsInGame || CoopSession.IsClient)
		{
			return true;
		}

		string key = command + ":" + argument;
		ulong now = Time.GetTicksMsec();
		if (_joinConfirmArmed == key && now - _joinConfirmAt <= JoinConfirmMsec)
		{
			_joinConfirmArmed = string.Empty;
			return true;
		}

		_joinConfirmArmed = key;
		_joinConfirmAt = now;
		Engine_UiNetworkSource.Say("Joining another game leaves this one, and anything you have not saved is lost." +
			"\n\nPress again to join.");
		return false;
	}

	/// <summary>
	/// One frame of a mod's menu opened over a screen's list (<see cref="Engine_UiModMenus"/>): the
	/// stick moves the view, A runs the row, B collapses a fold or closes the menu. The guest's
	/// stick latch is released, since none of these rows are in its pool.
	/// </summary>
	private void DriveOpenedModMenu(Engine_FrontEndMenuView view, string rootMenu, double delta)
	{
		HoldScreenStick(0);
		view.Navigate(GameInput.MenuDirection, delta);
		bool clicked = PointerList(view);
		ReportMenuView(view);

		if (GameInput.MenuBackPressed)
		{
			if (!view.Back())
			{
				Engine_UiModMenus.Back(view);
			}

			return;
		}

		if (!GameInput.MenuAcceptPressed && !clicked)
		{
			return;
		}

		Engine_UiExpandableMenu.Item? chosen = view.Menu?.Highlighted;
		string? action = view.Activate();
		Engine_UiModMenus.Activated(view, rootMenu, chosen, action, Engine_UiCommandList.Screen);
		view.Refresh();
	}

	private void OpenScreen(string screen)
	{
		if (screen == Engine_FrontEndMenu.HeroCardScreen && _cardView is not null)
		{
			// Reset on the way IN, not on the way out. The guest re-enters through the same
			// `Intro` slide and substate 2 every time, whatever the player did last, so a
			// cancelled choice must not be waiting when the card is opened again.
			_cardView.Reset();
			_cardView.Visible = true;

			// New Game past its guards: the narrator's line as the card comes in
			// (`TEXT_QUEST_QC010_FRONT_END_20`, 0x826E0244).
			Cue(frontEnd: true, "newgame");

			// The rows out of the way while the card is up — see the field's remarks for why
			// that is ours rather than the original's.
			if (_menuView is not null)
			{
				_menuView.Visible = false;
			}

			GD.Print($"[game-ui] the gender card is up — {_cardView.Describe()}");
			return;
		}

		GD.PushWarning($"[game-ui] menu entry asked for screen '{screen}', which nothing here " +
			"draws — the entry will appear to do nothing");
	}

	/// <summary>
	/// One frame of the gender card.
	///
	/// B closes it, which is the guest's own answer too: its B arm sets the `Outro` slide from
	/// any of the three card substates, and `Outro` switches on the group's `BehaviorUnFreeze`
	/// element, whose event drives the screen back to substate 0 — the menu list.
	///
	/// A with NEITHER card up does nothing, and that is read rather than invented: the guest's
	/// A arm has no case for substate 2 and falls through to `default: return` at
	/// `0x826DF3E8`. So the panel opens with a real question and will not answer it for you.
	/// </summary>
	/// <summary>
	/// The pump's names for the menu's two movement sounds. Host-only: on the guest's side
	/// these are command ids rather than menu entries, so it has no such strings.
	/// </summary>
	private const string MenuSlideUpAction = "MenuSlideUp";

	private const string MenuSlideDownAction = "MenuSlideDown";

	/// <summary>Last direction reported to the card, so the trace fires on change only.</summary>
	private int _lastCardDirection;

	private int _lastFanDirection;

	/// <summary>The load fan's save file names in card order, read when it opened (ABI 218).</summary>
	private System.Collections.Generic.List<string> _saveCardNames = new();

	private void DriveCardChoice()
	{
		if (_cardView is null)
		{
			return;
		}

		// WHAT THE CARD IS BEING TOLD, reported when it changes.
		//
		// The panel draws but has been seen not responding to left and right, and the two
		// explanations need opposite fixes: either the direction never arrives here — the
		// masking, the pad mapping, the axis fold — or it arrives and the pose does not follow.
		// Only the input side is invisible from a screenshot, so it is the side that says so.
		// Keyed on change, because this runs at the display rate.
		int sideways = GameInput.MenuHorizontal;
		if (sideways != _lastCardDirection)
		{
			_lastCardDirection = sideways;
			GD.Print($"[game-ui] gender card input {sideways:+#;-#;0} — {_cardView.Describe()}");
		}

		Engine_FrontEndCardChoice.Choice raisedBefore = _cardView.Chosen;
		if (_cardView.Navigate(sideways))
		{
			GD.Print($"[game-ui] gender card moved — {_cardView.Describe()}");
		}

		// The mouse: pointing raises a card, clicking one chooses it; a click elsewhere is
		// taken so it cannot confirm the raised card by accident.
		bool cardClicked = false;
		if (Engine_UiPointer.Canvas is { } cardAt)
		{
			Engine_FrontEndCardChoice.Choice pointed = _cardView.ChoiceAt(cardAt);
			if (Engine_UiPointer.Moved)
			{
				_cardView.Choose(pointed);
			}

			if (Engine_UiPointer.TakeLeft() && pointed != Engine_FrontEndCardChoice.Choice.Neither)
			{
				_cardView.Choose(pointed);
				cardClicked = true;
			}
		}

		// A card raised or swapped turns the page (`SE_GUI_NEW_PAGE`, 0x826DF380 and its three
		// siblings), by stick or by pointer.
		if (_cardView.Chosen != raisedBefore)
		{
			Cue(frontEnd: true, "page");
		}

		if (GameInput.MenuBackPressed)
		{
			// The card's B slides the blade out (`SE_GUI_BLADE_SLIDE_OUT`, 0x826DFBDC).
			Cue(frontEnd: true, "bladeout");

			// The guest sees this press too, through XInput, and it is sitting in substate 0
			// with the menu list live — so its own B handler may leave for the title screen at
			// the same moment we cancel back to the rows. That is inherited rather than
			// introduced: the row menu has always shared its A and B with the guest (which is
			// how B on the root menu reaches the title at all), and closing it needs a way to
			// suppress guest input for a press, which the bridge does not have.
			CloseCardChoice();
			GD.Print("[game-ui] the gender card was cancelled — back to the menu");
			return;
		}

		if (!GameInput.MenuAcceptPressed && !cardClicked)
		{
			return;
		}

		if (_cardView.Activate() is not { } chosen)
		{
			// Neither card is up. Nothing to confirm, and nothing to say — pressing A on an
			// unanswered question is not a fault.
			return;
		}

		// The choice's line (`..._30_HM` 0x826DF5E8 / `..._30_HF` 0x826DF734), then the commit once
		// the card's own slide has run (`ChooseBoy` 600 ms, `ChooseGirl` 603 ms), as the guest
		// commits after it: the line is not cut by the transition.
		bool boy = _cardView.Chosen == Engine_FrontEndCardChoice.Choice.Boy;
		Cue(frontEnd: true, boy ? "boy" : "girl");
		CloseCardChoice();
		GetTree().CreateTimer(boy ? 0.600 : 0.603).Timeout += () => Post(chosen, "gender card");
	}

	/// <summary>
	/// Plays one of the guest's own UI sounds for a press Godot handled itself (the pump's
	/// `UiCue:`): on the front end's emitter or the in-game GUI's.
	/// </summary>
	private static void Cue(bool frontEnd, string cue) =>
		HostBridge.PostFrontEndAction($"UiCue:{(frontEnd ? "fe" : "game")}:{cue}");

	/// <summary>
	/// Takes the card down and gives the rows back.
	///
	/// One place, because a card closed without restoring the menu leaves the front end with
	/// nothing on it and no way back — and there are two ways out of the panel.
	/// </summary>
	private void CloseCardChoice()
	{
		if (_cardView is not null)
		{
			_cardView.Visible = false;
		}

		if (_menuView is not null)
		{
			_menuView.Visible = true;
		}
	}

	private string? _showing;
	private int[]? _showingSlides;
	private bool[]? _showingVisible;

	/// <summary>
	/// Reports which presentations the guest has live, whenever that set CHANGES.
	///
	/// This exists because "the in-game UI is missing" is two different problems wearing one
	/// face, and they need opposite fixes. Either the guest has a HUD standing up and Godot is
	/// not drawing it, or the guest never built one under a null device — and until the
	/// registry could be read at all, there was no way to tell which.
	///
	/// Keyed on the set rather than polled, because it changes rarely (a menu opening) and
	/// printing 30 screens every frame would bury the transition that matters.
	/// </summary>
	private void TracePresentations()
	{
		if (HostBridge.TryGetAbiVersion() is null)
		{
			return;
		}

		// Sampled, not polled. The registry walk allocates and reads guest memory, the set it
		// reports changes on the scale of a menu opening, and this runs at the display rate —
		// so a few times a second is the honest sampling rate for it.
		//
		// EXCEPT WHILE THE GUEST IS AHEAD OF US — see SampleIsOverdue. That is the one window
		// in which the cadence is wrong rather than merely lazy.
		bool overdue = SampleIsOverdue();
		if (++_presentationTick % PresentationSampleFrames != 0 && !overdue)
		{
			return;
		}

		System.Collections.Generic.List<Engine_UiModel.Presentation> live =
			HostBridge.GetPresentations();

		// A presentation the registry has dropped takes its menu rows with it. The menu model is
		// keyed by address and the guest reuses addresses, so a screen rebuilt where its last
		// instance stood would otherwise inherit that instance's rows. Only an address this
		// walk has itself seen live is forgotten: the chunks are drained on the frame and the
		// registry a few times a second, so rows can land for a presentation the walk has not
		// listed yet, and those must stand.
		foreach (Engine_UiModel.Presentation p in live)
		{
			_menuModelSeenLive.Add(p.Address);
		}

		_menuModelSeenLive.RemoveWhere(address =>
		{
			if (live.Exists(p => p.Address == address))
			{
				return false;
			}

			_menuModel.Forget(address);
			return true;
		});

		// Drawing comes first and happens EVERY sample, because the guest keeps mutating the
		// element tree of a presentation that is already up — the HUD's numbers change without
		// the registry changing at all. The report below is the part that only fires on a
		// change, so a stable screen does not fill the log.
		if (!HostBridge.FrontEndGuiIsUp)
		{
			FollowInGameUi(live);
		}
		else
		{
			HasInGameUi = false;
			FollowFrontEndExtras(live);
		}

		// WHICH SCREEN THE GUEST SAYS IT IS ON, reported on change and separately from the set
		// of live presentations — because for the in-game screens those two are not the same
		// claim. The pause menu's presentation is created once at game entry and never freed,
		// so it is in the list below from the first frame of play; only this says whether the
		// player is actually in it.
		Engine_UiModel.InGameScreens? manager = HostBridge.GetInGameScreens();
		Engine_UiModel.InGameScreen? screen = manager?.Current;
		_screenOwners = manager?.Owned;
		if (screen != _inGameScreen)
		{
			_inGameScreen = screen;

			// A NEW SCREEN OPENS ON ITS FIRST PAGE.
			Engine_UiPageProviders.BookPage = 0;
			_screenPageHeld = 0;

			// THE SLOTS TOO, because "which screen is current" and "which presentation is in
			// which slot" are separate claims and the draw gate needs both to be right. A gate
			// that silently never fires looks exactly like a gate that fires and finds nothing,
			// and only the addresses tell them apart — they can be read straight against the
			// `[guest-ui] N live presentation(s)` list printed below, which gives each
			// presentation's address.
			string slots = manager is { } m && m.Owned.Count > 0
				? string.Join(", ", System.Linq.Enumerable.Select(
					System.Linq.Enumerable.OrderBy(m.Owned, e => e.Value),
					e => $"[{e.Value}]={e.Key:X8}"))
				: "none built yet";

			GD.Print(screen is null
				? "[guest-ui] no screen manager yet — the guest is not in game"
				: $"[guest-ui] the guest's current in-game screen is {screen} " +
				  $"({(int)screen.Value}); manager holds {slots}");
		}

		// WHICH SCREEN THE FOLLOWER IS ACTUALLY DRAWING, restated on every sample. It is the
		// question both <see cref="SampleIsOverdue"/> and the pause menu's decline report ask,
		// and neither can answer it from the screen id alone: the guest states a screen the
		// moment it pushes it, several of its own ticks before the presentation exists.
		//
		// THE MANAGER'S SLOT MAP IS THE JOIN, the same stated join <see cref="PresentationFor"/>
		// makes — read here off the manager already in hand rather than through a second bridge
		// call. The STATED screen wins when it is among the drawn, because two of the manager's
		// presentations are briefly drawn together across a transition and only one of them is
		// the answer.
		//
		// ANY of them still drawn answers when the stated screen is not, which is what covers
		// LEAVING a menu: the guest drops to `None` and this stays on the screen whose tree is
		// still up, so the follower hurries until that tree is retired instead of leaving a
		// stale book over the running game for a sampling period.
		uint followed = (uint)Engine_UiModel.InGameScreen.None;
		if (manager is { } screens)
		{
			uint current = (uint)(screen ?? Engine_UiModel.InGameScreen.None);
			foreach ((uint address, int id) in screens.Owned)
			{
				if (!_drawn.ContainsKey(address))
				{
					continue;
				}

				if ((uint)id == current)
				{
					followed = current;
					break;
				}

				followed = (uint)id;
			}
		}

		_followedScreen = followed;

		string signature = string.Join('|', live.ConvertAll(p => $"{p.Address:X8}:{p.Name}"));
		if (signature == _presentationSignature)
		{
			return;
		}

		_presentationSignature = signature;

		if (live.Count == 0)
		{
			GD.Print("[guest-ui] no live presentations at all — the guest has built no UI");
			return;
		}

		GD.Print($"[guest-ui] {live.Count} live presentation(s):");
		foreach (Engine_UiModel.Presentation p in live)
		{
			// The ROOT is the half that decides whether it can be drawn. A presentation with a
			// null root is constructed but not yet deserialised, which looks identical from the
			// outside to one we simply are not rendering.
			GD.Print($"    {p.Address:X8} root={p.Root:X8} " +
				$"{(p.IsShowing ? "SHOWING" : "hidden")} " +
				$"active={p.ActiveElements}{(p.IsFrozen ? " FROZEN" : string.Empty)} " +
				$"{(p.Root == 0 ? "(NO ROOT — built but not populated)" : string.Empty)} " +
				$"'{p.Name}'");
		}
	}

	// No list of in-game screens any more, deliberately. Which presentations are drawn is the
	// guest's statement — see Engine_UiModel.Presentation.IsShowing — so there is nothing here to
	// keep in step with the thirty in-game screens.

	/// <summary>
	/// True when the guest has an in-game presentation standing up.
	///
	/// The window uses this to decide whether the UI layer belongs on screen at all. It is
	/// deliberately about what the GUEST has live rather than about whether we happen to be
	/// drawing it, so a presentation we cannot yet render still keeps the layer up instead of
	/// silently taking the whole UI away.
	/// </summary>
	public bool HasInGameUi { get; private set; }

	private readonly System.Collections.Generic.Dictionary<uint, Engine_UiDrawnPresentation> _drawn = new();

	/// <summary>
	/// Follows the guest's per-frame movement on every drawn presentation.
	///
	/// The transforms are the guest's own statement and they change constantly — the front end's
	/// backdrop scrolls, the HUD's needles swing — so they are written onto the existing nodes
	/// rather than being allowed to trigger a rebuild. Slides, visibility and opacity still go
	/// through <see cref="Follow"/>, because those DO change the shape of the tree.
	///
	/// Reported the first time each presentation actually moves, so "the guest never moved it"
	/// and "we never applied it" stay distinguishable — they were not, for two turns.
	/// </summary>
	private void AnimateDrawn()
	{
		if (_drawn.Count == 0 || HostBridge.TryGetAbiVersion() is null)
		{
			return;
		}

		// THE LIVE VALUES, once per frame for every drawn screen that has bindings.
		//
		// ONE READ FOR ALL OF THEM, not one per bar. The guest keeps every meter in a single map
		// keyed by name hash, so the whole HUD's state is one walk — and a per-bar lookup would
		// mean re-walking a red-black tree fifty-two times a frame for values that were all
		// written together.
		//
		// Read outside the loop for the same reason: two drawn screens can bind the same name,
		// and sampling twice in one frame could hand them different values from a map the guest
		// is still writing.
		++_valueTick;

		System.Collections.Generic.Dictionary<uint, float>? meters = null;
		System.Collections.Generic.Dictionary<uint, string>? texts = null;
		foreach (Engine_UiDrawnPresentation candidate in _drawn.Values)
		{
			if (candidate.Values is not null)
			{
				meters = HostBridge.GetHudMeters();
				texts = HostBridge.GetHudTexts();
				break;
			}
		}

		foreach ((uint address, Engine_UiDrawnPresentation drawn) in _drawn)
		{
			if (meters is not null && drawn.Values is { } values)
			{
				values.Apply(meters, texts);

				// Reported when the picture CHANGES, so "the guest never published" and "we
				// never applied" stay distinguishable — the same distinction the transform
				// report below exists to keep, and the one that cost two turns. EITHER half
				// counts. A HUD whose bars move but whose text never arrives, and one whose text
				// arrives but whose bars sit at zero, are different faults — and a report that
				// fired only on meters would stay silent through the second.
				//
				// ON CHANGE RATHER THAN ONCE, because a single sample cannot tell a value that
				// IS zero from one we looked at too early. This used to fire the first time
				// anything landed, which on the HUD is while the world is still loading — and it
				// duly reported PlayerHealth=0% for a hero that had not spawned yet, with no
				// second line to correct it. Sampled rather than polled: the string is rebuilt
				// once a second per screen, not once a frame.
				// NO "SOMETHING LANDED" PRECONDITION, and removing it is the fix for a report
				// that went completely silent. Requiring a non-zero applied count AND the
				// sample tick meant both had to be true in the SAME frame — one frame in
				// sixty — so a run where the guest published nothing on those exact frames
				// produced no line at all, and "the HUD has no values" became
				// indistinguishable from "the report never ran". A description reading
				// `meters=0/0` is itself the answer, so it is worth printing.
				if (_valueTick % ValueSampleFrames == 0)
				{
					string described = values.Describe(meters, texts);
					if (!_valued.TryGetValue(address, out string? wasValued) ||
						!string.Equals(wasValued, described, System.StringComparison.Ordinal))
					{
						_valued[address] = described;
						GD.Print($"[game-ui] '{drawn.Name}' live values — {described}");
					}
				}
			}

			if (drawn.ElementCount == 0)
			{
				continue;
			}

			if (Engine_UiLiveAttributes.Transform(address, drawn.ElementCount) is not { } transform)
			{
				continue;
			}

			// THE OWNED BOOKS READ THIS. A presentation Godot owns has no mirror to move, but
			// its book is fed `LastTransform` every frame — gating the read on the mirror left
			// every owned book without the guest's live transforms, which is a panorama that
			// never scrolls and a book whose guest-driven slides never arrive.
			drawn.LastTransform = transform;
			if (drawn.Builder is null)
			{
				continue;
			}

			int moved = drawn.Builder.ApplyLiveTransform(transform);

			// THE WIDE LAYOUT LAST, and every frame. The guest still believes it is in 4:3 —
			// nothing calls `GUI:SetToDisplayInWidescreen()` here, because that is a guest CALL
			// and this bridge only reads and writes memory — so it keeps restating the
			// standard-definition position on anything it animates. Applying the override after
			// the transform is the same final word the shipped event handler has.
			if (drawn.Widescreen is { } wide && HostAppliesWidescreen)
			{
				wide.Apply(drawn.Builder);
			}

			// LAST, because a clip has to follow whatever moved the mask — the live transform
			// above, or the widescreen override after it. A mask is an ordinary scene node and
			// the pause menu animates its own in lockstep with the page slide.
			drawn.Builder.RefreshClips();
			if (moved > 0 && _animated.Add(address))
			{
				GD.Print($"[game-ui] '{drawn.Name}' is animating — {moved} element(s) moved " +
					"on the first frame it did");
			}

			_lastAddress = address;
			ReportScroll(drawn, moved);
			FollowMovie(drawn, address);
		}
	}

	/// <summary>
	/// WHAT IS ACTUALLY ON SCREEN AT A GIVEN X — a scanline across the canvas, once, late.
	///
	/// The backdrop measurement closed its own question and opened this one: all 28 of its quads
	/// paint, continuously, right across the visible band, with nothing hidden, untextured or
	/// transparent. So the black stripe is not a hole in the backdrop — something is drawn OVER
	/// it. Which means the useful question is no longer "does the backdrop reach here" but "what
	/// is painting here", and no amount of reading the backdrop can answer that.
	///
	/// So this walks EVERY node under the presentation host — the guest's screens, our cloned
	/// rows, the card, the warning — and at each sample x reports the painting quads that cover
	/// it, nearest first. Three of them, because the answer to "why is this black" is usually the
	/// second one.
	///
	/// SAMPLED LATE. It runs once, well after the menu is up, because a scanline taken while the
	/// tree is still being assembled describes a screen the player never saw — the failure this
	/// project has hit repeatedly with once-only diagnostics.
	/// </summary>
	private void Scanline()
	{
		if (_presentation is null)
		{
			return;
		}

		var quads = new System.Collections.Generic.List<(float Min, float Max, float Z, string What)>();
		Collect(_presentation);

		void Collect(Node node)
		{
			foreach (Node child in node.GetChildren())
			{
				Collect(child);
			}

			if (node is not MeshInstance3D mesh || !mesh.IsVisibleInTree())
			{
				return;
			}

			Engine_UiSceneBuilder.QuadPaint paint = Engine_UiSceneBuilder.DescribeQuad(mesh);
			if (!paint.Painted)
			{
				return;
			}

			Aabb box = mesh.GlobalTransform * mesh.GetAabb();

			// Only what crosses the canvas centreline, which is where the stripe was measured.
			if (box.Position.Y > 0f || box.End.Y < 0f)
			{
				return;
			}

			// AN UNTEXTURED QUAD IS NOT SKIPPED, it is NAMED. A quad with no texture paints its
			// tint flat across its whole area — which for the default white is a solid white
			// plate. That is the single most likely cause of a screen going white, so a
			// scanline that filtered it out would have dutifully reported whatever was behind
			// the very thing being looked for.
			string texture = paint.Texture is { } albedo
				? albedo.ResourcePath.GetFile()
				: "NO-TEXTURE";
			Color tint = paint.Tint;

			quads.Add((box.Position.X, box.End.X, box.Position.Z,
				$"{mesh.Name}<{(texture.Length > 0 ? texture : "runtime")} " +
				$"{tint.R:F2},{tint.G:F2},{tint.B:F2}@a{tint.A:F2}>"));
		}

		var line = new System.Text.StringBuilder(
			$"[game-ui] scanline on '{_scanning}' at y=0 ({quads.Count} painting quad(s), nearest first)");
		foreach (float x in new[] { -600f, -480f, -400f, -340f, -280f, -160f, 0f, 200f, 400f, 600f })
		{
			var over = new System.Collections.Generic.List<(float Z, string What)>();
			foreach ((float min, float max, float z, string what) in quads)
			{
				if (x >= min && x <= max)
				{
					over.Add((z, what));
				}
			}

			over.Sort((a, b) => b.Z.CompareTo(a.Z));

			line.Append($"\n    x={x,5:F0}: ");
			line.Append(over.Count == 0
				? "NOTHING PAINTS HERE"
				: string.Join("  ", over.ConvertAll(o => $"{o.What}@z{o.Z:F0}").GetRange(
					0, System.Math.Min(3, over.Count))) + $"  (+{System.Math.Max(0, over.Count - 3)} more)");
		}

		GD.Print(line.ToString());
		Inventory();
	}

	/// <summary>
	/// Every named element on a small screen, painting or not — the "what is missing" report.
	///
	/// THE SCANLINE CANNOT ANSWER THIS. It samples the centreline, so anything that lives above
	/// or below y=0 — "Press A To Start", the legal text, the logo's particle group — is invisible
	/// to it whether it is drawn correctly or not there at all. Asking "what is painting at this
	/// x" and "does this element exist" are different questions and the first one silently
	/// answers "no" to both.
	///
	/// So this lists the built tree: name, world bounds, and why it is not on screen if it is
	/// not. An element absent from this list was never built; one present with `HIDDEN` or
	/// `a0.00` was built and suppressed; one with sane bounds and full alpha is on screen and the
	/// complaint is about placement. Those are three different fixes.
	///
	/// SMALL SCREENS ONLY. `frontendmainmenu` carries 3812 elements and would bury the log; the
	/// title screen carries about twenty. The cap is stated rather than silent — a report that
	/// quietly covers some screens and not others is how "it printed nothing" gets read as
	/// "nothing is wrong".
	/// </summary>
	private void Inventory()
	{
		// 128, not 64. The title screen has 77 elements and the first cap skipped it — the one
		// screen the inventory existed to explain. Sized to clear it with room rather than to
		// sit just above it, while still excluding `frontendmainmenu`'s 3812.
		const int cap = 128;

		if (_presentation?.CurrentScreen is not { } screen)
		{
			return;
		}

		// The two front-end screens are exempt from the cap while their rendering is under
		// investigation: stray A-prompt glyphs and missing page furniture cannot be named
		// without the element-by-element state, and these are the screens the report exists
		// for. Over the cap the print is restricted to elements that made it into the BUILT
		// tree — that is where a stray glyph lives, it is a few dozen lines instead of 3,812,
		// and the missing-furniture side is carried by the count of unbuilt elements.
		// THE EXEMPTION IS OVER. Both front-end screens now render correctly, and printing all
		// 3,812 of the main menu's elements cost a visible hitch every time the menu came up —
		// on the title screen, of all places. The cap applies to every screen; the built-tree
		// summary below still carries what a future investigation needs.
		const bool investigating = false;

		if (screen.Nodes.Length > cap && !investigating)
		{
			GD.Print($"[game-ui] inventory of '{_scanning}' SKIPPED — {screen.Nodes.Length} " +
				$"elements, over the {cap} cap");
			return;
		}

		// The built tree, gathered by NAME. Names are not guaranteed unique, but on a screen
		// small enough to pass the cap a collision is visible in the output rather than hidden
		// by it — and the alternative, the builder's ordinal map, is not reachable for a screen
		// the host mounted itself.
		var found = new System.Collections.Generic.Dictionary<string, string>();
		Gather(_presentation);

		void Gather(Node node)
		{
			foreach (Node child in node.GetChildren())
			{
				Gather(child);
			}

			string name = node.Name;
			if (found.ContainsKey(name))
			{
				return;
			}

			if (node is not MeshInstance3D mesh)
			{
				found[name] = node is Node3D { Visible: false } ? "group HIDDEN" : "group";
				return;
			}

			if (!mesh.IsVisibleInTree())
			{
				found[name] = "HIDDEN";
				return;
			}

			Engine_UiSceneBuilder.QuadPaint paint = Engine_UiSceneBuilder.DescribeQuad(mesh);
			Aabb box = mesh.GlobalTransform * mesh.GetAabb();

			found[name] =
				$"x[{box.Position.X:F0}..{box.End.X:F0}] y[{box.Position.Y:F0}..{box.End.Y:F0}] " +
				$"a{paint.Opacity:F2}";
		}

		var line = new System.Text.StringBuilder(
			$"[game-ui] inventory of '{_scanning}' ({screen.Nodes.Length} authored element(s))");

		bool builtOnly = screen.Nodes.Length > cap;
		int unbuilt = 0;

		for (int i = 0; i < screen.Nodes.Length; ++i)
		{
			UiNode authored = screen.Nodes[i];
			string name = authored.HasAuthoredName ? authored.Name : $"#{i}";
			string kind = authored.Quad is not null
				? "quad"
				: authored.Text is not null ? $"text \"{authored.Text.Display}\"" : "group";

			if (!found.TryGetValue(name, out string? state))
			{
				++unbuilt;
				if (builtOnly)
				{
					continue;
				}

				state = "NOT IN THE BUILT TREE";
			}

			line.Append($"\n    {name} [{kind}] {state}");
		}

		if (builtOnly)
		{
			line.Append($"\n    (+ {unbuilt} authored element(s) not in the built tree, elided)");
		}

		// CHUNKED, because one print of a whole screen is silently truncated — the menu's
		// 3,812-element run lost its tail, including the elided-count line that says whether
		// the print is even complete. ~60 rows per print stays far inside the cap.
		string[] rows = line.ToString().Split('\n');
		var chunk = new System.Text.StringBuilder();
		for (int i = 0; i < rows.Length; ++i)
		{
			chunk.Append(rows[i]).Append('\n');
			if ((i + 1) % 60 == 0 || i == rows.Length - 1)
			{
				GD.Print(chunk.ToString().TrimEnd('\n'));
				chunk.Clear();
			}
		}
	}

	/// <summary>
	/// One scanline per presentation, taken once it has settled.
	///
	/// KEYED ON WHICH SCREEN IS UP, not on a single once-ever latch. The first version counted
	/// frames only after the main-menu guard, so it could never fire for the title screen — the
	/// exact screen that turned out to be drawing a white plate. A diagnostic that cannot run on
	/// the broken screen is not a diagnostic, and this is the second time on this feature that
	/// the gate, not the code, was the defect.
	/// </summary>
	private void ScanWhenSettled()
	{
		if (!IsRevealed)
		{
			_scanning = null;
			return;
		}

		string current = _showing ?? Screen;
		if (current != _scanning)
		{
			_scanning = current;
			_scanFrames = 0;
			return;
		}

		// Two seconds in. Long enough for a fade to finish, so an element caught mid-transition
		// is not reported as a permanent one.
		if (++_scanFrames == 120)
		{
			Scanline();
		}
	}

	private string? _scanning;

	private int _scanFrames;

	/// <summary>
	/// Runs the title screen's logo flash — see <see cref="Engine_FrontEndTitle"/>.
	///
	/// GODOT DRIVES IT, which is the point rather than an implementation detail. The alternative
	/// was to follow the guest's own `frontendstartupscreen` presentation and take the opacity it
	/// states, and that would have worked for this one element while handing the screen's
	/// animation back to a presentation the guest reports as `hidden` throughout. The front end
	/// is ours: the menu, the card and the warning are all driven here, and the title screen is
	/// the last piece that was not.
	/// </summary>
	private void DriveTitle(double delta)
	{
		// NO SCREEN-NAME TEST. The first version only ran while `_showing` matched
		// "frontendstartupscreen", and produced no output at all on a run where the scanline —
		// same method, same frames, one line above — reported that very screen twice. Rather
		// than keep reasoning about why a string comparison did not hold, the comparison is
		// gone: the flash is driven wherever the current screen actually has one, and a screen
		// without it says so.
		string current = _showing ?? Screen;
		if (current != _titleScreen)
		{
			_titleScreen = current;
			_titleFrames = 0;
			_title.Reset();
		}

		// THE OWNED BOOK'S CLONE FIRST. With Godot drawing the title, the live catalogue points
		// into the hidden mirror — fading that one leaves the white plate on screen and reports
		// success. Falls back to the catalogue for the frames before the book has stood up.
		Node? flash = null;
		if (_books.Book(Engine_UiBookSet.TitleScreenId) is { } titleBook &&
			IsInstanceValid(titleBook) && titleBook.Visible)
		{
			flash = titleBook.Find(Engine_FrontEndTitle.FlashName);
		}

		string before = _title.LastReport;
		if (flash is not null)
		{
			_title.Advance(flash, delta);
		}
		else
		{
			_title.Advance(_presentation?.Theme, delta);
		}

		++_titleFrames;

		// REPORTED ON CHANGE, not on a frame count. The frame-count version fired once, sixty
		// frames after process start — which is before the title screen is revealed and before
		// its catalogue exists — and so reported "not in this screen's catalogue" on a run where
		// the fade then worked perfectly. A one-shot sample of a state that has not happened yet
		// is worse than no sample: it reads as a failure.
		if (_title.LastReport != before)
		{
			GD.Print($"[game-ui] logo flash on '{current}' — {_title.LastReport}");
		}

	}


	private int _titleFrames;

	private readonly Engine_FrontEndTitle _title = new();

	private string? _titleScreen;

	/// <summary>
	/// One element's live tint, as weight plus colour, or null when the guest states no weight.
	///
	/// Both halves or neither: a weight with no colour subtracts nothing, and a colour with no
	/// weight is inert. The guest writes them together and so does this.
	/// </summary>
	private static Engine_UiScreen.LiveTint? ReadTint(Engine_UiModel.LiveAttribute[,] attributes, int i)
	{
		if (!attributes[i, Engine_UiLiveAttributes.TintingAmountSlot].Present)
		{
			return null;
		}

		return new Engine_UiScreen.LiveTint(
			attributes[i, Engine_UiLiveAttributes.TintingAmountSlot].AsFloat,
			attributes[i, Engine_UiLiveAttributes.DiffuseRSlot].Present
				? attributes[i, Engine_UiLiveAttributes.DiffuseRSlot].AsFloat * Engine_UiLiveAttributes.DiffuseScale : null,
			attributes[i, Engine_UiLiveAttributes.DiffuseGSlot].Present
				? attributes[i, Engine_UiLiveAttributes.DiffuseGSlot].AsFloat * Engine_UiLiveAttributes.DiffuseScale : null,
			attributes[i, Engine_UiLiveAttributes.DiffuseBSlot].Present
				? attributes[i, Engine_UiLiveAttributes.DiffuseBSlot].AsFloat * Engine_UiLiveAttributes.DiffuseScale : null);
	}

	/// <summary>The video player, so an embedded movie can be drawn into the element hosting it.</summary>
	public GameVideo? Video { get; set; }

	/// <summary>Element ordinals currently hosting a movie, per presentation address.</summary>
	private readonly System.Collections.Generic.Dictionary<uint, int> _movieSlots = new();

	/// <summary>Set by the frame that asks for an embedded preview; cleared in _Process. A frame
	/// with no ask takes the host's movie down — see the orphan teardown there.</summary>
	private bool _embeddedMovieAsked;

	/// <summary>
	/// True while any drawn presentation hosts an embedded movie — so the window's
	/// "hide the UI" handler does not conceal the screen a preview is playing inside.
	/// </summary>
	public bool HasEmbeddedMovie => _movieSlots.Count > 0;

	/// <summary>
	/// Draws the guest's embedded movie into the element hosting it. A sprite whose `Movie`
	/// string is non-empty draws a movie into its OWN rect instead of a texture;
	/// `RefiiHostGetUiMovie` reads it off the same sprite base as the texture hash, so its
	/// ordinal joins like every other live array. The frame is STRETCHED — the guest's draw
	/// never reads the movie's own size.
	/// </summary>
	private void FollowMovie(Engine_UiDrawnPresentation drawn, uint address)
	{
		if (Video is null || drawn.Builder is null)
		{
			return;
		}

		if (HostBridge.GetUiMovie(address) is not { } movie)
		{
			if (_movieSlots.Remove(address) && !HasEmbeddedMovie)
			{
				Video.EmbedOrdinal = -1;
			}

			return;
		}

		if (!_movieSlots.TryGetValue(address, out int was) || was != movie.Ordinal)
		{
			_movieSlots[address] = movie.Ordinal;
			GD.Print($"[game-ui] '{drawn.Name}' element {movie.Ordinal} is hosting a movie: " +
				$"{movie.Path}");
		}

		Video.EmbedOrdinal = movie.Ordinal;

		// The element the ATTRIBUTE sits on is a Node whose quad is the card; the guest also
		// authors `Movie` on its Material child, which has no quad of its own. Painting is
		// therefore skipped rather than forced when the ordinal has no quad.
		if (movie.Ordinal < drawn.Builder.ByOrdinal.Length &&
			drawn.Builder.ByOrdinal[movie.Ordinal] is MeshInstance3D quad &&
			Video.FrameTexture is { } frame)
		{
			Engine_UiSceneBuilder.SetQuadTexture(quad, frame);
		}
	}

	/// <summary>
	/// Reports WHERE the moving parts have got to, about once a second.
	///
	/// The backdrop is a strip of 512-wide panels drawn twice, and the copy is nested inside the
	/// scrolling group — so whether the two copies abut, overlap or leave a hole is decided by a
	/// couple of live positions and nothing else. A still frame cannot tell a hole in the wrap
	/// from a panel that failed to draw, and the file's own numbers cannot either: it parks the
	/// second copy 131 units short of the first copy's end, which the guest then moves.
	///
	/// Generic rather than keyed on element names — it reports whatever moved, so it is worth
	/// the same on the HUD.
	/// </summary>
	private void ReportScroll(Engine_UiDrawnPresentation drawn, int moved)
	{
		if (drawn.Builder is null)
		{
			return;
		}

		// A SETTLED SCREEN IS REPORTED TOO, once. Gating on `moved > 0` only ever described a
		// screen mid-animation, and the state worth measuring is the one after it stops.
		bool settled = moved == 0 && _animated.Contains(_lastAddress) && _stilled.Add(_lastAddress);

		if (!settled && (moved == 0 || ++_scrollTick % ScrollSampleFrames != 0))
		{
			return;
		}

		var report = new System.Text.StringBuilder(
			$"[game-ui] '{drawn.Name}' {(settled ? "settled" : "moving")}:");
		int shown = 0;

		Node3D?[] byOrdinal = drawn.Builder.ByOrdinal;
		for (int i = 0; i < byOrdinal.Length && shown < ScrollReportLimit; ++i)
		{
			// Only the groups are interesting — a panel inherits its parent's motion and would
			// fill the line with numbers that say the same thing.
			if (byOrdinal[i] is not { } node || node.GetChildCount() == 0)
			{
				continue;
			}

			// Scale only when it is not 1. The panels are laid out edge to edge in their own
			// space, so the ONE thing that can open a hole between the two copies of a strip is
			// a scale on a group — the positions alone say they always overlap by 131.
			string scale = node.Scale.IsEqualApprox(Vector3.One)
				? string.Empty
				: $"*{node.Scale.X:F3},{node.Scale.Y:F3}";

			report.Append($"  {node.Name}=({node.Position.X:F0},{node.Position.Y:F0}){scale}");
			++shown;
		}

		report.Append(Coverage(drawn));

		if (shown > 0)
		{
			// Change-gated: a screen that is mid-animation reports identical geometry every
			// sample, and printing that at the sample rate buries the transitions that matter.
			// The throttle above narrows it; this stops the identical repeats entirely.
			string text = report.ToString();
			if (text == _lastScrollReport)
			{
				return;
			}
			_lastScrollReport = text;
			GD.Print(text);
		}
	}

	private string _lastScrollReport = string.Empty;

	/// <summary>
	/// Where a drawn presentation's quads actually PAINT, and the largest hole in them.
	///
	/// THIS MEASURED THE WRONG THING FIRST TIME and the correction is the whole point. It took
	/// the span of every BUILT quad and reported "no holes" on every frame, across spans as wide
	/// as x=[-1173..5864] — while a black band sat on screen. Both statements were true: the
	/// geometry is continuous and the pixels are not. A quad that is hidden, has no albedo
	/// texture, or is fully transparent occupies its span and paints none of it, so counting it
	/// answers a question nobody asked.
	///
	/// So a span is only counted when the quad actually paints, and the three reasons one might
	/// not are reported SEPARATELY — hidden, untextured and transparent are three different
	/// defects with three different fixes, and a single "not drawing" total cannot tell them
	/// apart. If the hole is real the edges are exact; if the painted band is contiguous and the
	/// black is still there, it belongs to a different presentation.
	/// </summary>
	private static string Coverage(Engine_UiDrawnPresentation drawn)
	{
		if (drawn.Builder is null)
		{
			return string.Empty;
		}

		var spans = new System.Collections.Generic.List<(float Min, float Max)>();
		int hidden = 0;
		int untextured = 0;
		int transparent = 0;

		// NAMED, not just counted. "2 PAINTING UNTEXTURED" says a flat plate is being painted
		// over the game somewhere and gives no way to find it; a screenshot of a magenta
		// rectangle on the HUD and a count of two are the same fact reported twice, neither
		// actionable. The name is the join to the file, where the element's texture and colour
		// can be read directly.
		var untexturedNames = new System.Collections.Generic.List<string>();

		// Every painting quad as (front Z, area, node), for the covering report below.
		var frontMost = new System.Collections.Generic.List<(float Z, float Area, MeshInstance3D Mesh)>();

		MeshInstance3D? leftMost = null;
		MeshInstance3D? rightMost = null;
		float leftX = 0f;
		float rightX = 0f;
		MeshInstance3D? widest = null;
		float widestSpan = 0f;

		foreach (Node3D? node in drawn.Builder.ByOrdinal)
		{
			if (node is not MeshInstance3D mesh)
			{
				continue;
			}

			if (!mesh.IsVisibleInTree())
			{
				++hidden;
				continue;
			}

			// A MeshInstance3D WITH NO MESH DRAWS NOTHING — skip it before it can be counted
			// or measured. The builder returns exactly that for a text element whose glyph
			// build produced no geometry: an empty label keeps its node so the metadata still
			// hangs off it, but gets neither a mesh nor a material.
			//
			// Counting those cost two wrong readings at once. They have no material, so they
			// were filed under PAINTING UNTEXTURED and named as suspects — `Subtitles/Group/Text`
			// and `LookAt/LookAt/Text`, neither of which paints a pixel. And `GetAabb()` on a
			// meshless instance is an empty box at the node's ORIGIN, which still joins the
			// span union: that is what put a HUD whose real content spans x=[-466..575] on
			// record as reaching x=-1283.
			if (mesh.Mesh is null)
			{
				continue;
			}

			// The builder puts the quad's material on the override slot; the surface slot is the
			// fallback for anything cloned or re-materialised since. Read through the builder
			// so a quad and a text mesh both answer — see DescribeQuad.
			Engine_UiSceneBuilder.QuadPaint paint = Engine_UiSceneBuilder.DescribeQuad(mesh);
			if (paint.Opacity <= 0f && paint.Texture is null && !paint.DetailMapped)
			{
				++untextured;
				Name(untexturedNames, mesh, "no material");
				continue;
			}

			if (!paint.Painted)
			{
				++transparent;
				continue;
			}

			// COUNTED AS PAINTING, and flagged. A quad with no albedo texture is not silent —
			// the material paints its albedo colour over the whole quad, so it covers its span
			// with a flat plate. Filing it under "not drawing" was backwards, and that is the
			// same mistake in miniature as counting geometry instead of pixels.
			// A DETAIL-MAPPED QUAD IS TEXTURED even with no primary: its RGB comes from the
			// detail map, and the primary would only have supplied alpha. Counting those as
			// untextured would report the entire frame widget as painting flat colour.
			if (paint.Texture is null && !paint.DetailMapped)
			{
				++untextured;

				// The COLOUR it is painting, because that is what identifies it on screen. A
				// quad with no albedo texture paints the .fac record's vertex colour flat, so
				// the magenta plate in a screenshot and the entry in this list are matched by
				// eye without another run.
				Color c = paint.Tint;
				Name(untexturedNames, mesh,
					$"rgba({c.R:0.##},{c.G:0.##},{c.B:0.##},{c.A:0.##})" +
					(mesh.HasMeta("texture_path")
						? $" wanted {(string)mesh.GetMeta("texture_path")}"
						: " no texture named"));
			}

			Aabb box = mesh.GlobalTransform * mesh.GetAabb();
			spans.Add((box.Position.X, box.End.X));

			// WHAT IS IN FRONT. The span says how wide the painting is, not what the player
			// sees. These presentations share one orthographic camera and write no depth, so
			// covering is decided purely by the Z sort.
			frontMost.Add((box.End.Z, box.Size.X * box.Size.Y, mesh));

			// THE OUTLIERS BY NAME — the span's union cannot say which element reaches past it.
			if (leftMost is null || box.Position.X < leftX)
			{
				leftX = box.Position.X;
				leftMost = mesh;
			}

			if (rightMost is null || box.End.X > rightX)
			{
				rightX = box.End.X;
				rightMost = mesh;
			}

			// THE WIDEST PAINTING QUAD, BY NAME. The span line reports the union of every
			// painting quad, so a single element reaching past the canvas edge shows up only as
			// an odd number — `x=[-1283..642]` on a 1280-wide canvas — with nothing to say
			// whether that is one deliberately over-wide backdrop or something the transform
			// pass has stretched. The name settles it without another run.
			if (box.Size.X > widestSpan)
			{
				widestSpan = box.Size.X;
				widest = mesh;
			}
		}

		string widestNote = string.Empty;
		if (widest is not null)
		{
			var named = new System.Collections.Generic.List<string>();
			Name(named, widest, $"{widestSpan:F0} wide");
			widestNote = $"  widest={named[0]}";
		}

		// The three front-most quads big enough to matter, nearest first. Area-gated at a
		// twentieth of the canvas so the report names backdrops and panels rather than the
		// topmost pixel of a cursor.
		string covering = string.Empty;
		if (frontMost.Count > 0)
		{
			frontMost.Sort((a, b) => b.Z.CompareTo(a.Z));
			var names = new System.Collections.Generic.List<string>();
			foreach ((float z, float area, MeshInstance3D mesh) in frontMost)
			{
				if (area < 960f * 720f / 20f)
				{
					continue;
				}

				var one = new System.Collections.Generic.List<string>();
				Name(one, mesh, $"z={z:F0} {Mathf.Sqrt(area):F0}²");
				names.Add(one[0]);
				if (names.Count == 3)
				{
					break;
				}
			}

			if (names.Count > 0)
			{
				covering = "  infront=" + string.Join(" > ", names);
			}
		}

		string edges = string.Empty;
		if (leftMost is not null && rightMost is not null)
		{
			var l = new System.Collections.Generic.List<string>();
			Name(l, leftMost, $"x={leftX:F0}");
			var r = new System.Collections.Generic.List<string>();
			Name(r, rightMost, $"x={rightX:F0}");
			edges = $"  leftmost={l[0]}  rightmost={r[0]}";
		}

		string silent = widestNote + edges + covering +
			$" ({hidden} hidden, {transparent} transparent, " +
			$"{untextured} PAINTING UNTEXTURED" +
			(untexturedNames.Count == 0
				? string.Empty
				: ": " + string.Join("; ", untexturedNames)) + ")";

		if (spans.Count == 0)
		{
			return $"  paints=none{silent}";
		}

		spans.Sort((a, b) => a.Min.CompareTo(b.Min));

		// The canvas half-width the camera shows at 16:9. Only a hole INSIDE what the player can
		// see is a defect; the strip runs far off both sides by design.
		const float visible = 640f;
		float reach = spans[0].Max;
		float worstGap = 0f;
		float gapAt = 0f;

		foreach ((float min, float max) in spans)
		{
			if (min > reach && min < visible && reach > -visible)
			{
				float gap = min - reach;
				if (gap > worstGap)
				{
					worstGap = gap;
					gapAt = reach;
				}
			}

			reach = System.Math.Max(reach, max);
		}

		return $"  paints x=[{spans[0].Min:F0}..{reach:F0}] in {spans.Count} quad(s)" +
			(worstGap > 0.5f ? $"  HOLE {worstGap:F0} wide at x={gapAt:F0}" : "  no holes") +
			silent;
	}

	private int _scrollTick;

	/// <summary>Frames between scroll reports — about once a second at any display rate.</summary>
	private const int ScrollSampleFrames = 60;

	/// <summary>Groups per report. Enough to see a wrap, few enough to stay one line.</summary>
	private const int ScrollReportLimit = 6;

	/// <summary>Presentations already reported as animating, so the line fires once each.</summary>
	private readonly System.Collections.Generic.HashSet<uint> _animated = new();

	/// <summary>
	/// Presentations already reported in their SETTLED state, so that census fires once each
	/// rather than every frame the screen sits still — which is most of them.
	/// </summary>
	private readonly System.Collections.Generic.HashSet<uint> _stilled = new();

	/// <summary>The address whose <see cref="Engine_UiDrawnPresentation"/> is being reported, for the settled test.</summary>
	private uint _lastAddress;

	/// <summary>Presentations reported as still being built, so that line fires once each too.</summary>
	private readonly System.Collections.Generic.HashSet<uint> _mismatched = new();

	/// <summary>Presentations already reported as Godot-owned, so the line prints once each.</summary>
	private readonly System.Collections.Generic.HashSet<string> _unmirrored = new();

	/// <summary>
	/// Presentations withheld because the guest says no screen is up, so that line fires once
	/// each rather than several times a second for as long as the player is walking around.
	/// </summary>
	private readonly System.Collections.Generic.HashSet<uint> _suppressed = new();

	/// <summary>
	/// Presentations whose live VALUES have landed at least once, so that line fires once each.
	///
	/// Separate from <see cref="_animated"/> because they are different claims about the same
	/// screen: a HUD can be animating — its needles swinging, its groups fading — while every
	/// bar it owns sits at the artist's authored zero because no meter ever resolved.
	/// </summary>
	private readonly System.Collections.Generic.Dictionary<uint, string> _valued = new();

	/// <summary>Frames between live-value samples — about once a second at any display rate.</summary>
	private const int ValueSampleFrames = 60;

	private int _valueTick;

	/// <summary>
	/// What each presentation's <c>ExpandableMenu</c> last contained, so a change is reported.
	///
	/// REPORTED ON CHANGE RATHER THAN ONCE, and that is the whole reason this is a dictionary
	/// and not another <c>HashSet</c>. The guest spawns a menu's entries AFTER the presentation
	/// appears — the pause menu shows up with an empty 26-button pool and fills it over the
	/// following frames — so a once-per-build line samples the one moment the menu is
	/// guaranteed to be empty and reports that as the answer. Keyed by address like the rest,
	/// and cleared in <see cref="Retire"/>.
	/// </summary>
	private readonly System.Collections.Generic.Dictionary<uint, string> _menuContents = new();

	/// <summary>
	/// Says what the guest has put in this presentation's menu, whenever that changes.
	///
	/// The pause menu is the case this exists for. Its entries are not in the file and not in
	/// the shipped Lua's population table — they are spawned at runtime — so whether the guest
	/// has filled the button pool is a question no static read can answer, and it is the
	/// question that decides whether Godot can own the list the way it owns the front end's.
	/// Silent for every screen without an <c>ExpandableMenu</c>, which is most of them.
	/// </summary>
	/// <summary>
	/// Adds a node to a diagnostic list, by the path that locates it in the file.
	///
	/// The leaf name alone is useless on a HUD: slice names repeat across every frame group, so
	/// a dozen unrelated elements are all called <c>bar</c>. Two ancestors are enough to place
	/// one without making the line unreadable. Capped, because a fault that hits every quad
	/// would otherwise print three thousand names.
	/// </summary>
	private static new void Name(System.Collections.Generic.List<string> into, Node node, string why)
	{
		const int limit = 8;
		if (into.Count >= limit)
		{
			return;
		}

		if (into.Count == limit - 1)
		{
			into.Add("…more");
			return;
		}

		string path = node.Name;
		Node? parent = node.GetParent();
		for (int up = 0; up < 2 && parent is not null; ++up, parent = parent.GetParent())
		{
			path = parent.Name + "/" + path;
		}

		into.Add($"{path} [{why}]");
	}

	/// <summary>
	/// Which of a row's THREE stacked faces the guest is actually drawing.
	///
	/// A row is `Rim` + `InnerBrown` (outer pieces authored at alpha 0, only its inner `bar`
	/// opaque) + `InnerRed` (Left/Bar/Right at alpha 1, and NEARER the camera at z+2) +
	/// `Stars`. Cloned straight from the file that stacks up as InnerRed's pale face over the
	/// brown one, with a star — which is exactly how the owned rows look and the abilities
	/// screen's do not.
	///
	/// ALPHA CANNOT SETTLE IT: the draw gate is the globalactive bit, not opacity, so a face
	/// can sit at alpha 1 and never be drawn. This prints the guest's own ACTIVE flags for the
	/// three faces of the first filled row, which is the measurement that says whether the
	/// original hides InnerRed (and the star) or tints them.
	/// </summary>
	private static string DescribeRowFaces(Engine_UiScreen? screen, bool[] visible,
		Engine_UiScreen.LiveTint?[]? tint, float?[]? opacity)
	{
		if (screen is null)
		{
			return string.Empty;
		}

		UiNode? group = Engine_ExpandableMenuItems.FindGroup(screen);
		UiNode? row = group is null || group.Children.Count == 0 ? null : group.Children[0];
		UiNode? tab = Engine_UiStatedPanel.Child(row, "Tab");
		if (tab is null)
		{
			return string.Empty;
		}

		var parts = new System.Collections.Generic.List<string>();
		foreach (string face in new[] { "Rim", "InnerBrown", "InnerRed", "Stars" })
		{
			if (Engine_UiStatedPanel.Child(tab, face) is not { } node)
			{
				continue;
			}

			int active = 0;
			int total = 0;
			foreach (UiNode child in node.Children)
			{
				++total;
				if (child.Ordinal < visible.Length && visible[child.Ordinal])
				{
					++active;
				}
			}

			bool self = node.Ordinal < visible.Length && visible[node.Ordinal];

			// OPACITY IS WHAT SEPARATES THE TWO FACES, and it took reading the script to know
			// it: `UpdateTabInnerTextureMenu` / `…SubMenu` raise one face to 100 and drop the
			// other to 0 — on the GROUP, never the quads — which is invisible to an active-bit
			// count. Printed here so the running game confirms the contract the bytecode
			// states rather than us assuming we read it right.
			string alpha = opacity is not null && node.Ordinal < opacity.Length &&
				opacity[node.Ordinal] is { } liveOpacity ? $" op {liveOpacity:F0}" : " op unstated";

			// The tint rides on the BORDERS below each face, not on the face group, so this
			// is normally blank here — kept because a value appearing would mean the guest
			// tints a level higher than the script does.
			string colour = string.Empty;
			if (tint is not null && node.Ordinal < tint.Length &&
				tint[node.Ordinal] is { } stated)
			{
				colour = $" tint({stated.Amount:F2}" +
					(stated.R is { } r ? $" rgb {r:F2},{stated.G ?? 0f:F2},{stated.B ?? 0f:F2}" : " rgb unstated") +
					")";
			}

			parts.Add($"{face}={(self ? "on" : "OFF")} {active}/{total}{alpha}{colour}");
		}

		return parts.Count == 0 ? string.Empty : $"; row faces {string.Join(' ', parts)}";
	}

	/// <summary>
	/// Every named element on a screen that carries LIVE TEXT, outside the row pool.
	///
	/// THE RAW MATERIAL FOR OWNING THE REST OF A SCREEN. The list is owned; the panel beside
	/// it — the item's title, its description, its stat lines, the page counter — is still the
	/// guest's own tree with the guest's own layout written onto it. Owning those means
	/// reading the same values by NAME and laying them out ourselves, and the first thing that
	/// needs is the guest's own vocabulary: which elements it writes, and what it writes.
	///
	/// Printed on CHANGE and capped, so a screen that repaints one field says so in one line
	/// rather than dumping a tree. The pool is excluded because
	/// <see cref="Engine_ExpandableMenuItems"/> already reports it and it is the bulk of the
	/// text on these screens.
	/// </summary>
	private void ReportScreenText(uint address, string name, Engine_UiScreen? screen,
		string?[] texts, bool[] visible)
	{
		if (screen is null)
		{
			return;
		}

		int poolOrdinal = Engine_ExpandableMenuItems.FindGroup(screen)?.Ordinal ?? -1;
		var fields = new System.Collections.Generic.List<string>();
		foreach (UiNode node in screen.Nodes)
		{
			if (node.Text is null || node.Ordinal < 0 || node.Ordinal >= texts.Length ||
				texts[node.Ordinal] is not { Length: > 0 } live ||
				node.Ordinal >= visible.Length || !visible[node.Ordinal] ||
				Under(node, poolOrdinal))
			{
				continue;
			}

			// The AUTHORED tag is what a placeholder looks like; a field the guest never wrote
			// still answers with it, exactly as the row pool's buttons do.
			if (string.Equals(live, node.Text.Tag, System.StringComparison.Ordinal))
			{
				continue;
			}

			fields.Add($"{Path(node)}='{live}'");
			if (fields.Count >= ScreenTextCap)
			{
				break;
			}
		}

		if (fields.Count == 0)
		{
			return;
		}

		string described = string.Join("  ", fields);
		if (_screenText.TryGetValue(address, out string? was) &&
			string.Equals(was, described, System.StringComparison.Ordinal))
		{
			return;
		}

		_screenText[address] = described;
		GD.Print($"[game-ui] '{name}' live text: {described}");

		static bool Under(UiNode node, int ordinal)
		{
			for (UiNode? at = node; at is not null; at = at.Parent)
			{
				if (at.Ordinal == ordinal)
				{
					return true;
				}
			}

			return false;
		}

		static string Path(UiNode node)
		{
			string path = node.Name;
			for (UiNode? at = node.Parent; at?.Parent is not null; at = at.Parent)
			{
				path = $"{at.Name}.{path}";
			}

			return path;
		}
	}

	/// <summary>
	/// Prints what an owned menu view actually drew, when it differs from last time.
	///
	/// WHAT THE MODEL SAYS IS NOT EVIDENCE. The pause list reported eight entries with eight
	/// correct labels while the screen showed six blank tabs under two labelled ones, because
	/// every diagnostic we had described the MODEL and the model was right. This one walks the
	/// row nodes, so a row that drew nothing says so — and says whether it was the entry, the
	/// string, the glyph mesh or the position that failed.
	///
	/// Change-keyed rather than per frame: the row set is stable for as long as nobody moves,
	/// and a line per frame would bury it.
	/// </summary>
	private void ReportMenuView(Engine_FrontEndMenuView? view)
	{
		if (view is null || !IsInstanceValid(view))
		{
			return;
		}

		// Keyed with the positions STRIPPED: a slide animation moves every row a little each
		// frame, and a report that fires on coordinates prints sixteen near-identical lines
		// per press. The printed line keeps them — the change that matters is which entries,
		// labels and meshes the rows hold.
		//
		// Key first, string only on a change; runs every frame a view stands. See
		// Engine_FrontEndMenuView.DescribeKey.
		long changeKey = view.DescribeKey();

		// The report is per name; `Node.Name` allocates on every read, so the name is held per
		// instance. A rebuilt view is a new instance and reads its name again.
		ulong instance = view.GetInstanceId();
		if (!_menuViewNames.TryGetValue(instance, out string? key))
		{
			key = view.Name.ToString();
			_menuViewNames[instance] = key;
		}

		if (_menuViews.TryGetValue(key, out long was) && was == changeKey)
		{
			return;
		}

		_menuViews[key] = changeKey;
		GD.Print(view.Describe());
	}

	private readonly System.Collections.Generic.Dictionary<string, long> _menuViews = new();

	private readonly System.Collections.Generic.Dictionary<ulong, string> _menuViewNames = new();

	/// <summary>How many live text fields one report may name. A whole book page is ~20.</summary>
	private const int ScreenTextCap = 40;

	private readonly System.Collections.Generic.Dictionary<uint, string> _screenText = new();

	private void ReportMenuContents(uint address, string name, Engine_UiScreen? screen,
		string?[] texts, bool[] visible, int[] slides, float?[]?[]? transform)
	{
		System.Collections.Generic.IReadOnlyList<Engine_ExpandableMenuItems.Button> buttons =
			Engine_ExpandableMenuItems.Read(screen, texts, visible, slides);
		if (buttons.Count == 0)
		{
			return;
		}

		// WHETHER THE GUEST'S OWN FORMATTER RAN, which is the other half of the question and
		// not answerable from the labels. `expandablemenuformatting.lua` lays the pool out by
		// writing `position.x` and `position.y` on each button component — attributes we
		// already read — so buttons that all still share one position are buttons the
		// formatter never touched. The file authors all 26 on top of each other, so "stacked"
		// is the untouched state and reads as a single bar on screen.
		string laidOut = Engine_ExpandableMenuItems.DescribeLayout(buttons, transform);

		_drawn.TryGetValue(address, out Engine_UiDrawnPresentation? drawn);
		string described = $"{Engine_ExpandableMenuItems.Describe(buttons)}; " +
			$"{laidOut}{DescribeRowFaces(screen, visible, drawn?.Tint, drawn?.Opacity)}";
		if (_menuContents.TryGetValue(address, out string? was) &&
			string.Equals(was, described, System.StringComparison.Ordinal))
		{
			return;
		}

		_menuContents[address] = described;
		GD.Print($"[game-ui] '{name}' menu: {described}");
	}

	/// <summary>How many rebuild-cause lines each screen has printed. Capped at 3.</summary>
	private readonly System.Collections.Generic.Dictionary<uint, int> _rebuildReported = new();

	/// <summary>Screens that have taken the in-place opacity path, so it reports once each.</summary>
	private readonly System.Collections.Generic.HashSet<uint> _opacityInPlace = new();

	private Camera3D? _uiCamera;
	private Node3D? _uiCameraMount;

	/// <summary>
	/// Keeps the owned canvas camera CURRENT and FITTED, every frame.
	///
	/// Current: a presentation tree being freed promotes some other camera — including the
	/// authored perspective cameras mirrored screens carry — and one frame of somebody else's
	/// projection is a screen at the wrong scale. Fitted: the window can resize at any time,
	/// and the fit decides whether the design box is cropped or letterboxed.
	/// </summary>
	private void DriveUiCamera()
	{
		if (_viewport is null || _uiCamera is null || !GodotObject.IsInstanceValid(_uiCamera))
		{
			return;
		}

		if (_viewport.GetCamera3D() != _uiCamera)
		{
			string was = _viewport.GetCamera3D()?.Name.ToString() ?? "none";
			_uiCamera.MakeCurrent();
			GD.Print($"[game-ui] the UI camera was '{was}' — re-asserted the canvas camera");
		}

		Vector2 size = _viewport.Size;
		if (size != _uiCameraFitted && size.X > 0 && size.Y > 0)
		{
			_uiCameraFitted = size;
			Engine_UiSceneBuilder.FitCanvasCamera(_uiCamera, size);
		}
	}

	private Vector2 _uiCameraFitted;

	/// <summary>
	/// The key over the last `[ui-camera]` line — see <see cref="ReportCamera"/>. Nullable so
	/// the first sample cannot be mistaken for an unchanged one.
	/// </summary>
	private int? _cameraReported;

	/// <summary>
	/// The projection ground truth, printed on change.
	///
	/// EXISTS BECAUSE OF A SCALE DEFECT NOBODY COULD LOCATE. The in-game UI drew at roughly
	/// 0.57 of its harness size — suspiciously 720/1280 — and every candidate (live
	/// transforms, widescreen fixups, the camera, the SubViewport mapping) was invisible in
	/// the log. This line names the CURRENT camera, its projection numbers, and the three
	/// sizes a quad passes through on its way to the window, so a mis-fit is one grep instead
	/// of a run of pixel measurements on a screenshot.
	/// </summary>
	private void ReportCamera()
	{
		if (_viewport is null || _viewport.GetCamera3D() is not { } camera)
		{
			return;
		}

		// Key first, string only on a change. The key covers exactly what the line prints, at
		// the precision it prints it (`size` to one decimal), so a change too small to show
		// does not fire a report.
		bool pauseUp = _pauseView is not null && GodotObject.IsInstanceValid(_pauseView) &&
			_pauseView.Visible;
		Vector3 pauseAt = pauseUp ? _pauseView!.GlobalPosition : Vector3.Zero;
		var key = new System.HashCode();
		key.Add(camera.GetInstanceId());
		key.Add((int)camera.Projection);
		key.Add((int)camera.KeepAspect);
		key.Add(System.MathF.Round(camera.Size, 1, System.MidpointRounding.AwayFromZero));
		key.Add(camera.GlobalPosition);
		key.Add(_viewport.Size);
		key.Add(_container?.Size);
		key.Add(GetViewport().GetVisibleRect().Size);
		key.Add(pauseUp);
		key.Add(pauseAt);
		key.Add(pauseUp ? camera.UnprojectPosition(pauseAt) : Vector2.Zero);

		int reported = key.ToHashCode();
		if (reported == _cameraReported)
		{
			return;
		}

		_cameraReported = reported;
		GD.Print($"[ui-camera] '{camera.Name}' proj={camera.Projection} " +
			$"keep={camera.KeepAspect} size={camera.Size:0.#} pos={camera.GlobalPosition} " +
			$"viewport={_viewport.Size} container={_container?.Size} " +
			$"window={GetViewport().GetVisibleRect().Size}" +
			(pauseUp
				? $" pauseView@{pauseAt} screen={camera.UnprojectPosition(pauseAt)}"
				: string.Empty));
	}

	/// <summary>
	/// Names the input that failed a drawn screen's unchanged test, with the first differing
	/// ordinal and both values — the one question ~844 silent pause rebuilds could not answer.
	/// </summary>
	// Follow's per-sample scratch (see the comment at its read). Main thread; sized to the
	// largest presentation sampled, never shrunk.
	private int[] _followSlides = System.Array.Empty<int>();
	private string?[] _followTexts = System.Array.Empty<string?>();
	private bool[] _followVisible = System.Array.Empty<bool>();
	private float?[] _followOpacity = System.Array.Empty<float?>();
	private Engine_UiScreen.LiveTint?[] _followTint = System.Array.Empty<Engine_UiScreen.LiveTint?>();

	/// <summary>The HUD's texture hashes, one exactly-sized array refilled in place per cadence.</summary>
	private uint[]? _hudTextures;

	private static T[] Scratch<T>(ref T[] kept, int count)
	{
		if (kept.Length < count)
		{
			kept = new T[System.Math.Max(count, kept.Length * 2)];
		}

		return kept;
	}

	/// <summary>Whether the first <paramref name="count"/> of <paramref name="sample"/> equal <paramref name="held"/> whole.</summary>
	private static bool SameLeading<T>(T[] sample, int count, T[] held)
	{
		if (held.Length != count)
		{
			return false;
		}

		var comparer = System.Collections.Generic.EqualityComparer<T>.Default;
		for (int i = 0; i < count; ++i)
		{
			if (!comparer.Equals(sample[i], held[i]))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// The HUD's stated texture per element, into ONE array sized to the element count and
	/// refilled in place: the world icons read it during their sample and the HUD view holds
	/// it as the newest sample, which is what a fresh array per cadence gave them too.
	/// </summary>
	private uint[]? SampleHudTextures(int elementCount)
	{
		System.ReadOnlySpan<uint> read = HostBridge.ReadUiTextureHashes(_hudAddress, elementCount);
		if (read.Length != elementCount)
		{
			return null;
		}

		if (_hudTextures is null || _hudTextures.Length != elementCount)
		{
			_hudTextures = new uint[elementCount];
		}

		read.CopyTo(_hudTextures);
		return _hudTextures;
	}

	private static string DescribeChange(Engine_UiDrawnPresentation existing,
		int[] slides, bool[] visible, float?[]? opacity, uint[]? textures,
		Engine_UiScreen.LiveTint?[]? tint)
	{
		var causes = new System.Collections.Generic.List<string>();

		if (existing.Slides is null || existing.Visible is null)
		{
			causes.Add("no prior sample");
		}
		else
		{
			for (int i = 0; i < slides.Length && i < existing.Slides.Length; ++i)
			{
				if (slides[i] != existing.Slides[i])
				{
					causes.Add($"slide[{i}] {existing.Slides[i]}→{slides[i]}");
					break;
				}
			}

			for (int i = 0; i < visible.Length && i < existing.Visible.Length; ++i)
			{
				if (visible[i] != existing.Visible[i])
				{
					causes.Add($"active[{i}] {existing.Visible[i]}→{visible[i]}");
					break;
				}
			}
		}

		if ((opacity is null) != (existing.Opacity is null))
		{
			causes.Add($"opacity {(existing.Opacity is null ? "null→read" : "read→null")}");
		}
		else if (opacity is not null && existing.Opacity is not null)
		{
			for (int i = 0; i < opacity.Length && i < existing.Opacity.Length; ++i)
			{
				if (opacity[i] != existing.Opacity[i])
				{
					causes.Add($"opacity[{i}] {existing.Opacity[i]?.ToString("0.##") ?? "-"}→" +
						$"{opacity[i]?.ToString("0.##") ?? "-"}");
					break;
				}
			}
		}

		if ((textures is null) != (existing.Textures is null))
		{
			// GetUiTextureHashes returns null whenever the guest's count disagrees with the
			// element count for one sample — a transient that would otherwise read as a
			// texture change.
			causes.Add($"textures {(existing.Textures is null ? "null→read" : "read→NULL (transient count mismatch?)")}");
		}
		else if (textures is not null && existing.Textures is not null)
		{
			for (int i = 0; i < textures.Length && i < existing.Textures.Length; ++i)
			{
				if (textures[i] != existing.Textures[i])
				{
					causes.Add($"texture[{i}] {existing.Textures[i]:X8}→{textures[i]:X8}");
					break;
				}
			}
		}

		if ((tint is null) != (existing.Tint is null))
		{
			causes.Add($"tint {(existing.Tint is null ? "null→read" : "read→null")}");
		}
		else if (tint is not null && existing.Tint is not null)
		{
			for (int i = 0; i < tint.Length && i < existing.Tint.Length; ++i)
			{
				if (!System.Nullable.Equals(tint[i], existing.Tint[i]))
				{
					causes.Add($"tint[{i}] {existing.Tint[i]?.ToString() ?? "-"}→" +
						$"{tint[i]?.ToString() ?? "-"}");
					break;
				}
			}
		}

		return causes.Count == 0
			? "NO DIFFERENCE FOUND — lengths differ, or the test and this report disagree"
			: string.Join(", ", causes);
	}

	/// <summary>Takes one drawn presentation off screen and forgets it.</summary>
	private void Retire(uint address, string why)
	{
		if (!_drawn.TryGetValue(address, out Engine_UiDrawnPresentation? drawn))
		{
			return;
		}

		GD.Print($"[game-ui] retired '{drawn.Name}' — {why}");
		drawn.Node.QueueFree();
		_drawn.Remove(address);

		// The owned in-game list belongs to the presentation that carried it — its rows are
		// the guest's live text on THAT tree's elements, so both the model and the view go.
		_screenLists.Remove(address);
		if (_screenMenuAddress == address)
		{
			Engine_UiOptionsPending.Leave();
			_screenMenuAddress = 0;
			_screenMenu = null;
			_screenMenuView?.QueueFree();
			_screenMenuView = null;
			_screenMenuScreen = uint.MaxValue;
			HoldScreenStick(0);
			GameInput.ScreenMenuOwnsInput = false;
		}

		// The owned pause view stands over the mirrored pause tree; it comes down with it.
		// The B swallow clears HERE as well as in DrivePauseMenu, so B can never stay
		// masked past the menu that justified masking it.
		if (drawn.Name == PausePresentation)
		{
			_pauseTreeUp = false;
			if (_pauseView is not null)
			{
				_pauseView.Visible = false;
			}

			GameInput.PauseMenuSwallowsBack = false;
		}

		// So a presentation that comes back reports its movement again rather than staying
		// silent because a previous incarnation at the same address already had.
		_animated.Remove(address);
		_stilled.Remove(address);
		_movieSlots.Remove(address);
		_mismatched.Remove(address);
		_valued.Remove(address);
		_menuContents.Remove(address);
		_suppressed.Remove(address);
		_rebuildReported.Remove(address);
		_opacityInPlace.Remove(address);
	}

	/// <summary>
	/// Takes every drawn presentation down.
	///
	/// Needed wherever ownership of the screen changes hands, not just where the guest drops a
	/// presentation — otherwise a screen we were drawing outlives the thing that wanted it.
	/// </summary>
	private void RetireDrawn(string why)
	{
		if (_drawn.Count == 0)
		{
			return;
		}

		foreach (uint address in new System.Collections.Generic.List<uint>(_drawn.Keys))
		{
			Retire(address, why);
		}
	}

	/// <summary>
	/// Draws the front-end presentations the by-name path does not cover.
	///
	/// The by-name path knows two screens — the title and the main menu — because those are the
	/// two the guest names in its own constructor. It has never known about
	/// <c>scrollingbackground</c>, which the guest reports as SHOWING while the menu is up, and
	/// that is the grey void behind the menu: not a missing texture, a whole presentation
	/// nobody was drawing.
	///
	/// This is a step toward the by-name path going away entirely. It is NOT that step: the
	/// gate reports the title screen hidden for stretches while it is plainly on screen (the
	/// attract movie is up), so switching the active screen over to the gate wholesale would
	/// blank the front end. Adding what the gate says is showing, on top of what we already
	/// draw, is the half of that change which cannot regress.
	///
	/// Drawn BEHIND, at negative depth. These are backgrounds by definition — the by-name
	/// screen is the one the player is interacting with — and the front-end screen supplies the
	/// camera, so nothing here activates one.
	/// </summary>
	private void FollowFrontEndExtras(System.Collections.Generic.List<Engine_UiModel.Presentation> live)
	{
		if (_presentation is null)
		{
			return;
		}

		var wanted = new System.Collections.Generic.List<Engine_UiModel.Presentation>();
		foreach (Engine_UiModel.Presentation p in live)
		{
			if (!p.IsShowing)
			{
				continue;
			}

			// Skip whatever the by-name path is already drawing, or it would be built twice.
			string bank = System.IO.Path.GetFileNameWithoutExtension(p.Name.Replace('\\', '/'));
			if (string.Equals(bank, _showing, System.StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			// AND SKIP THE OTHER FRONT-END SCREEN, which is not an extra at all.
			//
			// There are exactly two — `frontendstartupscreen` and `frontendmainmenu` — and the
			// guest tells us which is ACTIVE; the by-name path draws that one and this path must
			// not draw the other. Its root keeps its active bit set after the hand-over, so
			// "the guest is showing it" is true of both, and drawing both puts the title
			// screen's "Press A" prompt over the menu. That is the stray A glyph floating in the
			// corner of every front-end screenshot: not a widget of ours, the whole title screen
			// underneath one.
			//
			// Derived from our own contract rather than a name list: `PresentationFor` is the
			// one place that enumerates the front end's screens, so a third would be covered
			// here the day it is added.
			if (Engine_UiFrontEndScreens.Is(bank))
			{
				continue;
			}

			wanted.Add(p);
		}

		var gone = new System.Collections.Generic.List<uint>();
		foreach (uint address in _drawn.Keys)
		{
			if (!wanted.Exists(p => p.Address == address))
			{
				gone.Add(address);
			}
		}

		foreach (uint address in gone)
		{
			Retire(address, "the guest stopped showing it");
		}

		foreach (Engine_UiModel.Presentation p in wanted)
		{
			Follow(p, -1);
		}
	}

	/// <summary>
	/// Draws the in-game UI the guest currently has live.
	///
	/// SAME PATH AS THE FRONT END, which is the point. The HUD is a Gameface presentation like
	/// every front-end screen — same factory, same element tree, same root at +500 — so it goes
	/// through the presentation host and the scene builder that already exist rather than
	/// through a second renderer. What the HUD needed was not drawing but FINDING: a walk that
	/// does not go through a field only the front-end GUI object has.
	/// </summary>
	private void FollowInGameUi(System.Collections.Generic.List<Engine_UiModel.Presentation> live)
	{
		if (_presentation is null)
		{
			return;
		}

		// WHAT THE GUEST SAYS IS SHOWING, not a list we keep.
		//
		// A hardcoded set of file names is the wrong shape twice over: it draws closed menus
		// (every live screen looks drawable) and it can never show a screen nobody thought to
		// add. The guest states this — a presentation is drawn
		// exactly when its ROOT element's `active` bit is set, which is the test its own update
		// makes before walking the tree at all — so following that needs no host-side list and
		// covers all thirty in-game screens for free.
		var wanted = new System.Collections.Generic.List<Engine_UiModel.Presentation>();
		bool intermissionSeen = false;
		bool displayBoxSeen = false;

		// OWNING A PANEL IS NOT THE SAME AS THERE BEING NO PANEL. `wanted` is the MIRROR set,
		// and an owned surface is deliberately kept out of it — but `HasInGameUi` is read by
		// GameWindow as "is there any UI at all", and it is what keeps the whole layer on
		// screen. Leaving the owned family uncounted meant a query box, a message box or a
		// menu box standing ALONE concealed the viewport it is drawn in: the layer came down
		// on top of the one thing it was up for. Intermission and the display box already
		// count themselves back in this way; this is the same statement for the rest.
		bool ownedSeen = false;
		foreach (Engine_UiModel.Presentation p in live)
		{
			if (!p.IsShowing)
			{
				continue;
			}

			// AN OWNED MODAL BOX REPLACES ITS MIRROR — drawing both is two boxes on one
			// screen, the same rule as every other owned panel. Only while ours is standing
			// in: a box outside the owned family (or a view that failed to build) falls back
			// to the mirror here.
			if (_surfaces.Owns(p))
			{
				ownedSeen = true;
				if (_suppressed.Add(p.Address))
				{
					GD.Print($"[game-ui] not mirroring '{p.Name}' — Godot owns this panel");
				}

				continue;
			}

			// THE DISPLAY-ONLY PANELS, owned at this sampling cadence — they take no input,
			// so the mirror's rhythm is all they ever had.
			if (_surfaces.Intermission.Matches(p))
			{
				intermissionSeen = _surfaces.Intermission.Sample(_presentation, p.Address);
				if (intermissionSeen)
				{
					continue;
				}
			}
			else if (_surfaces.DisplayBox.Matches(p))
			{
				displayBoxSeen = _surfaces.DisplayBox.Sample(_presentation, p.Address);
				if (displayBoxSeen)
				{
					continue;
				}
			}

			// A SCREEN CONTRIBUTING NO ELEMENTS IS NOT ON SCREEN, and the guest counts them
			// itself — so this is its statement, not our inference.
			//
			// The pause menu is why. Its presentation is created at game entry (`sub_823DC888`,
			// once, behind the one-shot byte at GUI+102) and never freed, so from the first
			// frame of play it sits in the registry with its root active. The registry read
			// from the run this was written against says exactly what it is:
			//
			//   SHOWING active=2616  hud.bgf           <- really drawing
			//   SHOWING active=52    intermissionbox   <- really drawing
			//   SHOWING active=0     pausemainmenu     <- root active, drawing NOTHING
			//   hidden  active=1878  pausemainmenu FROZEN
			//
			// Drawing that third one is what put a full-screen purple backdrop and a stack of
			// 26 blank tabs over the running game: the guest contributes nothing, but we
			// rebuild the screen from the FILE, where those elements are active and stacked.
			//
			// It costs nothing elsewhere. The one other screen seen at zero is the HUD on the
			// first frame after it stands up, where our own build produced `quads 0` anyway —
			// so this skips an empty tree we were building for no reason and picks the HUD up
			// on the next sample.
			//
			// This replaced a gate on the guest's current-screen ID. That one was sound in
			// principle and never fired: the screen manager's slot pointers did not match the
			// addresses the presentation registry reports, so the test could not match. The
			// element count needs no pointer identity at all.
			if (p.ActiveElements == 0)
			{
				if (_suppressed.Add(p.Address))
				{
					GD.Print($"[game-ui] not drawing '{p.Name}' — its root is active but the " +
						"guest has NO globally-active elements in it, so it is contributing " +
						"nothing to the frame");
				}

				continue;
			}

			_suppressed.Remove(p.Address);
			wanted.Add(p);
		}

		// The HUD underneath, everything else over it. Depth is not something the guest states
		// in a form we have read, so this is ours — and it is the one ordering that cannot put
		// a menu behind the health bar.
		wanted.Sort((a, b) =>
			(a.Is("hud.bgf") ? 0 : 1).CompareTo(b.Is("hud.bgf") ? 0 : 1));

		// A panel whose presentation left the live set comes down with it.
		if (!intermissionSeen)
		{
			_surfaces.Intermission.Withdraw();
		}

		if (!displayBoxSeen)
		{
			_surfaces.DisplayBox.Withdraw();
		}

		HasInGameUi = wanted.Count > 0 || intermissionSeen || displayBoxSeen || ownedSeen;

		// Retire anything the guest has taken away — and anything whose ADDRESS HAS BEEN REUSED
		// FOR A DIFFERENT PRESENTATION, which is a different fault with the same cure.
		//
		// THE GUEST RECYCLES PRESENTATION ADDRESSES. One run's registry shows 4C8C7BB0 holding
		// `worldmapscreen.bgf`, then — same address, same root — `giftgivingscreen.bgf` after
		// the player changed tab. Everything we key on an address (the built tree, the document,
		// the row pool, the panel's resolved fields) is stale from that moment until the
		// structural comparison happens to notice, and until then the SCREEN MANAGER'S OWN JOIN
		// hands the new screen id straight to the old screen's tree: the log read "Godot owns
		// screen 15's list in 'worldmapscreen' — Quests, Jobs, Sales, Families, Regions" and the
		// items panel duly reported its own fields "NOT IN THE DOCUMENT". Same class as the
		// kernel-handle rule — a guest address is a slot, not an identity.
		var gone = new System.Collections.Generic.List<uint>();
		var recycled = new System.Collections.Generic.List<uint>();
		foreach ((uint address, Engine_UiDrawnPresentation drawn) in _drawn)
		{
			int at = wanted.FindIndex(p => p.Address == address);
			if (at < 0)
			{
				gone.Add(address);
			}
			else if (!string.Equals(ShortName(wanted[at]), drawn.Name,
				System.StringComparison.OrdinalIgnoreCase))
			{
				recycled.Add(address);
			}
		}

		foreach (uint address in gone)
		{
			Retire(address, "the guest stopped showing it");
		}

		foreach (uint address in recycled)
		{
			Retire(address, "the guest reused this address for a different presentation");
		}

		if (wanted.Count > 0)
		{
			// NOTHING FROM THE FRONT END BELONGS OVER THE RUNNING GAME. `LeaveFrontEnd` frees
			// all three when the guest leaves properly; this is the belt to that braces, for the
			// case where in-game UI appears without that handover having been seen — which is
			// how the load fan ended up drawn over the world in the first place.
			if (_menuView is not null)
			{
				_menuView.Visible = false;
			}

			if (_cardView is not null)
			{
				_cardView.Visible = false;
			}

			if (_saveCardView is not null)
			{
				_saveCardView.Visible = false;
			}
		}

		using (Refii.Core.Engine_FramePhases.Measure("ui.follow"))
		{
			for (int depth = 0; depth < wanted.Count; ++depth)
			{
				Follow(wanted[depth], depth);
			}
		}
	}

	/// <summary>
	/// The bank entry name for a live presentation — the guest's string is a full guest path,
	/// e.g. <c>GAME:\data\art\gui\gameface\hud.bgf</c>, with backslashes that mean nothing here.
	///
	/// This is a presentation's IDENTITY, and its address is not: see the recycling note in
	/// <see cref="FollowInGameUi"/>.
	/// </summary>
	private static string ShortName(Engine_UiModel.Presentation presentation) =>
		System.IO.Path.GetFileNameWithoutExtension(presentation.Name.Replace('\\', '/'));

	/// <summary>Builds or refreshes one live presentation at a given depth.</summary>
	private void Follow(Engine_UiModel.Presentation presentation, int depth)
	{
		string name = ShortName(presentation);

		// THE HUD IS NOT MIRRORED. Godot rebuilds it — see BuildRebuiltHud — from this file's
		// own bindings, its own art and the guest's name-addressed values, so mirroring the
		// element tree as well would draw two HUDs at once and defeat the point: a mirrored
		// tree carries the artist's ARRANGEMENT, and the arrangement is the thing being
		// replaced so the HUD can be moved, modded and scaled to displays the game never
		// shipped for.
		if (string.Equals(name, HudPresentation, System.StringComparison.OrdinalIgnoreCase))
		{
			// The address is still taken: the world icons — the one part of the HUD the guest
			// arranges — are sampled off this presentation without mirroring it. See
			// Engine_HudWorldIcons.
			_hudAddress = presentation.Address;
			if (_hud is not null && GodotObject.IsInstanceValid(_hud) && !_hud.Visible &&
				!_fullCutscene)
			{
				// Back after a Quit to Title; LeaveGame hid it with the game.
				_hud.Visible = true;
			}

			return;
		}

		// THE PAUSE BANNER IS OWNED TOO — a whole-tree clone driven by the cycler's one live
		// text (the current screen's name). Sampled, never mirrored.
		if (string.Equals(name, CyclerPresentation, System.StringComparison.OrdinalIgnoreCase))
		{
			_cyclerAddress = presentation.Address;
			return;
		}

		// This presentation's OWN element state, keyed by its address — the whole point of the
		// registry. Reading the front end's would put one screen's slides on another's widgets.
		System.Collections.Generic.List<Engine_UiModel.UiElement>? elements =
			Engine_UiElementState.Current.Elements(presentation.Address);
		if (elements is not { Count: > 0 })
		{
			return;
		}

		// INTO SCRATCH, NOT FRESH ARRAYS. This samples every live presentation several times a
		// second and the common verdict is "unchanged" — five arrays per element count per
		// sample survived just long enough to be promoted and relocated by the next GC. The
		// sample is read into kept buffers, compared in place, and copied out only on the path
		// that stores it.
		int n = elements.Count;
		int[] slides = Scratch(ref _followSlides, n);
		string?[] texts = Scratch(ref _followTexts, n);
		bool[] visible = Scratch(ref _followVisible, n);
		for (int i = 0; i < n; ++i)
		{
			slides[i] = elements[i].Slide;
			texts[i] = elements[i].Text;
			visible[i] = elements[i].IsGloballyActive;
		}

		// LIVE OPACITY, passed rather than dropped.
		//
		// It matters more in game than in the front end: `hud.lua` binds Enable/Disable as a
		// single `setAttribute(Scene.Layer, 'opacity', 100 | 0)`, so the game's own way of
		// taking the HUD off screen — a cutscene, a loading screen — is one group going to
		// zero. Ignoring it leaves the HUD painted over those. The builder multiplies it down
		// the tree, which is what makes one group's zero reach every element beneath it.
		//
		// Read BEFORE the unchanged test below, because it is part of that test: a HUD fading
		// out changes no slide and no visibility, so comparing only those would never notice.
		float?[]? opacity = null;
		Engine_UiScreen.LiveTint?[]? tint = null;
		int attributeCount = 0;
		Engine_UiModel.LiveAttribute[,]? attributes =
			Engine_UiElementState.Current.Attributes(
				Engine_UiLiveAttributes.OpacityIds, presentation.Address);
		if (attributes is not null)
		{
			attributeCount = System.Math.Min(attributes.GetLength(0), elements.Count);
			opacity = Scratch(ref _followOpacity, attributeCount);
			tint = Scratch(ref _followTint, attributeCount);
			for (int i = 0; i < attributeCount; ++i)
			{
				opacity[i] = attributes[i, 0].Present ? attributes[i, 0].AsFloat : null;
				tint[i] = ReadTint(attributes, i);
			}
		}

		// THE GUEST'S OWN TEXTURE PER ELEMENT, read here rather than after the test below
		// BECAUSE IT IS PART OF THAT TEST. Anywhere the game chooses a picture at runtime the
		// file carries only a placeholder — every dpad quick-slot points at the same grey
		// `empty.tex`, all twenty-four HUD world-icon slots at the same icon — so following the
		// file alone draws one wrong image in every slot of a set. Equipping an item changes
		// the slot's texture and NOTHING else: no slide, no visibility, no opacity. Left out of
		// the comparison the swap would be read once and then never again.
		System.ReadOnlySpan<uint> read = HostBridge.ReadUiTextureHashes(presentation.Address, n);
		bool texturesRead = read.Length == n;

		bool known = _drawn.TryGetValue(presentation.Address, out Engine_UiDrawnPresentation? existing);
		bool structureSame = known && existing!.Slides is not null &&
			existing.Visible is not null &&
			SameLeading(slides, n, existing.Slides) && SameLeading(visible, n, existing.Visible);
		bool opacitySame =
			(existing?.Opacity is null && opacity is null) ||
			(existing?.Opacity is not null && opacity is not null &&
			 SameLeading(opacity, attributeCount, existing.Opacity));
		// A HASH OF ZERO IS "UNSTATED", NOT A CHANGE. The guest transiently drops a stated
		// texture to 0 mid-write, and the builder treats 0 as "use the file's" — so a flap to
		// zero rebuilt the whole screen to show exactly what it was already showing (the real
		// estate screen churned on precisely this). A zero that STAYS zero still matters the
		// frame something else changes, because the rebuild reads the live array fresh.
		bool texturesSame;
		if (existing?.Textures is null && !texturesRead)
		{
			texturesSame = true;
		}
		else if (existing?.Textures is not null && texturesRead &&
			read.Length == existing.Textures.Length)
		{
			texturesSame = true;
			for (int i = 0; i < read.Length; ++i)
			{
				if (read[i] != existing.Textures[i] && read[i] != 0)
				{
					texturesSame = false;
					break;
				}
			}
		}
		else
		{
			texturesSame = false;
		}
		bool tintSame =
			(existing?.Tint is null && tint is null) ||
			(existing?.Tint is not null && tint is not null &&
			 SameLeading(tint, attributeCount, existing.Tint));

		if (structureSame && opacitySame && texturesSame && tintSame)
		{
			return;
		}

		// Something moved: the sample is stored, so it leaves the scratch here. Exactly the
		// arrays the old per-sample allocation made, on the change path only.
		slides = slides[..n];
		texts = texts[..n];
		visible = visible[..n];
		opacity = opacity?[..attributeCount];
		tint = tint?[..attributeCount];
		uint[]? textures = texturesRead ? read.ToArray() : null;

		// AN OPACITY CHANGE IS A MATERIAL WRITE, NOT A SCREEN. The pause glow's pulse moved one
		// element's opacity a few points per tick, and rebuilding the 3444-element tree for it
		// — ~844 consecutive teardowns, measured — is what this branch ends. Only for a change
		// that is PURELY opacity with both samples in hand: a slide, a visibility bit, a
		// texture or a tint still rebuilds, and so does a read appearing or vanishing.
		if (structureSame && texturesSame && tintSame && !opacitySame &&
			existing!.Opacity is not null && opacity is not null)
		{
			// AN OWNED SCREEN HAS NO BUILDER TO PAINT: its book reads `Opacity` off this entry
			// every frame, so the assignment below is the whole update. Rebuilding it instead
			// replaced the entry (a null transform for one frame, every clone snapping to its
			// slide pose) and the list view, once per sample — the pulse of one page arrow's
			// opacity was blinking the whole right page of every item book.
			int pulsed = existing.Builder is { } inPlace ? inPlace.ApplyLiveOpacity(opacity) : 0;
			existing.Opacity = opacity;

			// THE TEXT SAMPLE LANDS TOO. Text is not part of the equality above, and the pool's
			// rows fill a few frames after the screen builds: the list adoption reads them off
			// this entry, so an entry kept in place must carry the latest read or the screen is
			// "left MIRRORED — no populated ExpandableMenu pool" for good.
			existing.Texts = texts;
			if (!_opacityInPlace.Contains(presentation.Address))
			{
				_opacityInPlace.Add(presentation.Address);
				GD.Print($"[game-ui] '{name}' opacity applied IN PLACE " +
					$"({pulsed} element(s) this sample) — no rebuild");
			}

			return;
		}

		// WHICH input broke the equality, when a screen churns. The pause screen tore down and
		// rebuilt its 3444-element tree on ~844 CONSECUTIVE samples and nothing in the log
		// could say why — five comparisons, one silent verdict. Rate-limited to the first few
		// per screen: the point is the NAME of the failing input, not a firehose.
		if (existing is not null)
		{
			_rebuildReported.TryGetValue(presentation.Address, out int seen);
			if (seen < 3)
			{
				_rebuildReported[presentation.Address] = seen + 1;
				GD.Print($"[game-ui] '{name}' rebuild #{seen + 1}: " +
					DescribeChange(existing, slides, visible, opacity, textures, tint));
			}
		}

		_presentation!.LiveSlides = slides;
		_presentation.LiveText = texts;
		_presentation.LiveVisible = visible;
		_presentation.LiveOpacity = opacity;
		_presentation.LiveTint = tint;
		float?[]?[]? transform = Engine_UiLiveAttributes.Transform(presentation.Address, elements.Count);
		_presentation.LiveTransform = transform;

		_presentation.LiveTextures = textures;
		_presentation.SceneSlide = 0;

		// NOTHING IS MIRRORED FOR A SCREEN GODOT OWNS.
		//
		// The plan cuts the UI at the `GUI` namespace and discards the DOM beneath it, and a
		// screen with chrome in its template is one we draw from its own pattern book. Building
		// the guest's tree beside that is a second copy of a scene nobody sees — 4434 elements,
		// 696 quads and 696 textures for `abilitiesscreen`, every time it opens.
		//
		// The DOCUMENT is still needed and is still read: it is what the ordinal join, the text
		// sweeps and the owned book's own resolve are keyed on. What goes is the NODES. The
		// live sample — texts, textures, transforms, slides, opacity — is a bridge read and was
		// never taken from this tree, so the book is fed exactly as before.
		// TWO WAYS A PRESENTATION CAN BE OURS, and both have to be asked.
		//
		// A book screen declares chrome in its template. The MODAL family does not — the six
		// box presentations carry no template at all and are stood up by `Engine_UiSurfaceSet`
		// instead, so a chrome test alone left `querybox`, `menubox`, `messageboxmulti` and
		// `numericadjusterbox` rebuilding the guest's tree behind a box Godot had already
		// drawn.
		//
		// `Owns` is asked of the SURFACES rather than of a list of names, and it answers false
		// for a surface that failed to build its art — which keeps the mirror exactly where it
		// is still the fallback, and nowhere else.
		bool owned = Engine_UiTemplates.AnyScreenOwns(name) || _surfaces.Owns(presentation) ||
			string.Equals(name, Engine_UiCreditsRoll.Presentation, System.StringComparison.OrdinalIgnoreCase);
		Node3D? built;
		Engine_UiScreen? screen;
		Engine_UiSceneBuilder? stats;
		if (owned)
		{
			screen = _presentation.DocumentFor(name);
			stats = null;

			// AN EMPTY STAND-IN, NOT NULL. Depth is the one thing downstream still asks this
			// node for, and it is ours to state — so the placeholder carries it and every
			// placement stays where it was.
			built = screen is null ? null : new Node3D { Name = $"Unmirrored_{name}" };
			if (built is not null && _unmirrored.Add(name))
			{
				GD.Print($"[game-ui] '{name}' is NOT mirrored — Godot owns it, so its " +
					$"{screen!.Nodes.Length} guest elements are sampled but never rebuilt");
			}
		}
		else
		{
			built = _presentation.BuildDetached(name, out screen, out stats);
		}
		if (built is null)
		{
			GD.PushWarning($"[game-ui] the guest has '{presentation.Name}' live but '{name}' " +
				"is not among the shipped presentations");
			return;
		}

		// A HALF-BUILT PRESENTATION IS NOT DRAWN.
		//
		// The guest populates an element tree over several frames, and while it is doing so the
		// registry already reports the presentation — `frontendmainmenu` shows up with 1023,
		// then 1402, against the file's 3812. Every piece of live state we apply is keyed by
		// ORDINAL, so a tree of the wrong length does not give a partial screen: it gives every
		// element after the first divergence someone else's slide, text and transform. Waiting
		// costs a frame or two of a screen the guest is not finished with anyway.
		if (screen is not null && screen.Nodes.Length != elements.Count)
		{
			if (_mismatched.Add(presentation.Address))
			{
				GD.Print($"[game-ui] '{name}' is still being built — file {screen.Nodes.Length} " +
					$"elements, guest {elements.Count}; not drawing it until they agree");
			}

			built.QueueFree();
			return;
		}

		if (existing is not null)
		{
			existing.Node.QueueFree();
			_drawn.Remove(presentation.Address);
		}
		else
		{
			// The build counts, because an unresolved TEXTURE still builds a quad — it just
			// draws with no albedo, which is a white rectangle. A screen that covers the title
			// in white and a screen that is correct look the same from "it built".
			string art = stats is null
				? string.Empty
				: $", quads {stats.QuadsBuilt} textures {stats.TexturesResolved}" +
				  $"{(stats.TexturesResolved < stats.QuadsBuilt ? " — TEXTURES MISSING, those quads draw untextured" : string.Empty)}" +
				  $", text {stats.TextsBuilt} ({stats.TextGlyphsBuilt} glyphs)";

			// The texture-swap breakdown is here rather than in its own line because it is only
			// meaningful beside the quad count. It reports STATED as well as swapped: a plain
			// "0 swapped" cannot tell "the guest names no textures" from "it names exactly what
			// the file already has" from "it names textures we cannot resolve", and those need
			// different work. Empty when the guest states none, which is most screens.
			string swaps = screen?.DescribeLiveTextures() ?? string.Empty;

			// THE TINT WEIGHT, reported separately from the swap counts because it answers a
			// different question. Every quad now draws its tint SUBTRACTIVELY at this weight,
			// and the weight is 0 on most authored records — so "the frames look pale" and
			// "the guest never stated a TintingAmount" are the same observation, and a screen
			// that states none needs to be distinguishable from one whose read is broken.
			string tints = screen is null || screen.LiveTintsApplied == 0
				? string.Empty
				: $", {screen.LiveTintsApplied} tint weight(s) stated";

			// AND WHY, when the answer is "none". The chain the host walks is the guest's own,
			// verified against `sub_82425C10` instruction for instruction — element+4 to the
			// scene node, node+0 to the render item, +0x200 for the hash — so an all-zero
			// result is not evidence that the walk is broken. It is equally consistent with the
			// guest building no render items at all in this configuration, which is a question
			// about the GUEST and not about us. This prints the breakdown once per screen so
			// the two stop looking identical.
			if (screen is { LiveTexturesStated: 0 } &&
				HostBridge.GetUiTextureStats(presentation.Address) is { } chain)
			{
				GD.Print($"[game-ui] '{name}' texture chain: {chain.Describe()}");
			}

			// The guest's tree is built hidden either way: an unowned presentation is not on
			// screen at all.
			GD.Print((owned
					? $"[game-ui] sampling '{name}' for its owner "
					: $"[game-ui] NO OWNER for '{name}' — sampled, NOT drawn ") +
				$"({elements.Count} live elements, " +
				$"{System.Array.FindAll(visible, v => v).Length} of them active{art}{swaps}{tints})");

			// THE ORDINAL JOIN, checked rather than believed — the same check the front end
			// makes, because this path applies slide, text and opacity BY POSITION too.
			//
			// It was verified 77/77 on the title screen and never on anything else. The HUD is
			// 3431 elements; if the guest's depth-first walk diverges from the .bgf's order
			// anywhere, every element past that point wears another element's state. That does
			// not look like a join fault — it looks like a HUD with the wrong numbers on it.
			//
			// Element TYPE is the cheap invariant: the guest holds a kind byte per element with
			// the same numbering as BgfElementType, and it carries no live state, so a mismatch
			// is a join failure and nothing else.
			if (screen is not null)
			{
				int compared = System.Math.Min(screen.Nodes.Length, elements.Count);
				int diverged = -1;
				int mismatches = 0;
				for (int i = 0; i < compared; ++i)
				{
					if ((int)screen.Nodes[i].Type == elements[i].Type)
					{
						continue;
					}

					++mismatches;
					if (diverged < 0)
					{
						diverged = i;
					}
				}

				GD.Print(diverged < 0
					? $"[game-ui] '{name}' ordinal join OK: {compared} elements, types agree end to end"
					: $"[game-ui] '{name}' ORDINAL JOIN BROKEN: {mismatches} of {compared} " +
					  $"element types disagree, first at index {diverged} " +
					  $"(file says {screen.Nodes[diverged].Type}, guest says {elements[diverged].Type})");

				if (screen.Nodes.Length != elements.Count)
				{
					GD.Print($"[game-ui] '{name}' element COUNT differs: file " +
						$"{screen.Nodes.Length}, guest {elements.Count} — the join cannot be " +
						"positional at all");
				}
			}
		}

		ReportMenuContents(presentation.Address, name, screen, texts, visible, slides, transform);
		ReportScreenText(presentation.Address, name, screen, texts, visible);

		// The owned panel reads its fields out of this array by name; without the refresh it
		// would keep drawing whatever the screen said when it was last BUILT.
		if (_drawn.TryGetValue(presentation.Address, out Engine_UiDrawnPresentation? sampled))
		{
			sampled.Texts = texts;
		}

		// THE OWNED LIST'S CONTENTS, re-read wherever the guest's live text is already in
		// hand. These screens repopulate with no event that says so — equipping, buying, a
		// page turn — and the model's own comparison is what turns that into a refresh. The
		// CURSOR is not read here: it moves far more often than this samples, and
		// Engine_UiScreenMenu.Track follows it per frame off the live transform.
		if (screen is not null && Engine_ExpandableMenuItems.FindGroup(screen) is not null)
		{
			if (!_screenLists.TryGetValue(presentation.Address, out Engine_UiScreenMenu? list))
			{
				list = new Engine_UiScreenMenu();
				_screenLists[presentation.Address] = list;
			}

			// THE STRING TABLE, because a FOLDED row has no pool button and therefore no
			// resolved label — all the tree carries for it is a Babel tag. Set every sample
			// rather than once: the database arrives with the presentation host, which may not
			// exist yet the first time a list is built.
			if (list.Strings is null)
			{
				list.Strings = _presentation?.Strings;
			}

			// The same two inputs for the DATA-AREA source, which needs the string book to name
			// an item and `globals.gdb` to find its tag. Read once and kept: the GDB is 3 MB and
			// its parse is shared, but re-resolving it per sample would still walk the banks.
			if (Engine_UiInventorySource.Strings is null || Engine_UiInventorySource.Strings != _presentation?.Strings)
			{
				Engine_UiInventorySource.Strings = _presentation?.Strings;
			}

			if (Engine_UiInventorySource.Globals is null &&
				Refii.Gdb.Engine_GameGdb.Database is { } gameGdb)
			{
				// The SHARED database, not a second parse: `Engine_GameGdb` already holds
				// `globals.gdb` and its supplements behind one resolver.
				Engine_UiInventorySource.Globals = gameGdb;
				GD.Print("[screen-menu] the inventory source has the game GDB — item rows can " +
					"be read from the data areas now");
			}

			// THE C++'s OWN STATEMENTS, WHICH WIN WHERE THEY EXIST. `Engine_UiMenuModel` holds
			// what the Gameface Lua would have held, built from the 28 `g_Menu:` mutations the
			// game makes; where it carries rows for this presentation the list is built from
			// them and the UI VM is not read. Set every sample rather than once, for the same
			// reason the string table is: a list can be built before the first statement lands.
			list.Model = _menuModel;

			// WHICH SCREEN THIS LIST IS, from the manager's own presentation-to-id map — the
			// only way a widget that names no population script can find its template. The gift
			// family is the case: see `Engine_UiScreenTemplate.MenuScript`.
			// THE UP SCREEN STANDS IN until the manager's map names this presentation. The
			// map registers an entry a sample or two after the presentation is first drawn,
			// and a list built in that gap at id 0 comes from the g_Menu model; when the id
			// then lands the template wins and the rebuild drops the host's fold and cursor —
			// the weapons book snapped shut the moment the preview model loaded. The guest
			// keeps one in-game screen up at a time, so the screen that is up IS this list's,
			// except for the pause presentation, whose own list is the root's.
			list.ScreenId =
				_screenOwners is { } owners &&
				owners.TryGetValue(presentation.Address, out int owning)
					? (uint)owning
					: !string.Equals(name, PausePresentation, System.StringComparison.OrdinalIgnoreCase)
						? UpScreenId()
						: 0u;

			bool rebuilt;
			using (Refii.Core.Engine_FramePhases.Measure("ui.list.rebuild"))
			{
				rebuilt = list.Rebuild(presentation.Address, screen, texts, visible, slides, transform);
			}

			if (rebuilt && ReferenceEquals(list, _screenMenu))
			{
				_screenMenuView?.Refresh();
			}

			// THE PAUSE ROOT'S LABELS, from the guest's own buttons. Its list is ours — the
			// guest keeps no C++ item list — but the STRING in each row is the guest's, and it
			// carries what a tag cannot: the unspent-ability count and the new-item marker,
			// both appended at spawn time. Read here because this is where the live text is
			// already joined to the file, and re-read on every sample because the decorations
			// change as the player picks things up.
			if (name == PausePresentation && _pauseMenu is not null)
			{
				// NAMED FIRST, so the label join and the sub-tab source read the same capture.
				Engine_UiMenuSources.StatedPresentation = presentation.Address;

				bool moved = Engine_PauseMenu.ApplyStatedLabels(
					_pauseMenu, Engine_ExpandableMenuItems.Read(screen, texts, visible, slides),
					_menuModel.For(presentation.Address).Rows);

				// AND THE SUB-TABS, from the same capture the in-game lists join against.
				//
				// The template authors the SHAPE — which tabs exist, what each dispatches — and
				// the capture supplies the CONTENTS under each. It has to run here rather than at
				// Build: the menu is built when the screen is adopted and the capture fills in
				// afterwards, so building once would freeze whatever the capture held at
				// adoption, which is usually nothing. That is the whole reason the pause tabs
				// had no sub-tabs while the in-game screens did — not a missing mechanism, a
				// mechanism that only ever ran too early.
				int before = CountRows(_pauseMenu);

				// WHOSE LUA THE SOURCE READS, named once for the whole pass. The pause tabs'
				// children are stated in this presentation's own `g_Menu` tree — folded and
				// unfolded alike — and the spawn capture is only the fallback behind it. The
				// set also invalidates the cached read, so one sample is one walk however many
				// tabs ask — it was named above, before the labels joined against it.
				if (Engine_UiTemplates.Menu("pause-root") is { } record &&
					record.RefreshSources(_pauseMenu))
				{
					moved = true;
					GD.Print($"[game-ui] pause sub-tabs re-sourced — {before} row(s) before, " +
						$"{CountRows(_pauseMenu)} after");
				}

				if (moved)
				{
					_pauseView?.Refresh();
					ReportMenuView(_pauseView);
				}
			}
		}

		// Depth by Z rather than by child order: these share one orthographic canvas camera
		// looking down -Z, so tree order does not decide what covers what.
		//
		// A WHOLE SLAB PER PRESENTATION, not half a unit. A screen is not flat — `frontendmainmenu`
		// spans z -5..171, a hundred and seventy-six units deep — so stacking screens 0.5 apart
		// does not order them at all: the one nominally behind sits half a unit back while the
		// one in front reaches a hundred and seventy units forward, and the two interleave
		// element by element. Everything here is alpha-blended and writes no depth, so what
		// covers what is decided purely by that sort. The step has to exceed the deepest screen
		// by a comfortable margin, and 1000 does with the whole set well inside the camera's
		// 100000 far plane.
		built.Position = new Vector3(0f, 0f, depth * Engine_UiPresentationDepth.Step);
		// BUILT HIDDEN. The tree is sampled for text, slides and values; it is never the picture.
		built.Visible = false;
		_presentation?.AddChild(built);

		// AFTER the attach, which is the contract `ApplyCameraAiming` states and the one
		// `BuildDetached` cannot keep: the aim rides through the camera's parent GLOBAL
		// transform, and a detached tree has none. Doing it here is what makes an in-game
		// presentation's cameras aim at the same points the viewer's do.
		Engine_UiSceneBuilder.ApplyCameraAiming(built);

		// The pause menu changes hands the moment its mirrored tree stands — and on every
		// rebuild after, because each rebuild brings the guest's own rows back visible.
		if (name == PausePresentation)
		{
			AdoptPauseMenu(built, presentation.Address);
		}

		// THE LIVE-VALUE BINDINGS, resolved once per build rather than per frame.
		//
		// This is what makes the HUD more than a picture. Its numbers are not in the element
		// tree and not in any script: `hud.bgf` binds an element with `MeterName = "PlayerHealth"`
		// and the guest publishes a percentage against a hash of that string every frame. Reading
		// the binding here — while the document and the ordinal map are both in hand — is what
		// lets the per-frame path be a dictionary lookup and a transform write.
		//
		// Only for a screen that HAS bindings, which is the HUD; the front-end screens carry
		// none and get a null, which costs nothing per frame.
		Engine_UiLiveValues? values = null;
		if (screen is not null && stats is not null)
		{
			Engine_UiBindings bindings = Engine_UiBindings.Read(screen.Document);
			if (bindings.All.Count > 0)
			{
				values = new Engine_UiLiveValues
				{
					// The same theme the front end draws with — the HUD's live text has to be
					// laid out in the game's own fonts, and without it a bound field simply
					// keeps whatever the file left in it.
					Theme = _presentation?.Theme,
				};

				values.Bind(screen.Document, bindings, stats.ByOrdinal);
				GD.Print($"[game-ui] '{name}' {bindings.Describe()} — {values.BarsBound} bar(s) " +
					$"and {values.FieldsBound} text field(s) bound to nodes, " +
					$"{values.BarsUnresolved} not in the built tree");
			}
		}

		// THE WIDE LAYOUT, read once per build. The game ships two layouts per element and the
		// authored one is 4:3; without this the HUD draws in its standard-definition arrangement
		// on a 16:9 canvas, which is why the ABXY prompts sat in the middle of the screen.
		Engine_UiWidescreen? widescreen = null;
		if (screen is not null)
		{
			widescreen = Engine_UiWidescreen.Read(screen.Document);
			if (widescreen.Count > 0)
			{
				GD.Print($"[game-ui] '{name}' {widescreen.Describe()}");
			}
			else
			{
				widescreen = null;
			}

			// THE NAMED FIXUPS, ONCE PER BUILD AND NEVER PER FRAME. One of the three divides the
			// vignette's existing scale by 0.75 rather than assigning, so repeating it grows the
			// border by a third every frame. A rebuild re-reads the artist's value from the file,
			// which is what makes once-per-build the same thing as the original's once-per-init.
			// Gated like the per-frame pass: a guest that knows it is widescreen runs the same
			// script itself, and the division applied by both sides quarters the vignette.
			if (HostAppliesWidescreen)
			{
				Engine_UiWidescreen.ApplyNamedFixups(built, out string fixups);
				if (fixups.Length > 0)
				{
					GD.Print($"[game-ui] '{name}' {fixups}");
				}
			}

			// REMASTER: the guest's own menu box (the travel stops, DLC prompts) authors its
			// option labels at node scale 0.4 over an 18pt face — seven pixels on the 720
			// canvas, as the shipped game showed them. Doubled here as the owned box doubles
			// its clone; the factor rides the live transform so the guest's restated scale
			// does not take it back. Art keeps its layout.
			if (string.Equals(name, "menubox", System.StringComparison.OrdinalIgnoreCase))
			{
				int doubled = 0;
				for (int i = 1; i <= Engine_UiMenuBox.MaxOptions; ++i)
				{
					if (Engine_UiTheme.FindByPath(built,
							$"Choices.Multiplayer_Button{i}.Choice1.Frame.Choice1") is { } label)
					{
						Engine_UiSceneBuilder.RemasterScale(label, 2f);
						++doubled;
					}
				}

				GD.Print($"[game-ui] '{name}' option labels doubled: {doubled}");
			}
		}

		_drawn[presentation.Address] = new Engine_UiDrawnPresentation
		{
			Name = name,
			Node = built,
			Mirrored = !owned,
			Slides = slides,
			Visible = visible,
			Opacity = opacity,
			Textures = textures,
			Tint = tint,
			Builder = stats,
			ElementCount = elements.Count,
			Values = values,
			Widescreen = widescreen,
			Screen = screen,
			Texts = texts,
			// The transform follow survives a rebuild: the book applies it every frame, and an
			// entry born without one reverts every clone to its slide pose for that frame.
			LastTransform = known ? existing!.LastTransform : null,
		};

		// A REBUILD DROPS OUR HIDE. The guest's own row components and highlight are hidden
		// per BUILD, and this is a fresh one — so the owned list re-adopts, which re-hides
		// them in every instance of the new tree. Without it the guest's rows come back
		// underneath ours the first time it changes a slide. Only a MIRRORED tree has rows to
		// re-hide; an owned screen's stand-in draws nothing, and re-adopting there rebuilt the
		// list view on every sample.
		if (presentation.Address == _screenMenuAddress && !owned)
		{
			_screenMenuAddress = 0;
		}

		// The owned PANEL survives a rebuild (it lives on the host), but the guest's own copies
		// of its fields come back visible in the fresh tree — so the hide has to run again.
		// Clearing the reported line is what re-arms it.
		if (presentation.Address == _infoPanel.Address)
		{
			_infoPanel.Invalidate();
		}

		// A camera has to be current or the sub-viewport renders through Godot's default one
		// and the whole layer is empty. The front end's own screen supplies it while the front
		// end is up; in game these are the only screens there are, so the first one does.
		if (depth == 0 && screen is not null)
		{
			Engine_UiSceneBuilder.ActivateCamera(built);
		}
	}

	private string? _presentationSignature;

	/// <summary>
	/// The guest's last stated in-game screen, so the line fires on a change rather than
	/// every sample. Null is a distinct value here — "no screen manager" is not
	/// <see cref="Engine_UiModel.InGameScreen.None"/>.
	/// </summary>
	private Engine_UiModel.InGameScreen? _inGameScreen;

	/// <summary>
	/// The screen manager's presentation-to-screen-id map, kept from the same read that settles
	/// <see cref="_inGameScreen"/> — a list needs its own screen id to find a template when the
	/// widget names no population script.
	/// </summary>
	private System.Collections.Generic.IReadOnlyDictionary<uint, int>? _screenOwners;

	/// <summary>Frames between registry samples — a few times a second at any display rate.</summary>
	private const int PresentationSampleFrames = 30;

	private int _presentationTick;

	/// <summary>
	/// The screen whose presentation the follower is drawing — <see cref="Engine_UiModel.InGameScreen.None"/>
	/// when it draws none of the manager's thirty. Restated on every sample, in TracePresentations.
	/// </summary>
	private uint _followedScreen = (uint)Engine_UiModel.InGameScreen.None;

	private int _overdueSamples;

	/// <summary>
	/// True while the guest states a screen the follower has not caught up with.
	///
	/// THE SCREEN ID LEADS THE REGISTRY. `manager+4` flips the instant the guest pushes a
	/// screen — the same word `RefiiHostGetPauseState` and `RefiiHostGetInGameScreen` both read
	/// — while the presentation that draws it is constructed over the guest's own following
	/// ticks. At a flat cadence the old screen's chrome stands there in the meantime with its
	/// rows already withdrawn, because the row view yields the moment the screen id leaves 0:
	/// on a pause tab press the book sat empty from "screen 15 is up" to "drawing the guest's
	/// 'giftgivingscreen'", up to a whole sampling period — 1.5s at the 19fps that run held.
	///
	/// FOUR WORDS TO FIND OUT WE ARE BEHIND, against a walk that reads a whole element tree per
	/// drawn presentation. The cheap read is what makes hurrying affordable; polling the
	/// registry itself is the thing the cadence exists to avoid.
	///
	/// BOUNDED, at one ordinary sampling period. A guest that never builds a presentation for
	/// what it states would otherwise hold the follower at the display rate for the rest of the
	/// session — and `NextEpisode` (13) and `NPCHighlightScreen` (20) are in no bank, so that
	/// is a reachable state and not a hypothetical. Giving up restores exactly the behaviour
	/// this replaces, one period late.
	/// </summary>
	private bool SampleIsOverdue()
	{
		// THE SPELL BLADE IS A LIVE WIDGET ON A LAZY CHANNEL. The guest expresses the whole
		// blade through SLIDES and TEXTURE SWAPS — `sub_826B4770` puts `Group.Spell{n}` on
		// slide 2 or 3, `sub_826B5710` writes the HL1/HR1 icons — and every one of those
		// reaches the book only in the presentation sample, thirty frames apart. Only the
		// transform is refreshed per frame. So a d-pad press moved nothing for half a second
		// and then jumped, and fast input read as dropped entirely.
		//
		// The screen id does not change while the blade is up, so the arm below cannot see it.
		// This is the same "the cadence is wrong rather than merely lazy" window: a widget the
		// player is driving directly has to sample at the rate they drive it.
		if (HostBridge.AbilitiesAssignmentOpen())
		{
			return true;
		}

		// Null is the front end, or no guest at all: the follower's other branch runs there and
		// this has nothing to say about it. Separate from the caught-up arm below, because
		// leaving the game is not catching up and must not be reported as it.
		if (HostBridge.GetPauseState() is not { } state)
		{
			_overdueSamples = 0;
			return false;
		}

		if (state.ScreenId == _followedScreen)
		{
			// WHAT IS LEFT OF THE GAP, said once per transition. Removing the cadence from this
			// window does not make it zero — the guest still constructs the presentation over
			// its own ticks — and the two costs look identical from a run unless the follower
			// says which of them it waited on.
			if (_overdueSamples > 0)
			{
				GD.Print(_overdueSamples >= PresentationSampleFrames
					? $"[game-ui] the follower gave up hurrying for screen {_followedScreen} and " +
						$"caught up at the ordinary cadence — the guest took longer than " +
						$"{PresentationSampleFrames} frame(s) to build it"
					: $"[game-ui] the follower caught up with screen {_followedScreen} in " +
						$"{_overdueSamples} frame(s) — the guest's own build time, with no " +
						$"sampling cadence added to it");
				_overdueSamples = 0;
			}

			return false;
		}

		if (_overdueSamples >= PresentationSampleFrames)
		{
			return false;
		}

		++_overdueSamples;
		return true;
	}

	/// <summary>Puts the UI on screen. Idempotent.</summary>
	public void Reveal()
	{
		if (IsRevealed || _container is null || _viewport is null)
		{
			return;
		}

		IsRevealed = true;
		_container.Visible = true;

		// Always while it is up, not Once-per-rebuild. A menu is static between guest state
		// changes, so redrawing on demand is tempting — but a window resize reallocates the
		// render target and an on-demand viewport has nothing to put back in it, so the front
		// end blanks until the guest happens to change something. 77 unshaded quads a frame is
		// not worth that.
		_viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;

		// _showing, not Screen: the export is the STARTING presentation and never changes, so
		// naming it here reports the title screen long after the guest has moved on.
		GD.Print($"[game-ui] revealed '{_showing ?? Screen}'");
	}

	/// <summary>
	/// Takes the UI back off screen — a movie playing over the front end.
	///
	/// SAYS WHY, because the reveal does and the pair is only readable together. There are two
	/// callers with completely different meanings — the guest having no live UI left, and a
	/// movie starting — and the movie one used to be silent, so a layer that went down and
	/// stayed down could not be told apart from one that was never revealed.
	/// </summary>
	public void Conceal(string reason = "unstated")
	{
		if (!IsRevealed || _container is null || _viewport is null)
		{
			return;
		}

		IsRevealed = false;
		_container.Visible = false;
		GD.Print($"[game-ui] concealed '{_showing ?? Screen}' — {reason}");

		// The render target goes idle too. Hiding the container alone leaves the front end
		// being drawn into a texture nothing samples.
		_viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
	}
}
