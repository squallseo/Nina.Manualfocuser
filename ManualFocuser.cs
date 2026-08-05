using Cwseo.NINA.ManualFocuser.Models;
using Cwseo.NINA.ManualFocuser.Properties;
using NINA.Core.Utility;
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

namespace Cwseo.NINA.ManualFocuser {
    /// <summary>
    /// This class exports the IPluginManifest interface and will be used for the general plugin information and options
    /// The base class "PluginBase" will populate all the necessary Manifest Meta Data out of the AssemblyInfo attributes. Please fill these accoringly
    /// 
    /// An instance of this class will be created and set as datacontext on the plugin options tab in N.I.N.A. to be able to configure global plugin settings
    /// The user interface for the settings will be defined by a DataTemplate with the key having the naming convention "ManualFocuser_Options" where ManualFocuser corresponds to the AssemblyTitle - In this template example it is found in the Options.xaml
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class ManualFocuser : PluginBase, INotifyPropertyChanged {
        private readonly IPluginOptionsAccessor pluginSettings;
        private readonly IProfileService profileService;
        [ImportingConstructor]
        public ManualFocuser(IProfileService profileService, IOptionsVM options) {
            if (Settings.Default.UpdateSettings) {
                Settings.Default.Upgrade();
                Settings.Default.UpdateSettings = false;
                CoreUtil.SaveSettings(Settings.Default);
            }

            // This helper class can be used to store plugin settings that are dependent on the current profile
            this.pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(this.Identifier));
            this.profileService = profileService;
        }

        public override Task Teardown() {
            // Make sure to unregister an event when the object is no longer in use. Otherwise garbage collection will be prevented.
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

        /// <summary>
        /// When set, the orientation measured from the image is used instead of
        /// <see cref="SpikeAngle"/>. The measurement is taken and reported either way.
        /// </summary>
        public bool AutoSpikeAngle {
            get => Properties.Settings.Default.AutoSpikeAngle;
            set {
                Properties.Settings.Default.AutoSpikeAngle = value;
                Properties.Settings.Default.Save();
                RaisePropertyChanged();
            }
        }

        public double SpikeAngle {
            get => Properties.Settings.Default.spikeAngleDeg;
            set {
                Properties.Settings.Default.spikeAngleDeg = Clamp(value, -360.0, 360.0);
                Properties.Settings.Default.Save();
                RaisePropertyChanged();
            }
        }

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
    }
}
