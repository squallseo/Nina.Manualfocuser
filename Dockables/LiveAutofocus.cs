using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Cwseo.NINA.ManualFocuser.Models;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using OxyPlot;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class ManualFocuserDockableVM {
        private bool autoFocusPreviewVisible;
        public bool AutoFocusPreviewVisible { get => autoFocusPreviewVisible; private set {
            autoFocusPreviewVisible = value; RaisePropertyChanged();
            RaisePropertyChanged(nameof(PreviewMetricText)); RaisePropertyChanged(nameof(PreviewAxisTitle)); RaisePropertyChanged(nameof(PreviewGraphPoints));
            RaisePropertyChanged(nameof(PreviewXAxisTitle)); RaisePropertyChanged(nameof(PreviewXAxisFormat)); RaisePropertyChanged(nameof(IsLivePreviewGraph));
            RaisePropertyChanged(nameof(GraphPositionMarkersVisible));
        } }
        public string PreviewMetricText => AutoFocusPreviewVisible ? AutoFocusMetricText : LiveHfrText;
        public string PreviewAxisTitle => AutoFocusPreviewVisible ? AutoFocusMetricAxis : "Local HFR (px)";
        public string PreviewXAxisTitle => AutoFocusPreviewVisible ? "Focuser position · drag to move" : "Elapsed seconds";
        public string PreviewXAxisFormat => AutoFocusPreviewVisible ? "0" : "0.##";
        public bool IsLivePreviewGraph => !AutoFocusPreviewVisible;
        public AsyncObservableCollection<DataPoint> PreviewGraphPoints => AutoFocusPreviewVisible ? AutoFocusMetricPoints : LiveHfrPoints;
        public string AutoFocusMetricText { get; private set; } = "Autofocus preview";
        public string AutoFocusMetricAxis { get; private set; } = "Focus metric (px)";
        public AsyncObservableCollection<DataPoint> AutoFocusMetricPoints { get; } = new();
        public ICommand SpikeAFCommand { get; private set; }

        private Task<int> RunLiveAutofocusAsync(bool mask) => RunCurveAutofocusAsync(mask ? "Bahtinov" : "Spike");
    }
}
