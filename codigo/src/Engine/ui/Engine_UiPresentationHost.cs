using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Refii.Bnk;
using Refii.Core;
using Refii.Text;
using Refii.Textures;

namespace Refii.Ui;

/// <summary>
/// Stands up a Fable 2 Gameface presentation and keeps it on screen — the one place a
/// <c>.bgf</c> becomes Godot nodes. Mounting the banks, the texture name table, the bitmap
/// fonts, the Babel database, parsing a screen and handing it to
/// <see cref="Engine_UiSceneBuilder"/> all live here; the viewer and the running game go
/// through it. Subclasses take the loaded resources off the protected members and the
/// <see cref="OnScreenShown"/> hook.
/// </summary>
public partial class Engine_UiPresentationHost : Node3D
{
	/// <summary>Fallback asset root, used when neither the env var nor the user data path resolves.</summary>
	[Export] public string AssetRoot { get; set; } = string.Empty;

	/// <summary>Screen to show first. Empty means whichever sorts first.</summary>
	[Export] public string StartScreen { get; set; } = string.Empty;

	/// <summary>
	/// Ignore the screen's own camera and frame the content instead — separates a layout fault
	/// from a framing one.
	/// </summary>
	[Export] public bool UseFramingCamera { get; set; }

	/// <summary>The shipped <c>data</c> directory to stream banks from.</summary>
	[Export(PropertyHint.GlobalDir)] public string GameDataRoot { get; set; } = string.Empty;

	/// <summary>
	/// A bank mount to serve the presentations and textures from instead of opening one — the
	/// editor's, which carries the enabled mods' layers. Its owner disposes it. Null opens the
	/// shipped banks under the data root. The fonts and the string book still come from the data
	/// root.
	/// </summary>
	public BnkFileSystem? Mount { get; set; }

	/// <summary>Screen names, in display order. How one is fetched is <see cref="_loadScreen"/>'s job.</summary>
	private readonly List<string> _screens = new();

	/// <summary>Set by whichever start path won — banks or an extracted tree.</summary>
	private Func<string, Engine_UiScreen>? _loadScreen;

	private readonly List<IDisposable> _owned = new();
	private Engine_TextureNameTable? _names;

	/// <summary>
	/// The hash-to-path table the screens were built with, for a caller holding a texture HASH
	/// from the guest rather than a path — the load-game card portraits, for one.
	/// </summary>
	public Engine_TextureNameTable? TextureNames => _names;

	/// <summary>
	/// Loads a texture by its game-relative path through the same cache the screens use — for a
	/// caller resolving a texture the file does not name. Never open a second cache.
	/// </summary>
	public Texture2D? LoadTexture(string relativePath) => Textures?.Load(relativePath);

	/// <summary>Where the presentations came from, for diagnostics.</summary>
	protected string SourceRoot = string.Empty;

	private Node3D? _menuLayer;

	/// <summary>
	/// The parent of every menu node — the in-game screens, the pause list and book, the info
	/// panel. Identity on the whole window; <see cref="SeatMenus"/> moves it into the opener's
	/// half on split screen. Built on first use.
	/// </summary>
	public Node3D MenuLayer
	{
		get
		{
			if (_menuLayer is null || !GodotObject.IsInstanceValid(_menuLayer))
			{
				_menuLayer = new Node3D { Name = "MenuLayer" };
				_menuLayer.AddToGroup(Engine_UiSeats.MenuLayerGroup);
				AddChild(_menuLayer);
			}

			return _menuLayer;
		}
	}

	private Vector4 _menuSeat = new(1f, 1f, 0f, 0f);

	private bool _watchingAdds;

	/// <summary>
	/// Seats the menus: <paramref name="scale"/> about the canvas origin, then moved to
	/// <paramref name="centre"/>. (1, origin) is the whole window. Every UI material under the
	/// layer carries the seat as its <c>clip_seat</c>, so its clip compares in laid-out space; a
	/// mesh added later takes it as it enters the tree. A material parameter, not an instance
	/// uniform: those crash this engine's separate render thread on Node.duplicate (see
	/// ui_quad.gdshader). The menu books are clones of their own templates, so their materials
	/// are theirs and the HUD's (another book) are never touched.
	/// </summary>
	public void SeatMenus(float scale, Vector2 centre)
	{
		var seat = new Vector4(scale, scale, centre.X, centre.Y);
		Node3D layer = MenuLayer;
		if (seat == _menuSeat)
		{
			return;
		}

		_menuSeat = seat;
		layer.Transform = new Transform3D(
			Basis.Identity.Scaled(new Vector3(scale, scale, 1f)), new Vector3(centre.X, centre.Y, 0f));
		StampSeat(layer);

		if (!_watchingAdds && IsInsideTree())
		{
			_watchingAdds = true;
			GetTree().NodeAdded += OnNodeAdded;
		}
	}

	/// <summary>A mesh entering the seated layer takes the seat once its material is on (a
	/// builder may assign it right after adding the node, so the stamp waits a frame). Unseated,
	/// the shader's default is the seat.</summary>
	private void OnNodeAdded(Node node)
	{
		if (node is not GeometryInstance3D || _menuSeat == new Vector4(1f, 1f, 0f, 0f) ||
			_menuLayer is not { } layer || !GodotObject.IsInstanceValid(layer) || !layer.IsAncestorOf(node))
		{
			return;
		}

		Callable.From(() =>
		{
			if (GodotObject.IsInstanceValid(node))
			{
				StampSeat(node);
			}
		}).CallDeferred();
	}

	private void StampSeat(Node node)
	{
		if (node is MeshInstance3D mesh && mesh.MaterialOverride is ShaderMaterial material &&
			Engine_UiSceneBuilder.IsUiShader(material.Shader))
		{
			material.SetShaderParameter("clip_seat", _menuSeat);
		}

		foreach (Node child in node.GetChildren())
		{
			StampSeat(child);
		}
	}

	/// <summary>The texture cache the built quads sample through. Null until loading succeeds.</summary>
	protected Engine_UiTextureCache? Textures { get; private set; }

	/// <summary>The bitmap fonts text elements are built with. Null until loading succeeds.</summary>
	public Engine_UiFontLibrary? Fonts { get; private set; }

	/// <summary>
	/// The localised string database, for anything holding a TAG but no presentation — a
	/// Godot-owned menu keeps its tags in the model, not in a <c>.bgf</c>. Never load
	/// <c>book.babel</c> a second time.
	/// </summary>
	public Engine_BabelDatabase? Strings { get; private set; }

	/// <summary>
	/// Raised whenever the localised string database is reloaded (e.g. after mod overlays mount).
	/// </summary>
	public event Action<Engine_BabelDatabase>? StringsChanged;

	/// <summary>
	/// The shipped look, for anything drawn alongside the presentation rather than out of it.
	/// Re-catalogued on every <see cref="Show"/>, so its art comes from the presentation
	/// currently up. Null until loading succeeds.
	/// </summary>
	public Engine_UiTheme? Theme { get; private set; }

	/// <summary>
	/// Art as the FILE authors it, with nothing the guest has done applied — the pattern book a
	/// host-authored screen clones from. <see cref="Theme"/> is the other catalogue: the live
	/// screen, carrying the guest's slides, visibility, transforms and opacity. Cloning from
	/// that one picks up `ApplyLiveOpacity`'s baked result, so a row the guest never faded in
	/// comes out invisible. Built once per screen NAME and kept out of the scene tree.
	/// </summary>
	public Engine_UiTheme? Templates { get; private set; }

	private readonly Dictionary<string, Node3D> _templateTrees = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>The node tree currently mounted, or null before the first screen is shown.</summary>
	protected Node3D? Built { get; private set; }

	/// <summary>
	/// The mounted tree, for a caller that owns this screen and needs the mirror out of the way.
	/// The tree is still built — it is where the live art catalogue comes from and where owned
	/// views take their placement (`group.GlobalTransform`); an owner takes away only the
	/// DRAWING, restated every frame (see <c>GameUi.DriveOwnedBook</c>).
	/// </summary>
	public Node3D? MirrorTree => Built;

	/// <summary>The presentation currently built, for diagnostics.</summary>
	public Engine_UiScreen? CurrentScreen { get; private set; }

	/// <summary>Index into <see cref="ScreenNames"/> of the presentation on screen.</summary>
	public int CurrentIndex { get; private set; }

	/// <summary>The presentations available, once loading has finished.</summary>
	public IReadOnlyList<string> ScreenNames => _screens;

	/// <summary>True once the banks or the extracted tree opened and a screen can be shown.</summary>
	public bool IsLoaded => _loadScreen is not null;

	/// <summary>
	/// Which scene-level slide to build, 1-based, or 0 for the preview heuristic. Front-end
	/// screens are one page per slide (<see cref="Engine_BgfDocument.SetSceneSlide"/>). Takes
	/// effect on the next <see cref="ShowByName(string,int)"/> — overrides resolve at build.
	/// </summary>
	public int SceneSlide { get; set; }

	/// <summary>
	/// Per-element slides read from the running guest, by element ordinal. When set, these
	/// win over <see cref="SceneSlide"/> — they are what the game is actually showing
	/// rather than a guess at it.
	/// </summary>
	public IReadOnlyList<int>? LiveSlides { get; set; }

	/// <summary>Per-element text read from the running guest, by element ordinal.</summary>
	public IReadOnlyList<string?>? LiveText { get; set; }

	/// <summary>Per-element opacity read from the running guest, by element ordinal.</summary>
	public IReadOnlyList<float?>? LiveOpacity { get; set; }

	/// <summary>
	/// Per-element <c>globalactive</c> read from the running guest, by element ordinal — the
	/// guest's own draw gate. When present it REPLACES the slide-derived visibility, which can
	/// only reproduce what the file authored.
	/// </summary>
	public IReadOnlyList<bool>? LiveVisible { get; set; }

	/// <summary>
	/// Per-element live TRANSFORM read from the guest, by element ordinal:
	/// <c>{position.x, position.y, position.z, scale.x, scale.y, scale.z}</c>, any of which
	/// may be null when the guest holds no such attribute on that element.
	///
	/// Without this everything laid out at runtime collapses onto one spot — a menu's twelve
	/// row components are authored at the same place and moved apart by script.
	/// </summary>
	public IReadOnlyList<float?[]?>? LiveTransform { get; set; }

	/// <summary>
	/// Per-element live TEXTURE HASH read from the guest, by element ordinal; 0 where the
	/// guest states none.
	///
	/// The file's texture is a placeholder wherever the game picks the picture at runtime — the
	/// dpad quick-slots are all authored on a grey `empty.tex`, the HUD's twenty-four world-icon
	/// slots all on one icon.
	/// </summary>
	public IReadOnlyList<uint>? LiveTextures { get; set; }

	/// <summary>
	/// The guest's <c>TintingAmount</c> per element, or null where it states none — the WEIGHT of
	/// the vertex tint, not the tint. The guest subtracts <c>(1 - tint)</c> scaled by it rather
	/// than multiplying by the colour; it starts at zero on most quads.
	/// </summary>
	public IReadOnlyList<Engine_UiScreen.LiveTint?>? LiveTint { get; set; }

	/// <summary>
	/// The camera the current screen renders through, so a window resize can re-fit it. A canvas
	/// camera is sized against the window's ASPECT — see
	/// <see cref="Engine_UiSceneBuilder.FitCanvasCamera"/>.
	/// </summary>
	private Camera3D? _canvasCamera;

	private Vector2 _fittedTo;

	/// <summary>Re-fits the current camera when the viewport has changed size.</summary>
	protected void FitToViewport()
	{
		if (_canvasCamera is null || !IsInsideTree() ||
			!GodotObject.IsInstanceValid(_canvasCamera))
		{
			// The previous screen's camera is disposed with its tree and Show reassigns
			// _canvasCamera; touching the stale reference throws ObjectDisposedException.
			return;
		}

		Vector2 size = GetViewport().GetVisibleRect().Size;
		if (size == _fittedTo)
		{
			return;
		}

		_fittedTo = size;

		// The framing camera is fitted to the tree's bounds for a screen that ships no camera;
		// re-sizing it against the design box would throw that framing away.
		if (_canvasCamera.Name == "CanvasCamera")
		{
			Engine_UiSceneBuilder.FitCanvasCamera(_canvasCamera, size);
		}
	}

	public override void _Process(double delta) => FitToViewport();

	public override void _Ready()
	{
		// Subscribe to the asset mount completion signal so that modded text overlays
		// (e.g. custom item names/descriptions in book.babel.csv) are applied reactively
		// even if this presentation host was initialized before the asset worker finished.
		if (Refii.Assets.Engine_GameAssets.Instance is { } assets)
		{
			assets.Mounted += OnMountChanged;
			_mountWatched = assets;
		}

		if (LoadsInBackground && StartInBackground())
		{
			return;
		}

		// Streaming from the shipped banks is the real path; the extracted-tree branch below is
		// a fallback for a checkout without the game data.
		if (TryStartFromBanks())
			return;

		if (!TryStartFromExtractedTree())
			return;

		OnLoaded();
	}

	/// <summary>
	/// The banks, the texture name table, the fonts and the string book are read on a worker and
	/// adopted on the main thread a few frames later, so the host's first appearance does not
	/// stall the frame it is added on — the editor's preview, which comes up beside a working
	/// editor. <see cref="IsLoading"/> holds until then; <see cref="OnLoaded"/> runs as it does
	/// synchronously. The game loads synchronously: its frame order needs the host loaded on the
	/// frame it is added.
	/// </summary>
	public bool LoadsInBackground { get; set; }

	/// <summary>True while a background load (<see cref="LoadsInBackground"/>) is reading.</summary>
	public bool IsLoading { get; private set; }

	/// <summary>The game's asset mount this host follows, when one was up at <c>_Ready</c>.</summary>
	private Refii.Assets.Engine_GameAssets? _mountWatched;

	/// <summary>The mount was built again: the string book, the mods' presentations and textures,
	/// and every pattern book whose file changed under it.</summary>
	private void OnMountChanged(int fileCount)
	{
		ReloadStrings();
		AddModLayers();
		RefreshPatternBooks();
	}

	/// <summary>
	/// What the banks give a host before it can stand a screen, read on whichever thread loads:
	/// the presentations by name, the texture name table, the fonts and the string book.
	/// </summary>
	private sealed record BankIndex(
		BnkFileSystem Banks, bool OwnsBanks, string DataRoot, Dictionary<string, string> ByName,
		Engine_TextureNameTable Names, Engine_UiFontLibrary Fonts, Engine_BabelDatabase? Strings,
		string StringsSource);

	/// <summary>Reads the bank index; null when the banks hold no presentation. Any thread.</summary>
	private static BankIndex? IndexBanks(BnkFileSystem banks, bool owns, string data)
	{
		var texturePaths = new List<string>();
		var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (BnkFile file in banks.EnumerateAllFiles())
		{
			string path = file.VirtualPath;
			if (path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
				texturePaths.Add(path);
			// One entry per presentation; the .fac sidecar is a separate bank entry.
			else if (path.EndsWith(".bgf", StringComparison.OrdinalIgnoreCase))
				byName[BankEntryName(path)] = path;
		}

		if (byName.Count == 0)
			return null;

		Engine_TextureNameTable names = Engine_TextureNameTable.FromPaths(texturePaths, new[] { ".tex" });
		Engine_UiFontLibrary fonts = Engine_UiFontLibrary.Load(data, Engine_GameCulture.Current);
		Engine_BabelDatabase? strings = ReadStrings(data, diskFallback: true, out string source);
		return new BankIndex(banks, owns, data, byName, names, fonts, strings, source);
	}

	/// <summary>Takes a read index on the main thread: the host can stand screens after this.</summary>
	private void AdoptBanks(BankIndex index)
	{
		BnkFileSystem banks = index.Banks;
		if (index.OwnsBanks)
		{
			_owned.Add(banks);
		}

		Dictionary<string, string> byName = index.ByName;
		_names = index.Names;
		Textures = new Engine_UiTextureCache(banks);
		AdoptTextResources(index.Fonts, index.Strings, index.StringsSource);

		_screens.AddRange(byName.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
		_bankScreens = byName;
		_banks = banks;
		SourceRoot = $"banks: {index.DataRoot}";

		// A mount that finished first already holds the mods' layers.
		AddModLayers();

		_loadScreen = name =>
		{
			string bgfPath = byName[name];
			byte[] bgf = ReadLayered(banks, bgfPath) ?? throw new IOException($"{bgfPath} vanished from the bank");
			byte[]? fac = ReadLayered(banks, Path.ChangeExtension(bgfPath, ".fac"));
			// The .bsg carries the base transform the .bgf overrides — all three
			// sidecars are separate bank entries under the same stem.
			byte[]? bsg = ReadLayered(banks, Path.ChangeExtension(bgfPath, ".bsg"));
			_lastLoadSignature = FileSignature(bgf, fac, bsg);
			// LiveOpacity and LiveVisible are passed HERE, and were not before: the properties
			// existed, GameUi set them every frame, and nothing ever read them. A live value
			// that never reaches the parser is worse than no live value, because the call site
			// looks like it is following the guest and is not.
			return Engine_UiScreen.Parse(name, bgf, fac, bsg, _names, Strings, SceneSlide, LiveSlides,
				LiveText, LiveOpacity, LiveVisible, LiveTransform, LiveTextures, LiveTint);
		};

		OnLoaded();
	}

	/// <summary>
	/// Starts the background load; false when there is no game data to read, so the caller takes
	/// the synchronous path (and its report).
	/// </summary>
	private bool StartInBackground()
	{
		string data = ResolveGameDataRoot();
		if (data.Length == 0)
		{
			return false;
		}

		BnkFileSystem? given = Mount;
		IsLoading = true;
		Report("Loading the game's UI…");
		System.Threading.Tasks.Task.Run(() =>
		{
			BankIndex? index = null;
			string failure = string.Empty;
			BnkFileSystem? opened = null;
			try
			{
				BnkFileSystem banks = given ?? (opened = BnkFileSystem.Mount(data));
				index = IndexBanks(banks, opened is not null, data);
				if (index is null)
				{
					opened?.Dispose();
				}
			}
			catch (Exception exception)
			{
				opened?.Dispose();
				failure = exception.Message;
			}

			Callable.From(() => FinishInBackground(index, failure)).CallDeferred();
		});
		return true;
	}

	private void FinishInBackground(BankIndex? index, string failure)
	{
		if (!GodotObject.IsInstanceValid(this) || !IsInsideTree())
		{
			// The host went first; what the worker opened goes with it.
			if (index is { OwnsBanks: true })
			{
				index.Banks.Dispose();
			}

			return;
		}

		IsLoading = false;
		if (index is not null)
		{
			AdoptBanks(index);
			return;
		}

		Report(failure.Length != 0
			? $"Bank streaming unavailable: {failure}"
			: "The banks hold no presentation.");
	}

	/// <summary>
	/// Opens the shipped banks and serves every screen and texture straight out of
	/// them. Returns false when the game data is not present, so the caller can fall
	/// back to an extracted tree.
	/// </summary>
	private bool TryStartFromBanks()
	{
		string data = ResolveGameDataRoot();
		if (data.Length == 0)
			return false;

		// One mount for everything: the bank filesystem indexes every bank under the game
		// directory and stitches the header/payload halves a .tex is split across.
		BnkFileSystem banks;
		bool owns = false;
		if (Mount is { } given)
		{
			banks = given;
		}
		else
		{
			try
			{
				banks = BnkFileSystem.Mount(data);
			}
			catch (Exception ex)
			{
				// A mount that is present but unreadable is surfaced rather than silently falling
				// through to the extracted tree.
				Report($"Bank streaming unavailable: {ex.Message}");
				return false;
			}

			owns = true;
		}

		BankIndex? index = IndexBanks(banks, owns, data);
		if (index is null)
		{
			if (owns)
			{
				_owned.Add(banks);
			}

			return false;
		}

		AdoptBanks(index);
		return true;
	}

	/// <summary>
	/// Serves screens from a bank extraction laid out as <c>gameface/</c>, <c>headers/</c>
	/// and <c>payloads/</c>. Returns false — having reported why — when there is no such
	/// tree, since by this point the banks have already been ruled out.
	/// </summary>
	private bool TryStartFromExtractedTree()
	{
		SourceRoot = ResolveAssetRoot();
		if (SourceRoot.Length == 0)
		{
			Report("No GUI asset root found.\nSet REFII_GUI_ASSETS to a directory containing headers/, payloads/ and gameface/.");
			return false;
		}

		string gameface = Path.Combine(SourceRoot, "gameface");

		foreach (string required in new[]
			{ gameface, Path.Combine(SourceRoot, "headers"), Path.Combine(SourceRoot, "payloads") })
		{
			if (!Directory.Exists(required))
			{
				Report($"Asset root {SourceRoot}\nis missing {Path.GetFileName(required)}/.");
				return false;
			}
		}

		// The name table hashes the real extracted paths rather than guessing from the
		// authoring strings, which is what takes UV resolution to 100%.

		var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (string file in Directory.EnumerateFiles(gameface, "*.bgf"))
			files[Path.GetFileNameWithoutExtension(file)] = file;

		_screens.AddRange(files.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
		if (_screens.Count == 0)
		{
			Report($"No .bgf files under {gameface}.");
			return false;
		}

		_loadScreen = name => Engine_UiScreen.Load(files[name], _names, null, SceneSlide, LiveSlides);
		return true;
	}

	/// <summary>Reads one bank entry, or null when it is not present.</summary>
	private static byte[]? Read(BnkFileSystem banks, string virtualPath) =>
		banks.TryGetFile(virtualPath, out BnkFile file) ? file.ReadAllBytes() : null;

	/// <summary>
	/// Reads one entry through the GAME'S mount when it is up — the one with the enabled mods'
	/// layers, so a mod's <c>.bgf</c>, <c>.fac</c> or <c>.bsg</c> stands in for the shipped one —
	/// and from this host's own bank mount otherwise (the editor's viewers, a run before the mount
	/// finishes).
	/// </summary>
	private static byte[]? ReadLayered(BnkFileSystem banks, string virtualPath) =>
		Refii.Assets.Engine_GameAssets.Instance is { IsMounted: true } assets &&
		assets.ReadAllBytes(virtualPath) is { } layered
			? layered
			: Read(banks, virtualPath);

	/// <summary>The presentations by name, as <see cref="_loadScreen"/> resolves them; null off the banks.</summary>
	private Dictionary<string, string>? _bankScreens;

	/// <summary>
	/// What the enabled mods ADD to the UI: a <c>.bgf</c> no bank ships becomes a presentation this
	/// host serves, and a <c>.tex</c> joins the name table so a <c>.fac</c> that names it by hash
	/// resolves. Replacements need nothing here — <see cref="ReadLayered"/> and the texture loader
	/// read through the mod-aware mount. Run when the game's mount completes, and on start.
	/// </summary>
	private void AddModLayers()
	{
		if (_bankScreens is null || Refii.Assets.Engine_GameAssets.Instance is not { IsMounted: true } assets)
		{
			return;
		}

		int screens = 0;
		int textures = 0;
		foreach (BnkFile file in assets.EnumerateAllFiles())
		{
			if (file.SourceMod.Length == 0)
			{
				continue;
			}

			string path = file.VirtualPath;
			if (path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
			{
				_names?.Add(path);
				++textures;
			}
			else if (path.EndsWith(".bgf", StringComparison.OrdinalIgnoreCase) &&
				_bankScreens.TryAdd(BankEntryName(path), path))
			{
				_screens.Add(BankEntryName(path));
				++screens;
			}
		}

		if (screens + textures != 0)
		{
			GD.Print($"[ui] mod layers: {screens} new presentation(s), {textures} mod texture(s) named");
		}
	}

	/// <summary>
	/// Takes the bitmap fonts and the localised string database. Both are LOOSE FILES under
	/// <c>language/</c>, not bank entries, so they come from the data root. Neither is fatal:
	/// without Babel the authored tag displays verbatim, as the engine falls back too.
	/// </summary>
	private void AdoptTextResources(Engine_UiFontLibrary fonts, Engine_BabelDatabase? strings, string source)
	{
		Fonts = fonts;
		Engine_UiKeyGlyphs.Fonts ??= Fonts;
		foreach (string failure in Fonts.Failures)
			GD.PushWarning($"[ui] font: {failure}");

		if (strings is not null)
		{
			AdoptStrings(strings, source);
		}

		// Built on every path out of here, including the two failures above: a theme with no
		// string book still carries the fonts and the art.
		Theme = new Engine_UiTheme { Fonts = Fonts, Strings = Strings, Textures = LoadTexture };
	}

	/// <summary>
	/// The guest changed language: the fonts are read again from the new culture's folder (en-uk's
	/// when it ships none — only ko-kr and zh-cht do), IN PLACE, so every theme, pattern book and
	/// resolver holding this library draws the new glyphs; then the string book. False when no
	/// fonts had been loaded yet — the first load reads the culture itself.
	/// </summary>
	public bool ReloadForCulture(string culture)
	{
		string dataRoot = ResolveGameDataRoot();
		bool fontsChanged = Fonts is not null && dataRoot.Length != 0 && Fonts.Reload(dataRoot, culture);
		if (fontsChanged)
		{
			foreach (string failure in Fonts!.Failures)
				GD.PushWarning($"[ui] font: {failure}");
			GD.Print($"[ui] fonts for '{culture}' from {Fonts.Directory}");
		}

		ReloadStrings(dataRoot);
		return Fonts is not null;
	}

	/// <summary>
	/// Reloads the localised string database. Tries Godot's mounted asset filesystem first (which
	/// includes mod row overlays, such as custom item names and descriptions from book.babel.csv)
	/// before falling back to raw disk.
	/// </summary>
	public void ReloadStrings(string? dataRoot = null)
	{
		dataRoot = !string.IsNullOrEmpty(dataRoot) ? dataRoot : ResolveGameDataRoot();
		if (ReadStrings(dataRoot, diskFallback: Strings is null, out string source) is { } strings)
		{
			AdoptStrings(strings, source);
		}
	}

	private void AdoptStrings(Engine_BabelDatabase strings, string source)
	{
		Strings = strings;
		Engine_UiLocale.Note(strings);
		if (Theme is not null)
		{
			Theme.Strings = Strings;
		}

		GD.Print($"[ui] Loaded string database {source} ({Strings.Count} strings)");
		StringsChanged?.Invoke(Strings);
	}

	/// <summary>
	/// The string database: through Godot's mounted asset filesystem (mod row overlays included)
	/// when it serves one, else — with <paramref name="diskFallback"/> — from the data root on disk.
	/// Null when neither reads. Any thread.
	/// </summary>
	private static Engine_BabelDatabase? ReadStrings(string dataRoot, bool diskFallback, out string source)
	{
		source = string.Empty;
		byte[]? mountBytes = null;
		string mountedPathUsed = string.Empty;
		// The culture the guest runs in (Engine_GameCulture), then en-uk.
		string culture = Engine_GameCulture.Current;
		if (Refii.Assets.Engine_GameAssets.Instance is { } assets)
		{
			foreach (string candidate in Engine_GameCulture.MountCandidates(culture, "text/book.babel"))
			{
				mountBytes = assets.ReadAllBytes(candidate);
				if (mountBytes is { Length: > 0 })
				{
					mountedPathUsed = candidate;
					break;
				}
			}
		}

		if (mountBytes is { Length: > 0 })
		{
			try
			{
				Engine_BabelDatabase parsed = Engine_BabelDatabase.Parse(mountBytes);
				source = $"via Engine_GameAssets mount ('{mountedPathUsed}', {mountBytes.Length} bytes)";
				return parsed;
			}
			catch (Exception exception)
			{
				GD.PushWarning($"[ui] string database from mount failed: {exception.Message}");
			}
		}

		if (!diskFallback || string.IsNullOrEmpty(dataRoot))
		{
			return null;
		}

		string? textDir = null;
		foreach (string folder in Engine_GameCulture.SearchOrder(culture))
		{
			textDir = Engine_UiFontLibrary.LanguageSubdirectory(dataRoot, folder, "text");
			if (textDir is not null && File.Exists(Path.Combine(textDir, "book.babel")))
			{
				break;
			}
		}

		string babel = textDir is null ? string.Empty : Path.Combine(textDir, "book.babel");
		if (babel.Length == 0 || !File.Exists(babel))
		{
			GD.PushWarning($"[ui] no string database under {dataRoot}; tags will display raw");
			return null;
		}

		try
		{
			Engine_BabelDatabase loaded = Engine_BabelDatabase.Load(babel);
			source = $"from raw disk ('{babel}')";
			return loaded;
		}
		catch (Exception exception)
		{
			GD.PushWarning($"[ui] string database: {exception.Message}");
			return null;
		}
	}

	/// <summary>
	/// Base name of a bank entry. Bank paths are authored with backslashes, which are
	/// ordinary characters on Linux — <see cref="Path.GetFileNameWithoutExtension"/>
	/// would hand back the whole path, so the separators are normalised first.
	/// </summary>
	private static string BankEntryName(string bankPath) =>
		Path.GetFileNameWithoutExtension(bankPath.Replace('\\', '/'));

	/// <summary>
	/// Called once the presentations are available. The default puts
	/// <see cref="StartScreen"/> up; the viewer overrides it to honour its environment
	/// variables first.
	/// </summary>
	protected virtual void OnLoaded()
	{
		int start = StartScreen.Length == 0
			? 0
			: _screens.FindIndex(n => string.Equals(n, StartScreen, StringComparison.OrdinalIgnoreCase));
		Show(start >= 0 ? start : 0);
	}

	/// <summary>
	/// Finds the shipped game data. <see cref="GameDataRoot"/> is an explicit override;
	/// everything else defers to <see cref="Engine_GameData.Resolve"/> — never a second copy of
	/// the search order.
	/// </summary>
	private string ResolveGameDataRoot()
	{
		if (Directory.Exists(GameDataRoot))
			return GameDataRoot;

		string resolved = Engine_GameData.Resolve();
		return Directory.Exists(resolved) ? resolved : string.Empty;
	}

	/// <summary>
	/// Finds the extraction. Kept separate so the precedence is readable and so a missing
	/// root reports which candidates were tried.
	/// </summary>
	private string ResolveAssetRoot()
	{
		string? fromEnv = System.Environment.GetEnvironmentVariable("REFII_GUI_ASSETS");
		if (!string.IsNullOrEmpty(fromEnv) && Directory.Exists(fromEnv))
			return fromEnv!;

		string? home = System.Environment.GetEnvironmentVariable("HOME");
		if (!string.IsNullOrEmpty(home))
		{
			string candidate = Path.Combine(home!, ".local", "share", "refii", "assets", "gui-extracted");
			if (Directory.Exists(candidate))
				return candidate;
		}

		return Directory.Exists(AssetRoot) ? AssetRoot : string.Empty;
	}

	/// <summary>
	/// Shows a screen by name, on a given scene slide. Returns false when the
	/// presentation is not among the shipped set, or when loading has not finished.
	/// </summary>
	public bool ShowByName(string name, int sceneSlide)
	{
		SceneSlide = sceneSlide;
		return ShowByName(name);
	}

	/// <summary>Shows a screen with the guest's live per-element slides applied.</summary>
	public bool ShowByName(string name, IReadOnlyList<int>? liveSlides)
	{
		LiveSlides = liveSlides;
		return ShowByName(name);
	}

	/// <summary>
	/// Shows a screen by name. Returns false when the presentation is not among the
	/// shipped set, or when loading has not finished.
	/// </summary>
	public bool ShowByName(string name)
	{
		int index = _screens.FindIndex(
			n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
		if (index < 0 || _loadScreen is null)
			return false;

		Show(index);
		return true;
	}

	/// <summary>
	/// Builds a screen and hands it back WITHOUT mounting it, so a caller can hold several at
	/// once — the guest keeps several standing together, and it hides what it does not want with
	/// per-element <c>globalactive</c>, so a presentation being live says nothing about whether
	/// it is showing anything. <see cref="Show"/> owns exactly one screen instead. Banks, texture
	/// cache, fonts and string book are shared. Set <see cref="LiveSlides"/> and friends first;
	/// they are read while the screen is parsed. Null when the name is not shipped.
	/// </summary>
	public Node3D? BuildDetached(string name, out Engine_UiScreen? screen) =>
		BuildDetached(name, out screen, out _);

	/// <summary>
	/// As above, and reports what was built. A screen whose textures did not resolve still
	/// builds — the quads draw with no albedo — so the counts are what separate the two.
	/// </summary>
	public Node3D? BuildDetached(string name, out Engine_UiScreen? screen,
		out Engine_UiSceneBuilder? stats, bool keepInactiveForCatalogue = false)
	{
		screen = null;
		stats = null;
		if (_loadScreen is null)
		{
			return null;
		}

		int index = _screens.FindIndex(
			n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
		if (index < 0)
		{
			return null;
		}

		try
		{
			screen = _loadScreen(_screens[index]);
		}
		catch (Exception ex)
		{
			Report($"{name}\nload failed: {ex.Message}");
			return null;
		}

		var builder = new Engine_UiSceneBuilder
		{
			TextureResolver = rel => Textures?.Load(rel),
			FontResolver = size => Fonts?.For(size),

			// The CATALOGUE build keeps slide-parked art, hidden and marked; a drawn build
			// never does — see Engine_UiSceneBuilder.KeepInactiveForCatalogue.
			KeepInactiveForCatalogue = keepInactiveForCatalogue,
		};

		Node3D built = builder.Build(screen);
		Engine_UiSceneBuilder.ApplyCameraAiming(built);
		stats = builder;
		return built;
	}

	/// <summary>
	/// Takes the mounted screen down and forgets it. <see cref="Show"/> frees the old screen
	/// only on its way to a new one, so a guest leaving the front end needs this. The art
	/// catalogue goes with it — every entry points into the tree being freed. Clones already
	/// taken out of it are unaffected.
	/// </summary>
	public void ClearScreen()
	{
		if (Built is null)
		{
			return;
		}

		RemoveChild(Built);
		Built.QueueFree();
		Built = null;
		CurrentScreen = null;
		Theme?.Clear();
	}

	/// <summary>Rebuilds the current screen — after the live slides have changed.</summary>
	public void Rebuild()
	{
		if (_screens.Count > 0)
			Show(CurrentIndex);
	}

	/// <summary>
	/// Element names hidden after every rebuild — the guest widgets a host-owned stand-in
	/// replaces (menu rows, its highlight). Registered by the stand-in's owner; cleared when
	/// the stand-in goes away.
	/// </summary>
	public readonly System.Collections.Generic.HashSet<string> SuppressedElementNames = new();

	private void ApplySuppressions(Node root)
	{
		if (SuppressedElementNames.Count == 0)
		{
			return;
		}

		int hidden = 0;
		void Walk(Node node)
		{
			if (node is Node3D spatial && SuppressedElementNames.Contains(node.Name))
			{
				if (spatial.Visible)
				{
					spatial.Visible = false;
					++hidden;
				}

				return;  // the subtree goes with its root
			}

			foreach (Node child in node.GetChildren())
			{
				Walk(child);
			}
		}

		Walk(root);
		if (hidden != 0)
		{
			GD.Print($"[game-ui] suppressed {hidden} guest element instance(s) after rebuild " +
				$"({string.Join(", ", SuppressedElementNames)})");
		}
	}

		/// <summary>Builds the presentation at <paramref name="index"/> and mounts it.</summary>
		public void Show(int index)
	{
		if (_loadScreen is null || index < 0 || index >= _screens.Count)
			return;

		CurrentIndex = index;
		string name = _screens[index];

		if (Built is not null)
		{
			RemoveChild(Built);
			Built.QueueFree();
			Built = null;
		}

		Engine_UiScreen screen;
		try
		{
			screen = _loadScreen(name);
		}
		catch (Exception ex)
		{
			Report($"{name}\nload failed: {ex.Message}");
			return;
		}

		CurrentScreen = screen;

		var builder = new Engine_UiSceneBuilder
		{
			TextureResolver = rel => Textures?.Load(rel),
			FontResolver = size => Fonts?.For(size),
		};
		Built = builder.Build(screen);
		// BUILT HIDDEN: the mirror is a data source — the owned books draw. Godot owns every
		// screen, so nothing ever turns this on.
		Built.Visible = false;
		AddChild(Built);
		Engine_UiSceneBuilder.ApplyCameraAiming(Built);

		// On EVERY rebuild: the tree is torn down per guest state change, so a suppression done
		// once from outside comes back visible. The stand-in's owner registers the name once.
		ApplySuppressions(Built);

		// The catalogue describes whatever is standing up now, so it is rebuilt here rather than
		// once at load — cataloguing up front would build all 52 presentations.
		Theme?.Catalogue(Built, name);
		CatalogueTemplates(name);

		// A camera has to be made CURRENT or the viewport renders through Godot's default one.
		Camera3D camera = Engine_UiSceneBuilder.ActivateCamera(Built, UseFramingCamera);
		_canvasCamera = camera;
		FitToViewport();

		// What the frame engine did, per screen. Its two failure modes look identical on screen:
		// no frames found is a detection miss; frames with no border slices means the screen
		// expects the original's runtime cloning step.
		Engine_UiFrameDriver.Report layout = screen.FrameLayout;
		if (layout.Frames > 0)
		{
			GD.Print($"[ui-frame] {name}: {layout.Frames} frame(s), " +
				$"{layout.Contents} item(s) placed, {layout.Slots} slice(s), " +
				$"{layout.Blocks} block(s)" +
				(layout.WithoutBorder > 0
					? $", {layout.WithoutBorder} with no border elements to drive"
					: string.Empty));
		}

		OnScreenShown(screen, builder, camera);
	}

	/// <summary>
	/// Catalogues <see cref="Templates"/> from a FILE-ONLY build of a screen: the live inputs are
	/// withheld for the duration and restored after, so the same parser and builder produce the
	/// artist's version. Built once per name — the pattern book does not change per rebuild.
	/// </summary>
	private void CatalogueTemplates(string name)
	{
		Node3D? tree = TemplateTree(name);
		if (tree is null)
		{
			return;
		}

		Templates ??= new Engine_UiTheme { Fonts = Fonts, Strings = Strings, Textures = LoadTexture };
		Templates.Catalogue(tree, name);
	}

	/// <summary>
	/// A CLONE of a screen's whole file-only build, for a host that owns a small presentation
	/// outright — the pause banner, whose element names are synthesized so the by-name art
	/// catalogue cannot address its pieces. The clone shares the template's materials, which
	/// is safe while the template itself is never drawn.
	/// </summary>
	public Node3D? CloneTemplateTree(string name) =>
		TemplateTree(name) is { } tree ? (Node3D)tree.Duplicate() : null;

	/// <summary>
	/// A pattern book for ONE screen, as its own catalogue. <see cref="Templates"/> is
	/// re-catalogued per shown screen; a host-owned panel needing art from a screen that is never
	/// shown by name takes it from here instead of clobbering that book.
	/// </summary>
	public Engine_UiTheme? PatternBookFor(string name)
	{
		Node3D? tree = TemplateTree(name);
		if (tree is null)
		{
			return null;
		}

		// ONE CATALOGUE PER TREE. The catalogue is a walk of every node in the template tree —
		// four interop calls a node, 4400 nodes on the abilities book — and every owner of a
		// screen asked for it again on every open (the book set, the list view, the info
		// panel). The tree it indexes is itself cached and only replaced on a remount, so the
		// catalogue is good exactly as long as the tree and the fonts it was built with are
		// the same instances; the string book is the one input that reloads underneath, and
		// it is restated on every hand-out.
		if (!_patternBooks.TryGet(name, out Engine_UiTheme book, tree, Fonts))
		{
			book = new Engine_UiTheme { Fonts = Fonts, Strings = Strings, Textures = LoadTexture };
			book.Catalogue(tree, name);
			_patternBooks.Put(name, book, tree, Fonts);
		}

		book.Strings = Strings;
		return book;
	}

	/// <summary>Per presentation, the catalogue of its template tree — see <see cref="PatternBookFor"/>.</summary>
	private readonly Engine_UiIdentityCache<string, Engine_UiTheme> _patternBooks =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// The parsed DOCUMENT of a screen, from the same file-only build the pattern book uses —
	/// for a host-owned panel that joins live state read off the bridge by ordinal, and needs
	/// the authored structure (names, ordinals, element count) to do it. Built (and cached)
	/// with the live inputs withheld, so a stale live array from the last mirrored screen can
	/// never leak into the parse.
	/// </summary>
	public Engine_UiScreen? DocumentFor(string name)
	{
		TemplateTree(name);
		return _templateScreens.TryGetValue(name, out Engine_UiScreen? screen) ? screen : null;
	}

	private readonly Dictionary<string, Engine_UiScreen> _templateScreens =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>The cached FILE-ONLY build of a screen — the artist's version.</summary>
	private Node3D? TemplateTree(string name)
	{
		if (!_templateTrees.TryGetValue(name, out Node3D? tree))
		{
			// A pattern book is a whole detached build with inactive subtrees KEPT (the world map
			// is 4125 elements), built synchronously on the frame the screen is first shown.
			long began = System.Diagnostics.Stopwatch.GetTimestamp();
			IReadOnlyList<int>? slides = LiveSlides;
			IReadOnlyList<string?>? text = LiveText;
			IReadOnlyList<float?>? opacity = LiveOpacity;
			IReadOnlyList<bool>? visible = LiveVisible;
			IReadOnlyList<float?[]?>? transform = LiveTransform;
			IReadOnlyList<uint>? textures = LiveTextures;
			IReadOnlyList<Engine_UiScreen.LiveTint?>? tint = LiveTint;
			int sceneSlide = SceneSlide;

			try
			{
				LiveSlides = null;
				LiveText = null;
				LiveOpacity = null;
				LiveVisible = null;
				LiveTransform = null;
				LiveTextures = null;
				LiveTint = null;

				// The scene slide goes too: a screen is built on ONE page and the builder drops
				// an inactive element's whole subtree, so cataloguing on the guest's current page
				// throws away the components a host-owned screen borrows. Zero is the preview
				// heuristic — each owner's first real slide.
				SceneSlide = 0;
				_lastLoadSignature = 0;
				tree = BuildDetached(name, out Engine_UiScreen? screen, out _,
					keepInactiveForCatalogue: true);
				if (screen is not null)
				{
					_templateScreens[name] = screen;
				}

				_templateSignatures[name] = _lastLoadSignature;
			}
			finally
			{
				LiveSlides = slides;
				LiveText = text;
				LiveOpacity = opacity;
				LiveVisible = visible;
				LiveTransform = transform;
				LiveTextures = textures;
				LiveTint = tint;
				SceneSlide = sceneSlide;
			}

			if (tree is null)
			{
				return null;
			}

			_templateTrees[name] = tree;

			double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000.0
				/ System.Diagnostics.Stopwatch.Frequency;
			int elements = _templateScreens.TryGetValue(name, out Engine_UiScreen? built)
				? built.Nodes.Length
				: 0;
			Report($"[ui-host] pattern book '{name}' built in {ms:0.0}ms — {elements} element(s); " +
				$"{_templateTrees.Count} book(s) now resident, never released until the host " +
				"leaves the tree");
		}

		return tree;
	}

	/// <summary>The bank mount the presentations are read through; null off the banks.</summary>
	private BnkFileSystem? _banks;

	/// <summary>The bytes the last <see cref="_loadScreen"/> read, as one signature; 0 off the banks.</summary>
	private ulong _lastLoadSignature;

	/// <summary>Per cached pattern book, the signature of the files it was built from.</summary>
	private readonly Dictionary<string, ulong> _templateSignatures = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>How many times each presentation's pattern book has been rebuilt on a new mount.</summary>
	private readonly Dictionary<string, int> _bookGenerations = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Books retired by <see cref="RefreshPatternBooks"/>, freed once their owners let go.</summary>
	private readonly List<Node3D> _retiredTrees = new();

	/// <summary>
	/// How many times <paramref name="name"/>'s pattern book has been rebuilt because the mount
	/// changed its files. An owner that built from the book compares it with the generation it
	/// built at and builds again when it moves.
	/// </summary>
	public int BookGeneration(string name) =>
		_bookGenerations.TryGetValue(name, out int generation) ? generation : 0;

	/// <summary>
	/// Raised, on the main thread, with the presentations whose pattern books the new mount
	/// retired — a mod's <c>.bgf</c>, <c>.fac</c> or <c>.bsg</c> now stands in for the file the
	/// book was built from, or no longer does. Every owner holding art, a theme or a node from one
	/// of those books drops it here and builds again from <see cref="PatternBookFor"/>: the retired
	/// trees are freed at the end of this frame.
	/// </summary>
	public event Action<IReadOnlyList<string>>? PatternBooksRetired;

	/// <summary>One signature of a presentation's three files (FNV-1a 64 over each, combined).</summary>
	private static ulong FileSignature(byte[]? bgf, byte[]? fac, byte[]? bsg)
	{
		ulong hash = 0xCBF29CE484222325UL;
		foreach (byte[]? file in new[] { bgf, fac, bsg })
		{
			hash = (hash ^ (ulong)(file?.Length ?? -1)) * 0x100000001B3UL;
			if (file is null)
			{
				continue;
			}

			foreach (byte b in file)
			{
				hash = (hash ^ b) * 0x100000001B3UL;
			}
		}

		return hash;
	}

	/// <summary>
	/// The bank path of a presentation by name — the path a mod's <c>.bgf</c> takes to stand in
	/// for it (<c>art/gui/gameface/hud.bgf</c> for <c>hud</c>); null off the banks or unknown.
	/// </summary>
	public string? PresentationPath(string name) =>
		_bankScreens is not null && _bankScreens.TryGetValue(name, out string? path) ? path : null;

	/// <summary>
	/// A presentation's file as the mount serves it now (a mod's file over the shipped one):
	/// the <c>.bgf</c> itself, or with <paramref name="extension"/> a sidecar beside it
	/// (<c>.fac</c>, <c>.bsg</c>, separate bank entries under the same stem). Null when not served.
	/// </summary>
	public byte[]? PresentationBytes(string name, string extension = ".bgf") =>
		_banks is { } banks && PresentationPath(name) is { } path
			? ReadLayered(banks, extension == ".bgf" ? path : Path.ChangeExtension(path, extension))
			: null;

	/// <summary>The signature of a presentation's files as the mount serves them now; 0 when unknown.</summary>
	private ulong CurrentSignature(string name)
	{
		if (_banks is not { } banks || _bankScreens is null || !_bankScreens.TryGetValue(name, out string? bgfPath))
		{
			return 0;
		}

		return FileSignature(
			ReadLayered(banks, bgfPath),
			ReadLayered(banks, Path.ChangeExtension(bgfPath, ".fac")),
			ReadLayered(banks, Path.ChangeExtension(bgfPath, ".bsg")));
	}

	/// <summary>
	/// The mount changed: every cached pattern book whose files now read differently is retired —
	/// dropped from the cache so the next <see cref="PatternBookFor"/> / <see cref="DocumentFor"/>
	/// builds it from the new files — and its owners are told (<see cref="PatternBooksRetired"/>).
	/// The retired trees are freed at the end of the frame, after the owners have let go; the
	/// shown screen's catalogue (<see cref="Templates"/>) is rebuilt at once.
	/// </summary>
	public void RefreshPatternBooks()
	{
		if (_templateTrees.Count == 0)
		{
			return;
		}

		var retired = new List<string>();
		foreach (string name in _templateTrees.Keys.ToList())
		{
			ulong now = CurrentSignature(name);
			if (now == 0 || (_templateSignatures.TryGetValue(name, out ulong was) && was == now))
			{
				continue;
			}

			retired.Add(name);
			_retiredTrees.Add(_templateTrees[name]);
			_templateTrees.Remove(name);
			_templateScreens.Remove(name);
			_templateSignatures.Remove(name);
			_patternBooks.Remove(name);
			_bookGenerations[name] = BookGeneration(name) + 1;
		}

		if (retired.Count == 0)
		{
			return;
		}

		// The shown screen's catalogue held art from the books it was built with; it is built
		// again from the new ones, as a Show would.
		Templates = null;
		if (CurrentScreen is { } current)
		{
			CatalogueTemplates(current.Name);
		}

		Report($"[ui-host] the mount changed {retired.Count} presentation(s) — pattern book(s) rebuilt on next " +
			$"use: {string.Join(", ", retired)}");
		PatternBooksRetired?.Invoke(retired);
		Callable.From(FreeRetiredTrees).CallDeferred();
	}

	private void FreeRetiredTrees()
	{
		foreach (Node3D tree in _retiredTrees)
		{
			if (GodotObject.IsInstanceValid(tree))
			{
				tree.QueueFree();
			}
		}

		_retiredTrees.Clear();
	}

	/// <summary>
	/// Called after a screen has been built, mounted and aimed. The base reports the
	/// counts; the viewer overrides it to add its dumps.
	/// </summary>
	protected virtual void OnScreenShown(
		Engine_UiScreen screen, Engine_UiSceneBuilder builder, Camera3D camera)
	{
		Report($"[{CurrentIndex + 1}/{_screens.Count}] {screen.Name}   " +
			$"{screen.CanvasWidth}x{screen.CanvasHeight}\n" +
			$"quads {builder.QuadsBuilt} (degenerate {builder.DegenerateQuads})   " +
			$"textures {builder.TexturesResolved}   " +
			$"text {builder.TextsBuilt} ({builder.TextGlyphsBuilt} glyphs)   " +
			$"camera {camera.Name}");
	}

	/// <summary>Where progress and failures go. The viewer also writes them to its label.</summary>
	protected virtual void Report(string message) => GD.Print(message);

	public override void _ExitTree()
	{
		if (_watchingAdds && IsInsideTree())
		{
			GetTree().NodeAdded -= OnNodeAdded;
			_watchingAdds = false;
		}

		if (_mountWatched is { } assets && GodotObject.IsInstanceValid(assets))
		{
			assets.Mounted -= OnMountChanged;
		}

		_mountWatched = null;

		// The template trees were never added to the scene, so nothing else will free them.
		foreach (Node3D tree in _templateTrees.Values)
			tree.QueueFree();
		_templateTrees.Clear();
		_templateScreens.Clear();
		_templateSignatures.Clear();
		_patternBooks.Clear();
		FreeRetiredTrees();

		foreach (IDisposable owned in _owned)
			owned.Dispose();
		_owned.Clear();
	}
}
