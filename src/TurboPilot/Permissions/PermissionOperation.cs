namespace TurboPilot.Permissions;

/// <summary>
/// One pre-approvable session operation: the kind string the agent runtime
/// records an approval under, paired with the text the dialog shows.
/// </summary>
public sealed record PermissionOperation(string Kind, string Label)
{
	public override string ToString() => Label;
}

/// <summary>
/// The session operations the permissions dialog offers as toggles, in
/// display order. A checked operation runs without asking; an unchecked one
/// stops for confirmation the first time it is attempted.
///
/// These are the gate on the action itself. The folder list decides where a
/// file action is allowed to happen once the gate is open.
/// </summary>
public static class PermissionOperations
{
	public static IReadOnlyList<PermissionOperation> All { get; } =
	[
		new("write", "Write / edit files"),
		new("read", "Read files"),
		new("shell", "Execute shell commands"),
		new("mcp", "Call MCP tools"),
		new("mcp_sampling", "MCP sampling"),
		new("memory", "Access memory"),
		new("custom_tool", "Custom tools"),
		new("url", "Fetch URLs"),
		new("hook", "Invoke hooks"),
	];

	/// <summary>
	/// True when <paramref name="kind"/> is one of the known operations.
	/// Unknown kinds read from disk are kept so nothing is dropped, but they
	/// have no toggle and never match a request.
	/// </summary>
	public static bool IsKnown(string kind) =>
		All.Any(o => string.Equals(o.Kind, kind, StringComparison.OrdinalIgnoreCase));
}
