using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TurboPilot.Permissions;
using TurboPilot.Storage;

namespace TurboPilot.Mediation;

public sealed class MediationStore
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		RespectNullableAnnotations = true,
	};
	public string DirectoryPath { get; }
	public string StatePath => Path.Combine(DirectoryPath, "state.json");
	public string WorklogPath => Path.Combine(DirectoryPath, "worklog.md");

	public MediationStore(string sessionId, string? workspace, string? root = null)
	{
		if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 160 || sessionId is "." or ".."
			|| !sessionId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
			throw new ArgumentException("Invalid session ID.", nameof(sessionId));
		var key = string.IsNullOrWhiteSpace(workspace) ? "no-workspace"
			: Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PermissionService.MakeKey(workspace))))[..24].ToLowerInvariant();
		root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurboPilot", "workspaces");
		DirectoryPath = Path.Combine(root, key, "sessions", sessionId);
	}

	public MediationState Load(string sessionId, string? workspace)
	{
		if (!File.Exists(StatePath))
			return new MediationState { SessionId = sessionId, Workspace = workspace };
		var state = JsonSerializer.Deserialize<MediationState>(File.ReadAllText(StatePath), JsonOptions)
			?? throw new InvalidDataException("The Mediator worklog state is empty.");
		if (state.SessionId != sessionId || !SameWorkspace(state.Workspace, workspace)
			|| state.Entries is null || state.Entries.Where((entry, index) => entry is null || entry.Sequence != index + 1).Any())
			throw new InvalidDataException("The Mediator worklog does not match this session.");
		return state;
	}

	public void Save(MediationState state)
	{
		AtomicFile.Write(StatePath, stream => JsonSerializer.Serialize(stream, state, JsonOptions));
		var text = new StringBuilder("# Session Worklog\r\n\r\n");
		text.AppendLine($"Workspace: {state.Workspace}");
		text.AppendLine($"Session: {state.SessionId}");
		foreach (var entry in state.Entries)
		{
			text.Append("\r\n## ").Append(entry.Sequence).Append(". ").Append(entry.Role);
			if (entry.Interrupted) text.Append(" (interrupted)");
			text.Append("\r\n\r\n").Append(entry.Content).Append("\r\n");
		}
		AtomicFile.Write(WorklogPath, stream =>
		{
			using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
			writer.Write(text.ToString().ReplaceLineEndings("\r\n"));
		});
	}

	public static bool SameWorkspace(string? first, string? second) =>
		!string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second)
			? PermissionService.MakeKey(first) == PermissionService.MakeKey(second)
			: string.IsNullOrWhiteSpace(first) && string.IsNullOrWhiteSpace(second);
}
