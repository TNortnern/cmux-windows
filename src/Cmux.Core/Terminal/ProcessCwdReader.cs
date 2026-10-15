using System.Runtime.InteropServices;
using System.Text;

namespace Cmux.Core.Terminal;

/// <summary>
/// Reads another process's current directory from its PEB (RTL_USER_PROCESS_PARAMETERS.CurrentDirectory).
/// Supports 64-bit targets only; returns false for WOW64 targets, when this process is 32-bit, or on any failure.
/// </summary>
public static class ProcessCwdReader
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;
    private const int ProcessBasicInformationClass = 0;

    // x64 offsets
    private const int PebProcessParametersOffset = 0x20;
    private const int ProcessParametersCurrentDirectoryOffset = 0x38; // CURDIR.DosPath (UNICODE_STRING)
    private const int UnicodeStringLengthOffset = 0x00;
    private const int UnicodeStringBufferOffset = 0x08;
    private const int UnicodeStringSize = 0x10;
    private const int MaxDirectoryBytes = 32767 * 2;

    public static bool TryGetCurrentDirectory(int pid, out string? cwd)
    {
        cwd = null;

        if (pid <= 0 || !Environment.Is64BitProcess)
            return false;

        IntPtr process = IntPtr.Zero;
        try
        {
            process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
            if (process == IntPtr.Zero)
                return false;

            if (!IsNativeProcess(process))
                return false;

            var info = new PROCESS_BASIC_INFORMATION();
            int status = NtQueryInformationProcess(process, ProcessBasicInformationClass, ref info,
                Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            if (status != 0 || info.PebBaseAddress == IntPtr.Zero)
                return false;

            var parametersPtr = ReadPointer(process, info.PebBaseAddress + PebProcessParametersOffset);
            if (parametersPtr == IntPtr.Zero)
                return false;

            var header = ReadBytes(process, parametersPtr + ProcessParametersCurrentDirectoryOffset, UnicodeStringSize);
            if (header is null)
                return false;

            int length = BitConverter.ToUInt16(header, UnicodeStringLengthOffset);
            var bufferPtr = (IntPtr)BitConverter.ToInt64(header, UnicodeStringBufferOffset);
            if (length <= 0 || length > MaxDirectoryBytes || (length & 1) != 0 || bufferPtr == IntPtr.Zero)
                return false;

            var raw = ReadBytes(process, bufferPtr, length);
            if (raw is null)
                return false;

            string path = Encoding.Unicode.GetString(raw);
            path = TrimTrailingSeparator(path);

            if (path.Length == 0 || path.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
                return false;

            cwd = path;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            cwd = null;
            return false;
        }
        finally
        {
            if (process != IntPtr.Zero)
                CloseHandle(process);
        }
    }

    private static bool IsNativeProcess(IntPtr process)
    {
        // processMachine is IMAGE_FILE_MACHINE_UNKNOWN (0) for a process running natively.
        if (!IsWow64Process2(process, out ushort processMachine, out _))
            return false;
        return processMachine == 0;
    }

    private static IntPtr ReadPointer(IntPtr process, IntPtr address)
    {
        var bytes = ReadBytes(process, address, IntPtr.Size);
        return bytes is null ? IntPtr.Zero : (IntPtr)BitConverter.ToInt64(bytes, 0);
    }

    private static byte[]? ReadBytes(IntPtr process, IntPtr address, int count)
    {
        var buffer = new byte[count];
        if (!ReadProcessMemory(process, address, buffer, (IntPtr)count, out IntPtr read) || read.ToInt64() != count)
            return null;
        return buffer;
    }

    private static string TrimTrailingSeparator(string path)
    {
        // Keep drive roots such as "C:\" intact.
        if (path.Length > 3 && path[^1] == '\\')
            return path.TrimEnd('\\');
        return path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr hProcess, out ushort pProcessMachine, out ushort pNativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);
}
