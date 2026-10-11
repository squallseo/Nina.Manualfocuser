using System;
using System.Windows;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class ManualFocuserDockableVM {
        private bool showHfrStarBoxes;
        private FocusHfrMeasurement starMeasurement;
        private (FocusHfrMeasurement Measurement, int Left, int Top, int Width, int Height, int SourceWidth, int SourceHeight, bool Inspector)? starProjection;
        public bool ShowHfrStarBoxes {
            get => showHfrStarBoxes;
            set {
                if (showHfrStarBoxes == value) return;
                showHfrStarBoxes = value; RaisePropertyChanged();
                HfrVisibilityChanged(); RefreshStarBoxes();
            }
        }
        public Rect[] HfrStarBoxes { get; private set; } = Array.Empty<Rect>();
        private void PublishStarMeasurement(FocusHfrMeasurement measurement) {
            starMeasurement = measurement;
            RefreshStarBoxes();
        }
        private FocusHfrMeasurement CreateStarAnnotation(FocusRawFrame frame) {
            var measurement = starMeasurement;
            if (!ShowHfrStarBoxes || measurement?.Generation != hfrGeneration) return null;
            return FocusStarBoxLocator.Locate(measurement, frame);
        }
        private void RefreshStarBoxes() {
            var entry = LiveDisplayImage != null && previewPixels.TryGetValue(LiveDisplayImage, out var pixels) ? pixels : null;
            var frame = entry?.Raw;
            if (!ShowHfrStarBoxes || !ImagePanelEnabled || IsSelectingRoi || frame == null || starMeasurement == null) {
                starProjection = null;
                if (HfrStarBoxes.Length == 0) return;
                HfrStarBoxes = Array.Empty<Rect>();
            } else {
                // This fallback only initializes a stopped frame when the user
                // enables boxes. Streaming frames prepare annotations on their
                // render worker, from the very pixels being displayed.
                if (entry.StarAnnotation == null) entry.StarAnnotation = CreateStarAnnotation(frame);
                var annotation = entry.StarAnnotation;
                if (annotation?.SourceFrameId != frame.FrameId || annotation.Generation != hfrGeneration) {
                    starProjection = null;
                    HfrStarBoxes = Array.Empty<Rect>(); RaisePropertyChanged(nameof(HfrStarBoxes)); return;
                }
                var key = (annotation, frame.Left, frame.Top, frame.Width, frame.Height,
                    frame.SourceImage?.Properties.Width ?? frame.Width, frame.SourceImage?.Properties.Height ?? frame.Height, entry.Inspector);
                if (starProjection == key) return;
                starProjection = key;
                HfrStarBoxes = FocusStarBoxes.Project(annotation, frame, entry.Inspector);
            }
            RaisePropertyChanged(nameof(HfrStarBoxes));
        }
    }
}
