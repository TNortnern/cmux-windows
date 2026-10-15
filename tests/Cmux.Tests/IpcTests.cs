using System.Text.Json;
using Cmux.Core.IPC;
using FluentAssertions;
using Xunit;

namespace Cmux.Tests;

public class ControlArgsParsingTests
{
    [Fact]
    public void ParseArgs_LegacyKeyValue_ParsesQuotedValuesAndPositionals()
    {
        var args = new Dictionary<string, string>();

        NamedPipeServer.ParseArgs("title=Hello body=\"two words\" positional", args);

        args["title"].Should().Be("Hello");
        args["body"].Should().Be("two words");
        args["_arg2"].Should().Be("positional");
    }

    [Fact]
    public void ParseArgs_Json_ParsesStringsAndConvertsOtherValues()
    {
        var args = new Dictionary<string, string>();

        NamedPipeServer.ParseArgs("{\"title\":\"Hi there\",\"count\":3,\"flag\":true,\"none\":null}", args);

        args["title"].Should().Be("Hi there");
        args["count"].Should().Be("3");
        args["flag"].Should().Be("true");
        args.Should().NotContainKey("none");
    }

    [Fact]
    public void ParseArgs_JsonWithBackslashPath_KeepsPathIntact()
    {
        var args = new Dictionary<string, string>();

        NamedPipeServer.ParseArgs("{\"cwd\":\"C:\\\\projects\\\\my app\"}", args);

        args["cwd"].Should().Be("C:\\projects\\my app");
    }
}

public class DaemonMessageTests
{
    private static readonly JsonSerializerOptions s_options = new();

    [Fact]
    public void DaemonRequest_IdSurvivesRoundTrip()
    {
        var request = new DaemonRequest { Id = "abc123", Type = DaemonMessageTypes.SessionCwd, PaneId = "pane-1" };

        var json = JsonSerializer.Serialize(request, s_options);
        var parsed = JsonSerializer.Deserialize<DaemonRequest>(json, s_options);

        parsed.Should().NotBeNull();
        parsed!.Id.Should().Be("abc123");
        parsed.Type.Should().Be(DaemonMessageTypes.SessionCwd);
        parsed.PaneId.Should().Be("pane-1");
    }

    [Fact]
    public void DaemonResponse_IdSurvivesRoundTrip()
    {
        var response = new DaemonResponse { Id = "abc123", Success = true, Data = "C:\\work" };

        var json = JsonSerializer.Serialize(response, s_options);
        var parsed = JsonSerializer.Deserialize<DaemonResponse>(json, s_options);

        parsed.Should().NotBeNull();
        parsed!.Id.Should().Be("abc123");
        parsed.Success.Should().BeTrue();
        parsed.Data.Should().Be("C:\\work");
    }

    [Fact]
    public void DaemonEvent_HasNoIdProperty()
    {
        // The client classifies lines by a non-empty "Id": events must never carry one
        var json = JsonSerializer.Serialize(new DaemonEvent { Type = DaemonMessageTypes.EventBell, PaneId = "p" }, s_options);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("Id", out _).Should().BeFalse();
        doc.RootElement.GetProperty("Type").GetString().Should().Be(DaemonMessageTypes.EventBell);
    }
}

public class DaemonInputValidationTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(120, 120)]
    [InlineData(5000, 1000)]
    public void ClampCols_KeepsWithinOneToThousand(int input, int expected)
    {
        DaemonInputValidation.ClampCols(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(30, 30)]
    [InlineData(9999, 500)]
    public void ClampRows_KeepsWithinOneToFiveHundred(int input, int expected)
    {
        DaemonInputValidation.ClampRows(input).Should().Be(expected);
    }

    [Fact]
    public void ValidateWorkingDirectory_AcceptsExistingLocalDirectory()
    {
        var temp = Path.GetTempPath().TrimEnd('\\');

        DaemonInputValidation.ValidateWorkingDirectory(temp).Should().Be(temp);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative\\dir")]
    [InlineData("\\\\server\\share")]
    [InlineData("\\\\?\\C:\\Windows")]
    public void ValidateWorkingDirectory_RejectsRelativeUncAndEmpty(string? path)
    {
        DaemonInputValidation.ValidateWorkingDirectory(path).Should().BeNull();
    }

    [Fact]
    public void ValidateWorkingDirectory_RejectsMissingDirectory()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cmux-missing-" + Guid.NewGuid().ToString("N"));

        DaemonInputValidation.ValidateWorkingDirectory(missing).Should().BeNull();
    }

    [Fact]
    public void IsCommandAllowed_AllowsNoCommand()
    {
        DaemonInputValidation.IsCommandAllowed(null).Should().BeTrue();
        DaemonInputValidation.IsCommandAllowed("   ").Should().BeTrue();
    }

    [Fact]
    public void IsCommandAllowed_AllowsAbsoluteExecutableWithArguments()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        DaemonInputValidation.IsCommandAllowed($"\"{cmd}\" /c echo hi").Should().BeTrue();
        DaemonInputValidation.IsCommandAllowed($"{cmd} /c echo hi").Should().BeTrue();
    }

    [Fact]
    public void IsCommandAllowed_AllowsBareNameResolvedOnPath()
    {
        // cmd.exe lives in System32, which is always on PATH; "cmd" also resolves via PATHEXT-style lookup
        DaemonInputValidation.ResolveExecutable("cmd.exe /c echo hi").Should().NotBeNull();
        DaemonInputValidation.ResolveExecutable("cmd /c echo hi").Should().NotBeNull();
    }

    [Theory]
    [InlineData("cmux-no-such-program-xyz.exe")]
    [InlineData("C:\\cmux-no-such-dir\\evil.exe")]
    [InlineData("..\\evil.exe")]
    [InlineData("\\\\server\\share\\evil.exe")]
    [InlineData("\"\"")]
    [InlineData("readme.txt")]
    public void IsCommandAllowed_RejectsMissingRelativeUncOrNonExecutable(string command)
    {
        DaemonInputValidation.IsCommandAllowed(command).Should().BeFalse();
    }

    [Fact]
    public void IsCommandAllowed_RejectsNonExecutableAbsoluteFile()
    {
        var file = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

        DaemonInputValidation.IsCommandAllowed(file).Should().BeFalse();
    }
}

public class NamedPipeRoundTripTests
{
    [Fact]
    public async Task ServerAndClient_RoundTripCommandWithJsonArgs()
    {
        var tag = "test" + Guid.NewGuid().ToString("N")[..12];
        using var server = new NamedPipeServer(tag);
        server.OnCommand = (command, args) => Task.FromResult(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["command"] = command,
            ["value"] = args.GetValueOrDefault("value", ""),
        }));
        server.Start();

        var response = await NamedPipeClient.SendCommand("ping", new Dictionary<string, string> { ["value"] = "hello world" }, tag);

        using var doc = JsonDocument.Parse(response);
        doc.RootElement.GetProperty("command").GetString().Should().Be("PING");
        doc.RootElement.GetProperty("value").GetString().Should().Be("hello world");
    }

    [Fact]
    public async Task ServerReportsHandlerExceptionAsErrorJson()
    {
        var tag = "test" + Guid.NewGuid().ToString("N")[..12];
        using var server = new NamedPipeServer(tag);
        server.OnCommand = (_, _) => throw new InvalidOperationException("boom");
        server.Start();

        var response = await NamedPipeClient.SendCommand("STATUS", null, tag);

        using var doc = JsonDocument.Parse(response);
        doc.RootElement.GetProperty("error").GetString().Should().Be("boom");
    }
}
