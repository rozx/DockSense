using System.Runtime.Intrinsics.X86;
using DockSense.Core;
using Microsoft.Win32;

namespace DockSense.App.Platform;

/// <summary>
/// Sets the processor's power limits (TDP) through the SMU, the way RyzenAdj and Universal x86 Tuning Utility do,
/// with the PawnIO driver for hardware access. This is the elevated half: it runs as
/// <c>DockSense.exe --set-tdp &lt;watts&gt;</c>, started by the helper task (<see cref="TdpHelper"/>).
/// <para>
/// Why not the ASUS interface: on the ROG Xbox Ally X (RC74XA, BIOS 303) the firmware accepts the three limits
/// through ATKACPI (<c>0x001200A3</c>, <c>A0</c>, <c>C1</c>) and ignores them in every performance mode. Sent to the
/// SMU, 20 W held the package at exactly 20 W under full load with Armoury Crate running.
/// </para>
/// <para>
/// Anyone signed in as the user can start the task with any number, so nothing here trusts its input: the device
/// and processor must be known ones, and the value is clamped to what the device's own firmware works with. It
/// shows no UI and writes no files (it runs elevated, and the user's folders are not a safe place to write then).
/// </para>
/// </summary>
internal static class TdpWriter
{
    private const string ModuleResource = "DockSense.RyzenSMU.bin";
    private const string ReadRegister = "ioctl_read_smu_register";
    private const string WriteRegister = "ioctl_write_smu_register";

    /// <summary>The mutex hardware tools share for SMU and PCI config access (HWiNFO, RyzenAdj, UXTU and others).</summary>
    private const string PciMutexName = @"Global\Access_PCI";

    private static readonly TimeSpan MutexWait = TimeSpan.FromSeconds(2);

    private const int PollLimit = 8192;
    private const int MailboxArguments = 6;
    private const uint TestArgument = 0x47;

    /// <summary>The device DockSense runs on, or null when its limits are not known.</summary>
    public static TdpDevice? Device { get; } = TdpDevice.Find(BoardName);

    /// <summary>How to reach this processor's SMU, or null for a processor DockSense has no commands for.</summary>
    public static AmdSmuPowerCommands? Commands { get; } = ReadCommands();

    /// <summary>The baseboard product name from the firmware tables Windows keeps in the registry (e.g. "RC74XA").</summary>
    public static string? BoardName
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                return key?.GetValue("BaseBoardProduct") as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }
    }

    /// <summary>"AuthenticAMD family 1Ah model 24h", for the log and the diagnostic report.</summary>
    public static string DescribeProcessor()
    {
        if (!X86Base.IsSupported)
        {
            return "not an x86 processor";
        }

        var (family, model) = AmdSmuPowerCommands.Identify(X86Base.CpuId(1, 0).Eax);
        return $"{CpuVendor()} family {family:X}h model {model:X}h";
    }

    /// <summary>The helper's entry point. The exit code is a <see cref="TdpWriteResult"/>.</summary>
    public static int RunFromCommandLine(string[] args)
    {
        try
        {
            return (int)Run(args);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (int)TdpWriteResult.Failed;
        }
    }

    private static TdpWriteResult Run(string[] args)
    {
        if (TdpHelperTask.ParseWatts(args) is not { } watts)
        {
            return TdpWriteResult.BadArguments;
        }

        if (Device is not { } device || Commands is not { } commands || !device.Offers(watts))
        {
            return TdpWriteResult.Unsupported;
        }

        using var stream = typeof(TdpWriter).Assembly.GetManifestResourceStream(ModuleResource);
        if (stream is null)
        {
            return TdpWriteResult.Failed;
        }

        var module = new byte[stream.Length];
        stream.ReadExactly(module);

        using var driver = PawnIo.Open(out var error);
        if (driver is null)
        {
            return error == PawnIo.ErrorAccessDenied ? TdpWriteResult.NotElevated : TdpWriteResult.DriverMissing;
        }

        if (!driver.Load(module))
        {
            return TdpWriteResult.ModuleRejected;
        }

        // Without the mutex another tool's request and this one could overwrite each other's arguments and take
        // each other's answers. So no mutex, no write: neither when it stays taken nor when it cannot be opened.
        using var mutex = OpenPciMutex();
        if (!Acquire(mutex))
        {
            return TdpWriteResult.Busy;
        }

        try
        {
            // The test message sets nothing. A wrong answer means these are not the registers of this processor.
            if (Send(driver, commands.Mailbox, AmdSmuPowerCommands.TestMessage, TestArgument)
                is not { Submitted: true, Status: AmdSmuPowerCommands.Ok, Value: TestArgument + 1 })
            {
                return TdpWriteResult.NoAnswer;
            }

            // On battery the firmware's own burst limit can be lower; never send more than that. Worked out
            // only now, right before sending: the charger may have been unplugged while the mutex was waited for.
            var milliwatts = checked((uint)device.Clamp(watts, DeviceProbe.OnAcPower) * 1000);

            // Whether anything was changed before a refusal decides what DockSense has to put right afterwards.
            var applied = 0;
            foreach (var command in new[] { commands.Sustained, commands.Fast, commands.Slow })
            {
                var reply = Send(driver, commands.Mailbox, command, milliwatts);
                if (reply is { Submitted: true, Status: AmdSmuPowerCommands.Ok })
                {
                    applied++;
                    continue;
                }

                // A limit may be in force when an earlier one went in, or when this one was handed to the SMU and
                // no answer came back. Otherwise nothing was changed: the SMU refused the first limit, or the
                // request never got as far as the SMU.
                if (applied > 0 || (reply.Submitted && reply.Status == 0))
                {
                    return TdpWriteResult.PartlyApplied;
                }

                return reply.Submitted ? TdpWriteResult.Rejected : TdpWriteResult.Failed;
            }

            return TdpWriteResult.Ok;
        }
        finally
        {
            mutex!.ReleaseMutex();
        }
    }

    /// <summary>How one SMU request ended.</summary>
    /// <param name="Submitted">
    /// False when the request never got as far as the SMU (the mailbox was not idle, or the driver refused a
    /// register write before the command), so nothing can have changed.
    /// </param>
    /// <param name="Status">The SMU's answer (1 = done; other values refuse); 0 when it was asked and no answer was read.</param>
    /// <param name="Value">The first argument register after a request that was carried out.</param>
    private readonly record struct SmuReply(bool Submitted, uint Status, uint Value);

    /// <summary>
    /// One SMU request: wait until the mailbox is idle, write the arguments, clear the response, write the
    /// command, wait for the response. The response is cleared only once the arguments are in place, so a failure
    /// before the command leaves the mailbox idle, as it was found, for whoever uses it next.
    /// </summary>
    private static SmuReply Send(PawnIo driver, SmuMailbox mailbox, uint command, uint argument)
    {
        if (!WaitForResponse(driver, mailbox.Response, out _))
        {
            return default;
        }

        for (var i = 0; i < MailboxArguments; i++)
        {
            if (!Write(driver, mailbox.Arguments + (uint)(i * sizeof(uint)), i == 0 ? argument : 0))
            {
                return default;
            }
        }

        if (!Write(driver, mailbox.Response, 0))
        {
            return default;
        }

        // From the command write on, the SMU may have taken the request even when nothing is heard back.
        if (!Write(driver, mailbox.Message, command) || !WaitForResponse(driver, mailbox.Response, out var status))
        {
            return new SmuReply(Submitted: true, Status: 0, Value: 0);
        }

        var value = status == AmdSmuPowerCommands.Ok && Read(driver, mailbox.Arguments, out var read) ? read : 0;
        return new SmuReply(Submitted: true, status, value);
    }

    private static bool WaitForResponse(PawnIo driver, uint register, out uint value)
    {
        value = 0;
        for (var i = 0; i < PollLimit; i++)
        {
            if (Read(driver, register, out value) && value != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Read(PawnIo driver, uint register, out uint value)
    {
        Span<long> output = stackalloc long[1];
        var ok = driver.Execute(ReadRegister, [register], output);
        value = unchecked((uint)output[0]);
        return ok;
    }

    private static bool Write(PawnIo driver, uint register, uint value) =>
        driver.Execute(WriteRegister, [register, value], []);

    private static Mutex? OpenPciMutex()
    {
        try
        {
            return new Mutex(initiallyOwned: false, PciMutexName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return null; // Another tool created it in a way this process cannot open: the write is not made.
        }
    }

    private static bool Acquire(Mutex? mutex)
    {
        try
        {
            return mutex?.WaitOne(MutexWait) == true;
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private static AmdSmuPowerCommands? ReadCommands()
    {
        if (!X86Base.IsSupported || CpuVendor() != "AuthenticAMD")
        {
            return null;
        }

        var (family, model) = AmdSmuPowerCommands.Identify(X86Base.CpuId(1, 0).Eax);
        return AmdSmuPowerCommands.For(family, model);
    }

    private static string CpuVendor()
    {
        var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
        Span<byte> vendor = stackalloc byte[12];
        BitConverter.TryWriteBytes(vendor, ebx);
        BitConverter.TryWriteBytes(vendor[4..], edx);
        BitConverter.TryWriteBytes(vendor[8..], ecx);
        return System.Text.Encoding.ASCII.GetString(vendor);
    }
}
