using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>CSV export of a vote's ranking and outcome (admin only).</summary>
public static class ResultCsv
{
    public static string Build(VoteSession s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Rank,Title,Year,Type,Library,Score,RemoveVotes,KeepVotes,RemoveVoters,KeepVoters,SizeBytes,SizeGB,Outcome,Message,Paths,ExternalSteps");
        var ranking = s.Result?.Ranking ?? Tally.Count(s);
        var candidates = s.Candidates.ToDictionary(c => c.ItemId);
        var outcomes = (s.Result?.Items ?? new()).ToDictionary(o => o.ItemId);
        int rank = 0;
        foreach (var t in ranking)
        {
            rank++;
            candidates.TryGetValue(t.ItemId, out var c);
            outcomes.TryGetValue(t.ItemId, out var o);
            long size = o?.SizeBytes > 0 ? o.SizeBytes : c?.SizeBytes ?? 0;
            var fields = new[]
            {
                rank.ToString(CultureInfo.InvariantCulture),
                c?.Name ?? o?.Name ?? t.ItemId.ToString(),
                c?.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                (c?.Kind ?? o?.Kind ?? MediaKind.Movie).ToString(),
                c?.LibraryName ?? string.Empty,
                t.Score.ToString(CultureInfo.InvariantCulture),
                t.RemoveVotes.ToString(CultureInfo.InvariantCulture),
                t.KeepVotes.ToString(CultureInfo.InvariantCulture),
                string.Join("; ", t.RemoveVoters),
                string.Join("; ", t.KeepVoters),
                size.ToString(CultureInfo.InvariantCulture),
                (size / 1024d / 1024d / 1024d).ToString("0.00", CultureInfo.InvariantCulture),
                o?.Status.ToString() ?? string.Empty,
                o?.Message ?? string.Empty,
                o is null ? c?.Path ?? string.Empty : string.Join(" | ", o.Paths),
                o is null ? string.Empty : string.Join(" | ", o.ExternalSteps.Select(e => $"{e.Service}: {e.Status} {e.Message}".Trim()))
            };
            sb.AppendLine(string.Join(",", fields.Select(Escape)));
        }

        return sb.ToString();
    }

    /// <summary>Quotes a field and neutralises spreadsheet formula injection (=, +, -, @ at the start).</summary>
    public static string Escape(string value)
    {
        value ??= string.Empty;
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0], StringComparison.Ordinal))
        {
            value = "'" + value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
