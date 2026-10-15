using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using static Cmux.Core.Terminal.ConPtyInterop;

namespace Cmux.Core.Terminal;

/// <summary>
/// Manages a shell process attached to a ConPTY pseudo console. The process is placed in a
/// Job Object with kill-on-close, so disposing this instance terminates the whole process tree.
/// </summary>
public sealed class TerminalProcess : IDisposable
{
    private PROCESS_INFORMATION _processInfo;
    private IntPtr _jobHandle;
    private bool _disposed;

    public int ProcessId => _processInfo.dwProcessId;
    public IntPtr ProcessHandle => _processInfo.hProcess;

    public event Action? Exited;

    public TerminalProcess(
        PseudoConsole console,
        string? command = null,
        string? workingDirectory = null,
        bool shellIntegration = true,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        var commandLine = command ?? DetectShell();
        IReadOnlyDictionary<string, string>? environment = extraEnvironment;

        if (shellIntegration)
        {
            var applied = ShellIntegration.Apply(commandLine);
            commandLine = applied.CommandLine;
            if (applied.ExtraEnv is { Count: > 0 })
                environment = MergeEnvironment(environment, applied.ExtraEnv);
        }

        _jobHandle = CreateKillOnCloseJob();
        try
        {
            // Created suspended so the process is in the job before it can spawn anything.
            _processInfo = CreateConPtyProcess(console.Handle, commandLine, workingDirectory, environment);

            if (!AssignProcessToJobObject(_jobHandle, _processInfo.hProcess))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed.");

            if (ResumeThread(_processInfo.hThread) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread failed.");
        }
        catch
        {
            ReleaseHandles();
            throw;
        }

        var processHandle = _processInfo.hProcess;
        var waitThread = new Thread(() => WaitForExitThread(processHandle))
        {
            IsBackground = true,
            Name = $"ConPTY-Wait-{_processInfo.dwProcessId}",
        };
        waitThread.Start();
    }

    /// <summary>
    /// Detects the best available shell on the system.
    /// Priority: pwsh.exe > powershell.exe > cmd.exe
    /// </summary>
    private static string DetectShell()
    {
        // Check for PowerShell 7+ (pwsh)
        var pwshPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
            "pwsh.exe",
        };

        foreach (var path in pwshPaths)
        {
            if (path == "pwsh.exe")
            {
                // Check if it's in PATH
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathEnv.Split(Path.PathSeparator))
                {
                    var fullPath = Path.Combine(dir, "pwsh.exe");
                    if (File.Exists(fullPath))
                        return fullPath;
                }
            }
            else if (File.Exists(path))
            {
                return path;
            }
        }

        // Fall back to Windows PowerShell
        var winPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(winPowerShell))
            return winPowerShell;

        // Last resort: cmd.exe from COMSPEC
        var comspec = Environment.GetEnvironmentVariable("COMSPEC");
        if (!string.IsNullOrEmpty(comspec) && File.Exists(comspec))
            return comspec;

        return "cmd.exe";
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info,
            (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            int error = Marshal.GetLastWin32Error();
            CloseHandle(job);
            throw new Win32Exception(error, "SetInformationJobObject failed.");
        }

        return job;
    }

    private static PROCESS_INFORMATION CreateConPtyProcess(
        IntPtr conPtyHandle,
        string commandLine,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment)
    {
        IntPtr attributeList = CreateAttributeList(conPtyHandle);
        IntPtr environmentBlock = environment is { Count: > 0 }
            ? BuildEnvironmentBlock(environment)
            : IntPtr.Zero;

        try
        {
            var startupInfo = new STARTUPINFOEX
            {
                lpAttributeList = attributeList,
            };
            startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

            // CreateProcessW may write into the command line buffer, so it must be mutable.
            var mutableCommandLine = new StringBuilder(commandLine, commandLine.Length + 1);

            bool success = CreateProcess(
                null,
                mutableCommandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED,
                environmentBlock,
                workingDirectory,
                ref startupInfo,
                out var processInfo);

            if (!success)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create process with ConPTY.");

            return processInfo;
        }
        finally
        {
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);

            if (environmentBlock != IntPtr.Zero)
                Marshal.FreeHGlobal(environmentBlock);
        }
    }

    private static Dictionary<string, string> MergeEnvironment(
        IReadOnlyDictionary<string, string>? a,
        IReadOnlyDictionary<string, string> b)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (a != null)
            foreach (var kv in a)
                merged[kv.Key] = kv.Value;
        foreach (var kv in b)
            merged[kv.Key] = kv.Value;
        return merged;
    }

    /// <summary>
    /// Builds a Unicode environment block: the current environment with the overrides applied,
    /// sorted case-insensitively by name and terminated by a double null.
    /// The caller frees the returned pointer.
    /// </summary>
    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string> overrides)
    {
        var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (key.Length == 0 || key[0] == '=')
                continue; // hidden per-drive cwd entries
            merged[key] = (string?)entry.Value ?? string.Empty;
        }

        foreach (var (key, value) in overrides)
        {
            if (key.Length == 0 || key.Contains('='))
                continue;
            merged[key] = value;
        }

        var block = new StringBuilder();
        foreach (var (key, value) in merged)
            block.Append(key).Append('=').Append(value).Append('\0');
        if (block.Length == 0)
            block.Append('\0');

        // StringToHGlobalUni appends one more null, producing the required double-null end.
        return Marshal.StringToHGlobalUni(block.ToString());
    }

    private static IntPtr CreateAttributeList(IntPtr conPtyHandle)
    {
        // Query the required size
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);

        var attributeList = Marshal.AllocHGlobal(size);

        if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
        {
            int error = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(attributeList);
            throw new Win32Exception(error, "InitializeProcThreadAttributeList failed.");
        }

        if (!UpdateProcThreadAttribute(
            attributeList,
            0,
            (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            conPtyHandle,
            (IntPtr)IntPtr.Size,
            IntPtr.Zero,
            IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
            throw new Win32Exception(error, "UpdateProcThreadAttribute failed.");
        }

        return attributeList;
    }

    private void WaitForExitThread(IntPtr processHandle)
    {
        WaitForSingleObject(processHandle, INFINITE);
        Exited?.Invoke();
    }

    public void WaitForExit()
    {
        if (_processInfo.hProcess != IntPtr.Zero)
            WaitForSingleObject(_processInfo.hProcess, INFINITE);
    }

    public bool HasExited
    {
        get
        {
            if (!GetExitCodeProcess(_processInfo.hProcess, out uint exitCode))
                return true;
            return exitCode != STILL_ACTIVE;
        }
    }

    /// <summary>
    /// Terminates the shell and every process in its job.
    /// </summary>
    public void Kill()
    {
        if (_disposed) return;
        KillCore();
    }

    private void KillCore()
    {
        if (_jobHandle != IntPtr.Zero)
            TerminateJobObject(_jobHandle, 1);
        else if (_processInfo.hProcess != IntPtr.Zero && !HasExited)
            TerminateProcess(_processInfo.hProcess, 1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ReleaseHandles();
    }

    private void ReleaseHandles()
    {
        KillCore();

        // Closing the last handle to a kill-on-close job terminates any remaining processes.
        if (_jobHandle != IntPtr.Zero)
        {
            CloseHandle(_jobHandle);
            _jobHandle = IntPtr.Zero;
        }

        if (_processInfo.hThread != IntPtr.Zero)
            CloseHandle(_processInfo.hThread);
        if (_processInfo.hProcess != IntPtr.Zero)
            CloseHandle(_processInfo.hProcess);

        _processInfo = default;
    }
}
