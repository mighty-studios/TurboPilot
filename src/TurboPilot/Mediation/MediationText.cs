using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

namespace TurboPilot.Mediation;

internal static class MediationText
{
	private static readonly Lazy<TiktokenTokenizer> Tokenizer = new(() => TiktokenTokenizer.CreateForEncoding("o200k_base"));
	// Constraint words cover negations, exceptions, and ordering such as "after you share your review".
	// Apostrophes inside words are not quote delimiters, and unquoted paths end at whitespace.
	private static readonly Regex ProtectedText = new(
		"""```[\s\S]*?(?:```|$)|~~~[\s\S]*?(?:~~~|$)|`[^`\r\n]+`|"[^"\r\n]+"|(?<!\w)'[^'\r\n]+'(?!\w)|https?://[^\s]+|(?:[A-Za-z]:[\\/]|\.{0,2}[\\/])[\w.\\/-]+|\b[\w-]+(?:[\\/][\w.-]+)+|\b[\w-]+\.[A-Za-z0-9]{1,12}\b|\b\d+(?:[.,]\d+)*\b|\b(?:not|no|nor|never|none|nothing|neither|without|unless|except|only|avoid|until|before|after|yet|cannot|\w+n['\u2019]t)\b""",
		RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
	private static readonly Regex WordLiteral = new(
		@"^(?:[A-Za-z]+(?:'[A-Za-z]+)?|\d+(?:[.,]\d+)*)$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
	private static readonly Regex RepeatedParagraph = new(@"\s+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

	public static int CountTokens(string text) => Tokenizer.Value.CountTokens(text);
	public static IReadOnlyList<string> GetProtectedText(string text) =>
		ProtectedText.Matches(text).Select(match => match.Value).Distinct(StringComparer.Ordinal).ToList();

	public static bool PreservesLiterals(string original, string candidate)
	{
		var normalizedCandidate = NormalizeApostrophes(candidate);
		foreach (var literal in GetProtectedText(original))
		{
			var word = NormalizeApostrophes(literal);
			var preserved = WordLiteral.IsMatch(word)
				? ContainsToken(normalizedCandidate, word)
				: candidate.Contains(literal, StringComparison.Ordinal);
			if (!preserved)
				return false;
		}
		return true;
	}

	// Words and numbers must survive as whole tokens: "note" does not preserve "not", and "142" does not preserve "42".
	private static bool ContainsToken(string text, string token) => Regex.IsMatch(text,
		@"(?<![\w'.,])" + Regex.Escape(token) + @"(?![\w']|[.,]\d)",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

	private static string NormalizeApostrophes(string text) => text.Replace('\u2019', '\'');

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
			// Cutting after whitespace keeps words such as "without" and paths intact across chunks.
			if (offset + length < text.Length)
			{
				var space = text.LastIndexOfAny([' ', '\t', '\n'], offset + length - 1, length);
				if (space >= offset + length / 2)
					length = space - offset + 1;
			}
			if (length > 1 && offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1]))
				length--;
			chunks.Add(text.Substring(offset, length));
			offset += length;
		}
		return chunks;
	}
}
