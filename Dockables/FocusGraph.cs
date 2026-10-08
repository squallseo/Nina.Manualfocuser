using System;
using System.Threading.Tasks;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class ManualFocuserDockableVM {
        private int? pendingPreviewTarget;
        private string graphMoveDeviceId;
        private bool graphPreviewMovesEnabled, graphSelectionVisible;
        private int graphSelectionPosition;
        public int GraphCurrentPosition => FocuserInfo?.Position ?? 0;
        public int GraphSelectionPosition => graphSelectionPosition;
        public string GraphSelectionLabel => $"Move → {graphSelectionPosition}";
        public bool GraphPositionMarkersVisible => FocuserInfo?.Connected==true && !IsSelectingRoi && (FocusMode!="Live" || AutoFocusPreviewVisible);
        public bool GraphSelectionVisible => graphSelectionVisible && GraphPositionMarkersVisible;
        public bool CanMoveFromGraph => !disposed && FocuserInfo?.Connected==true && !FocuserInfo.IsMoving && !IsMoving
            && !IsGoingToFocusTarget && !IsSelectingRoi && !isStoppingFocusPreview && pendingPreviewMove==0 && pendingPreviewTarget==null
            && (assistRunning ? graphPreviewMovesEnabled && CameraInfo?.Connected==true : CanMove()
                && (CameraInfo?.Connected!=true || (!CameraInfo.IsExposing && !CameraInfo.LiveViewEnabled && cameraMediator.IsFreeToCapture(this))));

        public bool SelectGraphPosition(double position) {
            if(!CanMoveFromGraph || !double.IsFinite(position)) return false;
            int maximum=(focuserMediator.GetDevice() as IFocuser)?.MaxStep ?? int.MaxValue;
            if(maximum<=0) maximum=int.MaxValue;
            graphSelectionPosition=(int)Math.Round(Math.Clamp(position,0,maximum));
            graphSelectionVisible=true;
            RaisePropertyChanged(nameof(GraphSelectionPosition));RaisePropertyChanged(nameof(GraphSelectionLabel));
            RaisePropertyChanged(nameof(GraphSelectionVisible));
            return true;
        }
        public void CancelGraphSelection() {
            graphSelectionVisible=false;RaisePropertyChanged(nameof(GraphSelectionVisible));
        }
        public Task<int> MoveFocusFromGraphAsync(double position) => RunGuarded("Graph focus move",async ()=> {
            if(!SelectGraphPosition(position)) {CancelGraphSelection();return 0;}
            int target=graphSelectionPosition;CancelGraphSelection();
            string device=FocuserInfo.DeviceId;
            if(target==GraphCurrentPosition) return 0;
            TargetPosition=target;
            if(assistRunning) {
                graphMoveDeviceId=device;pendingPreviewTarget=target;
                return 1;
            }
            ResetCts();IsMoving=true;
            try {
                if(FocusMode=="Manual") await CaptureFirstPoint();
                moveCts.Token.ThrowIfCancellationRequested();
                var current=focuserMediator.GetInfo();
                if(current?.Connected!=true || current.DeviceId!=device) throw new InvalidOperationException("Focuser disconnected or changed. Graph move canceled.");
                int actual=await focuserMediator.MoveFocuser(target,moveCts.Token);
                if(actual!=target) throw new InvalidOperationException("Focuser did not reach the selected graph position.");
                RaisePropertyChanged(nameof(GraphCurrentPosition));
                if(AutoFocusPreviewVisible) InvalidateGraphFocusVerification();
                return FocusMode=="Manual" ? await ExecuteShootAsync() : 1;
            } finally {IsMoving=false;}
        });
        private void InvalidateGraphFocusVerification() {
            AutoFocusMetricText="Manual adjustment · autofocus result is historical";
            RaisePropertyChanged(nameof(AutoFocusMetricText));RaisePropertyChanged(nameof(PreviewMetricText));
        }
        private Task<int> StartGraphPreviewMove() {
            int target=pendingPreviewTarget.Value;
            pendingPreviewTarget=null;
            var current=focuserMediator.GetInfo();
            if(current?.Connected!=true || current.DeviceId!=graphMoveDeviceId || current.IsMoving)
                throw new InvalidOperationException("Focuser disconnected, changed or busy. Graph move canceled.");
            assistCts.Token.ThrowIfCancellationRequested();
            IsMoving=true;
            if (Properties.Settings.Default.EnableFocusDiagnostics) Logger.Info($"[ManualFocuser/GraphMove] position={target} keepStream=true");
            return Move();
            async Task<int> Move() {
                int actual=await focuserMediator.MoveFocuser(target,assistCts.Token);
                if(actual!=target) throw new InvalidOperationException("Focuser did not reach the selected graph position.");
                RaisePropertyChanged(nameof(GraphCurrentPosition));
                return actual;
            }
        }
    }
}
