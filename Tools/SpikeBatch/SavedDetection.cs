using Cwseo.NINA.ManualFocuser.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cwseo.NINA.ManualFocuser.Tools.SpikeBatch {
    /// <summary>Reads Hocus Focus's saved results as data, without loading its types.</summary>
    public sealed class SavedDetection {
        public List<SpikeSeedStar> Stars { get; } = new List<SpikeSeedStar>();
        public double Hfr { get; private set; }
        public double HfrStdDev { get; private set; }

        public static SavedDetection LoadForFrame(string fitsPath, int region = 0) {
            var match = Regex.Match(Path.GetFileNameWithoutExtension(fitsPath), @"^(\d+_Frame\d+)_");
            if (!match.Success) throw new InvalidDataException("Unrecognized autofocus frame name: " + fitsPath);
            string path = Path.Combine(Path.GetDirectoryName(fitsPath),
                $"{match.Groups[1].Value}_Region{region:D2}_star_detection_result.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var result = new SavedDetection {
                Hfr = Number(root.GetProperty("AverageHFR")),
                HfrStdDev = Number(root.GetProperty("HFRStdDev"))
            };
            foreach (var star in root.GetProperty("StarList").EnumerateArray()) {
                var pos = star.GetProperty("Position");
                var bounds = star.GetProperty("BoundingBox").GetString().Split(',');
                if (bounds.Length != 4) throw new InvalidDataException("Invalid star BoundingBox in " + path);
                result.Stars.Add(new SpikeSeedStar {
                    X = Number(pos.GetProperty("X")), Y = Number(pos.GetProperty("Y")),
                    WidthPx = int.Parse(bounds[2].Trim(), CultureInfo.InvariantCulture),
                    HeightPx = int.Parse(bounds[3].Trim(), CultureInfo.InvariantCulture),
                    MaxBrightness = Number(star.GetProperty("MaxBrightness"))
                });
            }
            return result;
        }

        private static double Number(JsonElement value) => value.ValueKind == JsonValueKind.Number
            ? value.GetDouble() : double.Parse(value.GetString(), CultureInfo.InvariantCulture);
    }
}
