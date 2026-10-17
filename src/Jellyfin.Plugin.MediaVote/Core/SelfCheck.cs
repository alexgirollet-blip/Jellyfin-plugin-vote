using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>Result of one self-check line, e.g. "Users: OK, 16 found".</summary>
public sealed record SelfCheckResult(string Name, bool Ok, string Detail);

/// <summary>
/// Runs a list of independent checks. Each one is isolated: an exception (including a MissingMethodException
/// thrown when a method is JIT-compiled against a different Jellyfin version) becomes a failed line with a
/// readable message, and the remaining checks still run.
/// </summary>
public static class SelfCheckRunner
{
    public static List<SelfCheckResult> Run(IEnumerable<(string Name, Func<string> Check)> checks)
    {
        var results = new List<SelfCheckResult>();
        foreach (var (name, check) in checks)
        {
            try
            {
                results.Add(new SelfCheckResult(name, true, check()));
            }
#pragma warning disable CA1031 // a failing check must never stop the others
            catch (Exception ex)
#pragma warning restore CA1031
            {
                results.Add(new SelfCheckResult(name, false, ErrorText.Describe(ex)));
            }
        }

        return results;
    }
}
