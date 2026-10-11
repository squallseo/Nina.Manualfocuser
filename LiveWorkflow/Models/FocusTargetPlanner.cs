using System;
using System.Collections.Generic;
using System.Linq;
using NINA.Astrometry;

namespace Cwseo.NINA.ManualFocuser.LiveWorkflow.Models
{
    public static class FocusTargetPlanner
    {
        public static IReadOnlyList<FocusStarSuggestion> SolarSystemTargets { get; } = Array.AsReadOnly(new[]
        {
            Solar(NOVAS.Body.Moon, "달"), Solar(NOVAS.Body.Mercury, "수성"), Solar(NOVAS.Body.Venus, "금성"),
            Solar(NOVAS.Body.Mars, "화성"), Solar(NOVAS.Body.Jupiter, "목성"), Solar(NOVAS.Body.Saturn, "토성"),
            Solar(NOVAS.Body.Uranus, "천왕성"), Solar(NOVAS.Body.Neptune, "해왕성")
        });

        private static FocusStarSuggestion Solar(NOVAS.Body body, string alias) => new()
        {
            Kind = FocusTargetKind.SolarSystem, Id = body.ToString(), Name = body.ToString(), SolarBody = body,
            Magnitude = double.NaN, Aliases = new[] { alias }
        };

        public static bool Matches(FocusStarSuggestion target, string query)
        {
            string key = SearchKey(query);
            return key.Length == 0 || new[] { target.Id, target.Name }.Concat(target.Aliases ?? Array.Empty<string>())
                .Any(name => SearchKey(name).Contains(key, StringComparison.Ordinal));
        }

        private static string SearchKey(string text) => new string((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

        public static FocusStarSuggestion At(FocusStarSuggestion target, double latitude, double longitude, double elevation, DateTime utc)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            ValidateSite(latitude, longitude, elevation, utc);
            Coordinates coordinates = target.Kind == FocusTargetKind.SolarSystem
                ? SolarCoordinates(target.SolarBody ?? throw new ArgumentException("Select a Moon or planet target."), latitude, longitude, elevation, utc)
                : target.Coordinates;
            var position = FocusStarPlanner.Calculate(target.Name, coordinates, target.Magnitude, latitude, longitude, elevation, utc);
            return new FocusStarSuggestion
            {
                Kind = target.Kind, Id = target.Id, Name = target.Name, Aliases = target.Aliases, Description = target.Description,
                SolarBody = target.SolarBody, Magnitude = target.Magnitude, Coordinates = position.Coordinates,
                Altitude = position.Altitude, Azimuth = position.Azimuth
            };
        }

        public static bool IsAboveHorizon(FocusStarSuggestion target, double starMinimumAltitude, double horizonAltitude) =>
            FocusStarPlanner.IsAboveHorizon(target, target?.Kind == FocusTargetKind.Stars ? starMinimumAltitude : 5, horizonAltitude);

        public static Coordinates SolarCoordinates(NOVAS.Body body, double latitude, double longitude, double elevation, DateTime utc)
        {
            ValidateSite(latitude, longitude, elevation, utc);
            if (!SolarSystemTargets.Any(t => t.SolarBody == body)) throw new ArgumentOutOfRangeException(nameof(body));
            // NOVAS expects terrestrial time, not UTC. SOFA supplies leap seconds.
            double utc1 = 0, utc2 = 0, tai1 = 0, tai2 = 0, tt1 = 0, tt2 = 0;
            if (SOFA.Dtf2d("UTC", utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute,
                utc.Second + utc.Millisecond / 1000.0, ref utc1, ref utc2) < 0 ||
                SOFA.UtcTai(utc1, utc2, ref tai1, ref tai2) < 0 || SOFA.TaiTt(tai1, tai2, ref tt1, ref tt2) < 0)
                throw new InvalidOperationException("Could not calculate the target time.");
            var observer = new NOVAS.Observer
            {
                Where = (short)NOVAS.ObserverLocation.EarthSurface,
                OnSurf = new NOVAS.OnSurface { Latitude = latitude, Longitude = longitude, Height = elevation }
            };
            var celestialObject = new NOVAS.CelestialObject
            {
                Type = (short)NOVAS.ObjectType.MajorPlanetSunOrMoon, Number = (short)body,
                Name = body.ToString(), Star = new NOVAS.CatalogueEntry()
            };
            var position = new NOVAS.SkyPosition();
            short result = NOVAS.Place(tt1 + tt2, celestialObject, observer, AstroUtil.DeltaT(utc),
                NOVAS.CoordinateSystem.EquinoxOfDate, NOVAS.Accuracy.Full, ref position);
            if (result != 0 || !double.IsFinite(position.RA) || !double.IsFinite(position.Dec) || position.Dis <= 0)
                throw new InvalidOperationException($"Could not calculate {body} using NINA's ephemeris (code {result}).");
            return new Coordinates(Angle.ByHours(position.RA), Angle.ByDegree(position.Dec), Epoch.JNOW, utc);
        }

        private static void ValidateSite(double latitude, double longitude, double elevation, DateTime utc)
        {
            if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || !double.IsFinite(elevation) ||
                Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180) throw new ArgumentException("Set a valid NINA profile location.");
            if (utc.Kind != DateTimeKind.Utc) throw new ArgumentException("Target calculation requires UTC.");
        }
    }
}
