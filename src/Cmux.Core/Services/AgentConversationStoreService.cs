using System.Text;
using System.Text.Json;
using Cmux.Core.Models;

namespace Cmux.Core.Services;

public sealed class AgentConversationStoreService
{
    // Transcripts up to this size are read whole; larger ones only have their tail read.
    private const long MaxWholeFileReadBytes = 4 * 1024 * 1024;
    private const int TailReadBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions JsonLineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly object _lock = new();
    private readonly Dictionary<string, AgentConversationThread> _threads = new(StringComparer.Ordinal);
    private readonly string _rootDir;
    private readonly string _threadsDir;
    private readonly string _threadsIndexPath;
    private bool _indexLoaded;

    public event Action? StoreChanged;

    /// <summary>
    /// Constructing the store does no IO. The index is read on first use and directories are created on first write.
    /// </summary>
    public AgentConversationStoreService(string? rootDirectory = null)
    {
        _rootDir = rootDirectory ?? Path.Combine(CmuxPaths.DataDirectory, "agent");
        _threadsDir = Path.Combine(_rootDir, "threads");
        _threadsIndexPath = Path.Combine(_rootDir, "threads.json");
    }

    public string GetRootDirectoryPath()
    {
        EnsureStorage();
        return _rootDir;
    }

    public AgentConversationThread CreateThread(string workspaceId, string surfaceId, string paneId, string agentName)
    {
        lock (_lock)
        {
            EnsureLoaded();
            EnsureStorage();

            var createdAt = DateTime.UtcNow;
            var thread = new AgentConversationThread
            {
                Id = Guid.NewGuid().ToString("N"),
                WorkspaceId = workspaceId ?? "",
                SurfaceId = surfaceId ?? "",
                PaneId = paneId ?? "",
                AgentName = string.IsNullOrWhiteSpace(agentName) ? "assistant" : agentName.Trim(),
                Title = $"{(string.IsNullOrWhiteSpace(agentName) ? "assistant" : agentName.Trim())} · {createdAt.ToLocalTime():yyyy-MM-dd HH:mm}",
                CreatedAtUtc = createdAt,
                UpdatedAtUtc = createdAt,
            };

            _threads[thread.Id] = thread;
            PersistThreadsIndex();
            NotifyChanged();
            return CloneThread(thread);
        }
    }

    public AgentConversationThread? GetThread(string threadId)
    {
        lock (_lock)
        {
            EnsureLoaded();
            if (!_threads.TryGetValue(threadId, out var thread))
                return null;

            return CloneThread(thread);
        }
    }

    public IReadOnlyList<AgentConversationThread> GetThreads(string workspaceId, string surfaceId, string paneId, int maxEntries = 200)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var filterByWorkspace = !string.IsNullOrWhiteSpace(workspaceId);
            var filterBySurface = !string.IsNullOrWhiteSpace(surfaceId);
            var filterByPane = !string.IsNullOrWhiteSpace(paneId);
            return _threads.Values
                .Where(t => (!filterByWorkspace || string.Equals(t.WorkspaceId, workspaceId ?? "", StringComparison.Ordinal))
                            && (!filterBySurface || string.Equals(t.SurfaceId, surfaceId ?? "", StringComparison.Ordinal))
                            && (!filterByPane || string.Equals(t.PaneId, paneId ?? "", StringComparison.Ordinal)))
                .OrderByDescending(t => t.UpdatedAtUtc)
                .Take(Math.Max(1, maxEntries))
                .Select(CloneThread)
                .ToList()
                .AsReadOnly();
        }
    }

    public IReadOnlyList<AgentConversationThread> SearchThreads(string workspaceId, string surfaceId, string paneId, string query, int maxEntries = 200)
    {
        lock (_lock)
        {
            var trimmed = (query ?? "").Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return GetThreads(workspaceId, surfaceId, paneId, maxEntries);

            EnsureLoaded();
            var filterByWorkspace = !string.IsNullOrWhiteSpace(workspaceId);
            var filterBySurface = !string.IsNullOrWhiteSpace(surfaceId);
            var filterByPane = !string.IsNullOrWhiteSpace(paneId);
            return _threads.Values
                .Where(t => (!filterByWorkspace || string.Equals(t.WorkspaceId, workspaceId ?? "", StringComparison.Ordinal))
                            && (!filterBySurface || string.Equals(t.SurfaceId, surfaceId ?? "", StringComparison.Ordinal))
                            && (!filterByPane || string.Equals(t.PaneId, paneId ?? "", StringComparison.Ordinal)))
                .Where(t => t.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                            || t.LastMessagePreview.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                            || t.AgentName.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.UpdatedAtUtc)
                .Take(Math.Max(1, maxEntries))
                .Select(CloneThread)
                .ToList()
                .AsReadOnly();
        }
    }

    /// <summary>Returns the newest <paramref name="maxEntries"/> messages. Large transcripts are read from their tail only.</summary>
    public IReadOnlyList<AgentConversationMessage> GetMessages(string threadId, int maxEntries = 1000)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(threadId))
                return [];

            var path = GetThreadMessagesPath(threadId);
            if (!File.Exists(path))
                return [];

            var messages = new List<AgentConversationMessage>();
            ReadMessagesFromFile(path, messages);

            return messages
                .OrderBy(m => m.CreatedAtUtc)
                .TakeLast(Math.Max(1, maxEntries))
                .Select(CloneMessage)
                .ToList()
                .AsReadOnly();
        }
    }

    public AgentConversationMessage AppendMessage(AgentConversationMessage message)
    {
        lock (_lock)
        {
            EnsureLoaded();
            EnsureStorage();

            if (message == null)
                throw new ArgumentNullException(nameof(message));

            if (string.IsNullOrWhiteSpace(message.ThreadId))
                throw new InvalidOperationException("ThreadId is required to append a message.");

            if (!_threads.TryGetValue(message.ThreadId, out var thread))
                throw new InvalidOperationException($"Conversation thread '{message.ThreadId}' was not found.");

            if (string.IsNullOrWhiteSpace(message.Id))
                message.Id = Guid.NewGuid().ToString("N");

            if (message.CreatedAtUtc == default)
                message.CreatedAtUtc = DateTime.UtcNow;

            message.Content ??= "";
            message.Role = string.IsNullOrWhiteSpace(message.Role) ? "user" : message.Role.Trim().ToLowerInvariant();

            var path = GetThreadMessagesPath(message.ThreadId);
            var line = JsonSerializer.Serialize(message, JsonLineOptions);
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);

            thread.MessageCount++;
            thread.UpdatedAtUtc = message.CreatedAtUtc;
            thread.TotalInputTokens += Math.Max(0, message.InputTokens);
            thread.TotalOutputTokens += Math.Max(0, message.OutputTokens);
            thread.TotalTokens += Math.Max(0, message.TotalTokens);
            if (message.IsCompactionSummary)
                thread.CompactionCount++;

            var preview = (message.Content ?? "").Replace("\r", "", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Trim();
            if (preview.Length > 160)
                preview = preview[..160] + "...";
            thread.LastMessagePreview = preview;

            PersistThreadsIndex();
            NotifyChanged();

            return CloneMessage(message);
        }
    }

    private static AgentConversationThread CloneThread(AgentConversationThread source)
    {
        return new AgentConversationThread
        {
            Id = source.Id,
            WorkspaceId = source.WorkspaceId,
            SurfaceId = source.SurfaceId,
            PaneId = source.PaneId,
            AgentName = source.AgentName,
            Title = source.Title,
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
            MessageCount = source.MessageCount,
            TotalInputTokens = source.TotalInputTokens,
            TotalOutputTokens = source.TotalOutputTokens,
            TotalTokens = source.TotalTokens,
            CompactionCount = source.CompactionCount,
            LastMessagePreview = source.LastMessagePreview,
        };
    }

    private static AgentConversationMessage CloneMessage(AgentConversationMessage source)
    {
        return new AgentConversationMessage
        {
            Id = source.Id,
            ThreadId = source.ThreadId,
            CreatedAtUtc = source.CreatedAtUtc,
            Role = source.Role,
            Content = source.Content,
            Provider = source.Provider,
            Model = source.Model,
            ToolName = source.ToolName,
            InputTokens = source.InputTokens,
            OutputTokens = source.OutputTokens,
            TotalTokens = source.TotalTokens,
            IsCompactionSummary = source.IsCompactionSummary,
        };
    }

    private string GetThreadMessagesPath(string threadId)
    {
        return Path.Combine(_threadsDir, $"{threadId}.jsonl");
    }

    private void EnsureStorage()
    {
        Directory.CreateDirectory(_rootDir);
        Directory.CreateDirectory(_threadsDir);
    }

    /// <summary>Reads threads.json on first use. Callers must hold <see cref="_lock"/>.</summary>
    private void EnsureLoaded()
    {
        if (_indexLoaded)
            return;

        _indexLoaded = true;
        LoadThreadsIndex();
    }

    private void LoadThreadsIndex()
    {
        _threads.Clear();
        if (!File.Exists(_threadsIndexPath))
            return;

        try
        {
            var json = File.ReadAllText(_threadsIndexPath, Encoding.UTF8);
            var list = JsonSerializer.Deserialize<List<AgentConversationThread>>(json, JsonOptions) ?? [];
            foreach (var t in list)
            {
                if (string.IsNullOrWhiteSpace(t.Id))
                    continue;
                _threads[t.Id] = t;
            }
        }
        catch (Exception ex)
        {
            // Start with an empty index rather than failing the agent; the file is rewritten on the next change.
            Log.Debug($"[agent] threads index load failed: {ex.GetType().Name}");
        }
    }

    private void PersistThreadsIndex()
    {
        var list = _threads.Values
            .OrderByDescending(t => t.UpdatedAtUtc)
            .ToList();

        var json = JsonSerializer.Serialize(list, JsonOptions);
        File.WriteAllText(_threadsIndexPath, json, Encoding.UTF8);
    }

    private void NotifyChanged()
    {
        try { StoreChanged?.Invoke(); }
        catch (Exception ex) { Log.Debug($"[agent] store change handler failed: {ex.GetType().Name}"); }
    }

    private static void ReadMessagesFromFile(string filePath, List<AgentConversationMessage> output)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
        if (stream.Length == 0)
            return;

        if (stream.Length > MaxWholeFileReadBytes)
        {
            ReadTailLines(stream, output);
            return;
        }

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        ParseWholeFile(bytes, filePath, output);
    }

    /// <summary>Parses a transcript that is small enough to hold in memory. Accepts legacy multi-line JSON.</summary>
    private static void ParseWholeFile(byte[] bytes, string filePath, List<AgentConversationMessage> output)
    {
        ReadOnlySpan<byte> payload = bytes;
        if (payload.Length >= 3 && payload[0] == 0xEF && payload[1] == 0xBB && payload[2] == 0xBF)
            payload = payload[3..];

        if (payload.Length == 0)
            return;

        try
        {
            var reader = new Utf8JsonReader(payload, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                AllowMultipleValues = true,
            });

            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                    continue;

                using var doc = JsonDocument.ParseValue(ref reader);
                var msg = JsonSerializer.Deserialize<AgentConversationMessage>(doc.RootElement.GetRawText(), JsonLineOptions);
                if (msg != null)
                    output.Add(msg);
            }
        }
        catch (Exception ex)
        {
            // Malformed legacy content: fall back to line-by-line parsing.
            Log.Debug($"[agent] transcript parse fallback: {ex.GetType().Name}");
            output.Clear();
            ParseLines(File.ReadLines(filePath, Encoding.UTF8), output);
        }
    }

    /// <summary>Reads only the last window of a large transcript. The first, possibly partial, line is dropped.</summary>
    private static void ReadTailLines(FileStream stream, List<AgentConversationMessage> output)
    {
        var start = Math.Max(0, stream.Length - TailReadBytes);
        stream.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[stream.Length - start];
        stream.ReadExactly(buffer);

        int offset = 0;
        if (start > 0)
        {
            int newline = Array.IndexOf(buffer, (byte)'\n');
            if (newline < 0)
                return;
            offset = newline + 1;
        }

        var text = Encoding.UTF8.GetString(buffer, offset, buffer.Length - offset);
        ParseLines(text.Split('\n'), output);
    }

    private static void ParseLines(IEnumerable<string> lines, List<AgentConversationMessage> output)
    {
        foreach (var line in lines)
        {
            var normalizedLine = line.Trim().TrimStart('﻿');
            if (normalizedLine.Length == 0)
                continue;

            try
            {
                var msg = JsonSerializer.Deserialize<AgentConversationMessage>(normalizedLine, JsonLineOptions);
                if (msg != null)
                    output.Add(msg);
            }
            catch (JsonException)
            {
                // Skip malformed lines; a partial write can leave one behind.
            }
        }
    }
}
