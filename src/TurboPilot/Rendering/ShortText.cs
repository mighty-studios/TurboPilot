namespace TurboPilot.Rendering;

/// <summary>
/// Shortens the identifiers that appear in fixed-width furniture: the
/// session badge over the output area and the Past Sessions list. Model
/// names and session IDs are written for machines and routinely run past
/// any width a dialog can honor, so what is shown is cut down and the
/// whole value is offered in a tooltip instead.
/// </summary>
internal static class ShortText
{
	/// <summary>Ellipsis glyph used wherever text is cut. One cell wide in the bundled face.</summary>
	private const string Ellipsis = "...";

	/// <summary>
	/// Cuts <paramref name="text"/> to <paramref name="max"/> characters,
	/// ellipsis included, so the result never exceeds the budget it was
	/// given. Text already within budget is returned unchanged.
	/// </summary>
	internal static string Clip(string? text, int max)
	{
		text = (text ?? "").Trim();
		if (max <= 0) return "";
		if (text.Length <= max) return text;
		return max <= Ellipsis.Length ? text[..max] : text[..(max - Ellipsis.Length)] + Ellipsis;
	}

	/// <summary>
	/// The readable part of a model identifier. Vendors qualify their
	/// models with a publisher, a registry and a version
	/// ("Qwen3-Coder-30B-A3B-Instruct-GGUF/qwen3-coder-30b:4") and the
	/// leading segments repeat across every entry, so the last path
	/// segment carries what actually distinguishes one model from
	/// another. The result is still clipped, because that segment alone
	/// can be long.
	/// </summary>
	internal static string Model(string? model, int max = 34)
	{
		var text = (model ?? "").Trim();
		if (text.Length == 0) return "";
		var slash = text.LastIndexOfAny(['/', '\\']);
		if (slash >= 0 && slash < text.Length - 1)
			text = text[(slash + 1)..];
		return Clip(text, max);
	}

	/// <summary>
	/// A session ID cut to a recognizable stub. IDs are unique in their
	/// opening characters, so the head is kept and the tail dropped.
	/// </summary>
	internal static string SessionId(string? sessionId, int max = 14) => Clip(sessionId, max);
}
