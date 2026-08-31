using NINA.Core.Model;
using System;

namespace nina.plugin.phd2tools.Phd2ToolsSequenceItems {

    // Shared threshold evaluation for the RMS based triggers.
    // Values are compared in the same unit the RMS instance is scaled to (arcseconds when a pixel scale is known)

    public static class RmsThresholdEvaluator {

        /// <summary>
        /// Checks whether the given RMS recording currently exceeds the threshold
        /// </summary>
        /// <param name="rms">The live RMS recording</param>
        /// <param name="mode">Peak evaluates the peak values, RMS evaluates the total/RA/Dec RMS</param>
        /// <param name="threshold">The threshold the values are compared against</param>
        /// <param name="minimumPoints">Minimum amount of data points before the RMS mode yields a result</param>
        /// <param name="reason">A human readable description of the value that exceeded the threshold</param>
        public static bool IsAboveThreshold(RMS rms, GuideInterrupteMode mode, double threshold, int minimumPoints, out string reason) {
            reason = string.Empty;
            if (rms == null) { return false; }

            if (mode == GuideInterrupteMode.Peak) {
                if (Exceeds(rms.PeakRA, rms.Scale, threshold, "RA peak", ref reason)) { return true; }
                if (Exceeds(rms.PeakDec, rms.Scale, threshold, "Dec peak", ref reason)) { return true; }
            } else if (mode == GuideInterrupteMode.RMS) {
                if (rms.DataPoints <= minimumPoints) { return false; }

                if (Exceeds(rms.Total, rms.Scale, threshold, "Total RMS", ref reason)) { return true; }
                if (Exceeds(rms.RA, rms.Scale, threshold, "RA RMS", ref reason)) { return true; }
                if (Exceeds(rms.Dec, rms.Scale, threshold, "Dec RMS", ref reason)) { return true; }
            }

            return false;
        }

        private static bool Exceeds(double value, double scale, double threshold, string label, ref string reason) {
            var scaled = Math.Abs(value) * scale;
            if (scaled > threshold) {
                reason = $"{label} above threshold ({Math.Round(scaled, 2)} / {threshold})";
                return true;
            }
            return false;
        }
    }
}
