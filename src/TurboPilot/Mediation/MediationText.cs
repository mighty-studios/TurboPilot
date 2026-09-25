using Microsoft.ML.Tokenizers;

namespace TurboPilot.Mediation;

internal static class MediationText
{
	private static readonly Lazy<TiktokenTokenizer> Tokenizer = new(() => TiktokenTokenizer.CreateForEncoding("o200k_base"));

	public static int CountTokens(string text) => Tokenizer.Value.CountTokens(text);

	// Keeps both ends of long text: command output usually states its subject first and its outcome last.
	public static string Excerpt(string text, int maxChars, double headShare = 2.0 / 3)
	{
		if (text.Length <= maxChars)
			return text;
		const string marker = "\r\n[...]\r\n";
		var head = Math.Max(0, (int)((maxChars - marker.Length) * headShare));
		var tail = Math.Max(0, maxChars - marker.Length - head);
		if (head > 0 && char.IsHighSurrogate(text[head - 1]))
			head--;
		if (tail > 0 && char.IsLowSurrogate(text[^tail]))
			tail--;
		return text[..head] + marker + text[^tail..];
	}
}
