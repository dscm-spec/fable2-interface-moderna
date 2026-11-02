using Refii.Ui;
using Godot;
using Refii.Core;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// Feeds Godot's controller state to the guest, once per frame: reads the real device and
/// publishes a snapshot the guest's XInput driver serves from. Translation into the Xbox's
/// vocabulary happens once, here.
/// </summary>
public partial class GameInput : Node
{
	/// <summary>Xbox 360 supports four pads, and so does the guest.</summary>
	private const int MaxControllers = 4;

	/// <summary>
	/// XInput button bits. These are the guest's values, not Godot's — see
	/// <c>X_INPUT_GAMEPAD_BUTTON</c>.
	/// </summary>
	private const uint DpadUp = 0x0001;
	private const uint DpadDown = 0x0002;
	private const uint DpadLeft = 0x0004;
	private const uint DpadRight = 0x0008;
	private const uint Start = 0x0010;
	private const uint Back = 0x0020;
	private const uint LeftThumb = 0x0040;
	private const uint RightThumb = 0x0080;
	private const uint LeftShoulder = 0x0100;
	private const uint RightShoulder = 0x0200;
	private const uint ButtonA = 0x1000;
	private const uint ButtonB = 0x2000;
	private const uint ButtonX = 0x4000;
	private const uint ButtonY = 0x8000;

	/// <summary>Whether A is down on any source this frame, as published to the guest.</summary>
	public static bool AHeld { get; private set; }

	public static bool YHeld { get; private set; }

	public override void _Process(double delta)
	{
		using var _framePhase = Refii.Core.Engine_FramePhases.Measure("gameinput");
		// The guest can end while this node is up; nothing is published into a torn-down runtime.
		if (HostBridge.TryGetAbiVersion() is null)
		{
			// A joypad left buzzing by a guest that is no longer there would buzz until unplugged.
			SilenceRumble();
			return;
		}

		// INPUT REACHES THE GUEST ONLY WHILE THE GAME WINDOW HAS FOCUS.
		//
		// Godot polls joypads from the DEVICE, not from window events — so an unfocused game
		// still saw every stick and button, and a controller left alone on the desk drove the
		// hero while its owner was in another application. The keyboard does not have this
		// problem (no focus, no key events), which is why it only ever showed up as the pad.
		//
		// Unfocused publishes a NEUTRAL pad rather than nothing. Skipping the publish would
		// leave the guest holding whatever was last sent — a stick mid-push stays pushed, a
		// held button stays down — and the guest edge-detects, so it would never see the
		// release. Same rule as the menu stick: every press must get its release.
		//
		// The DEVICES are what focus gates — the keyboard and the joypads. The scripted
		// sources (the debug channel's presses, sticks and replay tapes, the auto driver) are
		// not devices and reach the guest focused or not: an unattended run is driven by them
		// with nobody at the keyboard and a window that may never take focus, and a
		// controller playing another game must not steer this one meanwhile.
		//
		// One gamepad per guest pad. The guest polls all four (`InputDeviceMgr_EnumeratePads`
		// 0x821A8CC8 loops XamInputGetCapabilities 0..3) and tags every event with its pad
		// (`ev+40`). A START on a pad that is not player one's is the local drop-in
		// (`sub_82402F70` 0x824031ec → `Coop_SetSharedScreenMode(game+140, 1, pad)`), and player
		// two's handler takes only `ev.pad == Player+40` (`sub_829C1C08` 0x829c1c88).
		// Each step under its own phase, so a slow frame here names the step on the debug channel.
		using (Refii.Core.Engine_FramePhases.Measure("input.settings"))
		{
			ReadControlSettings(delta);
			AdvancePointerButtons(delta);
		}

		bool devices = HasInputFocus();
		using (Refii.Core.Engine_FramePhases.Measure("input.kbm"))
		{
			UpdateKeyboardMouse(devices);
		}

		using (Refii.Core.Engine_FramePhases.Measure("input.pads"))
		{
			AssignPads(devices);
			for (int pad = 0; pad < MaxControllers; ++pad)
			{
				PublishPad(pad, devices);
			}
		}

		using (Refii.Core.Engine_FramePhases.Measure("input.rumble"))
		{
			DriveRumble(devices);
		}

		// The mods' own actions, on the keyboard and on the pads as just assigned, under the
		// same focus gate: edges only, posted to the mod event bus.
		using (Refii.Core.Engine_FramePhases.Measure("input.mods"))
		{
			Refii.Mods.ModInput.Pump(devices);
		}
	}

	public override void _ExitTree() => SilenceRumble();

	/// <summary>Each guest pad's rumble as last applied to its joypad.</summary>
	private static readonly Engine_Rumble[] _rumble =
	{
		new Engine_Rumble(), new Engine_Rumble(), new Engine_Rumble(), new Engine_Rumble(),
	};

	/// <summary>The joypad each pad's running effect was started on, or -1.</summary>
	private static readonly int[] _rumbleJoypad = { -1, -1, -1, -1 };

	/// <summary>
	/// RUMBLE, THE OTHER WAY DOWN THE SAME SEAM. The guest states each pad's motors through
	/// <c>XamInputSetState</c> every frame the pad is connected (<c>sub_822B38C0</c>: the effect
	/// mixer at <c>pad+72</c> sums into <c>pad+84/+88</c>, ×65535 into the <c>XINPUT_VIBRATION</c>
	/// at <c>pad+92</c>, then <c>XInputSetState(pad+4, …)</c>). The kernel routes user N to our
	/// driver's slot N, <see cref="HostBridge.TryGetVibration"/> reads it, and the joypad holding
	/// guest pad N is the one that buzzes — so on split screen each player's hits reach their own
	/// pad, since the guest picks the pad object off the hit entity's player (<c>sub_823EAA58</c>,
	/// <c>*(player+40)</c>).
	/// </summary>
	/// <remarks>
	/// <para>Change-driven through <see cref="Engine_Rumble"/>: Godot restarts the effect on every
	/// <c>StartJoyVibration</c>, so a steady pair is told once and silence stops it once, and the
	/// pair is compared at the pad's own byte resolution so a decay restarts the effect only when
	/// the motor would move. The effect runs until stopped (duration 0); the guest's own decay is
	/// what ends it, frame by frame, the way it ended on the console.</para>
	/// <para>None of this blocks: <c>Input::start_joy_vibration</c> takes the Input mutex and
	/// writes a struct (core/input/input.cpp), and the device write — <c>SDL_RumbleJoystick</c>
	/// in <c>JoypadSDL::process_events</c> — runs in <c>OS_LinuxBSD::run</c> before
	/// <c>Main::iteration</c>, outside every node's <c>_Process</c>.</para>
	/// <para>Under the focus rule: an unfocused window publishes a neutral pad and must not keep a
	/// controller buzzing on the desk either, so <paramref name="devices"/> false silences every
	/// pad, and the guest's pair is picked up again on the first focused frame.</para>
	/// <para>Two switches, both required: the guest's own (<c>byte_8331924F</c>, the Controls
	/// fold's Controller Vibration page — zero and no effect is ever stated) and the launcher's
	/// <see cref="ControllerVibration"/>, which silences the joypad whatever is stated.</para>
	/// </remarks>
	private static void DriveRumble(bool devices)
	{
		for (int pad = 0; pad < MaxControllers; ++pad)
		{
			int joypad = _padDevice[pad];
			uint left = 0;
			uint right = 0;
			bool stated = devices && ControllerVibration && joypad >= 0 &&
				HostBridge.TryGetVibration(pad, out left, out right);
			Engine_Rumble.Verdict verdict = stated ? _rumble[pad].Observe(left, right) : _rumble[pad].Silence();
			switch (verdict)
			{
				case Engine_Rumble.Verdict.Start:
					// The pad may have moved joypads while running (a reconnect takes a new id).
					if (_rumbleJoypad[pad] >= 0 && _rumbleJoypad[pad] != joypad)
					{
						Input.StopJoyVibration(_rumbleJoypad[pad]);
					}

					_rumbleJoypad[pad] = joypad;
					Input.StartJoyVibration(joypad, _rumble[pad].Weak, _rumble[pad].Strong, 0f);
					break;
				case Engine_Rumble.Verdict.Stop:
					if (_rumbleJoypad[pad] >= 0)
					{
						Input.StopJoyVibration(_rumbleJoypad[pad]);
					}

					_rumbleJoypad[pad] = -1;
					break;
			}
		}
	}

	/// <summary>Stops every running effect: the guest is down or this node is leaving.</summary>
	private static void SilenceRumble()
	{
		for (int pad = 0; pad < MaxControllers; ++pad)
		{
			if (_rumble[pad].Silence() == Engine_Rumble.Verdict.Stop && _rumbleJoypad[pad] >= 0)
			{
				Input.StopJoyVibration(_rumbleJoypad[pad]);
			}

			_rumbleJoypad[pad] = -1;
		}
	}

	/// <summary>
	/// One pad's rumble for the debug channel: the joypad holding it, the pair the guest states
	/// now, and what the joypad was last told.
	/// </summary>
	public static (int Joypad, uint StatedLeft, uint StatedRight, bool Running, float Weak, float Strong)
		RumbleReadout(int pad)
	{
		if (pad < 0 || pad >= MaxControllers)
		{
			return (-1, 0, 0, false, 0f, 0f);
		}

		uint left = 0;
		uint right = 0;
		if (!HostBridge.TryGetVibration(pad, out left, out right))
		{
			left = 0;
			right = 0;
		}

		Engine_Rumble rumble = _rumble[pad];
		return (_padDevice[pad], left, right, rumble.Running, rumble.Weak, rumble.Strong);
	}

	/// <summary>The joypad holding guest pad <paramref name="pad"/> (0..3), or -1 while it is free.</summary>
	public static int JoypadForPad(int pad) =>
		pad >= 0 && pad < MaxControllers ? _padDevice[pad] : -1;

	/// <summary>Mouse events go to the UI pointer, which resolves them against the UI camera.</summary>
	public override void _Input(InputEvent @event)
	{
		if (@event is InputEventMouse)
		{
			if (!MouseInput)
			{
				return;
			}

			Engine_UiPointer.Feed(@event);
			FeedMouseMotion(@event);
			DetectDevice(@event);
			return;
		}

		Engine_UiPointer.Feed(@event);
		FeedMouseMotion(@event);
		FeedMouseWheel(@event);
		DetectDevice(@event);
	}

	/// <summary>
	/// THE DEVICE SWITCH. Whatever was touched last is the device: a key, a mouse button or a
	/// real mouse movement makes it the keyboard and mouse; a pad button or a stick or trigger
	/// pushed past a deliberate threshold makes it the controller, at once. Joypads Godot has no
	/// mapping for are ignored — a keyboard's or mouse's own HID interface can enumerate as one.
	/// </summary>
	private static void DetectDevice(InputEvent @event)
	{
		switch (@event)
		{
			case InputEventKey { Pressed: true }:
				Engine_UiInputMode.KeyboardMouse = true;
				break;
			case InputEventMouseButton { Pressed: true } when MouseInput:
			case InputEventMouseMotion motion when MouseInput && motion.Relative.LengthSquared() > 4f:
				Engine_UiInputMode.KeyboardMouse = true;
				break;
			case InputEventJoypadButton { Pressed: true } button when Input.IsJoyKnown(button.Device):
				Engine_UiInputMode.KeyboardMouse = false;
				break;
			case InputEventJoypadMotion axis when Input.IsJoyKnown(axis.Device) &&
				Mathf.Abs(axis.AxisValue) > 0.5f:
				Engine_UiInputMode.KeyboardMouse = false;
				break;
		}
	}

	/// <summary>
	/// How long a pointer-made press is held. The guest reads the pad on its own 30 Hz step and
	/// edge-detects, so a one-frame pulse at a high display rate can fall between two reads:
	/// three guest steps is a press it cannot miss.
	/// </summary>
	private const double PointerHold = 0.1;

	private static double _pointerB;

	/// <summary>Any other pad buttons a pointer target presses (a tab arrow is a shoulder), and how
	/// long they stay held.</summary>
	private static uint _pointerExtra;
	private static double _pointerExtraFor;

	/// <summary>Presses pad buttons for a clicked target — held long enough for the guest's read.</summary>
	public static void QueuePointerPress(uint buttons)
	{
		_pointerExtra |= buttons;
		_pointerExtraFor = PointerHold;
	}

	/// <summary>The XInput bits a pointer target can press, for callers outside this class.</summary>
	public const uint PadLeftShoulder = LeftShoulder;
	public const uint PadRightShoulder = RightShoulder;
	public const uint PadA = ButtonA;
	public const uint PadB = ButtonB;
	public const uint PadX = ButtonX;
	public const uint PadY = ButtonY;
	public const uint PadLeft = DpadLeft;
	public const uint PadRight = DpadRight;
	public const uint PadUp = DpadUp;
	public const uint PadDown = DpadDown;

	/// <summary>Wheel notches still to be played out as D-pad steps, signed (negative up).</summary>
	private static int _wheelPending;

	/// <summary>
	/// Steps a list's scroll bar asked of a cursor that is the guest's, played out exactly as the
	/// wheel's notches are but uncapped: a bar names how far the cursor must go.
	/// </summary>
	private static int _stepsPending;
	private static double _wheelClock;
	private static uint _wheelBit;

	/// <summary>The pad bits the pointer holds this frame.</summary>
	private static uint _pointerBits;

	/// <summary>
	/// The UI's leftovers: a right click and wheel notches, played out as B and D-pad steps so a
	/// screen the guest drives itself answers them like the pad. A stray left click is not one.
	/// </summary>
	public static void QueuePointerButtons(bool right, int wheel)
	{
		if (right)
		{
			_pointerB = PointerHold;
		}

		_wheelPending = System.Math.Clamp(_wheelPending + wheel, -8, 8);
	}

	/// <summary>Queues signed highlight steps (negative up) to be played out as D-pad presses.</summary>
	public static void QueuePointerSteps(int steps) => _stepsPending += steps;

	/// <summary>Drops the scroll bar's steps not yet played out: a new grab counts from scratch.</summary>
	public static void ClearPointerSteps() => _stepsPending = 0;

	private static void AdvancePointerButtons(double delta)
	{
		_pointerB = System.Math.Max(0.0, _pointerB - delta);

		// One notch = a held step, then a released gap of the same length: every press needs its
		// release, or the guest sees one long hold.
		_wheelClock -= delta;
		if (_wheelClock <= 0.0)
		{
			if (_wheelBit != 0u)
			{
				_wheelBit = 0u;
				_wheelClock = PointerHold;
			}
			else if (_wheelPending != 0)
			{
				_wheelBit = _wheelPending < 0 ? DpadUp : DpadDown;
				_wheelPending -= System.Math.Sign(_wheelPending);
				_wheelClock = PointerHold;
			}
			else if (_stepsPending != 0)
			{
				_wheelBit = _stepsPending < 0 ? DpadUp : DpadDown;
				_stepsPending -= System.Math.Sign(_stepsPending);
				_wheelClock = PointerHold;
			}
		}

		_pointerExtraFor = System.Math.Max(0.0, _pointerExtraFor - delta);
		if (_pointerExtraFor <= 0.0)
		{
			_pointerExtra = 0u;
		}

		_pointerBits = (_pointerB > 0.0 ? ButtonB : 0u) |
			_wheelBit | _pointerExtra;
	}

	/// <summary>`camera_invert_x` in refii.toml — the horizontal invert the guest lacks.</summary>
	public static bool InvertCameraX { get; private set; }

	/// <summary>`mouse_sensitivity` in refii.toml, 1.0 by default.</summary>
	public static float MouseSensitivity { get; private set; } = 1f;

	/// <summary>`mouse_input` in refii.toml — whether mouse input is processed at all.</summary>
	public static bool MouseInput { get; private set; } = true;

	/// <summary>`mouse_lock` in refii.toml — whether the mouse cursor is captured during play.</summary>
	public static bool MouseLock { get; private set; } = true;

	/// <summary>`controller_vibration` in refii.toml, on by default — the host's rumble gate.</summary>
	public static bool ControllerVibration { get; private set; } = true;

	private static double _settingsClock;
	private static System.Collections.Generic.IReadOnlyDictionary<string, string>? _lastControlValues;

	/// <summary>
	/// The player's control settings, re-checked periodically: the launcher writes refii.toml
	/// while the game runs, and the reader caches by write time, so key bindings are only
	/// re-parsed when the returned dictionary instance actually changes.
	/// </summary>
	private static void ReadControlSettings(double delta)
	{
		_settingsClock -= delta;
		if (_settingsClock > 0.0)
		{
			return;
		}

		_settingsClock = 2.0;
		var values = Refii.Core.Engine_RefiiConfig.Read(out _);
		if (ReferenceEquals(values, _lastControlValues))
		{
			return;
		}
		bool prevMouseInput = MouseInput;
		_lastControlValues = values;
		InvertCameraX = Refii.Core.Engine_RefiiConfig.Bool(values, "camera_invert_x", false);
		MouseInput = Refii.Core.Engine_RefiiConfig.Bool(values, "mouse_input", true);
		MouseLock = Refii.Core.Engine_RefiiConfig.Bool(values, "mouse_lock", true);
		if (prevMouseInput && !MouseInput)
		{
			Engine_UiInputMode.KeyboardMouse = false;
		}
		ControllerVibration = Refii.Core.Engine_RefiiConfig.Bool(values, "controller_vibration", true);
		ReadKeyBindings(values);
		// The mods' actions take their rebinds (key_<mod>.<action>, pad_<mod>.<action>) from the same read.
		Refii.Mods.ModInput.ReadBindings(values);
		MouseSensitivity = Mathf.Clamp(
			Refii.Core.Engine_RefiiConfig.Float(values, "mouse_sensitivity", 1f), 0.05f, 10f);
	}

	/// <summary>
	/// The Godot joypad holding each guest pad, −1 free. A gamepad takes the lowest free pad
	/// on its first press and keeps it until it disconnects, so whoever presses A on the title
	/// is pad 0 — the pad the front end latches (dword_83496AF8, 0x826E30DC) and binds to
	/// player one. Pad 0 also carries the keyboard, the debug channel and every joypad Godot
	/// has no gamepad mapping for (a keyboard's or mouse's HID interface can enumerate as one),
	/// so an unmapped device never claims a pad.
	/// </summary>
	private static readonly int[] _padDevice = { -1, -1, -1, -1 };

	private static (int Id, bool Known)[] _connectedJoypads = System.Array.Empty<(int, bool)>();
	private static bool _joypadsHooked;

	private static void EnsureJoypadsHooked()
	{
		if (_joypadsHooked)
		{
			return;
		}

		_joypadsHooked = true;
		Input.JoyConnectionChanged += OnJoyConnectionChanged;
		RefreshConnectedJoypads();
	}

	private static void OnJoyConnectionChanged(long device, bool connected) =>
		RefreshConnectedJoypads();

	private static void RefreshConnectedJoypads()
	{
		Godot.Collections.Array<int> connected = Input.GetConnectedJoypads();
		try
		{
			if (connected.Count == 0)
			{
				_connectedJoypads = System.Array.Empty<(int, bool)>();
				return;
			}

			var list = new (int Id, bool Known)[connected.Count];
			for (int i = 0; i < connected.Count; ++i)
			{
				int id = connected[i];
				list[i] = (id, Input.IsJoyKnown(id));
			}

			_connectedJoypads = list;
		}
		finally
		{
			((Godot.Collections.Array)connected).Dispose();
		}
	}

	private static bool IsJoypadConnected(int id)
	{
		foreach ((int connectedId, _) in _connectedJoypads)
		{
			if (connectedId == id)
			{
				return true;
			}
		}

		return false;
	}

	private static void AssignPads(bool devices)
	{
		EnsureJoypadsHooked();
		for (int pad = 0; pad < MaxControllers; ++pad)
		{
			if (_padDevice[pad] >= 0 && !IsJoypadConnected(_padDevice[pad]))
			{
				GD.Print($"[input] joypad {_padDevice[pad]} disconnected — guest pad {pad} is free");
				_padDevice[pad] = -1;
			}
		}

		if (!devices)
		{
			return;
		}

		foreach ((int joypad, bool known) in _connectedJoypads)
		{
			if (!known || System.Array.IndexOf(_padDevice, joypad) >= 0 ||
				JoypadButtons(joypad) == 0)
			{
				continue;
			}

			int free = System.Array.IndexOf(_padDevice, -1);
			if (free < 0)
			{
				return;
			}

			_padDevice[free] = joypad;
			GD.Print($"[input] joypad {joypad} '{Input.GetJoyName(joypad)}' is guest pad {free}");
		}
	}

	/// <summary>One pad's raw state, before the menu masks.</summary>
	private struct PadState
	{
		public uint Buttons, LeftTrigger, RightTrigger;
		public int LeftX, LeftY, RightX, RightY;

		public void Merge(int joypad)
		{
			Buttons |= JoypadButtons(joypad);
			LeftTrigger = System.Math.Max(LeftTrigger, Trigger(joypad, JoyAxis.TriggerLeft));
			RightTrigger = System.Math.Max(RightTrigger, Trigger(joypad, JoyAxis.TriggerRight));
			LeftX = Dominant(LeftX, Axis(joypad, JoyAxis.LeftX));
			// Negated: Godot's stick Y is screen-space (down positive), the Xbox's is up-positive.
			LeftY = Dominant(LeftY, -Axis(joypad, JoyAxis.LeftY));
			RightX = Dominant(RightX, Axis(joypad, JoyAxis.RightX));
			RightY = Dominant(RightY, -Axis(joypad, JoyAxis.RightY));
		}
	}

	/// <summary>
	/// Pads 1-3: the gamepad holding the pad and the debug channel's presses aimed at it.
	/// Connected while either holds it — a debug press connects the pad for the rest of the
	/// run, since a pad that vanished between presses would read to the guest as unplugged.
	/// The menu masks apply as on pad 0: an owned menu's vocabulary must not reach the guest
	/// from any pad.
	/// </summary>
	private static void PublishPad(int pad, bool devices)
	{
		if (pad == 0)
		{
			Publish0(devices);
			return;
		}

		var state = new PadState { Buttons = GameDebugServer.Buttons(pad) };
		if (GameDebugServer.Sticks(pad, out int debugLX, out int debugLY, out int debugRX,
				out int debugRY, out uint debugLT, out uint debugRT))
		{
			state.LeftX = debugLX;
			state.LeftY = debugLY;
			state.RightX = debugRX;
			state.RightY = debugRY;
			state.LeftTrigger = debugLT;
			state.RightTrigger = debugRT;
		}

		int joypad = _padDevice[pad];
		if (devices && joypad >= 0)
		{
			state.Merge(joypad);
		}

		bool connected = joypad >= 0 || GameDebugServer.PadConnected(pad);
		SetMasked(pad, connected, state);

		// Local player two's own expression wheel, off their own pad.
		if (pad == PlayerTwoPad)
		{
			PublishWheelTwo(state.Buttons, state.LeftX, state.LeftY);
		}

		// Player two's pad drives the host-owned menu the guest says player two opened.
		if (pad == HostMenuPad)
		{
			PublishMenu(pad, state.Buttons, state.LeftX, state.LeftY, state.RightX, state.RightY);
		}
	}

	/// <summary>
	/// The player whose menu is open, as the guest states it: 0 not stated, 1 player one, 2
	/// player two. Set by <c>GameUi</c> every frame, and only ever non-zero on split screen.
	/// </summary>
	/// <remarks>
	/// <para>Not stated is the shared-screen rule, unchanged: the host-owned menus read pad 0 and
	/// the menu masks hold every pad. Stated, the menu belongs to ONE player — the guest's pause is
	/// owned by the player who opened it, and player two's START opens their own reduced root —
	/// so that player's pad drives the host menu and only that pad is masked; the other player's
	/// pad reaches the guest as it would on the console, where nothing masked it.</para>
	/// <para>The hold levers stay on pad 0 whoever owns the menu — the quick wheel (Back), the
	/// spells menu (RT), <see cref="AHeld"/>/<see cref="YHeld"/> and <see cref="LeftStick"/>: the
	/// pump's <c>QuickWheel:</c>, <c>SpellsMenu:</c> and <c>SpellBlade:</c> levers act for the
	/// local player, who is player one in local co-op, and the TopBox's hold prompt is player
	/// one's HUD.</para>
	/// </remarks>
	public static int MenuOwner { get; set; }

	/// <summary>Local player two's guest pad (1..3) while local co-op runs, else −1. Set by <c>GameUi</c>.</summary>
	public static int PlayerTwoPad { get; set; } = -1;

	/// <summary>The pad of the player <see cref="MenuOwner"/> names, or −1 while none is stated.</summary>
	private static int MenuPad => MenuOwner switch
	{
		1 => 0,
		2 when PlayerTwoPad is >= 1 and < MaxControllers => PlayerTwoPad,
		_ => -1,
	};

	/// <summary>The pad the host-owned menus read: the owner's, or pad 0 while no owner is stated.</summary>
	private static int HostMenuPad => MenuPad < 0 ? 0 : MenuPad;

	/// <summary>Whether the menu masks hold <paramref name="pad"/>: every pad until an owner is
	/// stated, then the owner's alone.</summary>
	private static bool MasksPad(int pad) => MenuPad < 0 || pad == MenuPad;

	/// <summary>
	/// Whether the DEVICES reach the guest this frame. No exemption for unattended runs: they
	/// are driven by the scripted sources, which <see cref="Publish0"/> publishes regardless.
	/// </summary>
	private bool HasInputFocus()
	{
		// `GetWindow()` is the nearest Window ancestor — the game's own window, not the
		// launcher's, so a focused launcher does not count as a focused game.
		Window? window = IsInsideTree() ? GetWindow() : null;
		return window is null || window.HasFocus();
	}

	/// <summary>Said once per focus change, not once per frame.</summary>
	private static bool _reportedUnfocused;

	/// <summary>
	/// Pad 0: the keyboard, the gamepad holding pad 0 and every unmapped joypad, merged, plus
	/// the scripted sources. Buttons are OR'd; triggers and sticks take whichever device is
	/// pushing hardest, so a pad at rest never cancels one in use. With
	/// <paramref name="devices"/> false the keyboard and joypads are muted (the window is
	/// unfocused) and the pad is still published every frame, so anything a device held is
	/// released exactly once and stays released — the guest edge-detects, and a publish skipped
	/// would leave a stick mid-push forever.
	///
	/// The host-owned menus read pad 0 unless the guest states that player two owns the open
	/// menu — see <see cref="MenuOwner"/>.
	/// </summary>
	private static void Publish0(bool devices)
	{
		if (devices == _reportedUnfocused)
		{
			_reportedUnfocused = !devices;
			GD.Print(devices
				? "[input] the game window has focus — the devices reach the guest again"
				: "[input] the game window lost focus — the devices are muted; only the debug channel and the auto driver reach the guest");
		}

		// NO AUTO-DRIVER. The debug channel (the MCP) presses the pad when a run has to be
		// driven; the title-to-world and menu-walking scripts that lived here are gone.
		var state = new PadState
		{
			Buttons = (devices ? _pointerBits : 0u) | GameDebugServer.Buttons(0),
		};

		if (devices)
		{
			KeyboardMouseState(ref state);
		}

		if (devices)
		{
			foreach ((int joypad, bool known) in _connectedJoypads)
			{
				if (joypad == _padDevice[0] || !known)
				{
					state.Merge(joypad);
				}
			}
		}

		// The debug channel's held sticks and triggers merge like one more device.
		if (GameDebugServer.Sticks(0, out int debugLX, out int debugLY, out int debugRX,
				out int debugRY, out uint debugLT, out uint debugRT))
		{
			state.LeftX = Dominant(state.LeftX, debugLX);
			state.LeftY = Dominant(state.LeftY, debugLY);
			state.RightX = Dominant(state.RightX, debugRX);
			state.RightY = Dominant(state.RightY, debugRY);
			state.LeftTrigger = System.Math.Max(state.LeftTrigger, debugLT);
			state.RightTrigger = System.Math.Max(state.RightTrigger, debugRT);
		}

		// A replay tape overrides the devices wholesale; the state masks below still apply,
		// because they are the game's, not the recording's.
		if (GameDebugServer.ReplayFrame(out GameDebugServer.PadFrame replay))
		{
			state.Buttons |= replay.Buttons;
			state.LeftTrigger = replay.Lt;
			state.RightTrigger = replay.Rt;
			state.LeftX = replay.Lx;
			state.LeftY = replay.Ly;
			state.RightX = replay.Rx;
			state.RightY = replay.Ry;
		}

		uint buttons = state.Buttons;
		int leftX = state.LeftX, leftY = state.LeftY, rightX = state.RightX, rightY = state.RightY;
		GameDebugServer.RecordInputFrame(
			buttons, state.LeftTrigger, state.RightTrigger, leftX, leftY, rightX, rightY);

		// The mask as the guest receives it, for host widgets that run their own hold timers
		// (the TopBox's hold-to-accept) — every source folded, focus gate included.
		AHeld = (buttons & ButtonA) != 0;
		YHeld = (buttons & ButtonY) != 0;

		LeftStick = new Vector2(leftX / 32767f, leftY / 32767f);

		// Logged on the threshold crossing only (30/255, the XInput convention).
		uint triggersHeld =
			(state.LeftTrigger > 30 ? 1u : 0u) | (state.RightTrigger > 30 ? 2u : 0u);
		if (triggersHeld != _reportedTriggers)
		{
			_reportedTriggers = triggersHeld;
			GD.Print($"[input] triggers LT={state.LeftTrigger} RT={state.RightTrigger}");
		}

		// THE SPELLS MENU'S HOLD — GameAction 42 on press, 43 on release, which the shipped
		// scheme binds to XboxButton 16: the RIGHT TRIGGER. Same 30/255 threshold as above.
		bool spells = state.RightTrigger > 30;
		SpellsMenuPressed = spells && !_spellsWasDown;
		SpellsMenuReleased = !spells && _spellsWasDown;
		SpellsMenuHeld = spells;
		_spellsWasDown = spells;

		SetMasked(0, connected: true, state);

		PublishHolds(buttons);
		if (HostMenuPad == 0)
		{
			PublishMenu(0, buttons, leftX, leftY, rightX, rightY);
		}
	}

	/// <summary>
	/// Publishes one pad with the owned menus' masks applied. Two consumers: the guest gets a
	/// filtered set, the host-owned menu gets the real one. The guest still runs its own
	/// front-end menu — see <see cref="WithoutMenuInput"/>.
	/// </summary>
	/// <summary>Per pad: the buttons the guest was denied last frame.</summary>
	private static readonly uint[] _maskBits = new uint[MaxControllers];

	/// <summary>Per pad: buttons still held since a mask over them lifted — hidden from the
	/// guest until released. See <see cref="SetMasked"/>.</summary>
	private static readonly uint[] _heldAcrossUnmask = new uint[MaxControllers];

	private static void SetMasked(int pad, bool connected, PadState state)
	{
		uint buttons = state.Buttons;

		// The owned menus' masks hold the menu owner's pad — every pad until an owner is stated.
		bool owned = MasksPad(pad);
		bool masked = owned && MaskMenuInput;
		bool goldenMenu = GameGoldenCock.OwnsInput || (owned && HostUiOwnsPad);
		bool pauseMasked = owned && !masked && MaskPauseInput;
		bool queryMasked = owned && !masked && !pauseMasked && MaskPauseQueryInput;
		// The card's mask keeps the sticks and the D-pad live: its screen does not pause the
		// game, so the stick-zeroing below must not apply to it.
		bool cardMasked = owned && !masked && !pauseMasked && !queryMasked && MaskReceiveItemsInput;
		bool boxMasked = owned && !masked && !pauseMasked && !queryMasked && !cardMasked &&
			MaskMenuBoxInput;
		bool listMasked = owned && !masked && !pauseMasked && !queryMasked && !cardMasked &&
			!boxMasked && MaskScreenMenuInput;
		bool menuMask = goldenMenu || masked || pauseMasked || queryMasked || boxMasked || listMasked;
		System.Func<uint, uint> filter =
			goldenMenu ? static _ => 0u
			: masked ? WithoutMenuInput
			: pauseMasked ? WithoutPauseInput
			: queryMasked ? WithoutQueryInput
			: cardMasked ? WithoutCardInput
			: boxMasked ? WithoutQueryInput
			: listMasked ? WithoutListInput
			: static b => b;

		// A MASK LIFTING NEVER HANDS THE GUEST A PRESS. The guest edge-detects against its last
		// pad read (`sub_821894A8`), so a held button that reappears is a fresh down edge, and
		// A's down edge emits GameAction 46/36/6 — which `sub_824047E8` hands to any box then
		// up. A button held across its unmask stays hidden until it is physically released.
		// THE SPELL WHEEL'S PICK BUTTON IS THE HOST'S while the wheel is held. The shipped combat
		// scheme binds every button under the held trigger — X (code 13) is the on-release attack,
		// parry and block, Y (14) the ranged aim — so a mod's secondary pick
		// (Engine_UiSpellAssignSecondary) is withheld from the guest for the hold, and a press
		// still down when the trigger lifts stays hidden until released, like any lifted mask.
		// X applies a partner and Y removes one, so both are the host's under the held trigger.
		// The trigger is pad 0's, so on split screen the pick is withheld from pad 0 alone.
		uint wheelBits = SpellsMenuHeld && Refii.Ui.Engine_UiSpellAssign.Secondary is not null &&
			(pad == 0 || !Refii.Core.Engine_Views.Split)
			? (ButtonX | ButtonY)
			: 0u;

		// LOCAL PLAYER TWO'S BACK IS THE HOST'S: it holds their expression wheel (the pump's
		// QuickWheel:2: levers). Shipped, the henchman's Back teleported them to the hero — the
		// leash's crutch, which free roam retires (Fable 3 has none).
		if (pad == PlayerTwoPad && PlayerTwoPad >= 1)
		{
			wheelBits |= Back;
		}
		uint maskBits = ~filter(uint.MaxValue) | wheelBits;
		_heldAcrossUnmask[pad] |= _maskBits[pad] & ~maskBits & buttons;
		_heldAcrossUnmask[pad] &= buttons;
		_maskBits[pad] = maskBits;

		HostBridge.SetControllerState(
			pad, connected,
			filter(buttons) & ~wheelBits & ~_heldAcrossUnmask[pad],
			goldenMenu ? (byte)0 : state.LeftTrigger, goldenMenu ? (byte)0 : state.RightTrigger,
			// The stick is masked with the buttons: `sub_826E39C0` dead-zones the left stick
			// against ±0.75 and emits the same codes the D-pad does — 28/29 for Y at
			// 0x826E3C04/0x826E3C58, 30/31 for X at 0x826E3B50/0x826E3BAC.
			menuMask ? 0 : state.LeftX, menuMask ? 0 : state.LeftY,
			// The card takes the right stick — it turns the model on the front and scrolls the
			// description on the back — so the camera does not swing under it. The left stick
			// stays live: the card does not pause the game.
			//
			// Invert X is ours: the guest has a vertical invert (CameraLookInverted, record33+0x2A,
			// negated at 0x825ADD28) and nothing for the horizontal. Applied only while the stick
			// is the camera's — never under a menu, which reads it as a direction.
			goldenMenu || cardMasked ? 0 : (InvertCameraX && !menuMask ? -state.RightX : state.RightX),
			goldenMenu || cardMasked ? 0 : state.RightY);
	}

	/// <summary>
	/// Whether the guest's own front-end menu should be shielded from this frame's input.
	/// True only while the guest's active screen is MainMenu in its root substate; the title
	/// keeps every button (A there is the guest's contract, `a2 == 36 &amp;&amp;
	/// *(BYTE*)(a1+80)` at 0x826E3078). `screen+24` is 0 for the menu list; 1 is the guest's own
	/// input lock, 2/3/4 the gender card, 5 the load-game carousel, 6/7 a modal — masking
	/// through those deadlocks, since a modal parks the screen in substate 1 until an A press.
	/// The vtable check is load-bearing: `dword_83496B08` has two writers through the same base
	/// ctor, and the game's base-only object has no screen pointers at +180/+184/+188.
	/// </summary>
	private static bool MaskMenuInput
	{
		get
		{
			bool wouldMask =
				HostBridge.FrontEndGuiIsUp &&
				HostBridge.TryGetFrontEnd(out Engine_UiModel.FrontEndScreen screen, out uint state,
					out _, out _, out _, out _) &&
				screen == Engine_UiModel.FrontEndScreen.MainMenu && state == MenuRootState;

			// Never mask once a world is up, whatever the front-end decode says: masking exists
			// only to stop Godot's main menu and the guest's from acting on one press, and a
			// misdecoded MainMenu here would zero the left stick and freeze the player.
			if (wouldMask && (HostBridge.IsInGame || HostBridge.WorldIsActive()))
			{
				if (!_reportedInGameMask)
				{
					_reportedInGameMask = true;
					GD.Print("[game-input] the front end decoded as MainMenu while a world is up — " +
						"NOT masking input. Left unmasked the player would have been unable to " +
						"move, and the camera with them.");
				}

				return false;
			}

			return wouldMask;
		}
	}

	private static bool _reportedInGameMask;

	/// <summary>Set while the owned pause menu has a group expanded: B then collapses the
	/// group instead of reaching the guest, whose own B on the pause root resumes the game.</summary>
	public static bool PauseMenuSwallowsBack { get; set; }

	/// <summary>
	/// Set while the owned front end answers B itself — a folder open in the list, or the gender
	/// card or warning box up. The guest's B on the root list leaves for the title, and its Lua
	/// highlight always reads "at root" here (28/29 are masked), so B must not reach it.
	/// </summary>
	public static bool FrontEndSwallowsBack { get; set; }

	/// <summary>
	/// What the front end GODOT DRAWS is showing, for the unattended driver.
	///
	/// The driver used to read the guest's scene slide and treat slide 1 as "PressStart, the row
	/// list is not up yet". That slide never leaves 1 on this build, so the driver pressed A on
	/// a list it could see perfectly well — re-opening `New Game` and the gender card once every
	/// cooldown, for ever, fighting anything else driving the run.
	///
	/// It is the wrong question to ask the guest at all. Godot owns this menu: it builds the
	/// rows, holds the highlight and decides whether the card or the warning stands. So the
	/// driver asks the thing that draws it, by ROW NAME rather than by counted steps.
	/// </summary>
	public readonly record struct FrontEndDriveState(
		bool ListUp, bool CardUp, bool WarningUp, string Highlighted);

	public static FrontEndDriveState FrontEndDrive { get; set; }

	/// <summary>
	/// The right stick's Y, −1..1, up positive — what `page.lua` scrolls a book page with
	/// (`Page.onUpdate` reads `GUI.State.RightThumbStickY`, page.dis main.1 pc18..21).
	/// </summary>
	public static float RightStickY { get; private set; }

	/// <summary>The right stick's X, −1..1, right positive, dead-zoned like Y — the item preview's spin.</summary>
	public static float RightStickX { get; private set; }

	/// <summary>
	/// Set by the <c>GameUi</c> modal-box drives while an owned box view is standing (query box,
	/// message box, numeric adjuster). A, B and the adjuster's D-pad are then the box's
	/// vocabulary, dispatched through the guest's router; the guest must not also see the press
	/// or the box answers twice. Re-derived every frame, and ANDed with the guest's own state.
	/// </summary>
	public static bool ModalBoxOwnsInput { get; set; }

	/// <summary>
	/// Set by <c>GameUi</c> while a host surface the guest has no part in stands over the play or
	/// the title: a host box on the front end, a mod's menu opened with no list under it. The whole
	/// pad is the host's then, as under the Golden Cock's menu — the guest sees no press, stick or
	/// trigger. Re-derived every frame; a button held across the lift stays hidden until released.
	/// </summary>
	public static bool HostUiOwnsPad { get; set; }

	/// <summary>Set while the guest has a display MessageBox open — stated by the class's own
	/// Open and Close, not the router's modal latch, which such a box never sets.</summary>
	public static bool DisplayMessageBoxUp { get; set; }

	/// <summary>Masks the box's buttons only while the guest still says a modal dialog is
	/// pending (the mode latch, over ANY screen — the openers never change the screen id), or
	/// has a display box open — which never sets that latch. Fails open.</summary>
	private static bool MaskPauseQueryInput =>
		ModalBoxOwnsInput &&
		(DisplayMessageBoxUp || HostBridge.GetDisplayBox() is not null ||
			HostBridge.GetPauseState() is { ModalUp: true });

	/// <summary>
	/// Set by <c>GameUi.DriveReceiveItems</c> while the owned received-item card is standing.
	/// The card does not pause the game, so only A (done) and Y (flip) are taken.
	/// </summary>
	public static bool ReceiveItemsOwnsInput { get; set; }

	private static bool MaskReceiveItemsInput =>
		ReceiveItemsOwnsInput &&
		HostBridge.GetPauseState() is { ScreenId: 8, ModalMode: 1 };

	private static uint WithoutCardInput(uint buttons) => buttons & ~(ButtonA | ButtonY);

	/// <summary>
	/// Set by <c>GameUi.DriveMenuBox</c> while the owned option list is standing. Its whole
	/// vocabulary is taken: the D-pad moves the guest's selection through our lever, so a press
	/// the guest also saw would double every step.
	/// </summary>
	public static bool MenuBoxOwnsInput { get; set; }

	private static bool MaskMenuBoxInput => MenuBoxOwnsInput;

	/// <summary>
	/// Set by <c>GameUi.DriveScreenMenu</c> while the owned in-game list is standing. The whole
	/// vocabulary goes, stick included: every press is forwarded to the guest as its own lever
	/// (a direction becomes <c>g_Menu:ScrollUp/ScrollDown()</c>, A/B become action codes 36/37),
	/// and `sub_826E39C0` turns the left stick into the same 28..31 codes the D-pad emits.
	/// Re-derived every frame and ANDed with the guest's own state. Fails open.
	/// </summary>
	public static bool ScreenMenuOwnsInput { get; set; }

	/// <summary>
	/// Set by <c>GameUi</c> while the co-op screen stands. The guest reads that screen's pad
	/// itself, so nothing is masked; it only stops counting as play — the cursor is free for its
	/// buttons and rows, and B is back, not magic.
	/// </summary>
	public static bool CoopScreenOwnsInput { get; set; }

	private static bool MaskScreenMenuInput =>
		ScreenMenuOwnsInput && HostBridge.GetPauseState() is { ModalMode: 1, ScreenId: not 0 };

	/// <summary>
	/// Set while the owned list's highlighted row is a MOD's ability, whose Y is the mod's unlearn.
	/// The guest must not see that Y: its abilities handler `sub_826C2958` takes code 39 as
	/// `+198 = 0` (0x826c2b30), and `sub_826C2D40` reads that byte to open the discard confirm on
	/// its next commit — for the guest's own highlighted ability, not ours.
	/// </summary>
	public static bool ScreenMenuHoldsFlip { get; set; }

	private static uint WithoutListInput(uint buttons) =>
		WithoutQueryInput(buttons) & (ScreenMenuHoldsFlip ? ~ButtonY : uint.MaxValue);

	/// <summary>The buttons with the owned query box's whole vocabulary taken out — A and B
	/// both, unlike the pause root where B is the guest's resume.</summary>
	private static uint WithoutQueryInput(uint buttons) =>
		buttons & ~(DpadUp | DpadDown | DpadLeft | DpadRight | ButtonA | ButtonB);

	/// <summary>
	/// Whether the guest's own pause menu should be shielded from this frame's input. Gated on
	/// the guest's own statement, not the UI's: the mask engages the frame the guest says the
	/// pause root is the active screen (id 0, no modal pending), because the owned view appears
	/// only on the mirror's sampling cadence. The bridge admits only the in-game GUI object
	/// (vtable 0x82001610), so this can never read "pause root" over the front end. Every pause
	/// modal changes the screen id or sets the modal id (query boxes 23/24/25/28), so a dialog
	/// unmasks the frame it opens. Fails open: no pause state, no mask.
	/// </summary>
	private static bool MaskPauseInput =>
		HostBridge.GetPauseState() is { RootActive: true };

	/// <summary>
	/// The buttons with the pause menu's navigation taken out: the guest's pause list is still
	/// populated and `expandablemenuinput.lua` still binds A. LB/RB stay through — the guest's
	/// own tab switch is the lever our tab entries write. B is conditional, see
	/// <see cref="PauseMenuSwallowsBack"/>.
	/// </summary>
	private static uint WithoutPauseInput(uint buttons)
	{
		uint filtered = buttons & ~(DpadUp | DpadDown | DpadLeft | DpadRight | ButtonA);
		if (PauseMenuSwallowsBack)
		{
			filtered &= ~ButtonB;
		}

		return filtered;
	}

	/// <summary>
	/// `screen+24` while the menu LIST is the thing on screen. Every other value is a substate
	/// the guest drives itself — see <see cref="MaskMenuInput"/>.
	/// </summary>
	private const uint MenuRootState = 0;

	/// <summary>
	/// The buttons with the front end's navigation taken out. Godot owns the main menu and
	/// dispatches by name, but the guest never stopped running its own: `sub_826DEE40` still
	/// forwards A and B to `g_Menu` through `OnPressA`/`OnPressB` while `screen+24` is 0, and
	/// the two highlights are different objects. B is left through at the root, where it is the
	/// guest's own path to the title; elsewhere <see cref="FrontEndSwallowsBack"/> masks it.
	/// </summary>
	private static uint WithoutMenuInput(uint buttons)
	{
		uint filtered = buttons & ~(DpadUp | DpadDown | DpadLeft | DpadRight | ButtonA);
		if (FrontEndSwallowsBack)
		{
			filtered &= ~ButtonB;
		}

		return filtered;
	}

	/// <summary>
	/// The same frame's input, in the terms a host-owned menu needs. Reuses the buttons already
	/// gathered for the guest rather than polling the devices a second time, so the two readers
	/// cannot drift. The stick folds into the direction on the shipped threshold —
	/// `THUMBSTICK_RELEASE_THRESHOLD` 0.65, from `guistateandconstants.lua`.
	/// </summary>
	private static void PublishMenu(int pad, uint buttons, int leftX, int leftY, int rightX, int rightY)
	{
		// A NEW SOURCE HANDS THE MENU NO PRESS. When the menu changes hands, a button already down
		// on the new pad is taken as held, not as a fresh edge.
		if (pad != _menuSourcePad)
		{
			_menuSourcePad = pad;
			_acceptWasDown = (buttons & ButtonA) != 0;
			_backWasDown = (buttons & ButtonB) != 0;
			_flipWasDown = (buttons & ButtonY) != 0;
			_tabPrevWasDown = (buttons & LeftShoulder) != 0;
			_tabNextWasDown = (buttons & RightShoulder) != 0;
			_equipWasDown = (buttons & ButtonX) != 0;
		}

		// The deadzone is ours here: `page.lua` reads `GUI.State.RightThumbStickY` raw (page.dis
		// main.1 pc18..21) because the guest has already dead-zoned it before firing the contract
		// event, and our page reads the pad instead. XInput's right-thumb constant is 8689 of
		// 32767, rescaled so travel starts at zero on the deadzone edge.
		const float RightThumbDeadzone = 8689f;
		float magnitude = System.Math.Abs(rightY);
		RightStickY = magnitude <= RightThumbDeadzone
			? 0f
			: System.Math.Sign(rightY) *
				((magnitude - RightThumbDeadzone) / (32767f - RightThumbDeadzone));

		float sideways = System.Math.Abs(rightX);
		RightStickX = sideways <= RightThumbDeadzone
			? 0f
			: System.Math.Sign(rightX) *
				((sideways - RightThumbDeadzone) / (32767f - RightThumbDeadzone));

		int direction = 0;
		if ((buttons & DpadUp) != 0)
		{
			direction = -1;
		}
		else if ((buttons & DpadDown) != 0)
		{
			direction = 1;
		}
		else
		{
			// Axis() returns the Xbox's signed range, and up is positive after the negation
			// above — so a positive Y is "up", which is -1 in list order.
			double stick = leftY / 32767.0;
			if (Held(ref _pushingUp, stick))
			{
				direction = -1;
			}
			else if (Held(ref _pushingDown, -stick))
			{
				direction = 1;
			}
		}

		MenuDirection = direction;

		// The same fold across X, for the panels navigated sideways (gender card, load-game
		// carousel). `sub_826E39C0` compares the dead-zoned X against ±0.75 at
		// 0x826E3B3C/0x826E3B98 and emits code 30 left, 31 right, as it emits 28/29 off Y. Its
		// 0.75 is a stick-as-button threshold; one threshold governs both axes here, or a
		// diagonal push scrolls a menu and flips a card at once.
		int horizontal = 0;
		if ((buttons & DpadLeft) != 0)
		{
			horizontal = -1;
		}
		else if ((buttons & DpadRight) != 0)
		{
			horizontal = 1;
		}
		else
		{
			double stick = leftX / 32767.0;
			if (Held(ref _pushingLeft, -stick))
			{
				horizontal = -1;
			}
			else if (Held(ref _pushingRight, stick))
			{
				horizontal = 1;
			}
		}

		MenuHorizontal = horizontal;

		// Edge-detected, as the guest does: holding A must not activate the entry every frame.
		bool a = (buttons & ButtonA) != 0;
		bool b = (buttons & ButtonB) != 0;
		bool y = (buttons & ButtonY) != 0;
		bool lb = (buttons & LeftShoulder) != 0;
		bool rb = (buttons & RightShoulder) != 0;
		bool x = (buttons & ButtonX) != 0;
		MenuAcceptPressed = a && !_acceptWasDown;
		MenuAcceptReleased = !a && _acceptWasDown;
		MenuBackPressed = b && !_backWasDown;
		MenuFlipPressed = y && !_flipWasDown;
		MenuTabPrevPressed = lb && !_tabPrevWasDown;
		MenuTabNextPressed = rb && !_tabNextWasDown;
		MenuEquipPressed = x && !_equipWasDown;
		_acceptWasDown = a;
		_backWasDown = b;
		_flipWasDown = y;
		_tabPrevWasDown = lb;
		_tabNextWasDown = rb;
		_equipWasDown = x;
	}

	/// <summary>The pad the host menu read last frame — see <see cref="PublishMenu"/>.</summary>
	private static int _menuSourcePad;

	/// <summary>
	/// The quick wheel's hold, edge-detected off pad 0 alone: the pump's quick-wheel levers act
	/// for the local player, who is player one in local co-op (see <see cref="MenuOwner"/>).
	/// </summary>
	private static void PublishHolds(uint buttons)
	{
		bool raw = (buttons & Back) != 0;
		bool wheel = raw;
		if (WheelToggle)
		{
			// U09: a press opens the wheel and the next press closes it; nothing has to be
			// held. The rest of the host sees the latch as the hold it replaces, so the
			// guest's open/close/select levers and the free cursor work unchanged.
			if (raw && !_wheelRawWasDown)
			{
				_wheelLatched = !_wheelLatched;
			}

			if (_wheelUnlatch)
			{
				_wheelUnlatch = false;
				_wheelLatched = false;
			}

			wheel = _wheelLatched;
		}

		_wheelRawWasDown = raw;
		QuickWheelPressed = wheel && !_wheelWasDown;
		QuickWheelReleased = !wheel && _wheelWasDown;
		QuickWheelHeld = wheel;
		_wheelWasDown = wheel;
	}

	private static bool _wheelRawWasDown;
	private static bool _wheelLatched;
	private static bool _wheelUnlatch;
	private static int _wheelToggle = -1;

	/// <summary>
	/// U09: the expression wheel opens on a press and stays open until the next press (or until
	/// the game closes it itself). <c>refii_wheel_hold.flag</c> beside the exe restores the
	/// shipped hold-to-open.
	/// </summary>
	public static bool WheelToggle
	{
		get
		{
			if (_wheelToggle < 0)
			{
				bool hold = false;
				try
				{
					string dir = System.IO.Path.GetDirectoryName(Godot.OS.GetExecutablePath()) ?? "";
					hold = System.IO.File.Exists(System.IO.Path.Combine(dir, "refii_wheel_hold.flag"));
				}
				catch (System.Exception)
				{
					hold = false;
				}

				_wheelToggle = hold ? 0 : 1;
				Godot.GD.Print($"[ui-u09] expression wheel: {(hold ? "hold (refii_wheel_hold.flag)" : "toggle (press Q to open, Q again to close)")}");
			}

			return _wheelToggle == 1;
		}
	}

	/// <summary>The game closed the wheel itself (an expression started): drop the latch.</summary>
	public static void WheelClosedByGame() => _wheelUnlatch = true;

	/// <summary>
	/// Local player two's expression-wheel levers, edge-detected off their own pad: Back holds
	/// the wheel, A selects (its release ends a held expression), B backs out, the left stick
	/// points. The pump's <c>QuickWheel:2:</c> levers open it for player two.
	/// </summary>
	private static void PublishWheelTwo(uint buttons, int leftX, int leftY)
	{
		bool wheel = (buttons & Back) != 0;
		bool a = (buttons & ButtonA) != 0;
		bool b = (buttons & ButtonB) != 0;
		QuickWheelTwoPressed = wheel && !_wheelTwoWasDown;
		QuickWheelTwoReleased = !wheel && _wheelTwoWasDown;
		QuickWheelTwoHeld = wheel;
		WheelTwoAcceptPressed = a && !_acceptTwoWasDown;
		WheelTwoAcceptReleased = !a && _acceptTwoWasDown;
		WheelTwoBackPressed = b && !_backTwoWasDown;
		bool x = (buttons & ButtonX) != 0;
		XTwoPressed = x && !_xTwoWasDown;
		LeftStickTwo = new Vector2(leftX / 32767f, leftY / 32767f);
		_wheelTwoWasDown = wheel;
		_acceptTwoWasDown = a;
		_backTwoWasDown = b;
		_xTwoWasDown = x;
	}

	private static bool _wheelTwoWasDown;
	private static bool _acceptTwoWasDown;
	private static bool _backTwoWasDown;
	private static bool _xTwoWasDown;

	/// <summary>True on the frame local player two's X went down — their hero toggle on the co-op screen.</summary>
	public static bool XTwoPressed { get; private set; }

	public static bool QuickWheelTwoPressed { get; private set; }
	public static bool QuickWheelTwoReleased { get; private set; }
	public static bool QuickWheelTwoHeld { get; private set; }
	public static bool WheelTwoAcceptPressed { get; private set; }
	public static bool WheelTwoAcceptReleased { get; private set; }
	public static bool WheelTwoBackPressed { get; private set; }
	public static Vector2 LeftStickTwo { get; private set; }

	/// <summary>
	/// One direction's stick hysteresis, as `guistateandconstants.lua` states it: engages above
	/// `THUMBSTICK_PUSH_THRESHOLD` (0.70), stays engaged until below
	/// `THUMBSTICK_RELEASE_THRESHOLD` (0.65), hence one held flag per direction.
	/// </summary>
	/// <param name="magnitude">The stick along that direction, signed so pushing this way is
	/// positive.</param>
	private static bool Held(ref bool held, double magnitude)
	{
		held = held
			? magnitude > Refii.Ui.Engine_FrontEndMenu.ThumbstickReleaseThreshold
			: magnitude >= Refii.Ui.Engine_FrontEndMenu.ThumbstickPushThreshold;
		return held;
	}

	/// <summary>The four `Pushing*Held` flags the shipped input model keeps.</summary>
	private static bool _pushingUp;
	private static bool _pushingDown;
	private static bool _pushingLeft;
	private static bool _pushingRight;

	/// <summary>-1 up, +1 down, 0 centred — for a host-owned menu.</summary>
	public static int MenuDirection { get; private set; }

	/// <summary>-1 left, +1 right, 0 centred — for a host-owned panel navigated sideways.</summary>
	public static int MenuHorizontal { get; private set; }

	/// <summary>True on the frame A went down.</summary>
	public static bool MenuAcceptPressed { get; private set; }

	/// <summary>True on the frame A came up — the expression wheel's held-expression end.</summary>
	public static bool MenuAcceptReleased { get; private set; }

	/// <summary>
	/// The left stick in the Xbox frame (x right, y UP, ±1) — the raw vector the expression
	/// wheel's cursor is computed from.
	/// </summary>
	public static Vector2 LeftStick { get; private set; }

	/// <summary>True on the frame B went down.</summary>
	public static bool MenuBackPressed { get; private set; }

	/// <summary>
	/// True on the frame LB / RB went down — the tab cycle. Forwarded as action codes 72/73 by
	/// the owned drives, because the guest's route from the shoulder bits to those codes is the
	/// GDB-driven action-consumer chain, which does not fire here. The screen handler keeps its
	/// own gates: an open submenu eats the cycle (`screen+420`), `sub_826B83D0` skips
	/// unavailable tabs.
	/// </summary>
	public static bool MenuTabPrevPressed { get; private set; }

	public static bool MenuTabNextPressed { get; private set; }

	private static bool _tabPrevWasDown;
	private static bool _tabNextWasDown;

	/// <summary>True on the frame Y went down — the received-item card's flip.</summary>
	public static bool MenuFlipPressed { get; private set; }

	/// <summary>True on the frame X went down — the received-item card's equip (code 75).</summary>
	public static bool MenuEquipPressed { get; private set; }

	private static bool _equipWasDown;

	private static bool _acceptWasDown;

	private static bool _backWasDown;

	private static bool _flipWasDown;

	/// <summary>
	/// The expression wheel's hold — GameAction 40 on press, 41 on release. A HOST binding: the
	/// shipped scheme binds GameAction 40 to `XboxButton` code 15 (the left trigger), but the
	/// wheel rides the pad's Back button and keyboard Q here. Deliberate, not undecoded — the
	/// code → button table IS known (10 right bumper, 15 left trigger, 16 right trigger), read
	/// out of the GDB, where these pairings live; the executable ships the binding table empty.
	/// </summary>
	public static bool QuickWheelPressed { get; private set; }

	public static bool QuickWheelReleased { get; private set; }

	public static bool QuickWheelHeld { get; private set; }

	private static bool _wheelWasDown;

	/// <summary>
	/// The spells menu's hold — GameAction 42 on press, 43 on release, on the RIGHT TRIGGER
	/// (`XboxButton` 16). Unlike the wheel above this is the SHIPPED binding, not a host one:
	/// `GameControlsCombatOnRelease` aliases a scheme that overrides `GameActionGUISpellsMenu`
	/// onto 16 over a base of 10.
	/// </summary>
	public static bool SpellsMenuPressed { get; private set; }

	public static bool SpellsMenuReleased { get; private set; }

	public static bool SpellsMenuHeld { get; private set; }

	private static bool _spellsWasDown;

	/// <summary>Whichever axis is further from centre — a pad at rest reads 0 and loses.</summary>
	private static int Dominant(int a, int b) =>
		System.Math.Abs(b) > System.Math.Abs(a) ? b : a;

	/// <summary>Triggers are 0..1 in Godot and 0..255 on the Xbox.</summary>
	private static uint Trigger(int pad, JoyAxis axis) => (uint)Mathf.Clamp(
		Mathf.RoundToInt(Input.GetJoyAxis(pad, axis) * 255.0f), 0, 255);

	/// <summary>Which triggers were last reported held.</summary>
	private static uint _reportedTriggers;

	private static uint JoypadButtons(int pad)
	{
		uint buttons = 0;
		buttons |= Bit(pad, JoyButton.DpadUp, DpadUp);
		buttons |= Bit(pad, JoyButton.DpadDown, DpadDown);
		buttons |= Bit(pad, JoyButton.DpadLeft, DpadLeft);
		buttons |= Bit(pad, JoyButton.DpadRight, DpadRight);
		buttons |= Bit(pad, JoyButton.Start, Start);
		buttons |= Bit(pad, JoyButton.Back, Back);
		buttons |= Bit(pad, JoyButton.LeftStick, LeftThumb);
		buttons |= Bit(pad, JoyButton.RightStick, RightThumb);
		buttons |= Bit(pad, JoyButton.LeftShoulder, LeftShoulder);
		buttons |= Bit(pad, JoyButton.RightShoulder, RightShoulder);
		buttons |= Bit(pad, JoyButton.A, ButtonA);
		buttons |= Bit(pad, JoyButton.B, ButtonB);
		buttons |= Bit(pad, JoyButton.X, ButtonX);
		buttons |= Bit(pad, JoyButton.Y, ButtonY);
		return buttons;
	}

	/// <summary>
	/// The title screen's idle state. A is gated by the armed byte `titleScreen+80` (what
	/// <c>canPressA</c> reports): `a2 == 36 &amp;&amp; *(BYTE *)(a1 + 80)` at 0x826E3078, armed by
	/// the presentation's CAN_PRESS_A event at 0x826E2E38; states 0, 9, 10 and 11 all reach it.
	/// Waiting for state 0 avoids state 8, where the attract movie takes A down 0x826E3030 and
	/// only sets its skip flag.
	/// </summary>
	private const uint TitleIdleState = 0;

	/// <summary>
	/// `PressStart` — the main menu's opening slide, where the row list is not up yet and A is
	/// the only button that does anything. The row sequence waits for the slide to leave it.
	/// </summary>
	private const int PressStartSlide = 1;


	private static uint Bit(int pad, JoyButton button, uint mask) =>
		Input.IsJoyButtonPressed(pad, button) ? mask : 0u;

	/// <summary>
	/// Godot reports -1..1; the guest wants -32768..32767. Scaled by 32767 both ways so a full
	/// deflection is symmetric and cannot overflow the positive end.
	/// </summary>
	private static int Axis(int pad, JoyAxis axis) =>
		Mathf.Clamp(Mathf.RoundToInt(Input.GetJoyAxis(pad, axis) * 32767.0f), -32767, 32767);
}
