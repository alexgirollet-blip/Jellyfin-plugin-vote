using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.MediaVote.Core;

namespace Jellyfin.Plugin.MediaVote.Storage;

/// <summary>Vote templates in templates.json in the plugin's data folder (atomic writes, like the vote store).</summary>
public sealed class JsonTemplateStore : ITemplateStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private List<VoteTemplate> _items;

    public JsonTemplateStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "templates.json");
        _items = Load();
    }

    public IReadOnlyList<VoteTemplate> GetAll() => _items.Select(Clone).ToList();

    public void Save(VoteTemplate template)
    {
        lock (_lock)
        {
            var next = _items.Where(t => t.Id != template.Id).ToList();
            next.Add(Clone(template));
            Persist(next);
            _items = next;
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var next = _items.Where(t => t.Id != id).ToList();
            if (next.Count == _items.Count)
            {
                return false;
            }

            Persist(next);
            _items = next;
            return true;
        }
    }

    private static VoteTemplate Clone(VoteTemplate t)
        => JsonSerializer.Deserialize<VoteTemplate>(JsonSerializer.SerializeToUtf8Bytes(t, JsonVoteStore.JsonOptions), JsonVoteStore.JsonOptions)!;

    private List<VoteTemplate> Load()
    {
        if (!File.Exists(_path))
        {
            return new List<VoteTemplate>();
        }

        var json = File.ReadAllText(_path);
        return string.IsNullOrWhiteSpace(json) ? new List<VoteTemplate>() : JsonSerializer.Deserialize<List<VoteTemplate>>(json, JsonVoteStore.JsonOptions) ?? new List<VoteTemplate>();
    }

    private void Persist(List<VoteTemplate> items)
    {
        var tmp = _path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, items, JsonVoteStore.JsonOptions);
            fs.Flush(true);
        }

        File.Move(tmp, _path, overwrite: true);
    }
}
