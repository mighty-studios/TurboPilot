using System.IO;
using System.Text.Json;
using GitHub.Copilot;

namespace TurboPilot.Customizations;

/// <summary>
/// Reads MCP server definitions from *.mcp.json configuration files.
/// Servers live under a top-level "mcpServers" object (Copilot CLI) or
/// "servers" object (VS Code style); each named property is one server.
/// Malformed or unreadable files yield empty results so one bad file
/// cannot break the scan.
/// </summary>
public static class McpConfig
{
	internal static McpServerConfig ReadServerConfiguration(string filePath, string serverName)
	{
		using var document = JsonDocument.Parse(File.ReadAllText(filePath));
		if ((!TryGetObject(document.RootElement, "mcpServers", out var servers)
				&& !TryGetObject(document.RootElement, "servers", out servers))
			|| !servers.TryGetProperty(serverName, out var server)
			|| server.ValueKind != JsonValueKind.Object)
			throw new InvalidDataException($"Server '{serverName}' was not found in '{filePath}'.");

		var type = server.TryGetProperty("type", out var typeValue)
			? typeValue.GetString()?.ToLowerInvariant()
			: server.TryGetProperty("command", out _) ? "stdio" : "http";
		var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
		McpServerConfig config = type switch
		{
			"stdio" or "local" => server.Deserialize<McpStdioServerConfig>(options)
				?? throw new InvalidDataException($"Invalid server '{serverName}'."),
			"http" or "sse" => server.Deserialize<McpHttpServerConfig>(options)
				?? throw new InvalidDataException($"Invalid server '{serverName}'."),
			_ => throw new InvalidDataException($"Unsupported server type for '{serverName}' in '{filePath}'."),
		};

		if (config is McpStdioServerConfig local && string.IsNullOrWhiteSpace(local.Command))
			throw new InvalidDataException($"Server '{serverName}' needs a command.");
		if (config is McpHttpServerConfig remote
			&& (!Uri.TryCreate(remote.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
			throw new InvalidDataException($"Server '{serverName}' needs an HTTP or HTTPS URL.");
		config.Tools ??= ["*"];
		return config;
	}

	/// <summary>
	/// Returns the server names declared in a file, in document order.
	/// </summary>
	public static List<string> ReadServerNames(string filePath)
	{
		var names = new List<string>();
		TryReadServers(filePath, servers =>
		{
			foreach (var server in servers.EnumerateObject())
				names.Add(server.Name);
		});
		return names;
	}

	/// <summary>
	/// Returns display-ready key/value pairs for one server definition.
	/// Scalars are rendered as text, arrays and objects as compact JSON.
	/// The env and headers objects are reduced to their key names so
	/// secrets never reach the display. Returns null when the file or
	/// server is missing.
	/// </summary>
	public static List<KeyValuePair<string, string>>? ReadServerFields(string filePath, string serverName)
	{
		List<KeyValuePair<string, string>>? fields = null;
		TryReadServers(filePath, servers =>
		{
			if (!servers.TryGetProperty(serverName, out var server)
				|| server.ValueKind != JsonValueKind.Object)
				return;

			fields = [];
			foreach (var property in server.EnumerateObject())
				fields.Add(new KeyValuePair<string, string>(property.Name, RenderValue(property.Name, property.Value)));
		});
		return fields;
	}

	private static void TryReadServers(string filePath, Action<JsonElement> handleServers)
	{
		try
		{
			using var document = JsonDocument.Parse(File.ReadAllText(filePath));
			if (document.RootElement.ValueKind != JsonValueKind.Object)
				return;

			if (!TryGetObject(document.RootElement, "mcpServers", out var servers)
				&& !TryGetObject(document.RootElement, "servers", out servers))
				return;

			handleServers(servers);
		}
		catch (JsonException)
		{
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private static bool TryGetObject(JsonElement root, string name, out JsonElement value)
	{
		foreach (var property in root.EnumerateObject())
		{
			if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
				&& property.Value.ValueKind == JsonValueKind.Object)
			{
				value = property.Value;
				return true;
			}
		}

		value = default;
		return false;
	}

	private static string RenderValue(string propertyName, JsonElement value) => value.ValueKind switch
	{
		JsonValueKind.String => value.GetString() ?? string.Empty,
		JsonValueKind.Number => value.GetRawText(),
		JsonValueKind.True => "true",
		JsonValueKind.False => "false",
		JsonValueKind.Null => "null",
		// env and headers map names to credential values; show only the names.
		JsonValueKind.Object when IsSecretsProperty(propertyName) => string.Join(", ", value.EnumerateObject().Select(p => p.Name)),
		JsonValueKind.Object or JsonValueKind.Array => JsonSerializer.Serialize(value),
		_ => string.Empty,
	};

	private static bool IsSecretsProperty(string propertyName) =>
		propertyName.Equals("env", StringComparison.OrdinalIgnoreCase)
		|| propertyName.Equals("headers", StringComparison.OrdinalIgnoreCase);
}
