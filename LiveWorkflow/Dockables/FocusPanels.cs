using System.ComponentModel.Composition;
using System.Windows.Input;
using NINA.Core.Utility;
using NINA.Core.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.ViewModel;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    [Export(typeof(IDockableVM))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public sealed class FocusImageDockableVM : DockableVM {
        public ManualFocuserDockableVM Session { get; }
        public override bool IsTool => true;
        [ImportingConstructor]
        public FocusImageDockableVM(IProfileService profiles, ManualFocuserDockableVM session) : base(profiles) {
            Title = "MF Image"; Session = session; ImageGeometry = session.ImageGeometry;
            session.RegisterFocusPanel(this);
        }
    }
    [Export(typeof(IDockableVM))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public sealed class FocusHistoryDockableVM : DockableVM {
        public ManualFocuserDockableVM Session { get; }
        public ICommand CloseHistorySettingsCommand { get; }
        public override bool IsTool => true;
        [ImportingConstructor]
        public FocusHistoryDockableVM(IProfileService profiles, ManualFocuserDockableVM session) : base(profiles) {
            Title = "MF HFR"; Session = session; ImageGeometry = session.ImageGeometry;
            CloseHistorySettingsCommand = new RelayCommand(_ => SettingsVisible = false);
            IsVisible = session.ShowFocusHfr;
            session.RegisterFocusPanel(this);
        }
    }
}
