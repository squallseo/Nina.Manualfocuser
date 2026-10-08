using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Cwseo.NINA.ManualFocuser.Models;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces;
using OxyPlot;
using OxyPlot.Series;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class ManualFocuserDockableVM {
        public AsyncObservableCollection<BoxPlotItem> AutoFocusBoxes { get; } = new();
        public AsyncObservableCollection<BoxPlotItem> AutoFocusVerificationBoxes { get; } = new();
        public AsyncObservableCollection<DataPoint> AutoFocusFitPoints { get; } = new();
        private double autofocusBoxWidth=500;
        public double AutoFocusBoxWidth => autofocusBoxWidth;
        public int AutofocusFramesPerPoint {
            get => Math.Clamp(profileService.ActiveProfile.FocuserSettings.AutoFocusNumberOfFramesPerPoint,5,50);
            set {
                if(!CanConfigureLive) return;
                profileService.ActiveProfile.FocuserSettings.AutoFocusNumberOfFramesPerPoint=Math.Clamp(value,5,50);
                RaisePropertyChanged();
            }
        }
        private void ClearAutofocusStatistics() {
            AutoFocusBoxes.Clear();AutoFocusVerificationBoxes.Clear();AutoFocusFitPoints.Clear();AutoFocusMetricPoints.Clear();
        }
        private async Task<int> RunCurveAutofocusAsync(string method,bool monitor=true) {
            if(!CanStartAssist() || FocuserInfo?.Connected!=true) return 0;
            var profile=profileService.ActiveProfile;
            int origin=FocuserInfo.Position;
            int step=profile.FocuserSettings.AutoFocusStepSize, offsets=profile.FocuserSettings.AutoFocusInitialOffsetSteps;
            int maximum=(focuserMediator.GetDevice() as IFocuser)?.MaxStep ?? int.MaxValue;
            if(maximum<=0) maximum=int.MaxValue;
            int[] positions=FocusCurveScanRunner.Plan(origin,step,offsets,maximum);
            int count=AutofocusFramesPerPoint;
            // Plugin choices override the host curve model without modifying its profile.
            // Snapshot once; options changed during a run apply to the next run.
            var fitting=FocusCurveOptions.Resolve(method,Properties.Settings.Default.HfrCurveFit,Properties.Settings.Default.SpikeCurveFit);
            double threshold=profile.FocuserSettings.RSquaredThreshold;
            if(!double.IsFinite(threshold) || threshold<0 || threshold>1) throw new InvalidOperationException("NINA AF R² threshold must be between 0 and 1.");
            bool mask=method=="Bahtinov", hfr=method=="Linear";
            string methodLabel=hfr ? "HFR" : method;
            var evaluateHfr=hfr ? DataModel.CreateFocusHfrEvaluator(filterWheelMediator.GetInfo()?.SelectedFilter?.Name) : null;
            var settings=DataModel.CreateFocusCaptureSettings(PreviewExposureMs/1000,PreviewRoiWidth,PreviewRoiHeight,PreviewCenterX,PreviewCenterY);
            double centerX=PreviewCenterX,centerY=PreviewCenterY;
            string cameraId=CameraInfo.DeviceId;
            string focuserId=FocuserInfo.DeviceId;
            bool streaming=UseFocusStreaming && DataModel.SupportsFocusStreaming, verified=false, reserved=false, manuallyAdjusted=false;
            LatestFrameStream<ManualFocuserModel.StreamPreviewFrame> stream=null;
            FocusDiagnosticSession diagnostics=null;
            Task<int> graphMovement=null;
            async Task FinishGraphMovement() {
                var movement=graphMovement;graphMovement=null;
                try {await movement;} finally {IsMoving=false;}
            }
            var spikeParams=ManualFocuserModel.BuildSpikeParams();
            spikeParams.metricKind=SpikeMetricKind.Fwhm;spikeParams.minUsedStarsForValidFrame=1;
            spikeParams.autoSpikeAngle=true;spikeParams.minimumRoiSizePx=128;spikeParams.resolveParallelSpikes=true;
            var spikeAnalysis=new SpikeAutofocusAnalysis(spikeParams);
            SpikeTrackingState tracking=null;BahtinovLine[] reference=null;
            long settledAt=Stopwatch.GetTimestamp(); bool needsFreshFrame=true;
            string phase="preflight";
            void EnsureDevices() {
                var camera=cameraMediator.GetInfo();
                var focuser=focuserMediator.GetInfo();
                if(disposed || camera?.Connected!=true || camera.DeviceId!=cameraId || focuser?.Connected!=true || focuser.DeviceId!=focuserId)
                    throw new InvalidOperationException("Autofocus camera/focuser disconnected or changed. No further moves.");
            }
            async Task CloseStream() {if(stream!=null){var closing=stream;stream=null;await closing.DisposeAsync();}}
            try {
                BeginAssist(true);reserved=true;IsSelectingRoi=false;AutoFocusPreviewVisible=true;
                int timeout=profile.FocuserSettings.AutoFocusTimeoutSeconds;
                if(timeout>0) assistCts.CancelAfter(TimeSpan.FromSeconds(timeout));
                var token=assistCts.Token;
                autofocusBoxWidth=step*.3;RaisePropertyChanged(nameof(AutoFocusBoxWidth));
                AutoFocusMetricAxis=hfr?"HFR (px)":mask?"Bahtinov signed error (px)":"Spike FWHM (px)";
                RaisePropertyChanged(nameof(AutoFocusMetricAxis));RaisePropertyChanged(nameof(PreviewAxisTitle));
                diagnostics=new FocusDiagnosticSession(methodLabel+"CurveAF");
                Logger.Info("[ManualFocuser/CurveAF] "+diagnostics.DirectoryPath);
                await diagnostics.EventAsync(new {method=methodLabel,origin,step,offsets,positions,FramesPerPoint=count,Streaming=streaming,settings.Roi,
                    settings.Seconds,Fitting=fitting.ToString(),FittingSource="Plugin",Threshold=threshold,AutomaticRoi=false});
                async Task<ManualFocuserModel.StreamPreviewFrame> ReadFrame(CancellationToken ct) {
                    ct.ThrowIfCancellationRequested();EnsureDevices();
                    if(streaming) {
                        // This one stream stays open across every motor move.
                        stream??=DataModel.StartFocusStreaming(settings.Seconds,settings.Roi.Width,centerX,centerY,token,settings.Roi.Height);
                        while(true) {
                            var frame=await stream.ReadAsync(ct);
                            if(!needsFreshFrame) return frame;
                            // Reject buffered/in-flight exposures: the read must start
                            // after settling, and allow a full exposure + one frame interval.
                            double interval=Math.Max(settings.Seconds,frame.Timing.CaptureAndDownloadMs/1000);
                            if(frame.AcquisitionStarted>=settledAt && frame.AvailableAt>=settledAt+(long)((settings.Seconds+interval)*Stopwatch.Frequency)) {
                                needsFreshFrame=false;return frame;
                            }
                        }
                    }
                    var single=await DataModel.CaptureFocusPreviewAsync(settings.Seconds,settings.Roi.Width,centerX,centerY,ct,roiHeight:settings.Roi.Height);
                    return new(single.Pixels,single.Width,single.Height,single.HardwareRoi,DataModel.LastPreviewTiming,
                        BitDepth:DataModel.LastPreviewBitDepth,IsBayered:DataModel.LastPreviewIsBayered);
                }
                async Task<int> Move(int position,CancellationToken ct) {
                    ct.ThrowIfCancellationRequested();EnsureDevices();
                    SetAssistStatus($"{methodLabel} AF | Moving to {position} · analyzing previous position");
                    int actual=await focuserMediator.MoveFocuser(position,ct);
                    ct.ThrowIfCancellationRequested();EnsureDevices();
                    settledAt=Stopwatch.GetTimestamp();needsFreshFrame=true;
                    return actual;
                }
                async Task<FocusFrameMetric> Analyze(ManualFocuserModel.StreamPreviewFrame frame,int position,string framePhase,CancellationToken ct) {
                    ct.ThrowIfCancellationRequested();
                    string id=await diagnostics.SaveAsync(frame.Pixels,frame.Width,frame.Height,new {
                        CameraId=cameraId,Position=position,Phase=framePhase,Streaming=streaming,ExposureSeconds=settings.Seconds,
                        SensorRoi=settings.Roi,frame.Width,frame.Height,frame.BitDepth,frame.IsBayered,frame.AcquisitionStarted,frame.AvailableAt,DisplayStretched=false});
                    if(FocusSaturation.HasClippedAnalysisArea(frame.Pixels,frame.Width,frame.Height,mask))
                        throw new FocusSaturationException("The focus measurement area is saturated.");
                    FocusFrameMetric metric;BahtinovMeasurement overlay=null;SpikeFrameResult spikes=null;
                    if(hfr) metric=await evaluateHfr(frame,ct);
                    else if(mask) {
                        overlay=BahtinovAnalyzer.Analyze(frame.Pixels,frame.Width,frame.Height);
                        double value=double.NaN;
                        if(overlay.IsValid) {
                            reference??=overlay.Lines;
                            double alignment=overlay.Lines[1].Nx*reference[1].Nx+overlay.Lines[1].Ny*reference[1].Ny;
                            double tolerance=Math.Cos(3*Math.PI/180);
                            if(Math.Abs(alignment)>=tolerance && reference.All(a=>overlay.Lines.Any(b=>Math.Abs(a.Nx*b.Nx+a.Ny*b.Ny)>=tolerance)))
                                value=overlay.SignedErrorPixels*(alignment<0?-1:1);
                        }
                        metric=new(value);
                    } else {
                        var local=FocusRoi.CenterWindow(frame.Pixels,frame.Width,frame.Height);
                        double localHfr=QuickFocusMetrics.HalfFluxRadius(local.Pixels,local.Width,local.Height);
                        metric=new(double.NaN);
                        if(double.IsFinite(localHfr)) {
                            if(tracking==null) {
                                int peak=Array.IndexOf(local.Pixels,local.Pixels.Max());
                                int footprint=Math.Clamp((int)Math.Ceiling(localHfr*4),6,60);
                                tracking=SpikeCore.CreateTrackingState(new[]{new SpikeSeedStar { X=peak%local.Width+(frame.Width-local.Width)/2,
                                    Y=peak/local.Width+(frame.Height-local.Height)/2,WidthPx=footprint,HeightPx=footprint,MaxBrightness=local.Pixels[peak]}},spikeParams);
                            }
                            var result=spikeAnalysis.Evaluate(frame.Pixels.Select(p=>(ushort)Math.Clamp(p,0,65535)).ToArray(),frame.Width,frame.Height,tracking,localHfr);
                            spikes=result;
                            if(result.IsValid && result.HasClearSpikes) metric=new(result.Metric);
                        }
                    }
                    await diagnostics.EventAsync(new {Frame=id,Position=position,Phase=framePhase,metric.Value,metric.StarScatter,metric.Stars});
                    var preview=RenderFocusPreview(frame.Pixels,frame.Width,frame.Height,overlay);
                    // Worker analysis never mutates WPF collections or host-owned images.
                    var dispatcher=Application.Current?.Dispatcher;
                    void Display() {PublishPreviewSpikeAngle(spikes);FocusPreviewImage=preview;RaisePropertyChanged(nameof(FocusPreviewImage));RaisePropertyChanged(nameof(LiveDisplayImage));}
                    if(dispatcher!=null) await dispatcher.InvokeAsync(Display); else Display();
                    return metric;
                }
                async Task<Task<FocusPointStatistics>> Acquire(int position,CancellationToken ct) {
                    string pointPhase=phase;
                    SetAssistStatus($"{methodLabel} AF | Acquiring {count} frames at {position}");
                    var analysis=await FocusFrameBatchPipeline.AcquireAsync(count,ReadFrame,
                        (frame,cancellation)=>Analyze(frame with {FocuserPosition=position,ExposureSeconds=settings.Seconds},position,pointPhase,cancellation),ct);
                    return Complete();
                    async Task<FocusPointStatistics> Complete() {
                        var values=await analysis;ct.ThrowIfCancellationRequested();
                        var point=FocusPointStatistics.Create(position,values,mask);
                        await diagnostics.EventAsync(new {Position=position,Phase=pointPhase,point.Median,point.Uncertainty,point.Values,point.Inliers,point.Attempted});
                        ct.ThrowIfCancellationRequested();
                        return point;
                    }
                }
                void ReportPoint(FocusPointStatistics point) {
                    AutoFocusBoxes.Add(point.Box);
                    AutoFocusMetricText=$"{AutoFocusMetricAxis}: {point.Median:F3} ± {point.Uncertainty:F3} | Position {point.Position} | {point.Inliers.Length}/{point.Attempted} frames";
                    RaisePropertyChanged(nameof(AutoFocusMetricText));RaisePropertyChanged(nameof(PreviewMetricText));
                }
                int reductions=0;
                while(true) {
                    ClearAutofocusStatistics();phase="preflight";
                    try {
                        if(focuserMediator.GetInfo().Position!=origin) {
                            int actual=await Move(origin,token);
                            if(actual!=origin) throw new InvalidOperationException("Focuser did not return to the scan origin.");
                        }
                        // Verify the chosen ROI before the first scan movement.
                        await (await Acquire(origin,token));
                        phase="scan";
                        var points=(await FocusCurveScanRunner.ScanAsync(positions,Move,Acquire,ReportPoint,token)).ToList();
                        while(FocusCurveScanRunner.Extension(points,mask,step,offsets,maximum) is int next) {
                            if(points.Count>=Math.Min(60,offsets*10)) throw new InvalidOperationException("NINA-style AF scan point limit reached without bracketing focus.");
                            SetAssistStatus($"{methodLabel} AF | Extending curve by one fixed {step}-step interval");
                            points.AddRange(await FocusCurveScanRunner.ScanAsync(new[]{next},Move,Acquire,ReportPoint,token));
                        }
                        var fit=await Task.Run(()=>FocusScanFit.Calculate(points,fitting,threshold),token);
                        for(int i=0;i<=200;i++) {
                            double x=points.Min(p=>p.Position)+(points.Max(p=>p.Position)-points.Min(p=>p.Position))*(i/200.0);
                            AutoFocusFitPoints.Add(new DataPoint(x,fit.Evaluate(x)));
                        }
                        await diagnostics.EventAsync(new {Fit=fit.Method,fit.Position,fit.Predicted,fit.RSquared});
                        SetAssistStatus($"{methodLabel} AF | {fit.Method} R² {fit.RSquared:F3} · verifying position {fit.Position}");
                        if(await Move(fit.Position,token)!=fit.Position) throw new InvalidOperationException("Focuser did not reach the fitted position.");
                        phase="verify";var verification=await(await Acquire(fit.Position,token));
                        token.ThrowIfCancellationRequested();
                        var best=points.MinBy(p=>mask?Math.Abs(p.Median):p.Median);
                        double uncertainty=3*Math.Sqrt(best.Uncertainty*best.Uncertainty+verification.Uncertainty*verification.Uncertainty);
                        if(mask ? Math.Abs(verification.Median)+2*verification.Uncertainty>.5 : verification.Median>best.Median+Math.Max(best.Median*.15,uncertainty))
                            throw new InvalidOperationException("The independently measured focus does not confirm the fitted minimum. Focus was not verified.");
                        AutoFocusVerificationBoxes.Add(verification.Box);
                        TargetPosition=fit.Position;verified=true;
                        assistCts.CancelAfter(Timeout.InfiniteTimeSpan);
                        AutoFocusMetricText=$"Verified · Position {fit.Position} · {verification.Median:F3} px · R² {fit.RSquared:F3}";
                        RaisePropertyChanged(nameof(AutoFocusMetricText));RaisePropertyChanged(nameof(PreviewMetricText));
                        await diagnostics.EventAsync(new {Verified=true,Position=fit.Position,verification.Median,verification.Uncertainty});
                        break;
                    } catch(FocusSaturationException) {
                        double minimum=Math.Max(.001,cameraMediator.GetInfo().ExposureMin), shorter=Math.Max(minimum,settings.Seconds/4);
                        if(reductions>=8 || shorter>=settings.Seconds) throw;
                        await CloseStream();token.ThrowIfCancellationRequested();EnsureDevices();
                        reductions++;settings=settings with {Seconds=shorter};PreviewExposureMs=shorter*1000;
                        tracking=null;reference=null;spikeAnalysis.RestartScan();needsFreshFrame=true;settledAt=Stopwatch.GetTimestamp();
                        await diagnostics.EventAsync(new {AutoExposure=true,ExposureSeconds=shorter,RestartWholeScan=true});
                        SetAssistStatus($"{methodLabel} AF | Saturation: exposure reduced to {shorter*1000:F1} ms · restarting scan");
                    }
                }
                Notification.ShowSuccess(mask?"Bahtinov focus verified. Remove the mask before imaging.":"Autofocus curve minimum verified.");
                if(!monitor) return 1;
                IsMoving=false;phase="monitor";graphPreviewMovesEnabled=true;
                RaisePropertyChanged(nameof(CanMoveFromGraph));
                long lastSaved=-1;
                long lastSpikeAnalysis=0;
                while(!token.IsCancellationRequested) {
                    EnsureDevices();
                    if(graphMovement?.IsCompleted==true) await FinishGraphMovement();
                    if(pendingPreviewTarget!=null) {
                        verified=false;manuallyAdjusted=true;InvalidateGraphFocusVerification();
                        graphMovement=StartGraphPreviewMove();
                        needsFreshFrame=false;
                        if(!streaming) await FinishGraphMovement();
                    }
                    var frame=await ReadFrame(token);
                    if (!hfr && !mask && (lastSpikeAnalysis==0 || Stopwatch.GetElapsedTime(lastSpikeAnalysis).TotalSeconds>=1)) {
                        var spikes=await Task.Run(()=>AnalyzePreviewSpikes(frame.Pixels,frame.Width,frame.Height),token);
                        token.ThrowIfCancellationRequested();PublishPreviewSpikeAngle(spikes);lastSpikeAnalysis=Stopwatch.GetTimestamp();
                    }
                    // Monitoring displays the current image without adding it to fit statistics.
                    FocusPreviewImage=await Task.Run(()=>RenderFocusPreview(frame.Pixels,frame.Width,frame.Height,
                        mask?BahtinovAnalyzer.Analyze(frame.Pixels,frame.Width,frame.Height):null),token);
                    RaisePropertyChanged(nameof(FocusPreviewImage));RaisePropertyChanged(nameof(LiveDisplayImage));
                    SetAssistStatus($"{AutoFocusMetricText} | Live preview · Stop to finish");
                    long now=Stopwatch.GetTimestamp();
                    if(lastSaved<0 || Stopwatch.GetElapsedTime(lastSaved,now).TotalSeconds>=5) {
                        await diagnostics.SaveAsync(frame.Pixels,frame.Width,frame.Height,new {CameraId=cameraId,Phase=phase,Position=focuserMediator.GetInfo()?.Position,FocuserMoving=IsMoving,ExposureSeconds=settings.Seconds,SensorRoi=settings.Roi});lastSaved=now;
                    }
                }
                return verified?1:0;
            } catch(OperationCanceledException) {
                AutoFocusMetricText=verified?"Preview stopped · focus verified":manuallyAdjusted?"Preview stopped · manual adjustment":"Autofocus stopped or timed out · focus not verified";
                RaisePropertyChanged(nameof(PreviewMetricText));return verified?1:0;
            } catch(Exception e) {
                if(diagnostics!=null) {
                    try {await diagnostics.EventAsync(new {Failure=e.ToString(),Verified=verified,Phase=phase});}
                    catch(Exception writeError) {Logger.Error("Focus diagnostics write failed",writeError);}
                }
                AutoFocusMetricText="Autofocus failed · focus not verified";RaisePropertyChanged(nameof(PreviewMetricText));throw;
            } finally {
                if(reserved) {graphPreviewMovesEnabled=false;assistCts?.Cancel();}
                try {await CloseStream();} finally {
                    try {if(graphMovement!=null) await FinishGraphMovement();}
                    catch(OperationCanceledException) when(assistCts?.IsCancellationRequested==true) { }
                    catch(Exception error) {Logger.Error("Graph focuser movement cleanup failed",error);}
                    finally {if(reserved) EndAssist(true);}
                }
            }
        }
    }
}
