using System.IO;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace TurboPilot.Customizations;

/// <summary>
/// Reads YAML headers and separates them from the markdown body.
/// </summary>
public static class FrontMatter
{
	/// <summary>An empty parse result.</summary>
	public static readonly FrontMatterData Empty = new() { Fields = [] };

	/// <summary>
	/// Reads and parses the front matter block of a file. Returns
	/// <see cref="Empty"/> when the file is missing, unreadable or has
	/// no front matter.
	/// </summary>
	public static FrontMatterData Read(string filePath)
	{
		try
		{
			var document = ReadDocument(filePath);
			return new FrontMatterData
			{
				Fields = document.Header.Children
					.Where(pair => pair.Key is YamlScalarNode && pair.Value is YamlScalarNode)
					.Select(pair => new KeyValuePair<string, string>(
						((YamlScalarNode)pair.Key).Value ?? "",
						((YamlScalarNode)pair.Value).Value ?? ""))
					.ToList(),
			};
		}
		catch (IOException)
		{
			return Empty;
		}
		catch (UnauthorizedAccessException)
		{
			return Empty;
		}
		catch (YamlException)
		{
			return Empty;
		}
	}

	internal static MarkdownDocument ReadDocument(string filePath)
	{
		var text = File.ReadAllText(filePath);
		using var reader = new StringReader(text);
		if (reader.ReadLine()?.Trim().TrimStart('\uFEFF') != "---")
			return new MarkdownDocument(text, new YamlMappingNode());

		var header = new StringBuilder();
		string? line;
		while ((line = reader.ReadLine()) is not null)
		{
			if (line.Trim() is "---" or "...")
			{
				var yaml = new YamlStream();
				yaml.Load(new StringReader(header.ToString()));
				if (yaml.Documents.Count == 0)
					return new MarkdownDocument(reader.ReadToEnd(), new YamlMappingNode());
				if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode mapping)
					throw new InvalidDataException($"The header in '{filePath}' must contain named fields.");
				return new MarkdownDocument(reader.ReadToEnd(), mapping);
			}
			header.AppendLine(line);
		}

		throw new InvalidDataException($"Missing closing header delimiter in '{filePath}'.");
	}
}

internal sealed record MarkdownDocument(string Body, YamlMappingNode Header);

/// <summary>
/// The top-level scalar pairs parsed from a front matter block, in file
/// order.
/// </summary>
public sealed class FrontMatterData
{
	public required IReadOnlyList<KeyValuePair<string, string>> Fields { get; init; }

	/// <summary>Value of a key (case-insensitive), or null when absent.</summary>
	public string? Get(string key)
	{
		foreach (var (k, v) in Fields)
		{
			if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
				return v;
		}
		return null;
	}
}
