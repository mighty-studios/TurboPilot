using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

namespace TurboPilot.Mediation;

internal static class MediationText
{
	private static readonly Lazy<TiktokenTokenizer> Tokenizer = new(() => TiktokenTokenizer.CreateForEncoding("o200k_base"));
	private static readonly Regex ProtectedText = new(
		"""```[\s\S]*?(?:```|$)|~~~[\s\S]*?(?:~~~|$)|`[^`\r\n]+`|"[^"\r\n]+"|'[^'\r\n]+'|https?://[^\s]+|(?:[A-Za-z]:[\\/]|\.{0,2}[\\/])[\w.\\/ -]+|\b[\w-]+(?:[\\/][\w.-]+)+|\b[\w-]+\.[A-Za-z0-9]{1,12}\b|\b\d+(?:[.,]\d+)*\b|\b(?:not|never|without|unless|except|only|don't|mustn't|cannot|can't)\b""",
		RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
	private static readonly Regex RepeatedParagraph = new(@"\s+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

	public static int CountTokens(string text) => Tokenizer.Value.CountTokens(text);
	public static IReadOnlyList<string> GetProtectedText(string text) =>
		ProtectedText.Matches(text).Select(match => match.Value).Distinct(StringComparer.Ordinal).ToList();

	public static bool PreservesLiterals(string original, string candidate)
	{
		foreach (var literal in GetProtectedText(original))
		{
			if (!candidate.Contains(literal, StringComparison.Ordinal))
				return false;
		}
		return true;
	}

	public static IReadOnlyList<MediatorWarning> FindRepetition(string output)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var paragraph in output.ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
		{
			if (paragraph.Length < 100 || paragraph.Contains("```") || paragraph.Contains("~~~"))
				continue;
			var normalized = RepeatedParagraph.Replace(paragraph.Trim(), " ");
			if (!seen.Add(normalized))
				return [new("repetition", "Possible repeated paragraph in the response.", paragraph.Length > 180 ? paragraph[..180] : paragraph)];
		}
		return [];
	}

	public static IReadOnlyList<string> SplitForContext(string text, int maxTokens)
	{
		var chunks = new List<string>();
		var offset = 0;
		while (offset < text.Length)
		{
			var length = Math.Min(text.Length - offset, maxTokens * 3);
			while (CountTokens(text.Substring(offset, length)) > maxTokens)
				length = Math.Max(1, length * 3 / 4);
			if (length > 1 && offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1]))
				length--;
			chunks.Add(text.Substring(offset, length));
			offset += length;
		}
		return chunks;
	}
}
