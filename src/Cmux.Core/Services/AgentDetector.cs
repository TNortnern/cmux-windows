using System.Runtime.InteropServices;

namespace Cmux.Core.Services;

public enum AgentType
{
    None, ClaudeCode, Codex, Aider, GithubCopilot, Cursor, Cline, Windsurf
}

public static class AgentDetector
{
    private static readonly (string pattern, AgentType type)[] Patterns =
    [
        ("claude", AgentType.ClaudeCode),
        ("codex", AgentType.Codex),
        ("aider", AgentType.Aider),
        ("copilot", AgentType.GithubCopilot),
        ("cursor", AgentType.Cursor),
        ("cline", AgentType.Cline),
        ("windsurf", AgentType.Windsurf),
    ];

    internal readonly record struct ProcessInfo(int Pid, int ParentPid, string Name);

    public static AgentType DetectFromProcessId(int shellPid)
    {
        try
        {
            var snapshot = TakeSnapshot();
            foreach (var proc in EnumerateDescendants(snapshot, shellPid))
                foreach (var (pattern, type) in Patterns)
                    if (proc.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        return type;
        }
        catch
        {
            // Detection is best effort.
        }
        return AgentType.None;
    }

    public static string GetLabel(AgentType t) => t switch
    {
        AgentType.ClaudeCode => "Claude Code",
        AgentType.Codex => "Codex",
        AgentType.Aider => "Aider",
        AgentType.GithubCopilot => "Copilot",
        AgentType.Cursor => "Cursor",
        AgentType.Cline => "Cline",
        AgentType.Windsurf => "Windsurf",
        _ => "",
    };

    public static string GetIcon(AgentType t) => t switch
    {
        AgentType.ClaudeCode => "",
        AgentType.Codex => "",
        AgentType.Aider => "",
        AgentType.GithubCopilot => "",
        AgentType.Cursor => "",
        AgentType.Cline => "",
        AgentType.Windsurf => "",
        _ => "",
    };

    /// <summary>
    /// One Toolhelp snapshot of every process (pid, parent pid, exe name). Far cheaper than
    /// a WMI query per call, and it yields the whole tree at once.
    /// </summary>
    internal static List<ProcessInfo> TakeSnapshot()
    {
        var result = new List<ProcessInfo>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandle)
            return result;

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry))
                return result;

            do
            {
                result.Add(new ProcessInfo((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile));
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    /// <summary>All processes below <paramref name="rootPid"/> in the tree (children, grandchildren, ...).</summary>
    internal static IEnumerable<ProcessInfo> EnumerateDescendants(IReadOnlyList<ProcessInfo> snapshot, int rootPid)
    {
        var childrenByParent = new Dictionary<int, List<ProcessInfo>>();
        foreach (var p in snapshot)
        {
            if (!childrenByParent.TryGetValue(p.ParentPid, out var list))
                childrenByParent[p.ParentPid] = list = [];
            list.Add(p);
        }

        var queue = new Queue<int>();
        var visited = new HashSet<int> { rootPid };
        queue.Enqueue(rootPid);

        while (queue.Count > 0)
        {
            var pid = queue.Dequeue();
            if (!childrenByParent.TryGetValue(pid, out var children))
                continue;

            foreach (var child in children)
            {
                // Guard against pid reuse loops in the snapshot.
                if (!visited.Add(child.Pid))
                    continue;

                yield return child;
                queue.Enqueue(child.Pid);
            }
        }
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly nint InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(nint hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(nint hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}
