using System.Windows.Media.Imaging;
using System;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;

namespace Cwseo.NINA.ManualFocuser.Dockables
{
    public partial class ManualFocuserDockableVM
    {
        private MainImageRoiEditor mainRoiEditor;
        private bool editingRoiInNinaImage;
        public bool IsEditingRoiInNinaImage => editingRoiInNinaImage;
        private void SetMainRoiAttached(bool attached)
        {
            if (editingRoiInNinaImage == attached) return;
            editingRoiInNinaImage = attached;
            RaisePropertyChanged(nameof(IsEditingRoiInNinaImage));
        }
        private void UpdateMainRoiEditor()
        {
            if (disposed || !IsSelectingRoi || !ShowInNinaImage || overviewImage is not BitmapSource bitmap)
            {
                StopMainRoiEditor(); return;
            }
            try
            {
                mainRoiEditor ??= new MainImageRoiEditor(this, imagingMediator.SetImage, SetMainRoiAttached);
                mainRoiEditor.SetImage(bitmap);
            }
            catch (Exception e)
            {
                StopMainRoiEditor(); ShowInNinaImage = false;
                Logger.Error("[LiveFocus] Main Image ROI output failed; using local editor", e);
                Notification.ShowWarning("Live Focus: could not display ROI in NINA Image. Use the local editor.");
            }
        }
        private void StopMainRoiEditor()
        {
            mainRoiEditor?.Dispose(); mainRoiEditor = null; SetMainRoiAttached(false);
        }
    }
}
