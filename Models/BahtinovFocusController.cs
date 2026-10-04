using System;
using System.Collections.Generic;

namespace Cwseo.NINA.ManualFocuser.Models {
    /// <summary>Bracket-only zero-score interpolation for a measured scan (see SharpCap focusing documentation).
    /// Caller owns repeated-measurement confirmation, backlash, settling and cancellation.</summary>
    public sealed class BahtinovFocusController {
        public static bool TryFindZero(IReadOnlyList<(int Position, double Error)> samples, out int position) {
            position = 0;
            if (samples == null) return false;
            for (int i = 0; i < samples.Count; i++) {
                var a = samples[i];
                if (!double.IsFinite(a.Error)) continue;
                if (Math.Abs(a.Error) < 1e-9) { position = a.Position; return true; }
                if (i + 1 >= samples.Count) continue;
                var b = samples[i + 1];
                if (!double.IsFinite(b.Error) || a.Position == b.Position || Math.Sign(a.Error) == Math.Sign(b.Error)) continue;
                double denominator = b.Error - a.Error;
                if (!double.IsFinite(denominator)) continue;
                double fraction = -a.Error / denominator;
                double value = a.Position + fraction * ((double)b.Position - a.Position);
                if (double.IsFinite(value) && fraction >= 0 && fraction <= 1) { position = (int)Math.Round(value); return true; }
            }
            return false;
        }
    }
}
