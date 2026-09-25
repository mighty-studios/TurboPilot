using System.Text;
using System.Text.Json;

namespace TurboPilot.Rendering;

/// <summary>
/// What a tool call was, in words a person can read.
///
/// The transcript used to say only that a tool ran, which told the user
/// nothing: "powershell" is not an account of what happened to their
/// machine. The arguments the runtime already sends carry the whole
/// story, so they are turned into a headline that fits on one line and
/// a body the user can open when the headline is not enough.
///
/// Everything here is bounded. A tool can be handed a file the size of
/// a novel, and neither the transcript nor the person reading it is
/// helped by receiving all of it.
/// </summary>
internal static class ToolDetail
{
	internal const int HeadlineBudget = 96;
	internal const int BodyBudget = 4000;
	internal const int BodyLineBudget = 60;
	internal const int OutcomeBudget = 1200;

	// The argument most worth putting in the headline, best first. A
	// shell command beats a path, a path beats a pattern, and anything
	// concrete beats a description written for the model.
	private static readonly string[] HeadlineKeys =
	[
		"command", "cmd", "path", "file_path", "filePath", "filename", "file",
		"paths", "pattern", "query", "url", "description", "title", "name",
	];

	private static readonly string[] OldKeys = ["old_str", "oldString", "old_string"];
	private static readonly string[] NewKeys = ["new_str", "newString", "new_string"];

	/// <summary>
	/// Reduces a tool call to a one-line headline and an openable body.
	/// </summary>
	internal static (string Headline, string Body) Describe(string? toolName, JsonElement? arguments, string? displayCommand)
	{
		var name = string.IsNullOrWhiteSpace(toolName) ? "tool" : toolName.Trim();
		if (arguments is not { ValueKind: JsonValueKind.Object } args)
			return (ShortText.Clip(displayCommand, HeadlineBudget), string.Empty);

		// A shell tool gets the command the driver will actually run,
		// which has the redundant leading directory change removed.
		var headline = ShortText.Clip(Flatten(displayCommand), HeadlineBudget);
		if (headline.Length == 0)
			headline = ShortText.Clip(Flatten(FirstOf(args, HeadlineKeys)), HeadlineBudget);

		var body = Edit(args) ?? Arguments(args);
		return (headline, body);
	}

	/// <summary>
	/// What the tool left behind: the error when it failed, an excerpt
	/// of the output when it did not. The excerpt is the opening of the
	/// output because that is where a command puts what went wrong.
	/// </summary>
	internal static string Outcome(bool success, string? error, string? content)
	{
		if (!success)
			return ShortText.Clip(string.IsNullOrWhiteSpace(error) ? "Failed." : error, OutcomeBudget);
		return Trim(content ?? string.Empty, BodyLineBudget, OutcomeBudget);
	}

	/// <summary>
	/// An edit shown as the change it makes, rather than as two quoted
	/// blobs the reader has to compare by eye.
	/// </summary>
	private static string? Edit(JsonElement args)
	{
		var before = FirstOf(args, OldKeys);
		var after = FirstOf(args, NewKeys);
		if (before is null && after is null) return null;

		var sb = new StringBuilder();
		var path = FirstOf(args, ["path", "file_path", "filePath"]);
		if (path is not null)
			sb.Append(path).Append("\r\n");
		foreach (var line in Lines(before))
			sb.Append('-').Append(' ').Append(line).Append("\r\n");
		foreach (var line in Lines(after))
			sb.Append('+').Append(' ').Append(line).Append("\r\n");
		return Trim(sb.ToString(), BodyLineBudget, BodyBudget);
	}

	/// <summary>
	/// Every argument as one line each, in the order the runtime sent
	/// them.
	/// </summary>
	private static string Arguments(JsonElement args)
	{
		var sb = new StringBuilder();
		foreach (var property in args.EnumerateObject())
		{
			var value = Flatten(Text(property.Value));
			if (value.Length == 0) continue;
			sb.Append(property.Name).Append(": ").Append(ShortText.Clip(value, 240)).Append("\r\n");
		}
		return Trim(sb.ToString(), BodyLineBudget, BodyBudget);
	}

	private static string? FirstOf(JsonElement args, IReadOnlyList<string> keys)
	{
		foreach (var key in keys)
			if (args.TryGetProperty(key, out var value) && Text(value) is { Length: > 0 } text)
				return text;
		return null;
	}

	/// <summary>Any JSON value as the text a person would read.</summary>
	private static string Text(JsonElement value) => value.ValueKind switch
	{
		JsonValueKind.String => value.GetString() ?? string.Empty,
		JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
		JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(Text).Where(t => t.Length > 0)),
		_ => value.ToString(),
	};

	/// <summary>Collapses a value onto one line so a headline stays one line.</summary>
	private static string Flatten(string? text)
	{
		if (string.IsNullOrWhiteSpace(text)) return string.Empty;
		var sb = new StringBuilder(text.Length);
		var space = false;
		foreach (var character in text.Trim())
		{
			if (char.IsWhiteSpace(character))
			{
				space = true;
				continue;
			}
			if (space && sb.Length > 0) sb.Append(' ');
			space = false;
			sb.Append(character);
		}
		return sb.ToString();
	}

	private static IEnumerable<string> Lines(string? text) =>
		string.IsNullOrEmpty(text) ? [] : text.Replace("\r\n", "\n").Split('\n');

	/// <summary>
	/// Holds a block to a readable size, saying plainly when it has been
	/// cut rather than trailing off and leaving the reader to guess.
	/// </summary>
	private static string Trim(string text, int maxLines, int maxLength)
	{
		var normalized = text.Replace("\r\n", "\n").TrimEnd('\n');
		if (normalized.Length == 0) return string.Empty;

		var lines = normalized.Split('\n');
		var cut = lines.Length > maxLines;
		var kept = cut ? lines[..maxLines] : lines;
		var joined = string.Join("\r\n", kept);
		if (joined.Length > maxLength)
		{
			joined = joined[..maxLength];
			cut = true;
		}
		return cut ? joined + "\r\n..." : joined;
	}
}
