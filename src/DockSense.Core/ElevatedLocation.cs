namespace DockSense.Core;

/// <summary>What an account may do to a file or folder, reduced to what matters for <see cref="ElevatedLocation"/>.</summary>
[Flags]
public enum LocationRights
{
    None = 0,

    /// <summary>Write, create or delete: change the file, or what is in the folder.</summary>
    ChangeContents = 1,

    /// <summary>Delete or rename what is inside, change the permissions or take ownership.</summary>
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
    /// <param name="ancestors">Every folder above, up to the drive. There only taking control counts: Windows
    /// lets everyone create new folders at the root of a drive, which does not touch the folders already there.</param>
    public static bool IsProtected(LocationAccess file, LocationAccess folder, IEnumerable<LocationAccess> ancestors)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(ancestors);

        const LocationRights any = LocationRights.ChangeContents | LocationRights.TakeControl;
        return Holds(file, any) && Holds(folder, any) && ancestors.All(a => Holds(a, LocationRights.TakeControl));
    }

    /// <summary>The owner can always change the permissions, so an owner that is not trusted fails by itself.</summary>
    private static bool Holds(LocationAccess access, LocationRights forbidden) =>
        IsTrusted(access.OwnerSid) && access.Grants.All(g => IsTrusted(g.Sid) || (g.Rights & forbidden) == 0);
}
