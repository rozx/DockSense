using System.Xml.Linq;

namespace DockSense.Core;

/// <summary>What the manufacturer's firmware uses for one power source, in watts.</summary>
/// <param name="Max">The highest sustained limit the manufacturer's own software offers.</param>
/// <param name="Ceiling">
/// The highest limit the firmware itself ever works with (its short-burst limit). DockSense never sends more.
/// </param>
public sealed record TdpBounds(int Max, int Ceiling);

/// <summary>
/// A device whose power limits DockSense knows. The numbers are the manufacturer's ranges as the Linux
/// <c>asus-armoury</c> driver lists them per board (<c>drivers/platform/x86/asus-armoury.h</c>).
/// </summary>
/// <param name="Board">The start of the baseboard product name, e.g. "RC74XA".</param>
/// <param name="MinWatts">The lowest sustained limit the manufacturer allows.</param>
public sealed record TdpDevice(string Board, int MinWatts, TdpBounds PluggedIn, TdpBounds OnBattery)
{
    private static readonly TdpDevice[] Known =
    [
        // Longer names first: a board is matched by the start of its name.
        new("RC74XA", 7, new TdpBounds(35, 55), new TdpBounds(35, 55)), // ROG Xbox Ally X (measured on this one)
        new("RC73XA", 7, new TdpBounds(35, 55), new TdpBounds(35, 55)), // ROG Xbox Ally X
        new("RC72", 7, new TdpBounds(30, 53), new TdpBounds(25, 35)),   // ROG Ally X
        new("RC71", 7, new TdpBounds(30, 53), new TdpBounds(25, 35)),   // ROG Ally
    ];

    /// <summary>The highest value that can be chosen. On battery less may be sent (<see cref="Clamp"/>).</summary>
    public int CeilingWatts => Math.Max(PluggedIn.Ceiling, OnBattery.Ceiling);

    /// <summary>Values above this exceed what the manufacturer allows on at least one power source.</summary>
    public int WarnAboveWatts => Math.Min(PluggedIn.Max, OnBattery.Max);

    public TdpBounds For(bool onAcPower) => onAcPower ? PluggedIn : OnBattery;

    /// <summary>True for a value DockSense is willing to send on this device.</summary>
    public bool Offers(int watts) => watts >= MinWatts && watts <= CeilingWatts;

    public bool ExceedsManufacturerMax(int watts) => watts > WarnAboveWatts;

    /// <summary>
    /// True when <paramref name="watts"/> may be sent without asking: it is within the manufacturer's maximum, or
    /// the user confirmed that much on this very board. A confirmation given on another board does not count,
    /// and neither does a value that merely arrived with a copied settings file.
    /// </summary>
    /// <param name="confirmedBoard">The board the user last confirmed a value above the maximum on, if any.</param>
    /// <param name="confirmedWatts">The highest value confirmed there.</param>
    public bool IsConfirmed(int watts, string? confirmedBoard, int confirmedWatts) =>
        !ExceedsManufacturerMax(watts)
        || (string.Equals(confirmedBoard, Board, StringComparison.OrdinalIgnoreCase) && watts <= confirmedWatts);

    /// <summary>The value to send for <paramref name="watts"/> on the given power source.</summary>
    public int Clamp(int watts, bool onAcPower) => Math.Clamp(watts, MinWatts, For(onAcPower).Ceiling);

    /// <summary>The device for a baseboard product name, or null when DockSense does not know its limits.</summary>
    public static TdpDevice? Find(string? board)
    {
        var name = board?.Trim();
        return string.IsNullOrEmpty(name)
            ? null
            : Known.FirstOrDefault(d => name.StartsWith(d.Board, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>The registers of one SMU mailbox (message, response, first argument).</summary>
public sealed record SmuMailbox(uint Message, uint Response, uint Arguments);

/// <summary>
/// How to set the package power limits of an AMD mobile processor through its System Management Unit: the mailbox
/// and the command numbers, as RyzenAdj and Universal x86 Tuning Utility use them. The argument is in milliwatts.
/// </summary>
/// <param name="Sustained">The sustained limit ("STAPM limit").</param>
/// <param name="Fast">The short-burst limit ("fast PPT").</param>
/// <param name="Slow">The medium-term limit ("slow PPT").</param>
public sealed record AmdSmuPowerCommands(SmuMailbox Mailbox, uint Sustained, uint Fast, uint Slow)
{
    /// <summary>The SMU's test message: it answers with its argument plus one. Sets nothing.</summary>
    public const uint TestMessage = 0x1;

    /// <summary>What the SMU puts in the response register for a command it carried out.</summary>
    public const uint Ok = 0x1;

    private static readonly AmdSmuPowerCommands StrixPoint = new(new SmuMailbox(0x3B10928, 0x3B10978, 0x3B10998), 0x14, 0x15, 0x16);
    private static readonly AmdSmuPowerCommands Phoenix = new(new SmuMailbox(0x3B10528, 0x3B10578, 0x3B10998), 0x14, 0x15, 0x16);

    /// <summary>
    /// The commands for a processor by its CPUID family and model, or null for one DockSense does not know.
    /// Strix Point (Ryzen AI Z2 Extreme, 1Ah/24h) is measured; Phoenix and Hawk Point (Z1 series) are taken from
    /// the two projects above.
    /// </summary>
    public static AmdSmuPowerCommands? For(int family, int model) => (family, model >> 4) switch
    {
        (0x1A, 0x2) => StrixPoint,
        (0x19, 0x7) => Phoenix,
        _ => null,
    };

    /// <summary>Family and model from CPUID leaf 1 (EAX), with the extended fields applied.</summary>
    public static (int Family, int Model) Identify(int cpuidEax)
    {
        var baseFamily = (cpuidEax >> 8) & 0xF;
        var family = baseFamily == 0xF ? baseFamily + ((cpuidEax >> 20) & 0xFF) : baseFamily;
        var model = (cpuidEax >> 4) & 0xF;
        if (baseFamily is 0x6 or 0xF)
        {
            model |= ((cpuidEax >> 16) & 0xF) << 4;
        }

        return (family, model);
    }
}

/// <summary>How a TDP write by the elevated helper ended. The values are the helper's exit codes.</summary>
public enum TdpWriteResult
{
    Ok = 0,

    /// <summary>The helper was not given a single whole number of watts.</summary>
    BadArguments = 1,

    /// <summary>Not a device and processor DockSense knows, or a value outside the device's range.</summary>
    Unsupported = 2,

    /// <summary>The helper did not run with administrator rights.</summary>
    NotElevated = 3,

    /// <summary>The PawnIO driver is not installed or not running.</summary>
    DriverMissing = 4,

    /// <summary>PawnIO refused the SMU module.</summary>
    ModuleRejected = 5,

    /// <summary>The SMU did not answer its test message as expected; nothing was set.</summary>
    NoAnswer = 6,

    /// <summary>The SMU refused the first limit; nothing was changed.</summary>
    Rejected = 7,

    /// <summary>Anything else, including a helper that could not be started or did not finish.</summary>
    Failed = 8,

    /// <summary>
    /// The mutex hardware tools share for the SMU could not be taken: another tool held it too long, or created
    /// it in a way the helper cannot open. Nothing was set.
    /// </summary>
    Busy = 9,

    /// <summary>
    /// The SMU took one limit and then refused another, or a limit was sent and no answer came back: the
    /// processor may be left with a mix of the new value and the old ones, which is neither what was asked for
    /// nor what the vendor software set.
    /// </summary>
    PartlyApplied = 10,

    /// <summary>
    /// The helper was started but did not report back in time. It may still set the value, so the value has to
    /// be treated as possibly in force. Never an exit code: DockSense concludes this by itself.
    /// </summary>
    Unconfirmed = 11,
}

/// <summary>How a TDP write ended and, when it went through, the watts the helper sent.</summary>
public readonly record struct TdpWriteOutcome(TdpWriteResult Result, int? SentWatts = null)
{
    public static implicit operator TdpWriteOutcome(TdpWriteResult result) => new(result);
}

/// <summary>Why a TDP override cannot be written right now, if anything is in the way.</summary>
public enum TdpReadiness
{
    Ready,

    /// <summary>The PawnIO driver is not installed.</summary>
    DriverMissing,

    /// <summary>DockSense runs from a folder that can be changed without administrator rights.</summary>
    UnprotectedFolder,

    /// <summary>The elevated helper task is not registered for this copy of DockSense.</summary>
    HelperMissing,
}

/// <summary>What DockSense remembers about the TDP it set, across its own restarts.</summary>
/// <param name="Boot">The boot session it was set in: a restart of the PC clears every limit.</param>
/// <param name="Watts">The value last sent.</param>
/// <param name="Pending">
/// True when the value may be in force without that being known: in the file while it is on its way, and after
/// a write whose outcome stayed open (the helper did not report back, or only part of it went in). It still
/// counts as in force: giving back a TDP that was never set does no harm, leaving one behind that nobody knows
/// of does.
/// </param>
public sealed record TdpState(BootSession Boot, int Watts, bool Pending = false);

/// <summary>
/// The elevated helper task: a Task Scheduler task without triggers that runs DockSense with the highest
/// privileges to set the TDP. Registering it takes administrator approval once; afterwards the signed-in user can
/// start it on demand, without a prompt. The watt value is the task's only parameter and the helper checks it
/// against its own limits, so starting the task can do no more than choose a TDP within the device's range.
/// </summary>
public static class TdpHelperTask
{
    /// <summary>The helper's command line switch, followed by the watts.</summary>
    public const string Argument = "--set-tdp";

    /// <summary>The task's arguments: Task Scheduler puts the value given at start in place of <c>$(Arg0)</c>.</summary>
    public const string Arguments = Argument + " $(Arg0)";

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>The task name. Task names are shared by all users of the PC, so it carries the user's SID.</summary>
    public static string NameFor(string userSid) => $"DockSense-TDP-{userSid}";

    /// <summary>
    /// Who may do what with the task: administrators and the system everything, the user read and run only.
    /// Nobody without administrator rights can change what the task starts.
    /// </summary>
    public static string SecurityDescriptorFor(string userSid) => $"D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGX;;;{userSid})";

    /// <summary>The task definition as Task Scheduler XML.</summary>
    /// <param name="executable">Full path of DockSense.exe, unquoted.</param>
    /// <param name="userSid">The SID of the user who starts the task.</param>
    public static string BuildXml(string executable, string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);

        var task = new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo",
                new XElement(Ns + "Author", "DockSense"),
                new XElement(Ns + "Description",
                    "Sets the processor's power limit (TDP) for DockSense's mode profiles. DockSense starts this task "
                    + "when a mode with a TDP override begins. Remove it under Settings in DockSense.")),
            new XElement(Ns + "Triggers"),
            new XElement(Ns + "Principals",
                new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(Ns + "UserId", userSid),
                    new XElement(Ns + "LogonType", "InteractiveToken"),
                    new XElement(Ns + "RunLevel", "HighestAvailable"))),
            new XElement(Ns + "Settings",
                // One write after the other, each with the value it was started with.
                new XElement(Ns + "MultipleInstancesPolicy", "Queue"),
                new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                new XElement(Ns + "AllowHardTerminate", "true"),
                new XElement(Ns + "StartWhenAvailable", "false"),
                new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                new XElement(Ns + "IdleSettings",
                    new XElement(Ns + "StopOnIdleEnd", "false"),
                    new XElement(Ns + "RestartOnIdle", "false")),
                new XElement(Ns + "AllowStartOnDemand", "true"),
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "Hidden", "false"),
                new XElement(Ns + "RunOnlyIfIdle", "false"),
                new XElement(Ns + "WakeToRun", "false"),
                new XElement(Ns + "ExecutionTimeLimit", "PT1M"),
                new XElement(Ns + "Priority", "4")),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(Ns + "Exec",
                    new XElement(Ns + "Command", $"\"{executable}\""),
                    new XElement(Ns + "Arguments", Arguments))));

        return task.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>True when the registered task is enabled and starts this executable as the helper.</summary>
    public static bool Starts(LogonTaskInfo? task, string executable) =>
        task is { Enabled: true } && LogonTaskDefinition.Starts(task, executable, Arguments);

    /// <summary>
    /// True when the task, as Task Scheduler XML, is set to run with the highest privileges. Without that the
    /// helper could not open the driver, however complete the rest of the task is.
    /// </summary>
    public static bool RunsElevated(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return false;
        }

        try
        {
            var level = XDocument.Parse(xml).Root?.Element(Ns + "Principals")?.Element(Ns + "Principal")?.Element(Ns + "RunLevel")?.Value;
            return string.Equals(level?.Trim(), "HighestAvailable", StringComparison.OrdinalIgnoreCase);
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// The helper's exit code after a write that went through: 100 plus the watts it sent. The value is clamped
    /// for the power source at the moment of sending, which only the helper knows for certain, and the exit code
    /// is the one thing it hands back.
    /// </summary>
    public static int ExitCodeForSent(int watts) => SentBase + watts;

    /// <summary>
    /// The outcome an exit code of the helper stands for, after it was asked for <paramref name="requestedWatts"/>.
    /// The helper only ever clamps downwards, so it cannot have sent more than it was asked for; such a code, like
    /// any other it does not use, is unconfirmed.
    /// </summary>
    public static TdpWriteOutcome OutcomeOf(int exitCode, int requestedWatts) => exitCode switch
    {
        > SentBase when exitCode - SentBase <= requestedWatts => new TdpWriteOutcome(TdpWriteResult.Ok, exitCode - SentBase),
        >= SentBase => new TdpWriteOutcome(TdpWriteResult.Unconfirmed),
        (int)TdpWriteResult.Ok => new TdpWriteOutcome(TdpWriteResult.Unconfirmed), // "done" without saying what was sent
        _ when Enum.IsDefined(typeof(TdpWriteResult), exitCode) => new TdpWriteOutcome((TdpWriteResult)exitCode),
        _ => new TdpWriteOutcome(TdpWriteResult.Unconfirmed),
    };

    private const int SentBase = 100;

    /// <summary>The watts from the helper's command line (<c>--set-tdp 20</c>), or null when it is anything else.</summary>
    public static int? ParseWatts(IReadOnlyList<string> args) =>
        args.Count == 2
        && string.Equals(args[0], Argument, StringComparison.OrdinalIgnoreCase)
        && args[1].Length is > 0 and <= 3
        && args[1].All(char.IsAsciiDigit)
            ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture)
            : null;
}

/// <summary>What to do about the TDP when a mode's profile is applied.</summary>
public enum TdpAction
{
    /// <summary>Nothing to do.</summary>
    None,

    /// <summary>Send the profile's value.</summary>
    Set,

    /// <summary>The mode has no override but DockSense's limit is still in force: give control back.</summary>
    HandBack,
}

public static class TdpPlanner
{
    /// <summary>What applying <paramref name="profile"/> means for the TDP.</summary>
    /// <param name="device">The device, or null when it is not supported.</param>
    /// <param name="limitInForce">True while a limit DockSense set is still in effect (same boot).</param>
    /// <param name="confirmedBoard">See <see cref="TdpDevice.IsConfirmed"/>.</param>
    /// <param name="confirmedWatts">See <see cref="TdpDevice.IsConfirmed"/>.</param>
    public static TdpAction Plan(ModeProfile profile, TdpDevice? device, bool limitInForce, string? confirmedBoard = null, int confirmedWatts = 0)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (device is null)
        {
            return TdpAction.None;
        }

        // A value above the maximum that nobody confirmed on this device is treated like no value at all.
        if (profile.TdpWatts is { } watts && device.Offers(watts) && device.IsConfirmed(watts, confirmedBoard, confirmedWatts))
        {
            return TdpAction.Set;
        }

        return limitInForce ? TdpAction.HandBack : TdpAction.None;
    }

    /// <summary>
    /// The ASUS performance mode value (ACPI device <c>0x00120075</c>) for the mode Armoury Crate has selected, so
    /// re-sending it makes the firmware load that mode's own limits again. Armoury Crate numbers its modes
    /// 0 Windows, 1 Silent, 2 Performance, 3 Turbo, 4 Manual; the firmware 0 Performance, 1 Turbo, 2 Silent.
    /// Without a usable selection: Turbo when plugged in and Performance on battery, the manufacturer's defaults.
    /// </summary>
    public static uint AsusModeToRestore(int? armouryCrateMode, bool onAcPower) => armouryCrateMode switch
    {
        1 => 2,
        2 => 0,
        3 => 1,
        _ => onAcPower ? 1u : 0u,
    };
}
