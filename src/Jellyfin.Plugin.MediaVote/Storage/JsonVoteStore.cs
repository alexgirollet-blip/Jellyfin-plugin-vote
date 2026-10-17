using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.MediaVote.Core;

namespace Jellyfin.Plugin.MediaVote.Storage;

/// <summary>
/// Stores vote sessions in a single JSON file in the plugin's data directory.
/// Copy-on-write: published session objects are never mutated, so readers need no lock.
/// Writes go to a temp file that is atomically renamed over the real file.
/// </summary>
public sealed class JsonVoteStore : IVoteStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    private readonly object _lock = new();
    private List<VoteSession> _sessions;

    public JsonVoteStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "votes.json");
        _sessions = Load(_path);
    }

    public string FilePath => _path;

    public IReadOnlyList<VoteSession> GetAll() => _sessions;

    public VoteSession? Get(Guid id) => _sessions.FirstOrDefault(s => s.Id == id);

    public void Add(VoteSession session)
    {
        lock (_lock)
        {
            if (_sessions.Any(s => s.Id == session.Id))
            {
                throw new InvalidOperationException("Duplicate session id.");
            }

            var next = _sessions.ToList();
            next.Add(Clone(session));
            Persist(next);
            _sessions = next;
        }
    }

    public T Update<T>(Guid id, Func<VoteSession, T> mutate)
    {
        lock (_lock)
        {
            int index = _sessions.FindIndex(s => s.Id == id);
            if (index < 0)
            {
                throw new KeyNotFoundException("Vote session not found.");
            }

            var copy = Clone(_sessions[index]);
            T result = mutate(copy);
            var next = _sessions.ToList();
            next[index] = copy;
            Persist(next);
            _sessions = next;
            return result;
        }
    }

    internal static VoteSession Clone(VoteSession s)
        => JsonSerializer.Deserialize<VoteSession>(JsonSerializer.SerializeToUtf8Bytes(s, JsonOptions), JsonOptions)!;

    private static List<VoteSession> Load(string path)
    {
        if (!File.Exists(path))
        {
            return new List<VoteSession>();
        }

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<VoteSession>();
        }

        // A corrupt file must not be silently replaced: keep a copy and fail loudly.
        try
        {
            return JsonSerializer.Deserialize<StoreFile>(json, JsonOptions)?.Sessions ?? new List<VoteSession>();
        }
        catch (JsonException)
        {
            var backup = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            File.Copy(path, backup, overwrite: true);
            throw;
        }
    }

    private void Persist(List<VoteSession> sessions)
    {
        var tmp = _path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, new StoreFile { Version = 1, Sessions = sessions }, JsonOptions);
            fs.Flush(true);
        }

        File.Move(tmp, _path, overwrite: true);
    }

    private sealed class StoreFile
    {
        public int Version { get; set; } = 1;

        public List<VoteSession> Sessions { get; set; } = new();
    }
}
