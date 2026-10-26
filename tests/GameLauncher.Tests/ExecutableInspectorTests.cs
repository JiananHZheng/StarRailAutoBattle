using GameLauncher.Core;

namespace GameLauncher.Tests;

public class ExecutableInspectorTests
{
    [Theory]
    [InlineData("""<requestedExecutionLevel level="requireAdministrator" uiAccess="false"></requestedExecutionLevel>""", ExecutionLevel.RequireAdministrator)]
    [InlineData("""<ms_asmv2:requestedExecutionLevel uiAccess='false' level='asInvoker'/>""", ExecutionLevel.AsInvoker)]
    [InlineData("""<requestedExecutionLevel level="highestAvailable"/>""", ExecutionLevel.HighestAvailable)]
    [InlineData("""<assembly><dependency/></assembly>""", ExecutionLevel.NotSpecified)]
    [InlineData("""<requestedExecutionLevel level="weird"/>""", ExecutionLevel.Unknown)]
    public void Parses_requested_execution_level(string xml, ExecutionLevel expected)
    {
        Assert.Equal(expected, ExecutableInspector.ParseExecutionLevel(xml));
    }

    [Fact]
    public void Reads_require_administrator_from_a_real_system_binary()
    {
        // cliconfg.exe ships with Windows and declares requireAdministrator, like StarRail.exe does.
        var path = Path.Combine(Environment.SystemDirectory, "cliconfg.exe");
        if (!File.Exists(path))
        {
            return;
        }

        Assert.Equal(ExecutionLevel.RequireAdministrator, ExecutableInspector.ReadExecutionLevel(path));
    }

    [Fact]
    public void Notepad_does_not_require_administrator()
    {
        var level = ExecutableInspector.ReadExecutionLevel(Path.Combine(Environment.SystemDirectory, "notepad.exe"));
        Assert.NotEqual(ExecutionLevel.RequireAdministrator, level);
        Assert.NotEqual(ExecutionLevel.Unknown, level);
    }
}
