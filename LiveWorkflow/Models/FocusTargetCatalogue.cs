using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NINA.Astrometry;

namespace Cwseo.NINA.ManualFocuser.LiveWorkflow.Models
{
    public enum FocusTargetKind { Stars, DeepSky, SolarSystem }

    public interface IFocusTargetCatalogue
    {
        Task<IReadOnlyList<FocusStarSuggestion>> SearchAsync(FocusTargetKind kind, string query, CancellationToken token);
    }

    /// <summary>Use the host's local catalogues and native ephemeris; no network lookup.</summary>
    public sealed class NinaFocusTargetCatalogue : IFocusTargetCatalogue
    {
        public const int DeepSkyLimit = 100;

        public async Task<IReadOnlyList<FocusStarSuggestion>> SearchAsync(FocusTargetKind kind, string query, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            switch (kind)
            {
                case FocusTargetKind.Stars:
                    var stars = await new DatabaseInteraction().GetBrightStars();
                    token.ThrowIfCancellationRequested();
                    return stars.Where(s => s.Coordinates != null).Select(s => new FocusStarSuggestion
                    {
                        Kind = kind, Id = s.Name, Name = s.Name, Coordinates = s.Coordinates, Magnitude = s.Magnitude
                    }).ToArray();
                case FocusTargetKind.DeepSky:
                    var parameters = new DatabaseInteraction.DeepSkyObjectSearchParams
                    {
                        ObjectName = NormalizeCatalogueQuery(query), Limit = DeepSkyLimit
                    };
                    var objects = await new DatabaseInteraction().GetDeepSkyObjects(string.Empty, null, parameters, token);
                    token.ThrowIfCancellationRequested();
                    return objects.Where(d => d.Coordinates != null).Select(d => new FocusStarSuggestion
                    {
                        Kind = kind, Id = d.Id, Name = d.Name, Coordinates = d.Coordinates,
                        Magnitude = d.Magnitude ?? double.NaN, Aliases = d.AlsoKnownAs?.ToArray() ?? Array.Empty<string>(),
                        Description = d.DSOType
                    }).ToArray();
                case FocusTargetKind.SolarSystem:
                    return FocusTargetPlanner.SolarSystemTargets;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        public static string NormalizeCatalogueQuery(string query)
        {
            string text = (query ?? "").Trim();
            var catalogue = Regex.Match(text, @"^(M|NGC|IC)\s*0*(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return catalogue.Success ? catalogue.Groups[1].Value.ToUpperInvariant() + catalogue.Groups[2].Value : text;
        }
    }
}
