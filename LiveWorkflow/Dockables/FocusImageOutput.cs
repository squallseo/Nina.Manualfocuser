using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Image.Interfaces;

namespace Cwseo.NINA.ManualFocuser.Dockables
{
    public partial class ManualFocuserDockableVM
    {
        // Retained for older bindings; live frames are displayed only in our workspace.
        public bool ShowInNinaImage { get => false; set { } }
        private IImageData syncSourceImage;
        private bool syncingImage;
        private CancellationTokenSource syncImageCts;
        public bool SyncImageOnStop { get; set; }
        public string ImageSyncStatus { get; private set; } = "Sync sends original pixels to NINA Image.";
        public ICommand SyncImageCommand { get; private set; }
        private bool CanSyncImage => !disposed && !syncingImage && syncSourceImage != null && !IsSelectingRoi;
        private void InitializeImageSync() => SyncImageCommand = new AsyncCommand<int>(
            () => RunGuarded("Sync image", SyncSourceImageAsync), _ => CanSyncImage);
        private void SetSyncSource(IImageData image) {
            syncSourceImage = image; CommandManager.InvalidateRequerySuggested();
        }
        private async Task<int> SyncSourceImageAsync() {
            if (!CanSyncImage) return 0;
            // Capture the exact source reference before awaiting. Later live frames
            // cannot change which raw data the native viewer processes.
            var source = syncSourceImage;
            syncingImage = true; CommandManager.InvalidateRequerySuggested();
            syncImageCts?.Dispose(); syncImageCts = new CancellationTokenSource();
            try {
                ImageSyncStatus = "Syncing source frame..."; RaisePropertyChanged(nameof(ImageSyncStatus));
                // A requested snapshot follows the user's native viewer settings.
                await imagingMediator.PrepareImage(source, new PrepareImageParameters(), syncImageCts.Token);
                ImageSyncStatus = $"Synced source {source.Properties.Width} × {source.Properties.Height} px to NINA Image.";
                return 1;
            }
            catch {
                ImageSyncStatus = "Image sync failed. Retry when NINA Image is available.";
                throw;
            }
            finally {
                syncingImage = false; RaisePropertyChanged(nameof(ImageSyncStatus)); CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}
