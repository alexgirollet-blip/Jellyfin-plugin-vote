using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.MediaVote.Core;

namespace Jellyfin.Plugin.MediaVote.Storage;

/// <summary>One line of the audit log.</summary>
public sealed class AuditEntry
{
    public DateTime TimeUtc { get; set; }

    public string Action { get; set; } = string.Empty;

    public string Actor { get; set; } = string.Empty;

    public Guid? SessionId { get; set; }

    public string Details { get; set; } = string.Empty;
}

/// <summary>
/// Append-only JSON-lines audit log (audit.jsonl), rotated at 5 MB (one previous file kept).
/// Never contains API keys: callers only pass vote and deletion details.
/// </summary>
public sealed class FileAuditLog : IAuditLog
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private readonly string _path;
    private readonly object _lock = new();
    private readonly IClock _clock;

    public FileAuditLog(string directory, IClock clock)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "audit.jsonl");
        _clock = clock;
    }

    public void Write(string action, string actor, Guid? sessionId, string details)
    {
        var line = JsonSerializer.Serialize(
            new AuditEntry { TimeUtc = _clock.UtcNow, Action = action, Actor = actor, SessionId = sessionId, Details = details },
            JsonVoteStore.JsonOptions);
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                {
                    File.Move(_path, _path + ".1", overwrite: true);
                }

                File.AppendAllText(_path, line + "\n", Encoding.UTF8);
            }
            catch (IOException)
            {
                // The audit log must never break voting; failures surface in the Jellyfin log via the caller's logger.
            }
        }
    }

    /// <summary>Most recent entries first.</summary>
    public IReadOnlyList<AuditEntry> Read(int max, Guid? sessionId = null)
    {
        var lines = new List<string>();
        lock (_lock)
        {
            foreach (var p in new[] { _path + ".1", _path })
            {
                if (File.Exists(p))
                {
                    lines.AddRange(File.ReadAllLines(p));
                }
            }
        }

        var result = new List<AuditEntry>();
        for (int i = lines.Count - 1; i >= 0 && result.Count < max; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            try
            {
                var e = JsonSerializer.Deserialize<AuditEntry>(lines[i], JsonVoteStore.JsonOptions);
                if (e is not null && (sessionId is null || e.SessionId == sessionId))
                {
                    result.Add(e);
                }
            }
            catch (JsonException)
            {
                // skip a torn line
            }
        }

        return result;
    }

    public IEnumerable<AuditEntry> ReadAll(Guid? sessionId) => Read(int.MaxValue, sessionId).Reverse();
}
