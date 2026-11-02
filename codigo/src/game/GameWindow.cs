using Refii.Ui;
using Refii.Host;
using Godot;

namespace Refii.Game;

/// <summary>
/// The game, occupying its process's MAIN window.
/// </summary>
/// <remarks>
/// THIS USED TO BE A CHILD `Window`, and that is what put two windows on screen. The game runs
/// in a process spawned by the launcher (see <see cref="GameChildProcess"/>), and that process
/// still boots `main.tscn` — so a child window meant the game's window PLUS the launcher shell
/// that hosted it. Blanking and minimising the shell did not help: it is still a window, in the
/// taskbar and in alt-tab.
///
/// So the game configures the MAIN window instead of creating one. One process, one window,
/// and the same arrangement whether the parent is the packaged launcher or the editor — the
/// editor is simply the Godot app taking the child's place.
///
/// The guest creates no window of its own — see the host bridge's REX_EMBED_HEADLESS.
/// </remarks>
public partial class GameWindow : Node
{
	private static GameWindow? _current;

	/// <summary>
	/// Whether a game window is already up. Exposed so an unattended launcher can tell "the
	/// window opened" from "the press did nothing" — <see cref="Open"/> answers null for both a
	/// refusal and an existing window, which had the auto-driver pressing the primary action
	/// ninety-odd times into an already-running game.
	/// </summary>
	public static bool IsOpen => _current is not null && GodotObject.IsInstanceValid(_current);

	private GameSession? _session;
	private GameUi? _ui;
	private GameVideo? _video;

	private DisplayServer.WindowMode _launcherMode = DisplayServer.WindowMode.Windowed;
	private bool _launcherRestored;

	/// <summary>
	/// Boot the guest with the window. Off opens the window and mounts assets only.
	/// </summary>
	public bool StartGuest { get; set; } = true;

	/// <summary>The live session, once <see cref="_Ready"/> has run.</summary>
	public GameSession? Session => _session;

	public override void _Ready()
	{
		Refii.Launcher.Core.CrashReporting.Install();

		// EVERY SETTING GOES TO THE MAIN WINDOW, through DisplayServer rather than Window
		// properties — this node no longer owns a window of its own.
		const int Main = (int)DisplayServer.MainWindowId;
		DisplayServer.WindowSetTitle("Fable 2", Main);

		// refii.toml owns the window, as it does for the other renderers.
		GameWindowSettings settings = GameWindowSettings.Load();
		GD.Print($"[game] window: {settings}");

		DisplayServer.WindowSetSize(settings.Size, Main);

		// Centre on the configured monitor; an out-of-range index falls back to the primary.
		int monitor = settings.Monitor < DisplayServer.GetScreenCount() ? settings.Monitor : 0;
		Vector2I screen = DisplayServer.ScreenGetSize(monitor);
		DisplayServer.WindowSetPosition(
			((screen - settings.Size) / 2) + DisplayServer.ScreenGetPosition(monitor), Main);

		if (settings.Fullscreen)
		{
			DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen, Main);
		}
		else
		{
			// The shell came up minimised so the launcher's blank scene never flashed; the
			// game's window has to undo that.
			DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed, Main);
		}

		DisplayServer.WindowMoveToForeground(Main);

		// AND THE ROOT VIEWPORT DRAWS 1:1, which a child Window gave us for free and the main
		// window does not.
		//
		// `project.godot` sets the base viewport to 1280x760 with `stretch/mode=canvas_items`
		// — sized for the LAUNCHER, whose chrome needs the extra forty pixels. The game asks
		// for 1280x720, so the moment it draws into the root instead of its own window that
		// mismatch is stretched across the frame and everything sits low. Disabling the stretch
		// makes the viewport match the window exactly, which is what the child window did.
		// THE BASE SIZE HAS TO MOVE TOO, not just the scale mode.
		//
		// `project.godot` sets the base viewport to 1280x760 for the LAUNCHER's chrome. Turning
		// the stretch off was not enough on its own: the root viewport kept reporting 760 while
		// the OS window was 720, so the frame was composed 40 rows taller than the window and
		// everything sat low with the bottom cropped. The run said so plainly — the UI logged
		// `window=(1280, 760)` against a 720-tall window.
		//
		// Set through the root WINDOW rather than DisplayServer: the node's `Size` moves the
		// viewport and the OS window together, where `DisplayServer.WindowSetSize` moves only
		// the latter and leaves the viewport on the project's base.
		Window root = GetTree().Root;
		root.ContentScaleMode = Window.ContentScaleModeEnum.Disabled;
		root.ContentScaleAspect = Window.ContentScaleAspectEnum.Ignore;
		root.ContentScaleSize = settings.Size;
		root.Size = settings.Size;

		DisplayServer.WindowSetFlag(
			DisplayServer.WindowFlags.Borderless, settings.Borderless, Main);

		// THE FRAME CAP IS THE DRAWING RATE, NOT THE SIM. `GameSession` advances the guest on
		// its own fixed step with a catch-up ceiling, so capping frames here changes how often
		// the world is DRAWN and nothing about how often it is simulated.
		Engine.MaxFps = settings.MaxFps;
		Refii.Core.Engine_TickClock.Pin = settings.DrawLatencyTicks;

		// 3D SCALING LEAVES THE UI ALONE. Godot renders the 3D world at this fraction and
		// upscales it into a full-resolution frame, so the HUD, the books and every glyph stay
		// native — which is the whole reason to prefer this over resizing the window.
		Viewport view = GetViewport();
		view.Scaling3DScale = settings.RenderScale;
		// SCALED WITH HEIGHT: the mesh LOD keys are the guest's switch distances at 720 lines
		// (Engine_MdlLods), and Godot's threshold is in pixels, so 1 here follows the 360 at
		// any resolution.
		view.MeshLodThreshold = settings.MeshLodPixels * (settings.Size.Y / Refii.Models.Engine_MdlLods.ReferenceHeight);
		RenderingServer.DirectionalShadowAtlasSetSize(settings.SunShadowMap, true);
		view.Msaa3D = settings.Msaa switch
		{
			>= 8 => Viewport.Msaa.Msaa8X,
			>= 4 => Viewport.Msaa.Msaa4X,
			>= 2 => Viewport.Msaa.Msaa2X,
			_ => Viewport.Msaa.Disabled,
		};

		// THE UPSCALER IS THE PLAYER'S, AND OFF BY DEFAULT: bilinear at 100% is native, and
		// native is what the frame-rate target is met at.
		view.Scaling3DMode = settings.Scaler switch
		{
			"fsr" => Viewport.Scaling3DModeEnum.Fsr,
			"fsr2" => Viewport.Scaling3DModeEnum.Fsr2,
			"nearest" => Viewport.Scaling3DModeEnum.Nearest,
			_ => Viewport.Scaling3DModeEnum.Bilinear,
		};
		view.FsrSharpness = settings.FsrSharpness;
		view.TextureMipmapBias = settings.MipmapBias;
		view.ScreenSpaceAA = settings.ScreenSpaceAa switch
		{
			"fxaa" => Viewport.ScreenSpaceAAEnum.Fxaa,
			"smaa" => Viewport.ScreenSpaceAAEnum.Smaa,
			_ => Viewport.ScreenSpaceAAEnum.Disabled,
		};
		view.UseTaa = settings.Taa && view.Scaling3DMode != Viewport.Scaling3DModeEnum.Fsr2;
		view.AnisotropicFilteringLevel = settings.Anisotropic switch
		{
			>= 16 => Viewport.AnisotropicFiltering.Anisotropy16X,
			>= 8 => Viewport.AnisotropicFiltering.Anisotropy8X,
			>= 4 => Viewport.AnisotropicFiltering.Anisotropy4X,
			>= 2 => Viewport.AnisotropicFiltering.Anisotropy2X,
			_ => Viewport.AnisotropicFiltering.Disabled,
		};
		view.UseDebanding = settings.Debanding;

		ApplyHdr(root, settings);

		// Vsync is a display-server setting, not a per-window one, so this is the whole
		// application's.
		DisplayServer.VSyncMode wanted = !settings.Vsync ? DisplayServer.VSyncMode.Disabled
			: settings.VsyncMode switch
			{
				"on" => DisplayServer.VSyncMode.Enabled,
				"adaptive" => DisplayServer.VSyncMode.Adaptive,
				_ => DisplayServer.VSyncMode.Mailbox,
			};
		DisplayServer.WindowSetVsyncMode(wanted, (int)DisplayServer.MainWindowId);

		// A DRIVER WITHOUT MAILBOX IS HANDED PLAIN VSYNC BY GODOT ("The requested V-Sync mode Mailbox
		// is not available. Falling back to V-Sync mode Enabled" — this machine's NVIDIA/X11 stack,
		// 2026-09-22), and plain vsync holds a frame that misses one refresh until the next: at
		// 120 Hz a 9 ms frame is shown at 16.7. Mailbox was asked for precisely to avoid that, so
		// what stands in for it is ADAPTIVE — the same wait when the frame is on time, a tear
		// instead of a held frame when it is late. The mode Godot settled on is only known once
		// the swap chain has been rebuilt, a frame or more after the request.
		if (wanted == DisplayServer.VSyncMode.Mailbox)
		{
			GetTree().CreateTimer(1.0).Timeout += SettleVsync;
		}

		// Nothing to minimise: the launcher is a DIFFERENT PROCESS, and it stood itself down
		// when it spawned this one.

		// Godot renders this window; the guest never draws into it.
		_session = new GameSession { Name = "GameSession", StartGuest = StartGuest };
		AddChild(_session);

		// Beside the session, not inside it: the guest's audio worker stalls if nothing drains
		// its output, so this must be up for the whole life of the session.
		AddChild(new GameAudio { Name = "GameAudio" });

		// Alongside the session, not inside it: the UI stands up from the shipped assets and
		// needs no running guest, so a failed boot still shows a screen.
		_ui = new GameUi { Name = "GameUi" };
		AddChild(_ui);

		// The world, drawn through the guest's camera. Added before the UI so the UI and
		// the videos compose over it.
		AddChild(new GameWorld { Name = "GameWorld" });

		// Above the UI: the startup logos play over everything.
		_video = new GameVideo { Name = "GameVideo" };
		AddChild(_video);

		// Last, so it draws over the video too. Hidden until F5 or `perf_overlay = true`.
		AddChild(new GamePerfOverlay { Name = "GamePerfOverlay" });

		// F6: the dev menu — the full diagnostic view behind F5's spot check.
		AddChild(new GameDevMenu { Name = "GameDevMenu" });

		// Discord: "Playing Fable 2: Return to Albion" and the level the player is in.
		AddChild(new GamePresence { Name = "GamePresence" });

		// Video over UI: a movie covers the front end and hands it back when it ends.
		// Concealing is immediate; revealing waits for the guest to say it has a front end
		// (see _Process), not for a movie to end. Never for an embedded movie — an ability
		// preview plays inside the card, and concealing would hide the screen it belongs to.
		_video.MovieStarted += _ =>
		{
			_movieConcealPending = true;
			_movieConcealWaited = 0;
			ResolveMovieConceal();
		};

		// So the UI can draw an embedded movie into the element hosting it: the presentation
		// and its ordinal map live in GameUi, the decoded frame lives here.
		_ui.Video = _video;

		_session.Stopped += OnSessionStopped;

		// Closing ends the session rather than orphaning a running guest, and brings the
		// launcher back: this is a child window, so closing it is not quitting the app.
		// THE MAIN WINDOW'S CLOSE IS THE PROCESS'S CLOSE. Godot's root Window raises this, and
		// the default handling would quit anyway — it is taken here so the session is ended
		// deliberately rather than a running guest being torn down under it.
		GetTree().Root.CloseRequested += () =>
		{
			GD.Print("[window] close_requested from the window manager");
			GetTree().Quit();
		};
	}

	private bool _movieConcealPending;
	private int _movieConcealWaited;

	/// <summary>Frames a conceal decision waits for a live player's rect before taking the default.</summary>
	private const int MovieRectWaitFrames = 30;

	/// <summary>
	/// Conceals the UI under a movie the guest stated FULLSCREEN — decided once per movie, and
	/// only once the guest has stated the rect.
	/// </summary>
	/// <remarks>
	/// The guest states the rect in a second call after the open (`sub_82A41E30`), so it may
	/// not be known on the frame the movie starts; an ability preview states its own small
	/// frame and must not conceal. The video's EmbedOrdinal is checked as well as the slot map:
	/// `PlayEmbedded` sets it before the signal fires, where the slot registration once lost
	/// the race and a preview concealed the whole UI viewport for the rest of the session.
	/// Every successful open is followed by a statement; the wait is bounded for one that is
	/// not, and says so.
	/// </remarks>
	private void ResolveMovieConceal()
	{
		if (!_movieConcealPending || _ui is null || _video is null)
		{
			return;
		}

		if (!_video.IsPlaying || _video.EmbedOrdinal >= 0 || _ui.HasEmbeddedMovie)
		{
			_movieConcealPending = false;
			return;
		}

		bool? fullscreen = _video.StatedFullscreenIfKnown();
		if (fullscreen is null)
		{
			if (++_movieConcealWaited < MovieRectWaitFrames)
			{
				return;
			}

			GD.Print($"[game-video] the guest has a live movie player and stated no rect for it " +
				$"in {MovieRectWaitFrames} frames — treating the movie as fullscreen");
		}

		_movieConcealPending = false;
		if (fullscreen ?? true)
		{
			_ui.Conceal("a full-screen movie started over it");
		}
	}

	/// <summary>
	/// `hdr_output`: requests HDR on the main window, and keeps the post-FX ceiling on the
	/// window's headroom, which moves when the window changes screens or the OS setting changes.
	/// The luminances are only set when the player gave one; -1 leaves them to the OS.
	/// </summary>
	private static void ApplyHdr(Window root, GameWindowSettings settings)
	{
		const int Main = (int)DisplayServer.MainWindowId;
		Refii.Lighting.Engine_PostFxParams.OutputHeadroom = 1.0f;
		if (!settings.Hdr)
		{
			return;
		}

		if (!DisplayServer.HasFeature(DisplayServer.Feature.HdrOutput))
		{
			GD.Print("[game] hdr: this display server has no HDR output; drawing SDR");
			return;
		}

		if (settings.HdrReferenceNits > 0f)
		{
			DisplayServer.WindowSetHdrOutputReferenceLuminance(settings.HdrReferenceNits, Main);
		}

		if (settings.HdrPeakNits > 0f)
		{
			DisplayServer.WindowSetHdrOutputMaxLuminance(settings.HdrPeakNits, Main);
		}

		root.HdrOutputRequested = true;
		root.OutputMaxLinearValueChanged += _ => FollowHeadroom(root, settings);
		FollowHeadroom(root, settings);
	}

	private static void FollowHeadroom(Window root, GameWindowSettings settings)
	{
		const int Main = (int)DisplayServer.MainWindowId;
		float headroom = Mathf.Max(root.GetOutputMaxLinearValue(), 1.0f);
		Refii.Lighting.Engine_PostFxParams.OutputHeadroom = headroom;
		bool hdrActive = DisplayServer.WindowIsHdrOutputEnabled(Main) && headroom > 1.001f;
		// Closed-form dynamic mathematical model:
		// Places legacy 2008 emissive textures into the lower ~37.5% of the specular headroom
		// (e.g. 500 nits on a 1000-nit / 200-nit white display), leaving the remaining dynamic range for
		// specular glints, direct sun reflections, and bloom without clipping.
		float boost = hdrActive ? 1.0f + 0.375f * (headroom - 1.0f) : 1.0f;
		Refii.Lighting.Engine_PostFxParams.EmissionBoost = boost;

		try
		{
			RenderingServer.GlobalShaderParameterSet("fable_emission_boost", boost);
		}
		catch
		{
		}

		GD.Print(hdrActive
			? $"[game] hdr: on, white {DisplayServer.WindowGetHdrOutputCurrentReferenceLuminance(Main):0} nits, " +
				$"peak {DisplayServer.WindowGetHdrOutputCurrentMaxLuminance(Main):0} nits, headroom {headroom:0.00}x, emission boost {boost:0.00}x"
			: "[game] hdr: requested, but HDR output is not active (screen is SDR, or rendering driver does not support HDR output on this platform)");
	}

	/// <summary>
	/// A second after mailbox was asked for: where the driver fell back to plain vsync, asks for
	/// adaptive instead, and logs what the swap chain settled on.
	/// </summary>
	private void SettleVsync()
	{
		int window = (int)DisplayServer.MainWindowId;
		DisplayServer.VSyncMode settled = DisplayServer.WindowGetVsyncMode(window);
		if (settled == DisplayServer.VSyncMode.Mailbox)
		{
			return;
		}

		if (settled == DisplayServer.VSyncMode.Enabled && !_triedAdaptive)
		{
			_triedAdaptive = true;
			DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Adaptive, window);
			GetTree().CreateTimer(1.0).Timeout += SettleVsync;
			return;
		}

		GD.Print($"[game] vsync: mailbox is not available on this driver; using {settled.ToString().ToLowerInvariant()}" +
			(settled == DisplayServer.VSyncMode.Enabled ? " (adaptive is not available either)" : string.Empty));
	}

	private bool _triedAdaptive;

	public override void _Process(double delta)
	{
		using var _framePhase = Refii.Core.Engine_FramePhases.Measure("gamewindow");

		// The guest frame is sampled at frame start by Engine_FrameStartNode (int.MinValue);
		// on a new guest frame the entity walk runs on its worker while the UI and the world
		// process, and GameEntities takes it later in this same frame.
		if (Refii.Core.Engine_GuestFrame.Frame == 0u)
		{
			HostBridge.SampleGuestFrame();
		}
		if (Refii.Core.Engine_GuestFrame.Changed || GameEntities.FeedEveryFrame)
		{
			HostEntityWalk.Kick();
		}
		TraceFrontEnd();
		WarmItemIndex();
		StepShots();
		ResolveMovieConceal();

		if (_ui is null || _video is null || _video.IsPlaying)
		{
			return;
		}

		if (HostBridge.TryGetAbiVersion() is null)
		{
			return;
		}

		// The layer follows whether the guest has any live presentation (the HUD, pause menu
		// and menu cycler all stand up in game); which one is drawn is GameUi's business.
		// Reveal and conceal are two different conditions and must not be collapsed: reveal on
		// a non-zero front-end state, but the title sits at state 0 while perfectly visible,
		// so conceal only when the front-end GUI object is gone and no in-game presentation is
		// standing up.
		bool guestHasUi = HostBridge.FrontEndGuiIsUp || _ui.HasInGameUi;

		if (_ui.IsRevealed)
		{
			if (!guestHasUi)
			{
				GD.Print("[game-ui] the guest has no live UI; hiding the layer");
				_ui.Conceal("the guest has no live UI left");
			}

			return;
		}

		if (HostBridge.FrontEndState() != 0 || _ui.HasInGameUi)
		{
			_ui.Reveal();
		}
	}

	/// <summary>
	/// Writes periodic screenshots of the real window when <c>REFII_SHOT_DIR</c> asks for it.
	/// Interval is <c>REFII_SHOT_EVERY</c> in frames (default 600, ~10s at 60fps).
	/// </summary>
	private void StepShots()
	{
		if (_shotDirectory is null)
		{
			_shotDirectory = OS.GetEnvironment("REFII_SHOT_DIR");
			if (_shotDirectory.Length == 0)
			{
				return;
			}

			if (int.TryParse(OS.GetEnvironment("REFII_SHOT_EVERY"), out int every) && every > 0)
			{
				_shotEvery = every;
			}

			DirAccess.MakeDirRecursiveAbsolute(_shotDirectory);
			GD.Print($"[game-shot] writing a shot every {_shotEvery} frames to {_shotDirectory}");
		}

		if (_shotDirectory.Length == 0 || ++_shotFrames % _shotEvery != 0)
		{
			return;
		}

		// Never during a movie: the frame covers everything and says nothing about what Godot
		// rendered.
		if (_video is { IsPlaying: true })
		{
			// Try again next frame rather than waiting out another whole interval.
			--_shotFrames;
			return;
		}

		// The ROOT viewport's texture. This used to be a separate Window with a viewport of its
		// own; the game now occupies the main window, so the root is what the player sees.
		Image image = GetViewport().GetTexture().GetImage();
		if (GetWindow() is not { } win || !win.UseHdr2D || win.GetOutputMaxLinearValue() <= 1.001f)
		{
			if (image.GetFormat() is Image.Format.Rgbah or Image.Format.Rgbaf or Image.Format.Rgba8)
			{
				image.LinearToSrgb();
			}
		}

		string path = $"{_shotDirectory}/shot_{_shotFrames:D6}.png";
		GD.Print(image.SavePng(path) == Error.Ok
			? $"[game-shot] {path} (frontEnd={HostBridge.FrontEndGuiIsUp} inGame={HostBridge.IsInGame})"
			: $"[game-shot] could not write {path}");
	}

	private string? _shotDirectory;

	private int _shotEvery = 600;

	private int _shotFrames;

	/// <summary>
	/// Reports the guest crossing into the game, and the fence its level load waits on —
	/// CmdLevelChange_Update's gates are silent, so a stalled load looks like an idle guest.
	/// </summary>
	private void TraceLevelLoad()
	{
		if (HostBridge.TryGetAbiVersion() is null)
		{
			return;
		}

		bool inGame = HostBridge.IsInGame;
		if (inGame != _wasInGame)
		{
			_wasInGame = inGame;
			GD.Print($"[guest-game] byte_83496A5E={(inGame ? 1 : 0)} — the guest says it is " +
				$"{(inGame ? "IN GAME" : "out of the game")}");
		}

		// Not gated on the in-game flag: it is set at 0x82311648, after the level change is
		// queued at 0x823115D0, so gating on it would hide the fence during the wait.
		if (HostBridge.GetRenderFence() is not { } fence)
		{
			return;
		}

		// Every 60th tick: this is a poll, and the interesting thing is whether it MOVES.
		if (++_fenceTicks % 60 != 0)
		{
			return;
		}

		// Change-gated on a signature that quantises the values which legitimately jitter
		// (the fence's submitted counter), so only a real state change re-reports.
		string loadingKey = HostBridge.GetLoadingScreen() is { } ls
			? $"{ls.Manager:X8}/{ls.Screen:X8}/{ls.Fade:F1}/{ls.Requested}/{ls.Done}"
			: "none";
		string ringKey = string.Empty;
		bool ringBlocks = false;
		foreach (HostBridge.SceneStream s in HostBridge.GetSceneStreams())
		{
			ringKey += $"{s.WriteCursor:X}:{s.ReaderConsumed:X};";
			ringBlocks |= s.WouldBlock(4u);
		}
		HostBridge.InitMarkers markers = HostBridge.GetInitMarkers();

		// Sim time and submitted climb every second in steady gameplay: include them only while
		// out of game or while a level load or loading screen is active so steady gameplay does
		// not re-log every 60 frames.
		HostBridge.GameLoopPacer pacer = HostBridge.GetGameLoopPacer();
		bool steadyInGame = inGame && !fence.BlocksLevelLoad && !fence.Busy && loadingKey == "none";
		string fenceProgress = steadyInGame ? "steady" : $"{fence.Submitted:F1}";
		string simProgress = steadyInGame ? "steady" : $"{(int)pacer.SimTime}";
		// The scene-stream cursors move on every guest frame; in steady play that is traffic, not
		// a state change, and re-logged all four lines whenever a menu opened. A ring that would
		// block is still news, and REFII_UI_TRACE=1 restores the cursor-by-cursor trace.
		string ringProgress = steadyInGame && !ringBlocks && !Engine_UiTrace.Verbose ? "steady" : ringKey;
		string signature = $"{fence.Busy}/{fence.BlocksLevelLoad}/{fenceProgress}|{loadingKey}|{ringProgress}|" +
			$"{markers.GdbManager:X8}/{markers.InGame}/{markers.GameMode:X8}/{markers.FrontEndMode:X8}|" +
			$"{pacer.Known}/{simProgress}/{pacer.SimulationGate}/{pacer.WorldGate}";
		if (signature == _lastLoadTrace)
		{
			return;
		}
		_lastLoadTrace = signature;

		// +176 is a render deadline barrier (writers: the ctor and sub_82388B48); it standing
		// still while submitted climbs past it is the resting state, not a stall. The busy
		// byte carries no state of its own, so the holder is the part worth reading.
		GD.Print($"[guest-fence] mgr=0x{fence.Manager:X8} preloading={fence.Busy} " +
			$"holder={fence.Holder} " +
			$"(submitted={fence.Submitted:F2} deadline={fence.Deadline:F2}) " +
			$"{(fence.BlocksLevelLoad ? "BLOCKS the level load" : "clear")}");

		// The loading screen's take-down handshake: the game thread's one unbounded wait sits
		// on this, and only the render thread's fade tick releases it.
		if (HostBridge.GetLoadingScreen() is { } loading)
		{
			// `obj != 0` only means a loading screen is wanted; the art names which one.
			string art = HostBridge.GetLoadingScreenArt() is { } picked
				? $" type={picked.Type} art='{picked.Name}' body=0x{picked.Body:X8}"
				: " art=UNREADABLE";

			GD.Print($"[guest-loadscreen] mgr=0x{loading.Manager:X8} obj=0x{loading.Screen:X8} " +
				$"fade={loading.Fade} requested={loading.Requested} done={loading.Done}" +
				$"{art} worldReady={HostBridge.WorldIsReady()}");
		}

		// `WouldBlock` is sub_8222ABE0's own condition for the 4-byte reserve it makes twice
		// per message.
		foreach (HostBridge.SceneStream stream in HostBridge.GetSceneStreams())
		{
			GD.Print($"[guest-ring] obj=0x{stream.Object:X8} cap=0x{stream.Capacity:X} " +
				$"write=0x{stream.WriteCursor:X} consumed=0x{stream.WriteConsumed:X} " +
				$"reader=0x{stream.ReaderConsumed:X} lastRead=0x{stream.LastRead:X}" +
				$"{(stream.WouldBlock(4u) ? "  WOULD BLOCK" : string.Empty)}");
		}

		// Whether the guest is ticking at all. `sim` is `game+0x110`, rewritten every game-loop
		// iteration including the ones where no tick is due, so a sim time standing still is a
		// blocked tick thread, not a paced one. `inFlight` is presented minus completed, read
		// from the record the frame-sync index names; it is the term `sub_82B6FCE8` adds to the
		// render deadline, and while it grows the command-stream drain burns a full present
		// period per call holding the global GPU-access lock.
		if (pacer.Known)
		{
			GD.Print($"[guest-pacer] game=0x{pacer.Game:X8} sim={pacer.SimTime:F2}s " +
				$"gates sim={pacer.SimulationGate} world={pacer.WorldGate} quit={pacer.QuitByte} " +
				$"| presented={pacer.Presented} completed={pacer.Completed} " +
				$"inFlight={pacer.InFlight} slot={pacer.SyncSlot} lock={pacer.ReaderLock} " +
				$"| timebase epoch={pacer.TimebaseEpoch:F0} hz={pacer.TimebaseTicksPerSecond:F0} " +
				$"| gui players={pacer.GuiRegisteredPlayers} update={pacer.GuiUpdateEnabled} " +
				$"window={pacer.GuiDoStringWindow} hudT={pacer.HudPresentationTime:F0} " +
				$"screenT={pacer.ScreenPresentationTime:F0}");

			// No registered player means no Gameface update at all. Posted through the guest's
			// own routine (sub_82371860), once per sample while the count stays 0.
			//
			// IN GAME ONLY. `game+0x105` stays set through the game holder's shutdown, and the
			// GUI it counts players on is the one `dword_83496B08` names — which by then is the
			// front end's, freshly constructed with no players. Registering one there is not a
			// thing the guest ever does; `byte_83496A5E` is the guest's own statement that the
			// in-game GUI exists.
			if (pacer.WorldGate && HostBridge.IsInGame && pacer.GuiRegisteredPlayers == 0 &&
				pacer.GuiUpdateEnabled != 0)
			{
				GD.Print("[guest-pacer] the in-game GUI reports 0 registered players — asking the " +
					"guest to register the first one (sub_82371860)");
				HostBridge.PostFrontEndAction("RegisterPlayer");
			}
		}

		// How far into starting a game the guest has got: Game_InitDatabases says nothing
		// until its last statement.
		GD.Print($"[guest-init] gdbManager=0x{markers.GdbManager:X8} inGame={markers.InGame} " +
			$"gameMode=0x{markers.GameMode:X8} frontEndMode=0x{markers.FrontEndMode:X8}");
	}

	private bool _wasInGame;
	private int _fenceTicks;
	private string _lastLoadTrace = string.Empty;

	/// <summary>D3c: the database the item books' name-tag index was warmed for.</summary>
	private Refii.Gdb.GdbMulti? _itemIndexWarmedFor;
	private int _itemIndexTicks;

	/// <summary>
	/// D3c: builds the item books' name-tag index while a loading screen is up, so the first
	/// item book of a session does not pay for it on the frame it opens. Polled every 30th
	/// frame; it waits for the database and never builds it.
	/// </summary>
	private void WarmItemIndex()
	{
		if (++_itemIndexTicks % 30 != 0 || HostBridge.TryGetAbiVersion() is null ||
			HostBridge.GetLoadingScreen() is null ||
			Refii.Gdb.Engine_GameGdb.DatabaseIfBuilt is not { } multi ||
			ReferenceEquals(multi, _itemIndexWarmedFor))
		{
			return;
		}

		_itemIndexWarmedFor = multi;
		long started = System.Diagnostics.Stopwatch.GetTimestamp();
		int tags = Engine_UiItemPage.WarmNameTagIndex(multi);
		double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 /
			System.Diagnostics.Stopwatch.Frequency;
		GD.Print(tags < 0
			? "[item-page] name-tag index was already built (lazily, before a loading screen)"
			: $"[item-page] name-tag index built during the loading screen: {tags} tag(s) in {ms:0} ms");
	}

	private void TraceFrontEnd()
	{
		TraceLevelLoad();

		if (HostBridge.TryGetAbiVersion() is null ||
			!HostBridge.TryGetFrontEnd(out Engine_UiModel.FrontEndScreen screen,
				out uint state, out bool canPressA, out uint cursor,
				out int sceneSlide, out int slideCount))
		{
			return;
		}

		// Called every frame: a widget changing state does not show up in the screen-level
		// fields below, and FollowGuest is a no-op when the live slide table has not moved.
		_ui?.FollowGuest(screen, sceneSlide);

		var now = (screen, state, canPressA, cursor, sceneSlide);
		if (now == _lastFrontEnd)
		{
			return;
		}

		_lastFrontEnd = now;

		// dword_83496B08 is written by two constructions through the same base (0x8237132C):
		// the front-end GUI, and a base-only instance from Game_InitDatabases whose
		// +180/+184/+188 are not screen pointers. The vtable is what tells them apart.
		string where = string.Empty;
		if (screen == Engine_UiModel.FrontEndScreen.None &&
			HostBridge.GetFrontEndRaw() is { } words)
		{
			where = $" gui=0x{words[0]:X8} guiVtable=0x{words[9]:X8}" +
				$" active=0x{words[1]:X8} activeVtable=0x{words[8]:X8}";

			// Say which of the two it is, or every later reader has to know this trap.
			where += HostBridge.FrontEndGuiIsUp
				? " (front-end GUI, no screen active)"
				: " (NOT the front-end GUI — the game has taken it over)";
		}

		GD.Print($"[guest-ui] {screen} state={state} slide={sceneSlide}/{slideCount}" +
			(screen == Engine_UiModel.FrontEndScreen.Title ? $" canPressA={canPressA}" : $" cursor={cursor}") +
			where);

		ReportFrontEndInit();
	}

	/// <summary>
	/// Says which of the front end's failure modes is in play, on change: never activated,
	/// activated into a gated-off tick, or ticking with nothing to draw. `InitRan` and
	/// `TickGatePasses` separate them; see <see cref="HostBridge.FrontEndInit"/> for the bases.
	/// </summary>
	private void ReportFrontEndInit()
	{
		if (HostBridge.TryGetFrontEndInit() is not { } init)
		{
			return;
		}

		var now = (init.InitRan, init.TickGatePasses, init.Presentation != 0u, init.Stamped);
		if (now == _lastFrontEndInit)
		{
			return;
		}

		_lastFrontEndInit = now;
		GD.Print($"[guest-ui] front end: Init {(init.InitRan ? "RAN" : "NEVER RAN")}, " +
			$"tick gate {(init.TickGatePasses ? "passes" : "BLOCKS the whole front-end frame")} " +
			$"(logo {init.LogoReady}, button {init.ButtonReady}), " +
			$"presentation 0x{init.Presentation:X8}, " +
			$"input stamp {(init.Stamped ? "written" : "never — this is the CONSTRUCTED state")}");
	}

	private (bool, bool, bool, bool) _lastFrontEndInit = (true, true, true, true);

	private (Engine_UiModel.FrontEndScreen, uint, bool, uint, int) _lastFrontEnd;

	public override void _ExitTree()
	{
		// The orphan count names what the tree's teardown will NOT free: a node outside the
		// tree keeps its rendering-server instance, and Godot's own exit then destroys that
		// instance after the storage it depends on — the SIGSEGV in ~RendererSceneCull that
		// closed every session until 2026-10-11 (15 leaked instances in the core).
		GD.Print($"[window] leaving the tree after {Engine.GetProcessFrames()} frames " +
			$"(inTree={IsInsideTree()}); nodes " +
			$"{Performance.GetMonitor(Performance.Monitor.ObjectNodeCount):0}, orphan nodes " +
			$"{Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount):0}");

		if (_current == this)
		{
			_current = null;
		}
	}

	/// <summary>
	/// Drops the launcher out of the way for the duration of the game; it keeps running (it
	/// owns the process and the host bridge). Its mode is remembered for the restore.
	/// </summary>
	private void MinimiseLauncher()
	{
		_launcherMode = DisplayServer.WindowGetMode((int)DisplayServer.MainWindowId);

		// Never remember "minimised" as the state to return to, or closing the game leaves
		// the user with no window at all and no obvious way back.
		if (_launcherMode == DisplayServer.WindowMode.Minimized)
		{
			_launcherMode = DisplayServer.WindowMode.Windowed;
		}

		DisplayServer.WindowSetMode(
			DisplayServer.WindowMode.Minimized, (int)DisplayServer.MainWindowId);
	}

	private void RestoreLauncher()
	{
		if (_launcherRestored)
		{
			return;
		}

		_launcherRestored = true;
		DisplayServer.WindowSetMode(_launcherMode, (int)DisplayServer.MainWindowId);
		DisplayServer.WindowMoveToForeground((int)DisplayServer.MainWindowId);
	}

	private void OnSessionStopped(string reason)
	{
		GD.Print($"[game] session stopped: {reason}");

		// Leave the window up on failure so the reason is readable; the user closes it.
		DisplayServer.WindowSetTitle(
			$"Fable 2 — stopped ({reason})", (int)DisplayServer.MainWindowId);
	}

	/// <summary>
	/// Opens a game window as a child of <paramref name="parent"/>. One at a time: the guest
	/// runtime holds process-global state (memory map, kernel, MMIO handler).
	/// </summary>
	public static GameWindow? Open(Node parent, bool startGuest = true)
	{
		// A field rather than a scan of the root's children: the add below is deferred, so a
		// window asked for but not yet attached is invisible to a scan.
		if (_current is not null && IsInstanceValid(_current))
		{
			return null;
		}

		Viewport root = parent.GetTree().Root;
		var window = new GameWindow { Name = "GameWindow", StartGuest = startGuest };
		_current = window;

		// Deferred: add_child fails while the tree is busy setting up children (the state
		// during a _Ready), and it fails by printing rather than throwing.
		root.CallDeferred(Node.MethodName.AddChild, window);
		return window;
	}
}
