using Godot;

namespace Refii.Ui;

/// <summary>
/// U12: the mouse pointer in the book's own colours, in place of the Windows arrow.
/// <list type="bullet">
/// <item><c>refii_cursor.png</c> beside the exe: that picture is the pointer (scaled to the
/// size bucket, aspect kept). Its hotspot is read from <c>refii_cursor_hotspot.txt</c> as
/// "x,y" in the PNG's own pixels; without it, the top-left corner.</item>
/// <item>Otherwise a gold arrow with a dark-brown rim, the gold and brown of the menu tabs,
/// drawn here from a polygon (supersampled), so no asset ships. (A drawn gauntlet was tried
/// 2026-11-01 and rejected: hand-drawn shapes at 32 px read crude.)</item>
/// </list>
/// Scaled with the window height. <c>refii_cursor_off.flag</c> keeps the system arrow.
/// </summary>
public static class Engine_UiCursor
{
	private static bool _applied;
	private static int _appliedSize;

	// The arrow on a 32-unit grid: tip at the top-left (the hotspot), the shaft and the notch.
	private static readonly Vector2[] Arrow =
	{
		new(2f, 2f), new(2f, 25f), new(7.5f, 20f), new(11.5f, 28.5f),
		new(15.5f, 26.5f), new(11.5f, 18.5f), new(19.5f, 18.5f),
	};

	private static readonly Color Rim = new(0.16f, 0.09f, 0.03f);
	private static readonly Color GoldTop = new(1.00f, 0.92f, 0.62f);
	private static readonly Color GoldBottom = new(0.72f, 0.50f, 0.17f);
	private static readonly Color Shine = new(1f, 0.98f, 0.86f);

	/// <summary>Sets the pointer once, and again if the window moves to another size bucket.</summary>
	public static void Apply(float windowHeight)
	{
		int size = windowHeight >= 1900f ? 64 : windowHeight >= 1300f ? 48 : 32;
		if (_applied && size == _appliedSize)
		{
			return;
		}

		_applied = true;
		_appliedSize = size;
		string dir = ExeDir();
		if (System.IO.File.Exists(System.IO.Path.Combine(dir, "refii_cursor_off.flag")))
		{
			GD.Print("[ui-u12] fable pointer: off (refii_cursor_off.flag)");
			return;
		}

		if (FromFile(dir, size) is { } custom)
		{
			Set(custom.Image, custom.Hotspot);
			GD.Print($"[ui-u12] fable pointer: refii_cursor.png ({custom.Image.GetWidth()}x{custom.Image.GetHeight()}, hotspot {custom.Hotspot})");
			return;
		}

		Set(Draw(size), new Vector2(2f, 2f) * (size / 32f));
		GD.Print($"[ui-u12] fable pointer: gold arrow ({size}px)");
	}

	private static void Set(Image image, Vector2 hotspot)
	{
		Input.SetCustomMouseCursor(image, Input.CursorShape.Arrow, hotspot);
		Input.SetCustomMouseCursor(image, Input.CursorShape.PointingHand, hotspot);
	}

	private static string ExeDir()
	{
		try
		{
			return System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? "";
		}
		catch (System.Exception)
		{
			return "";
		}
	}

	/// <summary>The player's own picture, scaled so its longer side is the bucket size.</summary>
	private static (Image Image, Vector2 Hotspot)? FromFile(string dir, int size)
	{
		string path = System.IO.Path.Combine(dir, "refii_cursor.png");
		if (!System.IO.File.Exists(path))
		{
			return null;
		}

		var image = new Image();
		if (image.Load(path) != Error.Ok || image.GetWidth() <= 0 || image.GetHeight() <= 0)
		{
			GD.PushWarning($"[ui-u12] refii_cursor.png could not be read; the gold arrow stands");
			return null;
		}

		image.Convert(Image.Format.Rgba8);
		var hotspot = Vector2.Zero;
		string hotPath = System.IO.Path.Combine(dir, "refii_cursor_hotspot.txt");
		if (System.IO.File.Exists(hotPath))
		{
			string[] parts = System.IO.File.ReadAllText(hotPath).Split(',', ';', ' ');
			if (parts.Length >= 2 &&
				float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float hx) &&
				float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float hy))
			{
				hotspot = new Vector2(hx, hy);
			}
		}

		int longer = Mathf.Max(image.GetWidth(), image.GetHeight());
		if (longer != size)
		{
			float k = (float)size / longer;
			image.Resize(Mathf.Max(1, Mathf.RoundToInt(image.GetWidth() * k)),
				Mathf.Max(1, Mathf.RoundToInt(image.GetHeight() * k)), Image.Interpolation.Lanczos);
			hotspot *= k;
		}

		hotspot = hotspot.Clamp(Vector2.Zero, new Vector2(image.GetWidth() - 1, image.GetHeight() - 1));
		return (image, hotspot);
	}

	private static Image Draw(int size)
	{
		const int Samples = 4;
		float unit = 32f / size;
		const float RimWidth = 1.6f;
		Image image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
		for (int y = 0; y < size; ++y)
		{
			for (int x = 0; x < size; ++x)
			{
				float r = 0f, g = 0f, b = 0f, a = 0f;
				for (int sy = 0; sy < Samples; ++sy)
				{
					for (int sx = 0; sx < Samples; ++sx)
					{
						var p = new Vector2((x + (sx + 0.5f) / Samples) * unit, (y + (sy + 0.5f) / Samples) * unit);
						bool inside = Inside(p);
						float edge = EdgeDistance(p);
						Color c;
						if (inside && edge > RimWidth)
						{
							float t = Mathf.Clamp((p.Y - 2f) / 26f, 0f, 1f);
							c = GoldTop.Lerp(GoldBottom, t);
							if (edge < RimWidth + 1.1f && p.X < 4.6f)
							{
								c = c.Lerp(Shine, 0.6f);
							}
						}
						else if (inside || edge < 0.9f)
						{
							c = Rim;
						}
						else
						{
							continue;
						}

						r += c.R;
						g += c.G;
						b += c.B;
						a += 1f;
					}
				}

				if (a > 0f)
				{
					image.SetPixel(x, y, new Color(r / a, g / a, b / a, a / (Samples * Samples)));
				}
			}
		}

		return image;
	}

	private static bool Inside(Vector2 p)
	{
		bool inside = false;
		for (int i = 0, j = Arrow.Length - 1; i < Arrow.Length; j = i++)
		{
			Vector2 a = Arrow[i], b = Arrow[j];
			if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
			{
				inside = !inside;
			}
		}

		return inside;
	}

	private static float EdgeDistance(Vector2 p)
	{
		float best = float.MaxValue;
		for (int i = 0, j = Arrow.Length - 1; i < Arrow.Length; j = i++)
		{
			Vector2 a = Arrow[j], b = Arrow[i];
			Vector2 ab = b - a;
			float t = Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0f, 1f);
			best = Mathf.Min(best, p.DistanceTo(a + ab * t));
		}

		return best;
	}
}
