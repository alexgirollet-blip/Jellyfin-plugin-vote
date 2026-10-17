using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>Marks candidates as removable (or not) via Sonarr/Radarr, from a read-only library snapshot.</summary>
public static class Removability
{
    public static void Annotate(IEnumerable<CandidateItem> candidates, ArrLibrary library)
    {
        foreach (var c in candidates)
        {
            var m = ArrMatcher.Match(MatchKey.From(c), library);
            c.Removable = m.State switch
            {
                ArrMatchState.Matched => true,
                ArrMatchState.Unreachable => null,
                _ => false
            };
            c.RemovalNote = m.IsMatched ? m.Entry!.Describe() : m.Reason;
        }
    }
}
