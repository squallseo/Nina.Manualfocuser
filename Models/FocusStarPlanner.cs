using System;
using NINA.Astrometry;

namespace Cwseo.NINA.ManualFocuser.Models {
    public sealed class FocusStarSuggestion {
        public string Name { get; init; }
        public Coordinates Coordinates { get; init; }
        public double Magnitude { get; init; }
        public double Altitude { get; init; }
        public double Azimuth { get; init; }
        public string Display => $"{Name} · mag {Magnitude:F1} · alt {Altitude:F1}° · az {Azimuth:F1}°";
    }

    public static class FocusStarPlanner {
        public static FocusStarSuggestion Calculate(string name, Coordinates coordinates, double magnitude,
            double latitude, double longitude, double elevation, DateTime utc) {
            if (coordinates == null) throw new ArgumentNullException(nameof(coordinates));
            if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || !double.IsFinite(elevation) ||
                Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180) throw new ArgumentException("Set a valid NINA profile location.");
            if (utc.Kind != DateTimeKind.Utc) throw new ArgumentException("Focus-star calculation requires UTC.");
            var horizontal = coordinates.Transform(Angle.ByDegree(latitude), Angle.ByDegree(longitude), elevation, utc);
            return new FocusStarSuggestion { Name = name, Coordinates = coordinates, Magnitude = magnitude,
                Altitude = horizontal.Altitude.Degree, Azimuth = horizontal.Azimuth.Degree };
        }

        public static bool IsAboveHorizon(FocusStarSuggestion star, double minimumAltitude, double horizonAltitude) =>
            star != null && double.IsFinite(star.Altitude) && double.IsFinite(minimumAltitude) &&
            double.IsFinite(horizonAltitude) && star.Altitude >= Math.Max(minimumAltitude, horizonAltitude + 5);
    }
}
