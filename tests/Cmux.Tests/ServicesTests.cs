using Cmux.Core;
using Cmux.Core.Services;
using FluentAssertions;
using Xunit;

namespace Cmux.Tests;

/// <summary>
/// Tests that redirect CmuxPaths.RootOverride live in this one class so xunit runs them sequentially.
/// Each test gets its own temp root, removed on dispose.
/// </summary>
public sealed class ServicesTests : IDisposable
{
    private readonly string _root;

    public ServicesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cmux-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        CmuxPaths.RootOverride = _root;
    }

    public void Dispose()
    {
        CmuxPaths.RootOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Log ─────────────────────────────────────────────────────

    [Fact]
    public void Log_RotatesAtOneMegabyte_KeepingOneOldFile()
    {
        var message = new string('x', 1000);
        for (int i = 0; i < 1200; i++)
            Log.Info(message);

        var logsDir = CmuxPaths.LogsDirectory;
        var current = Path.Combine(logsDir, "cmux.log");
        var old = Path.Combine(logsDir, "cmux.log.old");

        File.Exists(old).Should().BeTrue();
        new FileInfo(old).Length.Should().BeGreaterThanOrEqualTo(1024 * 1024);
        new FileInfo(current).Length.Should().BeLessThan(1024 * 1024);
    }

    [Fact]
    public void Log_NeverThrows_WhenLogsDirectoryIsUnusable()
    {
        // A regular file where the data directory should be makes every write fail.
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "not a directory");
        CmuxPaths.RootOverride = blocker;

        var act = () =>
        {
            Log.Info("hello");
            Log.Error("boom", new InvalidOperationException("bad"));
        };

        act.Should().NotThrow();
    }

    // ── Redaction ───────────────────────────────────────────────

    [Theory]
    [InlineData("curl -H \"Authorization: Bearer eyJhbGciOi.payload.sig\" https://api.test", "eyJhbGciOi.payload.sig")]
    [InlineData("Authorization: Basic dXNlcjpodW50ZXIy", "dXNlcjpodW50ZXIy")]
    [InlineData("x-api-key: abc123SECRETvalue", "abc123SECRETvalue")]
    [InlineData("git clone https://ghp_abcdefghijklmnopqrstuvwxyz0123456789@github.com/x/y", "ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("echo sk-proj-abcdefghijklmnopqrstuvwxyz", "sk-proj-abcdefghijklmnopqrstuvwxyz")]
    [InlineData("aws configure AKIAIOSFODNN7EXAMPLE", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("PRIVATE_KEY=MIIEpAIBAAKCAQEA123", "MIIEpAIBAAKCAQEA123")]
    [InlineData("export GIT_PASS=hunter2 && git pull", "hunter2")]
    [InlineData("PASSPHRASE='correct horse'", "correct horse")]
    [InlineData("CREDENTIALS=abc999", "abc999")]
    [InlineData("COOKIE=session=deadbeef", "deadbeef")]
    [InlineData("curl -u alice:s3cretPw https://example.com/api", "s3cretPw")]
    [InlineData("curl --user bob:hunter9 https://x", "hunter9")]
    public void Redact_RemovesSecretValue_ForEachRule(string input, string secret)
    {
        var result = CommandLogService.Redact(input);

        result.Should().NotContain(secret);
        result.Should().Contain("[REDACTED]");
    }

    [Theory]
    [InlineData("ls -la")]
    [InlineData("git status")]
    [InlineData("dotnet build Cmux.sln -c Debug")]
    public void Redact_LeavesOrdinaryCommandsAlone(string input)
    {
        CommandLogService.Redact(input).Should().Be(input);
    }

    // ── SecretStore ─────────────────────────────────────────────

    [Fact]
    public void SecretStore_RoundTrips_ThroughDpapi()
    {
        SecretStoreService.SetSecret("agent.openai.apiKey", "sk-test-123").Should().BeTrue();
        SecretStoreService.SetSecret("agent.anthropic.apiKey", "ant-456").Should().BeTrue();

        SecretStoreService.GetSecret("agent.openai.apiKey").Should().Be("sk-test-123");
        SecretStoreService.GetSecret("agent.anthropic.apiKey").Should().Be("ant-456");

        // Values are stored as DPAPI ciphertext, never plaintext.
        File.ReadAllText(Path.Combine(_root, "secrets.json")).Should().NotContain("sk-test-123");

        SecretStoreService.RemoveSecret("agent.openai.apiKey").Should().BeTrue();
        SecretStoreService.GetSecret("agent.openai.apiKey").Should().BeNull();
        SecretStoreService.GetSecret("agent.anthropic.apiKey").Should().Be("ant-456");
    }

    [Fact]
    public void SecretStore_CorruptFile_IsNotOverwritten()
    {
        var path = Path.Combine(_root, "secrets.json");
        const string corrupt = "{ this is not json";
        File.WriteAllText(path, corrupt);

        SecretStoreService.SetSecret("agent.exa.apiKey", "exa-789").Should().BeFalse();

        File.ReadAllText(path).Should().Be(corrupt);
        SecretStoreService.GetSecret("agent.exa.apiKey").Should().BeNull();
    }

    [Fact]
    public void SecretStore_Write_KeepsPreviousVersionAsBak()
    {
        SecretStoreService.SetSecret("first", "one").Should().BeTrue();
        SecretStoreService.SetSecret("second", "two").Should().BeTrue();

        File.Exists(Path.Combine(_root, "secrets.json.bak")).Should().BeTrue();
        SecretStoreService.GetSecret("first").Should().Be("one");
        SecretStoreService.GetSecret("second").Should().Be("two");
    }

    // ── GitService ──────────────────────────────────────────────

    [Theory]
    [InlineData(@"\\server\share\repo")]
    [InlineData(@"\\?\UNC\server\share\repo")]
    [InlineData("//server/share/repo")]
    [InlineData("relative/path")]
    public void GitService_NonLocalPaths_ReturnNull(string path)
    {
        GitService.GetBranch(path).Should().BeNull();
        GitService.GetRemoteUrl(path).Should().BeNull();
    }

    [Fact]
    public void GitService_ReadsHeadDirectly()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/feature/x\n");

        GitService.GetBranch(repo).Should().Be("feature/x");
    }

    [Fact]
    public void GitService_DirectoryOutsideRepo_IsNull()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);

        GitService.GetBranch(plain).Should().BeNull();
    }

    // ── AgentDetector ───────────────────────────────────────────

    [Fact]
    public void AgentDetector_Snapshot_FindsCurrentProcessAndItsParentChain()
    {
        var snapshot = AgentDetector.TakeSnapshot();

        var self = snapshot.FirstOrDefault(p => p.Pid == Environment.ProcessId);
        self.Pid.Should().Be(Environment.ProcessId);
        self.Name.Should().NotBeNullOrEmpty();

        snapshot.Should().Contain(p => p.Pid == self.ParentPid);

        var descendantsOfParent = AgentDetector.EnumerateDescendants(snapshot, self.ParentPid).ToList();
        descendantsOfParent.Should().Contain(p => p.Pid == Environment.ProcessId);
    }

    [Fact]
    public void AgentDetector_EnumerateDescendants_WalksWholeTree()
    {
        var snapshot = new List<AgentDetector.ProcessInfo>
        {
            new(10, 1, "pwsh.exe"),
            new(11, 10, "node.exe"),
            new(12, 11, "claude.exe"),
            new(20, 1, "other.exe"),
            new(21, 20, "codex.exe"),
        };

        var names = AgentDetector.EnumerateDescendants(snapshot, 10).Select(p => p.Name).ToList();

        names.Should().BeEquivalentTo(new[] { "node.exe", "claude.exe" });
    }
}
