using System;
using System.Linq;
using Jellyfin.Plugin.MediaVote.Core;
using MediaVoteKillSwitch = Jellyfin.Plugin.MediaVote.Services.KillSwitch;
using MediaVoteUsers = Jellyfin.Plugin.MediaVote.Services.JellyfinUserDirectory;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaVote.Web;

/// <summary>Payload Home Screen Sections sends to section result handlers.</summary>
public sealed class SectionPayload
{
    public Guid UserId { get; set; }

    public string? AdditionalData { get; set; }
}

/// <summary>
/// Results for the optional "Media Vote: current leaders" Home Screen Sections row.
/// Has a parameterless constructor on purpose: Home Screen Sections creates it with its own service
/// provider, so we resolve our services from Media Vote's provider instead.
/// Shows nothing when there is no open vote, the user is not eligible, or the section is disabled.
/// </summary>
public sealed class HomeSectionResults
{
    /// <summary>Set at start-up by the startup task.</summary>
    internal static IServiceProvider? Services { get; set; }

    public QueryResult<BaseItemDto> GetResults(SectionPayload payload)
    {
        try
        {
            return GetResultsCore(payload);
        }
#pragma warning disable CA1031 // must never break Home Screen Sections' rendering
        catch (Exception)
#pragma warning restore CA1031
        {
            return new QueryResult<BaseItemDto>();
        }
    }

    private static QueryResult<BaseItemDto> GetResultsCore(SectionPayload payload)
    {
        var empty = new QueryResult<BaseItemDto>();
        var sp = Services;
        var cfg = Plugin.Instance?.Configuration;
        if (sp is null || payload is null || cfg is null || !cfg.EnableHomeSection || MediaVoteKillSwitch.IsActive)
        {
            return empty;
        }

        var votes = sp.GetRequiredService<VoteService>();
        var session = votes.GetOpenSession();
        if (session is null || !votes.IsEligible(session, payload.UserId))
        {
            return empty;
        }

        var user = sp.GetRequiredService<IUserManager>().GetUserById(payload.UserId);
        if (user is null || AccessRules.IsHiddenFor(MediaVoteKillSwitch.IsActive, cfg.AdminPreviewOnly, sp.GetRequiredService<MediaVoteUsers>().IsAdministrator(payload.UserId)))
        {
            return empty;
        }

        // Live tallies hidden: show the user's own picks instead of the ranking.
        var ids = session.Settings.ShowLiveTallies
            ? Tally.Count(session, includeNames: false).Where(t => t.Score > 0).Take(16).Select(t => t.ItemId).ToList()
            : session.Votes.Where(v => v.UserId == payload.UserId).Select(v => v.ItemId).Distinct().Take(16).ToList();
        if (ids.Count == 0)
        {
            return empty;
        }

        var library = sp.GetRequiredService<ILibraryManager>();
        var items = library.GetItemList(new InternalItemsQuery(user) { ItemIds = ids.ToArray() });
        var order = ids.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i);
        var sorted = items.OrderBy(i => order.TryGetValue(i.Id, out var o) ? o : int.MaxValue).ToList();
        var dtos = sp.GetRequiredService<IDtoService>().GetBaseItemDtos(sorted, new DtoOptions(true), user, null);
        return new QueryResult<BaseItemDto>(0, dtos.Count, dtos);
    }
}
