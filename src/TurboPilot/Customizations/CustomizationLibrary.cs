using System.Text.Json.Serialization;

namespace TurboPilot.Customizations;

/// <summary>
/// The collected customization items, one map per type, keyed by the full
/// file path of the item (case-insensitive, since this is Windows).
/// </summary>
public sealed class CustomizationLibrary
{
	/// <summary>Key comparer used for all path-keyed maps and lookups.</summary>
	[JsonIgnore]
	public static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

	/// <summary>Prompt templates from prompts/*.md under each search root.</summary>
	public Dictionary<string, CustomizationItem> Prompts { get; set; } = new(PathComparer);

	/// <summary>Custom agent definitions from agents/*.md under each search root.</summary>
	public Dictionary<string, CustomizationItem> Agents { get; set; } = new(PathComparer);

	/// <summary>Skills from skills/[name]/SKILL.md under each search root.</summary>
	public Dictionary<string, CustomizationItem> Skills { get; set; } = new(PathComparer);

	/// <summary>Instructions from instructions/*.instructions.md under each root.</summary>
	public Dictionary<string, CustomizationItem> Instructions { get; set; } = new(PathComparer);
}
