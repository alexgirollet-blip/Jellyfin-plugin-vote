using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaVote.Services;

/// <summary>
/// Optional, read-only enrichment from the Playback Reporting plugin's SQLite database
/// (playback_reporting.db in Jellyfin's data folder, table PlaybackActivity).
/// There is no compile-time dependency: the SQLite ADO.NET provider that Jellyfin itself
/// already loads is found at runtime and used through System.Data.Common base classes.
/// If anything is missing or different, this silently returns no data.
/// </summary>
public sealed class PlaybackReportingReader
{
    private readonly string _dbPath;
    private readonly ILogger _logger;

    public PlaybackReportingReader(string dataPath, ILogger logger)
    {
        _dbPath = Path.Combine(dataPath, "playback_reporting.db");
        _logger = logger;
    }

    public bool DatabaseExists => File.Exists(_dbPath);

    /// <summary>Returns per-item play count and last play time, keyed by Jellyfin item id.</summary>
    public IReadOnlyDictionary<Guid, (int Count, DateTime? Last)> Read()
    {
        var result = new Dictionary<Guid, (int, DateTime?)>();
        if (!DatabaseExists)
        {
            return result;
        }

        var connType = AssemblyLoadContext.All
            .SelectMany(c => c.Assemblies)
            .Where(a => a.GetName().Name == "Microsoft.Data.Sqlite")
            .Select(a => a.GetType("Microsoft.Data.Sqlite.SqliteConnection"))
            .FirstOrDefault(t => t is not null);
        if (connType is null)
        {
            _logger.LogDebug("Media Vote: SQLite provider not loaded; skipping Playback Reporting data");
            return result;
        }

        try
        {
            using var conn = (DbConnection)Activator.CreateInstance(connType, $"Data Source={_dbPath};Mode=ReadOnly")!;
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ItemId, COUNT(*), MAX(DateCreated) FROM PlaybackActivity GROUP BY ItemId";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var idText = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
                if (!Guid.TryParse(idText, out var id))
                {
                    continue;
                }

                int count = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                DateTime? last = null;
                if (!reader.IsDBNull(2) && DateTime.TryParse(Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
                {
                    last = d;
                }

                result[id] = (count, last);
            }
        }
#pragma warning disable CA1031 // optional data source: never fail the caller
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Media Vote: could not read Playback Reporting database; using Jellyfin user data only");
            result.Clear();
        }

        return result;
    }
}
