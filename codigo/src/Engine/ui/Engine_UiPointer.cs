using Godot;
using Refii.Game;

namespace Refii.Ui;

/// <summary>
/// The mouse, in the UI's own terms: where it points on the canvas, whether it moved, and the
/// clicks and wheel steps of this frame.
/// </summary>
/// <remarks>
/// The UI draws through an orthographic camera in its own SubViewport (`GameUi`), sized to the
/// window, with every owned view under an identity-transformed host — so a UI node's global X/Y
/// ARE canvas units (Y up, origin at the centre), and a window pixel unprojects through the
/// camera to the same space. Nothing in the UI carries a collider, so picking is by bounds.
///
/// Events arrive through <see cref="Feed"/> from a node's <c>_Input</c>; <see cref="EndFrame"/>
/// clears the per-frame edges once every consumer has read them.
/// </remarks>
public static class Engine_UiPointer
{
	/// <summary>
	/// The pointer on the canvas, or null when there is no UI camera, no cursor, or the controller is
	/// the device in use (<see cref="Engine_UiInputMode.KeyboardMouse"/> false): a resting mouse must
	/// not hold a highlight, steer a wheel or catch a click while the player drives the menus with the
	/// pad. The first real mouse movement, click or key makes it the device again.
	/// </summary>
	public static Vector2? Canvas { get; private set; }

	/// <summary>
	/// The pointer moved this frame by more than sensor jitter (the same 2-pixel threshold the device
	/// switch uses) — the only thing that may re-pick a list row.
	/// </summary>
	public static bool Moved { get; private set; }

	public static bool LeftPressed { get; private set; }

	public static bool RightPressed { get; private set; }

	/// <summary>Wheel notches this frame: negative up, positive down (list order).</summary>
	public static int WheelSteps { get; private set; }

	/// <summary>The canvas rectangle the window shows (Y up), or null before the first resolve.</summary>
	public static Rect2? VisibleCanvas { get; private set; }

	/// <summary>Whether the cursor is free for the menus (not captured for the camera).</summary>
	public static bool Free =>
		GameInput.MouseInput &&
		Input.MouseMode != Input.MouseModeEnum.Captured &&
		(!GameInput.Playing || GameInput.InOverlay);

	private static Vector2 _window;
	private static bool _motion;

	/// <summary>One input event. Mouse events only; the rest are ignored.</summary>
	public static void Feed(InputEvent @event)
	{
		// U08: Home / End on a list jump to its first / last row. Edges only (no auto-repeat),
		// read by GameUi.PointerList and cleared with the clicks at the end of the frame.
		if (@event is InputEventKey { Pressed: true, Echo: false } key)
		{
			if (key.Keycode == Key.Home)
			{
				HomePressed = true;
			}
			else if (key.Keycode == Key.End)
			{
				EndPressed = true;
			}

			return;
		}

		if (!GameInput.MouseInput)
		{
			return;
		}
		switch (@event)
		{
			case InputEventMouseMotion motion:
				_window = motion.Position;
				_motion |= motion.Relative.LengthSquared() > 4f;
				break;
			case InputEventMouseButton { Pressed: true } button:
				_window = button.Position;
				switch (button.ButtonIndex)
				{
					case MouseButton.Left:
						LeftPressed = true;
						break;
					case MouseButton.Right:
						RightPressed = true;
						break;
					case MouseButton.WheelUp:
						--WheelSteps;
						break;
					case MouseButton.WheelDown:
						++WheelSteps;
						break;
				}

				break;
		}
	}

	/// <summary>
	/// Resolves the window position through the UI camera. Once per frame, before any view asks.
	/// </summary>
	public static void Resolve(Camera3D? camera, Viewport? viewport, Vector2 windowSize)
	{
		Moved = _motion && Free;
		_motion = false;
		if (camera is null || viewport is null || windowSize.X <= 0f || windowSize.Y <= 0f)
		{
			Canvas = null;
			return;
		}

		Vector2 size = viewport.GetVisibleRect().Size;
		Vector3 topLeft = camera.ProjectRayOrigin(Vector2.Zero);
		Vector3 bottomRight = camera.ProjectRayOrigin(size);
		VisibleCanvas = new Rect2(topLeft.X, bottomRight.Y, bottomRight.X - topLeft.X, topLeft.Y - bottomRight.Y);
		if (!Free || !Engine_UiInputMode.KeyboardMouse)
		{
			Canvas = null;
			Moved = false;
			return;
		}

		Vector2 inViewport = _window * size / windowSize;
		Vector3 origin = camera.ProjectRayOrigin(inViewport);
		Canvas = new Vector2(origin.X, origin.Y);
	}

	/// <summary>Clears this frame's clicks and wheel. After every consumer has read them.</summary>
	public static void EndFrame()
	{
		LeftPressed = false;
		RightPressed = false;
		WheelSteps = 0;
		HomePressed = false;
		EndPressed = false;
	}

	/// <summary>U08: Home was pressed this frame (jump to the first row of the list).</summary>
	public static bool HomePressed { get; private set; }

	/// <summary>U08: End was pressed this frame (jump to the last row of the list).</summary>
	public static bool EndPressed { get; private set; }

	/// <summary>U08: takes this frame's Home/End press: -1 Home, +1 End, 0 none.</summary>
	public static int TakeJump()
	{
		int jump = HomePressed ? -1 : EndPressed ? 1 : 0;
		HomePressed = false;
		EndPressed = false;
		return jump;
	}

	/// <summary>Takes this frame's left click, so two consumers cannot both act on it.</summary>
	public static bool TakeLeft()
	{
		bool pressed = LeftPressed;
		LeftPressed = false;
		return pressed;
	}

	/// <summary>Takes this frame's wheel notches, so they do not also step the pad.</summary>
	public static int TakeWheel()
	{
		int steps = WheelSteps;
		WheelSteps = 0;
		return steps;
	}

	/// <summary>
	/// The canvas rectangle a UI subtree covers: the union of its drawn meshes' bounds, in X/Y.
	/// Null when nothing under it is drawn. A quad at opacity 0 is not drawn: authored-invisible
	/// art (a page-wide backing, an unlit choice) would otherwise stretch a row's hit box across
	/// the right page or lay one answer over the other.
	/// </summary>
	public static Rect2? CanvasBounds(Node3D root)
	{
		Rect2? bounds = null;
		Accumulate(root, ref bounds);
		return bounds;
	}

	private static void Accumulate(Node node, ref Rect2? bounds)
	{
		if (node is Node3D { Visible: false })
		{
			return;
		}

		if (node is MeshInstance3D { Mesh: not null } mesh &&
			Engine_UiSceneBuilder.QuadOpacity(mesh) > 0.01f)
		{
			Aabb box = mesh.GlobalTransform * mesh.GetAabb();
			var rect = new Rect2(box.Position.X, box.Position.Y, box.Size.X, box.Size.Y);
			bounds = bounds is { } so ? so.Merge(rect) : rect;
		}

		// By index: `GetChildren()` is a Godot Array per node per call, and this walks every
		// hit-tested subtree each frame the mouse is up.
		for (int i = 0, count = node.GetChildCount(); i < count; ++i)
		{
			Accumulate(node.GetChild(i), ref bounds);
		}
	}
}
