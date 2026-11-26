using DockSense.Core;

namespace DockSense.Core.Tests;

public sealed class ElevatedLocationTests
{
    private const string System = "S-1-5-18";
    private const string Administrators = "S-1-5-32-544";
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private const string Users = "S-1-5-32-545";
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string SomeUser = "S-1-5-21-1-2-3-1001";

    private const LocationRights Everything = LocationRights.ChangeContents | LocationRights.TakeControl;

    // As Windows sets them up: read measured on C:\Program Files (x86)\DockSense on 2026-11-26.
    private static readonly LocationAccess InstalledFile = new(Administrators,
        [new(System, Everything), new(Administrators, Everything), new(Users, LocationRights.None)]);

    private static readonly LocationAccess InstalledFolder = new(Administrators,
        [new(TrustedInstaller, Everything), new(System, Everything), new(Administrators, Everything), new(Users, LocationRights.None)]);

    private static readonly LocationAccess ProgramFiles = new(TrustedInstaller,
        [new(System, LocationRights.ChangeContents), new(Administrators, LocationRights.ChangeContents), new(Users, LocationRights.None), new(TrustedInstaller, Everything)]);

    // Everyone signed in may create new folders at the root of a drive.
    private static readonly LocationAccess DriveRoot = new(TrustedInstaller,
        [new(AuthenticatedUsers, LocationRights.ChangeContents), new(System, Everything), new(Administrators, Everything), new(Users, LocationRights.None)]);

    [Fact]
    public void A_copy_in_program_files_is_protected()
    {
        Assert.True(ElevatedLocation.IsProtected(InstalledFile, InstalledFolder, [ProgramFiles, DriveRoot]));
    }

    [Fact]
    public void A_file_the_user_may_write_is_not()
    {
        var file = InstalledFile with { Grants = [.. InstalledFile.Grants, new(SomeUser, LocationRights.ChangeContents)] };

        Assert.False(ElevatedLocation.IsProtected(file, InstalledFolder, [ProgramFiles, DriveRoot]));
    }

    [Fact]
    public void A_folder_the_user_may_put_files_in_is_not()
    {
        // A library next to the executable can be loaded by it.
        var folder = InstalledFolder with { Grants = [.. InstalledFolder.Grants, new(Users, LocationRights.ChangeContents)] };

        Assert.False(ElevatedLocation.IsProtected(InstalledFile, folder, [ProgramFiles, DriveRoot]));
    }

    [Fact]
    public void A_file_or_folder_the_user_owns_is_not()
    {
        // The owner can always change the permissions.
        Assert.False(ElevatedLocation.IsProtected(InstalledFile with { OwnerSid = SomeUser }, InstalledFolder, [ProgramFiles, DriveRoot]));
        Assert.False(ElevatedLocation.IsProtected(InstalledFile, InstalledFolder with { OwnerSid = SomeUser }, [ProgramFiles, DriveRoot]));
        Assert.False(ElevatedLocation.IsProtected(InstalledFile, InstalledFolder, [ProgramFiles with { OwnerSid = SomeUser }, DriveRoot]));
    }

    [Fact]
    public void A_folder_above_that_the_user_controls_is_not()
    {
        // Whoever may delete or rename what is inside can put another folder in its place.
        var above = ProgramFiles with { Grants = [.. ProgramFiles.Grants, new(Users, LocationRights.TakeControl)] };

        Assert.False(ElevatedLocation.IsProtected(InstalledFile, InstalledFolder, [above, DriveRoot]));
    }

    [Fact]
    public void Creating_new_folders_further_up_does_not_matter()
    {
        var above = ProgramFiles with { Grants = [.. ProgramFiles.Grants, new(Users, LocationRights.ChangeContents)] };

        Assert.True(ElevatedLocation.IsProtected(InstalledFile, InstalledFolder, [above, DriveRoot]));
    }

    [Fact]
    public void A_folder_in_the_users_profile_is_not_protected()
    {
        var file = new LocationAccess(SomeUser, [new(System, Everything), new(Administrators, Everything), new(SomeUser, Everything)]);

        Assert.False(ElevatedLocation.IsProtected(file, file, [file, DriveRoot]));
    }

    [Theory]
    [InlineData(System, true)]
    [InlineData(Administrators, true)]
    [InlineData(TrustedInstaller, true)]
    [InlineData(Users, false)]
    [InlineData(AuthenticatedUsers, false)]
    [InlineData(SomeUser, false)]
    [InlineData("S-1-1-0", false)] // Everyone
    public void Only_administrators_and_windows_itself_are_trusted(string sid, bool trusted)
    {
        Assert.Equal(trusted, ElevatedLocation.IsTrusted(sid));
    }
}
