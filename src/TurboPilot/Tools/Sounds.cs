using System.IO;
using System.Runtime.InteropServices;

namespace TurboPilot.Tools;

/// <summary>
/// Audio cues for the three moments the user needs to look back at a
/// window they are not looking at.
///
/// TurboPilot is built to sit in a narrow column beside an editor, which
/// means the user is usually reading the editor. Sound is the only
/// channel that reaches them there, so a finished turn or a waiting
/// question is worth a chime even though a status line already says so.
///
/// The files are the stock Windows chimes in %SystemRoot%\Media, so
/// nothing has to be bundled and the cues already match the machine's
/// volume and mute state. Every failure is swallowed: a missing file, a
/// busy device, or an unusual Windows install must never be allowed to
/// interrupt the UI over a courtesy.
/// </summary>
internal static class Sounds
{
	private const string PromptSentFile = "notify.wav";
	private const string TurnCompleteFile = "chimes.wav";
	private const string AttentionFile = "Windows Exclamation.wav";

	/// <summary>
	/// Whether cues play at all. Set from settings at startup and
	/// whenever they change; off means every Play call does nothing.
	/// </summary>
	internal static bool Enabled { get; set; }

	/// <summary>Records what was played, for checks. Null in normal use.</summary>
	internal static List<string>? Played { get; set; }

	/// <summary>A prompt left for the model.</summary>
	internal static void PlayPromptSent() => Play(PromptSentFile);

	/// <summary>A turn finished and the session is idle again.</summary>
	internal static void PlayTurnComplete() => Play(TurnCompleteFile);

	/// <summary>The model is blocked on a question or a permission request.</summary>
	internal static void PlayAttention() => Play(AttentionFile);

	private static void Play(string fileName)
	{
		if (!Enabled)
			return;
		Played?.Add(fileName);
		try
		{
			var path = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", fileName);
			if (!File.Exists(path))
				return;
			// Async so playback never holds the UI thread; NoDefault so a
			// file that cannot be opened stays silent instead of falling
			// back to the system beep.
			PlaySound(path, IntPtr.Zero, SndAsync | SndFileName | SndNoDefault);
		}
		catch (Exception)
		{
			// Silent by design.
		}
	}

	private const uint SndAsync = 0x0001;
	private const uint SndNoDefault = 0x0002;
	private const uint SndFileName = 0x00020000;

	[DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = false)]
	private static extern bool PlaySound(string? sound, IntPtr module, uint flags);
}
