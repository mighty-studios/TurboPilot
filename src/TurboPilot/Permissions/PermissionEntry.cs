namespace TurboPilot.Permissions;

/// <summary>
/// The kind of file access a permission entry grants.
/// </summary>
public enum PermissionAccess
{
	/// <summary>Files may be read but not created or modified.</summary>
	Read,

	/// <summary>Files may be created or modified but not read.</summary>
	Write,

	/// <summary>Files may be read, created and modified.</summary>
	ReadWrite,
}

/// <summary>
/// One entry in a permission list: a folder pattern outside the workspace
/// and the file access granted there.
/// </summary>
public sealed class PermissionEntry
{
	/// <summary>
	/// Folder path, optionally using wildcards. A trailing ellipsis
	/// (C:\MyFiles\...) extends the entry to every folder below it.
	/// </summary>
	public string FolderPath { get; set; } = string.Empty;

	/// <summary>
	/// Access granted at <see cref="FolderPath"/>.
	/// </summary>
	public PermissionAccess Access { get; set; } = PermissionAccess.Read;

	public PermissionEntry()
	{
	}

	public PermissionEntry(string folderPath, PermissionAccess access)
	{
		FolderPath = folderPath;
		Access = access;
	}

	/// <summary>
	/// Copy, so a dialog can edit a working list without touching the store.
	/// </summary>
	public PermissionEntry Clone() => new(FolderPath, Access);
}

/// <summary>
/// Extension of one access level over another. Read/Write covers both a
/// read and a write request; the single-purpose levels cover only
/// themselves.
/// </summary>
public static class PermissionAccessExtensions
{
	public static bool Covers(this PermissionAccess granted, PermissionAccess requested) =>
		granted == PermissionAccess.ReadWrite || granted == requested;
}

/// <summary>
/// A selectable access level: the enum value paired with the text the
/// drop-down shows for it.
/// </summary>
public sealed record PermissionOption(PermissionAccess Access, string Label)
{
	public override string ToString() => Label;
}

/// <summary>
/// The access levels offered by the permissions dialog's drop-downs.
/// </summary>
public static class PermissionOptions
{
	public static PermissionOption[] All { get; } =
	[
		new(PermissionAccess.Read, "Read"),
		new(PermissionAccess.Write, "Write"),
		new(PermissionAccess.ReadWrite, "Read/Write"),
	];
}
