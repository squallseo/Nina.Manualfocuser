using Cwseo.NINA.ManualFocuser.Models;
using Cwseo.NINA.ManualFocuser.Properties;
using Cwseo.NINA.ManualFocuser.Util;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using Settings = Cwseo.NINA.ManualFocuser.Properties.Settings;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Cwseo.NINA.ManualFocuser {
    /// <summary>
    /// This class exports the IPluginManifest interface and will be used for the general plugin information and options
    /// The base class "PluginBase" will populate all the necessary Manifest Meta Data out of the AssemblyInfo attributes. Please fill these accoringly
    ///
    /// An instance of this class will be created and set as datacontext on the plugin options tab in N.I.N.A. to be able to configure global plugin settings
    /// The user interface for the settings will be defined by a DataTemplate with the key having the naming convention "ManualFocuser_Options" where ManualFocuser corresponds to the AssemblyTitle - In this template example it is found in the Options.xaml
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class ManualFocuser : PluginBase {
        private string lensesConfigPath { get => Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "lenses.json"); }
        private Integration integration;
		private static ManualFocuser instance;

        private readonly IPluginOptionsAccessor pluginSettings;
        private readonly IProfileService profileService;
        [ImportingConstructor]
        public ManualFocuser(IProfileService profileService, IMessageBroker messageBroker, IOptionsVM options) {
			instance = this;
            if (Settings.Default.UpdateSettings) {
                Settings.Default.Upgrade();
                Settings.Default.UpdateSettings = false;
                CoreUtil.SaveSettings(Settings.Default);
            }

            // This helper class can be used to store plugin settings that are dependent on the current profile
            this.pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(this.Identifier));
            this.profileService = profileService;
            ManualFocuser.ProfileService = profileService; // Ensure static reference is initialized

            if (File.Exists(lensesConfigPath))
            {
                var lenses = Newtonsoft.Json.JsonConvert.DeserializeObject<ObservableCollection<LensConfig>>(File.ReadAllText(lensesConfigPath));
                KnownLenses = lenses;
            }
            else
            {
                KnownLenses = new ObservableCollection<LensConfig>();
            }

            integration = new(messageBroker);
        }

        public override Task Teardown()
        {
            integration.Dispose();
            File.WriteAllText(lensesConfigPath, Newtonsoft.Json.JsonConvert.SerializeObject(KnownLenses));
            return base.Teardown();
        }
        // ------------------------------------------------------------------
        // Spike metric options.
        //
        // Every value the metric actually depends on is exposed here. Values
        // are clamped on write so a slip in a text box cannot produce a
        // degenerate window (a zero sigma divides by zero downstream).
        // ------------------------------------------------------------------

        private static double Clamp(double v, double lo, double hi)
            => double.IsNaN(v) ? lo : Math.Clamp(v, lo, hi);

        public bool EnableSpikeMetric {
            get => Properties.Settings.Default.EnableSpikeMetric;
            set {
                Properties.Settings.Default.EnableSpikeMetric = value;
                Properties.Settings.Default.Save();
                RaisePropertyChanged();
            }
        }

        public bool WriteSpikeDiagnostics {
            get => Properties.Settings.Default.WriteSpikeDiagnostics;
            set {
                Properties.Settings.Default.WriteSpikeDiagnostics = value;
                Properties.Settings.Default.Save();
                RaisePropertyChanged();
            }
        }

        public double RoiScale {
            get => Properties.Settings.Default.RoiScale;
            set {
                Properties.Settings.Default.RoiScale = Clamp(value, 1.0, 6.0);
                Properties.Settings.Default.Save();
                RaisePropertyChanged();
            }
        }

        public double BgRingFraction {
            get => Properties.Settings.Default.BgRingFraction;
            set {
                Properties.Settings.Default.BgRingFraction = Clamp(value, 0.2, 0.98);
                Properties.Settings.Default.Save();
                RaisePropertyChanged();
            }
        }

        public int MinStarSizePx {
            get => Properties.Settings.Default.MinStarSizePx;
            set {
                Properties.Settings.Default.MinStarSizePx = Math.Clamp(value, 3, 60);
                Properties.Settings.Default.Save();
                RaisePropertyChanged();
            }
        }

        public int MaxStars {
            get => Properties.Settings.Default.MaxStars;
            set {
                Properties.Settings.Default.MaxStars = Math.Clamp(value, 1, 50);
                Properties.Settings.Default.Save();
                RaisePropertyChanged();
            }
        }

        public int MinUsedStars {
            get => Properties.Settings.Default.MinUsedStars;
            set {
                Properties.Settings.Default.MinUsedStars = Math.Clamp(value, 1, 50);
                Properties.Settings.Default.Save();
                RaisePropertyChanged();
            }
        }

        // Spike angle and auto-detection are exposed on the dockable panel instead of
        // here, so they sit next to the measured value they are checked against.

        /// <summary>Local u-window sigma (tau). Caps how large varC can get.</summary>
        public double CoreSigmaPx {
            get => Properties.Settings.Default.CoreSigmaPx;
            set {
                Properties.Settings.Default.CoreSigmaPx = Clamp(value, 0.25, 40.0);
                Properties.Settings.Default.Save();
                RaisePropertyChanged();
            }
        }

        /// <summary>Radial core suppression radius r0.</summary>
        public double CoreRejectSigmaPx {
            get => Properties.Settings.Default.CoreRejectSigmaPx;
            set {
                Properties.Settings.Default.CoreRejectSigmaPx = Clamp(value, 0.5, 60.0);
                Properties.Settings.Default.Save();
                RaisePropertyChanged();
            }
        }

        /// <summary>Along-spike gaussian window. Should stay well inside the ROI half size.</summary>
        public double AxisSigmaPx {
            get => Properties.Settings.Default.AxisSigmaPx;
            set {
                Properties.Settings.Default.AxisSigmaPx = Clamp(value, 1.0, 300.0);
                Properties.Settings.Default.Save();
                RaisePropertyChanged();
            }
        }

        /// <summary>Centre rejection along the spike axis.</summary>
        public double AxisRejectSigmaPx {
            get => Properties.Settings.Default.AxisRejectSigmaPx;
            set {
                Properties.Settings.Default.AxisRejectSigmaPx = Clamp(value, 0.5, 100.0);
                Properties.Settings.Default.Save();
                RaisePropertyChanged();
            }
        }

        public double BetaVar {
            get => Properties.Settings.Default.BetaVar;
            set {
                Properties.Settings.Default.BetaVar = Clamp(value, 0.0, 1000.0);
                Properties.Settings.Default.Save();
                RaisePropertyChanged();
            }
        }

        public double BetaSplit {
            get => Properties.Settings.Default.BetaSplit;
            set {
                Properties.Settings.Default.BetaSplit = Clamp(value, 0.0, 1000.0);
                Properties.Settings.Default.Save();
                RaisePropertyChanged();
            }
        }

        public double SplitPower {
            get => Properties.Settings.Default.SplitPower;
            set {
                Properties.Settings.Default.SplitPower = Clamp(value, 1.0, 8.0);
                Properties.Settings.Default.Save();
                RaisePropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null) {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public static ICameraMediator Camera;
        public static IFocuserMediator Focuser;
        public static IProfileService ProfileService;

        public bool PrepareImage
        {
            get
            {
                return Settings.Default.PrepareImage;
            }
            set
            {
                Settings.Default.PrepareImage = value;
                CoreUtil.SaveSettings(Settings.Default);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PrepareImage)));
            }
        }

        public double Stretchfactor
        {
            get
            {
                return Settings.Default.Stretchfactor;
            }
            set
            {
                Settings.Default.Stretchfactor = value;
                CoreUtil.SaveSettings(Settings.Default);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Stretchfactor)));
            }
        }

        public double Blackclipping
        {
            get
            {
                return Settings.Default.Blackclipping;
            }
            set
            {
                Settings.Default.Blackclipping = value;
                CoreUtil.SaveSettings(Settings.Default);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Blackclipping)));
            }
        }

        public int FocusStopPosition
        {
            get
            {
                return Settings.Default.FocusStopPosition;
            }
            set
            {
                Settings.Default.FocusStopPosition = value;
                CoreUtil.SaveSettings(Settings.Default);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FocusStopPosition)));
            }
        }

        public int CalibrationLargeSteps
        {
            get => Settings.Default.CalibrationLargeSteps;
            set
            {
                Settings.Default.CalibrationLargeSteps = value;
                CoreUtil.SaveSettings(Settings.Default);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CalibrationLargeSteps)));
            }
        }

        private ObservableCollection<LensConfig> knownLenses;
        public ObservableCollection<LensConfig> KnownLenses
        {
            get => knownLenses;
            set
            {
                knownLenses = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KnownLenses)));
                if (knownLenses.Count > 0)
                {
                    SelectedLensName = knownLenses[^1].LensName;
                }
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KnownLensesNames)));

            }
        }

        private string lensName;
        public string SelectedLensName
        {
            get => lensName;
            set
            {
                lensName = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedLensName)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLensConfig)));
            }
        }

        public List<string> KnownLensesNames => KnownLenses.Select(x => x.LensName).ToList();

        public LensConfig CurrentLensConfig => KnownLenses.Where(x => x.LensName == SelectedLensName).FirstOrDefault();

        public static void AddLensConfigIfNecessary(string name)
        {
            var matches = instance.KnownLenses.Where(x => x.LensName == name);
            if (!matches.Any())
            {
                instance.KnownLenses.Add(new LensConfig(name, [new FocalLengthConfig(ProfileService.ActiveProfile.TelescopeSettings.FocalLength, 0)]));
                instance.PropertyChanged?.Invoke(instance, new PropertyChangedEventArgs(nameof(instance.KnownLensesNames)));
            }
            else
            {
                // If the lens is already known, but the focal length is not yet in the list, add it.
                if (!matches.First().FocusPosition.Where(x => x.FocalLength == ProfileService.ActiveProfile.TelescopeSettings.FocalLength).Any())
                {
                    matches.First().FocusPosition.Add(new FocalLengthConfig(ProfileService.ActiveProfile.TelescopeSettings.FocalLength, 0));
                }
            }
            var lensConfig = instance.KnownLenses.First(x => x.LensName == name);
            lensConfig.FocusPosition = lensConfig.FocusPosition.OrderBy(x => x.FocalLength).ToList();
            instance.SelectedLensName = name;
        }

        public static int GetFocusPosition(string name)
        {
            double focalLength = ProfileService.ActiveProfile.TelescopeSettings.FocalLength;
            var lens = instance.KnownLenses.Where(x => x.LensName == name).FirstOrDefault();
            if (lens is not null)
                {
                var config = lens.FocusPosition.Where(x => x.FocalLength == focalLength).FirstOrDefault();
                if (config is not null)
                    {
                        return config.FocusPosition;
                }
            }
            return 0;
        }

        public static event Func<Task> LinearAFRequested;

        /// <summary>
        /// Invoked by external components (sequence item) to request a linear AF run.
        /// Returns completed Task if no handler is registered.
        /// </summary>
        public static Task RequestLinearAFAsync() {
            try {
                var handler = LinearAFRequested;
                if (handler == null) return Task.CompletedTask;
                return handler.Invoke();
            } catch (Exception e) {
                // Do not throw — sequence should not crash NINA
                Logger.Error("Error invoking LinearAFRequested", e);
                return Task.CompletedTask;
            }
        }
    }
}
