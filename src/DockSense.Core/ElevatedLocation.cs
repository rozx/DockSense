namespace DockSense.Core;

/// <summary>What an account may do to a file or folder, reduced to what matters for <see cref="ElevatedLocation"/>.</summary>
[Flags]
public enum LocationRights
{
    None = 0,

    /// <summary>Write to the file, or create files and folders in the folder.</summary>
    ChangeContents = 1,

    /// <summary>
    /// Delete or rename the object itself or what is inside it, change the permissions or take ownership: enough
    /// to put something else in its place.
    /// </summary>
    TakeControl = 2,
}

/// <summary>One "allow" entry that applies to the object itself (not one that is only inherited by its children).</summary>
public sealed record LocationGrant(string Sid, LocationRights Rights);

/// <summary>Who owns a file or folder and who is allowed to change it.</summary>
public sealed record LocationAccess(string OwnerSid, IReadOnlyList<LocationGrant> Grants);

/// <summary>
/// Whether an executable is safe to run with administrator rights from a task anyone signed in can start: nobody
/// but administrators and Windows itself may be able to replace it. The helper task of the TDP override runs
/// DockSense.exe elevated, so from anywhere else a program without administrator rights could put its own code
/// in that place and have it run elevated, without a prompt.
/// </summary>
public static class ElevatedLocation
{
    private const string System = "S-1-5-18";
    private const string Administrators = "S-1-5-32-544";
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    // File system access mask bits (FILE_WRITE_DATA / FILE_ADD_FILE, FILE_APPEND_DATA / FILE_ADD_SUBDIRECTORY,
    // FILE_DELETE_CHILD, DELETE, WRITE_DAC, WRITE_OWNER, and the generic bits an unmapped entry can carry).
    private const int WriteData = 0x2;
    private const int AppendData = 0x4;
    private const int DeleteChild = 0x40;
    private const int Delete = 0x10000;
    private const int WriteDac = 0x40000;
    private const int WriteOwner = 0x80000;
    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;

    /// <summary>
    /// What a file system access mask allows, in the terms that matter here. Deleting an object also allows
    /// renaming it, which is all it takes to put another folder in the place of one further up the path, so it
    /// counts as taking control, not as changing contents.
    /// </summary>
    public static LocationRights RightsFrom(int accessMask) =>
        ((accessMask & (WriteData | AppendData | GenericWrite | GenericAll)) != 0 ? LocationRights.ChangeContents : LocationRights.None)
        | ((accessMask & (Delete | DeleteChild | WriteDac | WriteOwner | GenericAll)) != 0 ? LocationRights.TakeControl : LocationRights.None);

    /// <summary>True for the accounts that have administrator rights anyway.</summary>
    public static bool IsTrusted(string sid) =>
        string.Equals(sid, System, StringComparison.OrdinalIgnoreCase)
        || string.Equals(sid, Administrators, StringComparison.OrdinalIgnoreCase)
        || string.Equals(sid, TrustedInstaller, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when only trusted accounts can replace the file.
    /// </summary>
    /// <param name="file">The executable.</param>
    /// <param name="folder">The folder it is in. Creating a file there counts too: a library put next to the
    /// executable can be loaded by it.</param>
    /// <param name="ancestors">
    /// Every folder above, nearest first, up to the drive.
    /// <para>
    /// The folder directly above must not let anyone else create folders either: the task keeps the path, and
    /// once the installation is moved or removed, whoever can create a folder there could bring that path back
    /// with a file of their own. Directly under Program Files is fine; directly under the root of a drive,
    /// where Windows lets everyone create folders, is not.
    /// </para>
    /// <para>
    /// Further up only taking control counts, which includes deleting or renaming that folder itself. Creating
    /// new folders there does not touch the ones already in the path.
    /// </para>
    /// </param>
    public static bool IsProtected(LocationAccess file, LocationAccess folder, IEnumerable<LocationAccess> ancestors)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(ancestors);

        const LocationRights any = LocationRights.ChangeContents | LocationRights.TakeControl;
        return Holds(file, any)
            && Holds(folder, any)
            && ancestors.Select((access, index) => Holds(access, index == 0 ? any : LocationRights.TakeControl)).All(holds => holds);
    }

    /// <summary>The owner can always change the permissions, so an owner that is not trusted fails by itself.</summary>
    private static bool Holds(LocationAccess access, LocationRights forbidden) =>
        IsTrusted(access.OwnerSid) && access.Grants.All(g => IsTrusted(g.Sid) || (g.Rights & forbidden) == 0);
}
