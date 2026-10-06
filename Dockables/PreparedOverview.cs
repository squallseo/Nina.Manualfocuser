using System;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;
using NINA.Core.Utility;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class ManualFocuserDockableVM {
        private IImagingMediator overviewMediator;
        private readonly object preparedOverviewLock = new();
        private IImageData preparedOverview;
        private string preparedOverviewCamera;
        private void ObservePreparedImages(IImagingMediator mediator) {
            overviewMediator = mediator;
            overviewMediator.ImagePrepared += OnOverviewImagePrepared;
        }
        private void OnOverviewImagePrepared(object sender, ImagePreparedEventArgs args) {
            try {
                var image = args.RenderedImage?.RawImageData;
                var camera = cameraMediator.GetInfo();
                if (camera?.Connected != true || image?.Properties == null || image.Properties.Width != camera.XSize || image.Properties.Height != camera.YSize) return;
                lock (preparedOverviewLock) { preparedOverview = image; preparedOverviewCamera = camera.DeviceId; }
            } catch (Exception e) { Logger.Error("Failed to retain overview image", e); }
        }
        private IImageData GetPreparedOverview() {
            var camera = cameraMediator.GetInfo();
            lock (preparedOverviewLock) {
                return camera?.DeviceId == preparedOverviewCamera && preparedOverview?.Properties.Width == camera?.XSize && preparedOverview?.Properties.Height == camera?.YSize
                    ? preparedOverview : null;
            }
        }
        private void StopObservingPreparedImages() {
            if (overviewMediator != null) overviewMediator.ImagePrepared -= OnOverviewImagePrepared;
            lock (preparedOverviewLock) { preparedOverview = null; preparedOverviewCamera = null; }
        }
    }
}
