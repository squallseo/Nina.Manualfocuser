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
        public double RoiScale {
            get {
                // 저장된 값 가져오기
                return Properties.Settings.Default.RoiScale;
            }
            set {
                Properties.Settings.Default.RoiScale = value; // Settings에 저장
                Properties.Settings.Default.Save(); // 저장 반영
                RaisePropertyChanged(nameof(RoiScale));
            }
        }

        public double CoreCutFraction {
            get {
                // 저장된 값 가져오기
                return Properties.Settings.Default.CoreCutFraction;
            }
            set {
                Properties.Settings.Default.CoreCutFraction = value; // Settings에 저장
                Properties.Settings.Default.Save(); // 저장 반영
                RaisePropertyChanged(nameof(CoreCutFraction));
            }
        }

        public double BgRingFraction {
            get {
                // 저장된 값 가져오기
                return Properties.Settings.Default.BgRingFraction;
            }
            set {
                Properties.Settings.Default.BgRingFraction = value; // Settings에 저장
                Properties.Settings.Default.Save(); // 저장 반영
                RaisePropertyChanged(nameof(BgRingFraction));
            }
        }

        public int MinStarSizePx {
            get {
                // 저장된 값 가져오기
                return Properties.Settings.Default.MinStarSizePx;
            }
            set {
                Properties.Settings.Default.MinStarSizePx = value; // Settings에 저장
                Properties.Settings.Default.Save(); // 저장 반영
                RaisePropertyChanged(nameof(MinStarSizePx));
            }
        }


        public double SaturationLevel {
            get {
                // 저장된 값 가져오기
                return Properties.Settings.Default.SaturationLevel;
            }
            set {
                Properties.Settings.Default.SaturationLevel = value; // Settings에 저장
                Properties.Settings.Default.Save(); // 저장 반영
                RaisePropertyChanged(nameof(SaturationLevel));
            }
        }

        public int MaxStars {
            get {
                // 저장된 값 가져오기
                return Properties.Settings.Default.MaxStars;
            }
            set {
                Properties.Settings.Default.MaxStars = value; // Settings에 저장
                Properties.Settings.Default.Save(); // 저장 반영
                RaisePropertyChanged(nameof(MaxStars));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null) {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
