using RTG.ManualFocuser.Properties;
using RTG.ManualFocuser.Util;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Settings = RTG.ManualFocuser.Properties.Settings;

namespace RTG.ManualFocuser {
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

        public event PropertyChangedEventHandler PropertyChanged;

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
