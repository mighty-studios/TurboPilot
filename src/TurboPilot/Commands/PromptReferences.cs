using System.IO;
using System.Text;
using TurboPilot.Customizations;

namespace TurboPilot.Commands;

internal enum PromptReferenceKind
{
	File,
	Skill,
}

internal readonly record struct PromptReferenceSuggestion(
	PromptReferenceKind Kind,
	string Name,
	string Summary,
	string Marker);

internal sealed record PromptReferenceCompletion(
	int Start,
	int Length,
	IReadOnlyList<PromptReferenceSuggestion> Suggestions);

internal sealed record PromptReferenceResolution(
	IReadOnlyList<string> Attachments,
	string? Error)
{
	public bool Success => Error is null;
}

/// <summary>
/// Compact references embedded in a prompt. The short triggers grow into
/// explicit markers, which are validated before the message is sent.
/// </summary>
internal static class PromptReferences
{
	internal const string FilePrefix = "[file:";
	internal const string SkillPrefix = "[skill:";

	internal const string SystemInstructions =
		"## Prompt references\r\n\r\n"
		+ "User messages can contain compact reference markers.\r\n"
		+ "- `[file:NAME]` identifies the attachment on that message whose display name is `NAME`. Treat it as an explicit request to use that file.\r\n"
		+ "- `[skill:NAME]` explicitly requires applying the loaded skill named `NAME` to that request. Follow that skill's instructions before answering.\r\n"
		+ "Do not substitute another file or skill for a named reference.\r\n";

	internal static string FileMarker(string name) => Marker(FilePrefix, name);
	internal static string SkillMarker(string name) => Marker(SkillPrefix, name);

	/// <summary>
	/// Finds an unfinished reference at the caret. A short trigger is only
	/// active at a token boundary; ordinary Markdown such as [features]
	/// stops matching as soon as it diverges from the canonical prefix.
	/// </summary>
	internal static PromptReferenceCompletion? Suggest(
		string textBeforeCaret,
		IReadOnlyList<string> attachmentPaths,
		IEnumerable<CustomizationItem> skills)
	{
		if (!TryActiveToken(textBeforeCaret, out var start, out var kind, out var filter))
			return null;

		IEnumerable<PromptReferenceSuggestion> available = kind switch
		{
			PromptReferenceKind.File => attachmentPaths.Select(path =>
			{
				var name = Path.GetFileName(path);
				return new PromptReferenceSuggestion(kind, name, path, FileMarker(name));
			}),
			PromptReferenceKind.Skill => skills
				.Where(item => item.Enabled)
				.Select(item => new PromptReferenceSuggestion(
					kind, item.Name, item.FilePath, SkillMarker(item.Name))),
			_ => [],
		};

		var suggestions = available
			.Where(item => filter.Length == 0
				|| item.Name.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
			.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(item => item.Summary, StringComparer.OrdinalIgnoreCase)
			.ToArray();
		return suggestions.Length == 0
			? null
			: new PromptReferenceCompletion(start, textBeforeCaret.Length - start, suggestions);
	}

	/// <summary>
	/// Validates every complete marker and returns the files that should be
	/// attached. A customization file named by Add To Prompt is attached
	/// automatically, while a pending attachment takes precedence.
	/// </summary>
	internal static PromptReferenceResolution Resolve(
		string prompt,
		IReadOnlyList<string> attachmentPaths,
		IEnumerable<string> customizationFilePaths,
		IEnumerable<string> skillNames)
	{
		var attachments = attachmentPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var customizationFiles = customizationFilePaths
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var availableSkills = skillNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

		foreach (var reference in Parse(prompt))
		{
			if (reference.Kind == PromptReferenceKind.Skill)
			{
				if (!availableSkills.Contains(reference.Name))
					return Failure(attachments,
						$"Skill reference '{SkillMarker(reference.Name)}' is unavailable in this session.");
				continue;
			}

			var attachedMatches = attachments
				.Where(path => string.Equals(Path.GetFileName(path), reference.Name, StringComparison.OrdinalIgnoreCase))
				.ToArray();
			if (attachedMatches.Length == 1)
				continue;
			if (attachedMatches.Length > 1)
				return Failure(attachments,
					$"File reference '{FileMarker(reference.Name)}' is ambiguous because multiple attachments have that name.");

			var customizationMatches = customizationFiles
				.Where(path => string.Equals(Path.GetFileName(path), reference.Name, StringComparison.OrdinalIgnoreCase))
				.ToArray();
			if (customizationMatches.Length == 1)
			{
				attachments.Add(customizationMatches[0]);
				continue;
			}
			if (customizationMatches.Length > 1)
				return Failure(attachments,
					$"File reference '{FileMarker(reference.Name)}' is ambiguous because multiple customization files have that name.");
			return Failure(attachments,
				$"File reference '{FileMarker(reference.Name)}' is unavailable. Attach that file or remove the marker.");
		}

		return new PromptReferenceResolution(attachments, null);
	}

	private static PromptReferenceResolution Failure(IReadOnlyList<string> attachments, string error) =>
		new(attachments, error);

	private static bool TryActiveToken(
		string text,
		out int start,
		out PromptReferenceKind kind,
		out string filter)
	{
		start = text.LastIndexOf('[');
		kind = default;
		filter = "";
		if (start < 0 || (start > 0 && IsWordCharacter(text[start - 1])))
			return false;

		var token = text[start..];
		if (token.IndexOfAny([']', '\r', '\n']) >= 0)
			return false;
		if (TryPrefix(token, FilePrefix, out filter))
		{
			kind = PromptReferenceKind.File;
			return true;
		}
		if (TryPrefix(token, SkillPrefix, out filter))
		{
			kind = PromptReferenceKind.Skill;
			return true;
		}
		return false;
	}

	private static bool TryPrefix(string token, string prefix, out string filter)
	{
		filter = "";
		if (token.Length < 2)
			return false;
		if (token.Length <= prefix.Length)
			return prefix.StartsWith(token, StringComparison.OrdinalIgnoreCase);
		if (!token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			return false;
		filter = token[prefix.Length..];
		return true;
	}

	private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';

	private static string Marker(string prefix, string name) =>
		prefix + name.Replace("\\", "\\\\").Replace("]", "\\]") + "]";

	private static IEnumerable<(PromptReferenceKind Kind, string Name)> Parse(string text)
	{
		for (var index = 0; index < text.Length; index++)
		{
			var kind = StartsWith(text, index, FilePrefix)
				? PromptReferenceKind.File
				: StartsWith(text, index, SkillPrefix)
					? PromptReferenceKind.Skill
					: (PromptReferenceKind?)null;
			if (kind is null)
				continue;

			var cursor = index + (kind == PromptReferenceKind.File ? FilePrefix.Length : SkillPrefix.Length);
			var name = new StringBuilder();
			var complete = false;
			for (; cursor < text.Length; cursor++)
			{
				var character = text[cursor];
				if (character is '\r' or '\n')
					break;
				if (character == '\\' && cursor + 1 < text.Length
					&& text[cursor + 1] is '\\' or ']')
				{
					name.Append(text[++cursor]);
					continue;
				}
				if (character == ']')
				{
					complete = true;
					break;
				}
				name.Append(character);
			}

			if (complete && name.Length > 0)
			{
				yield return (kind.Value, name.ToString());
				index = cursor;
			}
		}
	}

	private static bool StartsWith(string text, int start, string value) =>
		start + value.Length <= text.Length
		&& text.AsSpan(start, value.Length).Equals(value, StringComparison.OrdinalIgnoreCase);
}
