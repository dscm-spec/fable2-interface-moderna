namespace Refii.Ui;

/// <summary>
/// Whether the UI's high-frequency traces are on. OFF by default: during play they repeat on
/// every menu step, and each line is a synchronous write to the log file, which showed up as
/// 25-214 ms frames inside the menus. Set <c>REFII_UI_TRACE=1</c> in the environment before
/// launching to bring every line back for an investigation.
/// </summary>
/// <remarks>
/// Only repetition is gated. State changes, failures, start-up, loading, warnings and errors
/// are logged either way.
/// </remarks>
public static class Engine_UiTrace
{
	/// <summary>True when <c>REFII_UI_TRACE=1</c>: log every repeat as well as every change.</summary>
	public static readonly bool Verbose =
		System.Environment.GetEnvironmentVariable("REFII_UI_TRACE") == "1";
}
