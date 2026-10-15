using System.Runtime.CompilerServices;
using System.Text;
using Cmux.Core.Terminal;
using FluentAssertions;
using Xunit;

namespace Cmux.Tests;

public class TerminalCellLayoutTests
{
    [Fact]
    public void TerminalCell_IsAtMost12Bytes()
    {
        // BEFORE this change: 20 bytes (char + 10-byte TerminalAttribute + bool + int).
        Unsafe.SizeOf<TerminalCell>().Should().BeLessThanOrEqualTo(12);
    }

    [Fact]
    public void TerminalCell_RoundTripsAttributeExactly()
    {
        var attr = new TerminalAttribute
        {
            Flags = CellFlags.Bold | CellFlags.Strikethrough,
            Foreground = TerminalColor.FromRgb(1, 2, 3),
            Background = TerminalColor.Default,
        };

        var cell = new TerminalCell { Character = 'Q', Attribute = attr, IsDirty = true, Width = 1 };

        cell.Character.Should().Be('Q');
        cell.Attribute.Flags.Should().Be(attr.Flags);
        cell.Attribute.Foreground.Should().Be(attr.Foreground);
        cell.Attribute.Background.Should().Be(TerminalColor.Default);
        cell.IsDirty.Should().BeTrue();
        cell.Width.Should().Be(1);
    }

    [Fact]
    public void TerminalCell_DefaultStructIsBlackNotDefaultColor()
    {
        // Matches the pre-packing behaviour of default(TerminalCell).
        var cell = default(TerminalCell);
        cell.Attribute.Foreground.Should().Be(new TerminalColor(0, 0, 0));
        cell.Attribute.Foreground.IsDefault.Should().BeFalse();
        cell.Width.Should().Be(0);
    }

    [Fact]
    public void TerminalCell_Empty_IsBlankDefault()
    {
        var empty = TerminalCell.Empty;
        empty.Character.Should().Be(' ');
        empty.Attribute.Should().Be(TerminalAttribute.Default);
        empty.Width.Should().Be(1);
        empty.IsDirty.Should().BeTrue();
    }
}

public class VtParserSplitTests
{
    [Fact]
    public void SgrSequenceSplitAcrossFeedCalls_AppliesRedForeground()
    {
        var session = new TerminalSession("split", 20, 2);

        session.FeedOutput(Encoding.ASCII.GetBytes("\x1b[3"));
        session.FeedOutput(Encoding.ASCII.GetBytes("1mX"));

        session.Buffer.CellAt(0, 0).Character.Should().Be('X');
        session.Buffer.CellAt(0, 0).Attribute.Foreground.Should().Be(TerminalColor.FromIndex(1));
    }

    [Fact]
    public void Utf8SequenceSplitAcrossFeedCalls_DecodesCharacter()
    {
        var parser = new VtParser();
        var printed = new List<char>();
        parser.OnPrint = c => printed.Add(c);

        var bytes = Encoding.UTF8.GetBytes("\u00E9");
        parser.Feed(bytes.AsSpan(0, 1));
        parser.Feed(bytes.AsSpan(1));

        printed.Should().Equal('\u00E9');
    }

    [Fact]
    public void OscTerminatedByEscBackslash_IsDispatched()
    {
        var parser = new VtParser();
        var received = new List<string>();
        parser.OnOscDispatch = s => received.Add(s);

        parser.Feed("\x1b]9;9;C:\\x\x1b\\");

        received.Should().ContainSingle().Which.Should().Be("9;9;C:\\x");
    }

    [Fact]
    public void OscPayloadLongerThan64Kb_IsTruncated()
    {
        var parser = new VtParser();
        string? received = null;
        parser.OnOscDispatch = s => received = s;

        parser.Feed("\x1b]0;" + new string('a', 200_000) + "\x07");

        received.Should().NotBeNull();
        received!.Length.Should().Be(64 * 1024);
    }

    [Fact]
    public void OscAfterLargePayload_StillParses()
    {
        var parser = new VtParser();
        string? received = null;
        parser.OnOscDispatch = s => received = s;

        parser.Feed("\x1b]0;" + new string('a', 100_000) + "\x07");
        parser.Feed("\x1b]2;small\x07");

        received.Should().Be("2;small");
    }
}

public class ScrollbackBufferTests
{
    [Fact]
    public void Add_BeyondCapacity_EvictsOldest()
    {
        var sb = new ScrollbackBuffer<int>(3);

        for (int i = 1; i <= 5; i++)
            sb.Add(i);

        sb.Count.Should().Be(3);
        sb.Capacity.Should().Be(3);
        sb.ToList().Should().Equal(3, 4, 5);
        sb[0].Should().Be(3);
        sb[2].Should().Be(5);
    }

    [Fact]
    public void Add_BelowCapacity_KeepsOrder()
    {
        var sb = new ScrollbackBuffer<string>(100);
        sb.Add("a");
        sb.Add("b");

        sb.Count.Should().Be(2);
        sb.ToList().Should().Equal("a", "b");
    }

    [Fact]
    public void Clear_ResetsRing()
    {
        var sb = new ScrollbackBuffer<int>(2);
        sb.Add(1);
        sb.Add(2);
        sb.Add(3);

        sb.Clear();
        sb.Add(9);

        sb.Count.Should().Be(1);
        sb.ToList().Should().Equal(9);
    }

    [Fact]
    public void Indexer_OutOfRange_Throws()
    {
        var sb = new ScrollbackBuffer<int>(2);
        sb.Add(1);

        var act = () => _ = sb[1];

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Buffer_ScrollbackIsCappedAtMaxScrollback()
    {
        var buffer = new TerminalBuffer(10, 2, maxScrollback: 3);

        for (int i = 0; i < 10; i++)
        {
            buffer.WriteChar((char)('a' + i));
            buffer.NewLine();
        }

        buffer.ScrollbackCount.Should().Be(3);
    }

    [Fact]
    public void Buffer_ScrollbackRowsAreTrimmedToLastNonBlankCell()
    {
        var buffer = new TerminalBuffer(10, 2, maxScrollback: 10);
        buffer.WriteString("AB");
        buffer.NewLine();
        buffer.NewLine(); // pushes row 0 into scrollback

        var line = buffer.GetScrollbackLine(0);

        line.Should().NotBeNull();
        line!.Length.Should().Be(2);
        line[0].Character.Should().Be('A');
        line[1].Character.Should().Be('B');
    }

    [Fact]
    public void Buffer_TrimmedScrollbackRow_ReadsAsBlankBeyondLength()
    {
        var buffer = new TerminalBuffer(10, 2, maxScrollback: 10);
        buffer.WriteString("AB");
        buffer.NewLine();
        buffer.NewLine();

        var line = buffer.GetScrollbackLine(0)!;
        for (int c = 0; c < buffer.Cols; c++)
        {
            var cell = c < line.Length ? line[c] : TerminalCell.Empty;
            if (c >= 2)
            {
                cell.Character.Should().Be(' ');
                cell.Attribute.Should().Be(TerminalAttribute.Default);
            }
        }
    }

    [Fact]
    public void Buffer_BlankRowInScrollback_HasZeroLength()
    {
        var buffer = new TerminalBuffer(10, 2, maxScrollback: 10);
        buffer.NewLine();
        buffer.NewLine();

        buffer.GetScrollbackLine(0)!.Length.Should().Be(0);
    }

    [Fact]
    public void Buffer_ResizeWithSameDimensions_KeepsContent()
    {
        var buffer = new TerminalBuffer(10, 3);
        buffer.WriteString("keep");

        buffer.Resize(10, 3);

        buffer.CellAt(0, 0).Character.Should().Be('k');
        buffer.Cols.Should().Be(10);
        buffer.Rows.Should().Be(3);
    }

    [Fact]
    public void Buffer_ResizeWhileAlternateScreen_RestoresMainScreenAtNewSize()
    {
        var buffer = new TerminalBuffer(10, 3);
        buffer.WriteString("main");
        buffer.SwitchToAlternateScreen();

        buffer.Resize(20, 6);
        buffer.SwitchToMainScreen();
        buffer.WriteString("!");

        buffer.CellAt(0, 0).Character.Should().Be('m');
        buffer.CellAt(0, 4).Character.Should().Be('!');
    }
}

public class OscWorkingDirectoryTests
{
    private static (OscHandler handler, List<string> dirs) CreateHandler()
    {
        var handler = new OscHandler();
        var dirs = new List<string>();
        handler.WorkingDirectoryChanged += d => dirs.Add(d);
        return (handler, dirs);
    }

    [Fact]
    public void Osc7_UncHost_IsRejected()
    {
        var (handler, dirs) = CreateHandler();

        handler.Handle("7;file://fileserver/share/project");

        dirs.Should().BeEmpty();
    }

    [Fact]
    public void Osc7_PlainUncPath_IsRejected()
    {
        var (handler, dirs) = CreateHandler();

        handler.Handle(@"7;\\fileserver\share");
        handler.Handle("7;//fileserver/share");

        dirs.Should().BeEmpty();
    }

    [Fact]
    public void Osc7_EncodedUncPath_IsRejected()
    {
        var (handler, dirs) = CreateHandler();

        handler.Handle("7;file:///%5C%5Cfileserver%5Cshare");

        dirs.Should().BeEmpty();
    }

    [Fact]
    public void Osc7_LocalhostHost_IsAccepted()
    {
        var (handler, dirs) = CreateHandler();

        handler.Handle("7;file://localhost/C:/Users/test/project");

        dirs.Should().ContainSingle().Which.Should().Be(@"C:\Users\test\project");
    }

    [Fact]
    public void Osc7_LocalMachineName_IsAccepted()
    {
        var (handler, dirs) = CreateHandler();

        handler.Handle($"7;file://{Environment.MachineName.ToLowerInvariant()}/C:/work");

        dirs.Should().ContainSingle().Which.Should().Be(@"C:\work");
    }

    [Fact]
    public void Osc7_EmptyHost_WithDriveLetter_IsAccepted()
    {
        var (handler, dirs) = CreateHandler();

        handler.Handle("7;file:///C:/Users/test");

        dirs.Should().ContainSingle().Which.Should().Be(@"C:\Users\test");
    }

    [Fact]
    public void Osc7_RelativePlainPath_IsRejected()
    {
        var (handler, dirs) = CreateHandler();

        handler.Handle("7;relative/path");
        handler.Handle("7;/unix/style");

        dirs.Should().BeEmpty();
    }

    [Fact]
    public void Osc9_9_WithQuotedPath_EmitsWorkingDirectory()
    {
        var (handler, dirs) = CreateHandler();

        handler.Handle("9;9;\"C:\\Users\\test\\my dir\"");

        dirs.Should().ContainSingle().Which.Should().Be(@"C:\Users\test\my dir");
    }

    [Fact]
    public void Osc9_9_WithUnquotedPath_EmitsWorkingDirectory()
    {
        var (handler, dirs) = CreateHandler();

        handler.Handle("9;9;C:\\Users\\test");

        dirs.Should().ContainSingle().Which.Should().Be(@"C:\Users\test");
    }

    [Fact]
    public void Osc9_9_UncPath_IsRejected()
    {
        var (handler, dirs) = CreateHandler();

        handler.Handle("9;9;\"\\\\server\\share\"");

        dirs.Should().BeEmpty();
    }

    [Fact]
    public void Osc9_4_Progress_IsIgnored()
    {
        var handler = new OscHandler();
        int notifications = 0, cwd = 0;
        handler.NotificationReceived += (_, _, _) => notifications++;
        handler.WorkingDirectoryChanged += _ => cwd++;

        handler.Handle("9;4;1;50");
        handler.Handle("9;4;0");

        notifications.Should().Be(0);
        cwd.Should().Be(0);
    }

    [Fact]
    public void Osc9_OtherPayload_IsStillNotification()
    {
        var handler = new OscHandler();
        string? body = null;
        handler.NotificationReceived += (_, _, b) => body = b;

        handler.Handle("9;Build finished");

        body.Should().Be("Build finished");
    }
}

public class UrlDetectorSchemeTests
{
    [Fact]
    public void FindUrls_IgnoresFileScheme()
    {
        UrlDetector.FindUrls("open file:///C:/Users/test/doc.txt now").Should().BeEmpty();
    }

    [Fact]
    public void FindUrls_IgnoresFtpScheme()
    {
        UrlDetector.FindUrls("get ftp://files.example.com/pub/a.zip").Should().BeEmpty();
    }

    [Fact]
    public void FindUrls_DetectsHttpAndHttps()
    {
        var urls = UrlDetector.FindUrls("http://a.test/x and https://b.test/y");

        urls.Select(u => u.url).Should().Equal("http://a.test/x", "https://b.test/y");
    }
}

public class ShellIntegrationTests
{
    private static string DecodeEncodedCommand(string commandLine)
    {
        const string marker = "-EncodedCommand ";
        int idx = commandLine.IndexOf(marker, StringComparison.Ordinal);
        idx.Should().BeGreaterThan(0, "the command line must carry -EncodedCommand");
        var base64 = commandLine[(idx + marker.Length)..].Trim();
        return Encoding.Unicode.GetString(Convert.FromBase64String(base64));
    }

    [Fact]
    public void PwshWithoutArgs_AppendsNoExitAndEncodedScript()
    {
        var (cmd, env) = ShellIntegration.Apply(@"C:\Program Files\PowerShell\7\pwsh.exe");

        cmd.Should().StartWith(@"C:\Program Files\PowerShell\7\pwsh.exe");
        cmd.Should().Contain(" -NoExit -EncodedCommand ");
        env.Should().BeNull();
    }

    [Fact]
    public void EncodedScript_WrapsPromptAndEmitsOsc9_9()
    {
        var (cmd, _) = ShellIntegration.Apply("powershell.exe");

        var script = DecodeEncodedCommand(cmd);

        script.Should().Contain("$global:__cmuxPrompt = $function:prompt");
        script.Should().Contain("function global:prompt");
        script.Should().Contain("$([char]27)]9;9;`\"$($l.ProviderPath)`\"$([char]27)\\");
        script.Should().Contain("FileSystem");
        script.Should().Contain("& $global:__cmuxPrompt");
    }

    [Fact]
    public void PowershellWithExistingCommand_IsUnchanged()
    {
        var (cmd, env) = ShellIntegration.Apply("powershell.exe -NoLogo -Command echo hi");

        cmd.Should().Be("powershell.exe -NoLogo -Command echo hi");
        env.Should().BeNull();
    }

    [Fact]
    public void PowershellWithFileArg_IsUnchanged()
    {
        var (cmd, _) = ShellIntegration.Apply("pwsh.exe -File C:\\scripts\\run.ps1");

        cmd.Should().Be("pwsh.exe -File C:\\scripts\\run.ps1");
    }

    [Fact]
    public void Cmd_SetsPromptEnvironmentWithOsc9_9()
    {
        var (cmd, env) = ShellIntegration.Apply("cmd.exe");

        cmd.Should().Be("cmd.exe");
        env.Should().NotBeNull();
        env!["PROMPT"].Should().StartWith("$E]9;9;$P$E\\");
    }

    [Fact]
    public void Bash_IsUnchanged()
    {
        var (cmd, env) = ShellIntegration.Apply("bash.exe -l");

        cmd.Should().Be("bash.exe -l");
        env.Should().BeNull();
    }

    [Fact]
    public void QuotedExecutablePath_IsDetected()
    {
        var (cmd, _) = ShellIntegration.Apply("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo");

        cmd.Should().Contain("-EncodedCommand");
    }
}

public class ProcessCwdReaderTests
{
    [Fact]
    public void OwnProcess_ReturnsCurrentDirectory()
    {
        if (!Environment.Is64BitProcess)
            return;

        bool ok = ProcessCwdReader.TryGetCurrentDirectory(Environment.ProcessId, out var cwd);

        ok.Should().BeTrue();
        cwd.Should().NotBeNull();
        Normalize(cwd!).Should().BeEquivalentTo(Normalize(Environment.CurrentDirectory));
    }

    [Fact]
    public void InvalidPid_ReturnsFalse()
    {
        ProcessCwdReader.TryGetCurrentDirectory(0, out var cwd).Should().BeFalse();
        cwd.Should().BeNull();
    }

    private static string Normalize(string path)
    {
        if (path.Length > 3)
            path = path.TrimEnd('\\');
        return path;
    }
}

public class TerminalSessionLifecycleTests
{
    [Fact]
    public void GetLiveWorkingDirectory_UsesShellReportedCwdWhenAvailable()
    {
        var session = new TerminalSession("live", 20, 2);

        session.FeedOutput(Encoding.ASCII.GetBytes("\x1b]9;9;\"C:\\reported\"\x1b\\"));

        session.HasShellReportedCwd.Should().BeTrue();
        session.GetLiveWorkingDirectory().Should().Be(@"C:\reported");
    }

    [Fact]
    public void GetLiveWorkingDirectory_WithoutProcess_FallsBackToWorkingDirectory()
    {
        var session = new TerminalSession("nolive", 20, 2);
        session.WorkingDirectory = @"C:\start";

        session.HasShellReportedCwd.Should().BeFalse();
        session.GetLiveWorkingDirectory().Should().Be(@"C:\start");
    }

    [Fact]
    public void Resize_ClampsDimensions()
    {
        var session = new TerminalSession("clamp", 20, 2);

        session.Resize(100_000, 100_000);

        session.Buffer.Cols.Should().Be(1000);
        session.Buffer.Rows.Should().Be(500);

        session.Resize(-5, 0);

        session.Buffer.Cols.Should().Be(1);
        session.Buffer.Rows.Should().Be(1);
    }
}

public class TerminalProcessTests
{
    private static int CountProcesses(string name) => System.Diagnostics.Process.GetProcessesByName(name).Length;

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return condition();
    }

    private static void DrainOutput(PseudoConsole console)
    {
        // ConPTY blocks when nobody reads its output, so keep a reader running.
        _ = Task.Run(() =>
        {
            var buffer = new byte[4096];
            try
            {
                using var stream = new FileStream(console.ReadPipe, FileAccess.Read, 1);
                while (stream.Read(buffer, 0, buffer.Length) > 0) { }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        });
    }

    [Fact]
    public void Dispose_KillsShellAndItsDescendants()
    {
        var pingsBefore = CountProcesses("PING");
        using var console = PseudoConsole.Create(80, 24);
        DrainOutput(console);

        var process = new TerminalProcess(console, "cmd.exe /d /c ping -n 120 127.0.0.1 > nul", shellIntegration: true);

        WaitUntil(() => CountProcesses("PING") > pingsBefore, TimeSpan.FromSeconds(15))
            .Should().BeTrue("the shell should have started ping as a child");

        process.Dispose();

        WaitUntil(() => CountProcesses("PING") <= pingsBefore, TimeSpan.FromSeconds(15))
            .Should().BeTrue("disposing the process should kill its whole tree");
    }

    [Fact]
    public void Start_WithUncWorkingDirectory_FallsBackToUserProfile()
    {
        using var session = new TerminalSession("uncwd", 80, 24);

        session.Start("cmd.exe /d /c ping -n 120 127.0.0.1 > nul", @"\\server\share");

        session.WorkingDirectory.Should().Be(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    [Fact]
    public void Start_WithExistingLocalWorkingDirectory_UsesIt()
    {
        using var session = new TerminalSession("localcwd", 80, 24);

        session.Start("cmd.exe /d /c ping -n 120 127.0.0.1 > nul", Environment.SystemDirectory);

        session.WorkingDirectory.Should().Be(Environment.SystemDirectory);
    }
}
