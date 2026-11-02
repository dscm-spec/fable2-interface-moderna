using Godot;
using Refii.Text;

namespace Refii.Ui;

/// <summary>
/// U13: whether the string book the guest reads is the community PT-BR translation. The game has
/// no Portuguese culture — the translation replaces the en-uk book.babel in place — so the
/// culture name cannot say it. The text can: the shipped en-uk book has a dozen strings with
/// ç/ã/õ (loan words), the PT-BR book has tens of thousands. The host-only strings this build adds
/// ("(Equipado)", the Quit to Desktop/Title rows) are shown in Portuguese only when this is true,
/// so an English player keeps English text.
/// </summary>
public static class Engine_UiLocale
{
	/// <summary>Strings with a Portuguese-only letter above which the book counts as PT-BR.</summary>
	private const int PortugueseThreshold = 1000;

	private static readonly char[] PortugueseLetters = { 'ç', 'ã', 'õ', 'Ç', 'Ã', 'Õ' };

	private static Engine_BabelDatabase? _seen;

	/// <summary>True once a loaded string book reads as the PT-BR translation.</summary>
	public static bool IsPortuguese { get; private set; }

	/// <summary>Called whenever the host adopts a string book; one pass over its text.</summary>
	public static void Note(Engine_BabelDatabase strings)
	{
		if (ReferenceEquals(strings, _seen))
		{
			return;
		}

		_seen = strings;
		int hits = 0;
		try
		{
			foreach ((uint _, string value) in strings.Entries())
			{
				if (value.IndexOfAny(PortugueseLetters) >= 0 && ++hits >= PortugueseThreshold)
				{
					break;
				}
			}
		}
		catch (System.Exception exception)
		{
			GD.PushWarning($"[ui-u13] locale probe failed: {exception.Message}");
		}

		IsPortuguese = hits >= PortugueseThreshold;
		GD.Print($"[ui-u13] string book reads as {(IsPortuguese ? "PT-BR" : "English")} ({hits}+ strings with ç/ã/õ)");
	}
}
