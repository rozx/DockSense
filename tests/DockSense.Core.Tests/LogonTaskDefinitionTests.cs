using System.Xml.Linq;
using DockSense.Core;

namespace DockSense.Core.Tests;

public sealed class LogonTaskDefinitionTests
{
    private const string Exe = @"C:\Program Files (x86)\DockSense\DockSense.exe";
    private const string Arguments = "--autostart --logon-task";
    private const string User = @"ALLY\rozx";
    private const string Sid = "S-1-5-21-2015448536-3398815750-3657890105-1001";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static XElement Build(string exe = Exe) => XElement.Parse(LogonTaskDefinition.BuildXml(exe, Arguments, User, Sid));

    [Fact]
    public void The_name_carries_the_users_sid()
    {
        Assert.Equal("DockSense-" + Sid, LogonTaskDefinition.NameFor(Sid));
    }

    [Fact]
    public void The_task_runs_at_this_users_sign_in_without_elevation()
    {
        var task = Build();

        var trigger = Assert.Single(task.Element(Ns + "Triggers")!.Elements());
        Assert.Equal(Ns + "LogonTrigger", trigger.Name);
        Assert.Equal(User, trigger.Element(Ns + "UserId")!.Value);
        Assert.Null(trigger.Element(Ns + "Delay"));

        var principal = task.Element(Ns + "Principals")!.Element(Ns + "Principal")!;
        Assert.Equal(Sid, principal.Element(Ns + "UserId")!.Value);
        Assert.Equal("InteractiveToken", principal.Element(Ns + "LogonType")!.Value);
        Assert.Equal("LeastPrivilege", principal.Element(Ns + "RunLevel")!.Value);
    }

    [Fact]
    public void The_task_starts_on_battery_and_is_never_ended_or_slowed_down()
    {
        var settings = Build().Element(Ns + "Settings")!;

        Assert.Equal("false", settings.Element(Ns + "DisallowStartIfOnBatteries")!.Value);
        Assert.Equal("false", settings.Element(Ns + "StopIfGoingOnBatteries")!.Value);
        Assert.Equal("PT0S", settings.Element(Ns + "ExecutionTimeLimit")!.Value);
        Assert.Equal("4", settings.Element(Ns + "Priority")!.Value);
        Assert.Equal("IgnoreNew", settings.Element(Ns + "MultipleInstancesPolicy")!.Value);
        Assert.Equal("false", settings.Element(Ns + "IdleSettings")!.Element(Ns + "StopOnIdleEnd")!.Value);
    }

    [Fact]
    public void The_program_is_quoted_and_special_characters_survive()
    {
        const string odd = @"D:\Tools & <Games>\DockSense.exe";

        var exec = Build(odd).Element(Ns + "Actions")!.Element(Ns + "Exec")!;

        Assert.Equal($"\"{odd}\"", exec.Element(Ns + "Command")!.Value);
        Assert.Equal(Arguments, exec.Element(Ns + "Arguments")!.Value);
    }

    [Fact]
    public void A_built_task_reads_back_as_starting_this_copy()
    {
        var info = LogonTaskDefinition.Parse(LogonTaskDefinition.BuildXml(Exe, Arguments, User, Sid));

        Assert.Equal(new LogonTaskInfo($"\"{Exe}\"", Arguments, Enabled: true), info);
        Assert.True(LogonTaskDefinition.Starts(info, Exe, Arguments));
        Assert.True(LogonTaskDefinition.Starts(info, Exe.ToUpperInvariant(), Arguments));
    }

    [Fact]
    public void A_task_for_another_copy_or_other_arguments_does_not_start_this_one()
    {
        var info = LogonTaskDefinition.Parse(LogonTaskDefinition.BuildXml(Exe, Arguments, User, Sid));

        Assert.False(LogonTaskDefinition.Starts(info, @"D:\Old\DockSense.exe", Arguments));
        Assert.False(LogonTaskDefinition.Starts(info, Exe, "--autostart"));
        Assert.False(LogonTaskDefinition.Starts(null, Exe, Arguments));
    }

    [Fact]
    public void Task_scheduler_xml_is_read_as_it_comes_back()
    {
        // As IRegisteredTask.Xml returns it: with a declaration, the unquoted path Task Scheduler's own tools
        // write, and the task disabled by the user.
        const string xml = """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Settings>
                <Enabled>false</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>C:\Program Files (x86)\DockSense\DockSense.exe</Command>
                  <Arguments>--autostart --logon-task</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        var info = LogonTaskDefinition.Parse(xml);

        Assert.NotNull(info);
        Assert.False(info.Enabled);
        Assert.True(LogonTaskDefinition.Starts(info, Exe, Arguments));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Actions /></Task>")]
    public void Anything_else_is_not_a_task_that_starts_a_program(string? xml)
    {
        Assert.Null(LogonTaskDefinition.Parse(xml));
    }
}
