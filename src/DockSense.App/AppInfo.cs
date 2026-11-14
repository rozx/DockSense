using System.Reflection;
using System.Runtime.InteropServices;
using DockSense.Core;

namespace DockSense.App;

/// <summary>What this build is: its release version and flavor.</summary>
internal static class AppInfo
{
    /// <summary>The release version, three parts (0.2 is 0.2.0).</summary>
    public static Version Version { get; } = ReleaseVersion.Parse(
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? new Version(0, 0, 0);

    /// <summary>The version as the release tags write it, e.g. "0.2" or "0.2.1".</summary>
    public static string VersionText => ReleaseVersion.Format(Version);

    /// <summary>The flavor of the release zip this build came from: "x64" or "arm64".</summary>
    public static string Architecture => RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
}
