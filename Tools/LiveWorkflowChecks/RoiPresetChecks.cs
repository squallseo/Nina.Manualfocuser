using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Cwseo.NINA.ManualFocuser.Dockables;
using NINA.Equipment.Equipment.MyCamera;

internal static partial class PreviewDisplayChecks
{
    private static void VerifySensorRoiPresets(ManualFocuserDockableVM vm, ManualFocuserDockableView view, Action<bool,string> check)
    {
        var originalCamera = vm.CameraInfo;
        var combo = Children(view).OfType<ComboBox>().Single(c => AutomationProperties.GetName(c)=="ROI size preset");
        void Bind() => view.Dispatcher.Invoke(()=>{},DispatcherPriority.DataBind);
        void Choose(string preset) { combo.SelectedValue=preset;Bind(); }
        vm.UpdateDeviceInfo(new CameraInfo());Bind();
        var percentages=combo.Items.Cast<ComboBoxItem>().Where(i=>i.Tag.ToString().EndsWith("%")).ToArray();
        var inspector=combo.Items.Cast<ComboBoxItem>().Single(i=>Equals(i.Tag,"Inspector"));
        check(!inspector.IsEnabled,"Inspector selection is disabled while sensor dimensions are unknown");
        check(percentages.Length==3 && percentages.All(i=>!i.IsEnabled),
            "Sensor-percentage choices stay disabled until sensor dimensions are known");
        vm.UpdateDeviceInfo(new CameraInfo {Connected=true,DeviceId="QHY600M-ROI.Test",XSize=9576,YSize=6388});Bind();
        check(percentages.All(i=>i.IsEnabled),"Connecting a sensor enables the percentage choices through their real bindings");
        typeof(ManualFocuserDockableVM).GetProperty(nameof(vm.IsSelectingRoi)).SetValue(vm,true);
        Choose("Inspector");
        check(inspector.IsEnabled && vm.IsAberrationInspector && !vm.IsSelectingRoi && !vm.CanUseBahtinovOverlay && Equals(combo.SelectedValue,"Inspector"),
            "The actual Inspector dropdown ends ROI editing and disables the single-star overlay for Inspector mode");
        vm.PreviewCenterX=60;vm.PreviewCenterY=40;
        Choose("50%");var half=vm.PreviewRoiRectangle;
        check(half.Width==4788 && half.Height==3192 && vm.PreviewRoiPreset=="50%" && Equals(combo.SelectedValue,"50%"),
            "Choosing 50% creates a rectangular half-width/half-height QHY ROI and retains the dropdown selection");
        check(Math.Abs((double)half.Width*half.Height/(9576L*6388)-.25)<.001,
            "A 50% size uses one quarter of the sensor pixel area");
        check(vm.PreviewCenterX==60 && vm.PreviewCenterY==40 && Math.Abs(half.X+half.Width/2.0-9576*.6)<4 && Math.Abs(half.Y+half.Height/2.0-6388*.4)<4,
            "Enlarging the ROI preserves the selected star anchor and applies required camera alignment");
        Choose("67%");var crop=vm.PreviewRoiRectangle;
        check(crop.Width==6412 && crop.Height==4276 && Math.Abs((double)crop.Width/crop.Height-9576.0/6388)<.002,
            "The crop-size preset keeps the camera sensor aspect ratio");
        vm.PreviewCenterX=100;vm.PreviewCenterY=100;
        Choose("100%");var full=vm.PreviewRoiRectangle;
        check(full.X==0 && full.Y==0 && full.Width==9576 && full.Height==6388 && vm.PreviewRoiPreset=="100%",
            "100% reaches the entire QHY sensor from an off-center star selection");
        Choose("50%");var edge=vm.PreviewRoiRectangle;
        check(edge.X+edge.Width==9576 && edge.Y+edge.Height==6388 && vm.PreviewCenterX==100 && vm.PreviewCenterY==100,
            "Returning from full frame preserves the star anchor and clamps the smaller crop at the sensor edge");
        var type=typeof(ManualFocuserDockableVM);
        type.GetProperty(nameof(vm.IsSelectingRoi)).SetValue(vm,true);
        vm.EditPreviewRoi(new Rect(200,400,1000,700));Bind();
        check(vm.PreviewRoiPreset=="Custom" && Equals(combo.SelectedValue,"Custom"),
            "Mouse resizing away from a percentage immediately changes the preset to Custom");
        type.GetProperty(nameof(vm.IsSelectingRoi)).SetValue(vm,false);
        Choose("1024");check(vm.PreviewRoiRectangle.Width==1024 && vm.PreviewRoiRectangle.Height==1024 && vm.PreviewRoiPreset=="1024",
            "Pixel-size presets remain square after using percentage presets");
        var before=vm.PreviewRoiRectangle;
        type.GetField("assistRunning",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(vm,true);
        vm.PreviewRoiPreset="100%";
        check(vm.PreviewRoiRectangle==before,"An active live run cannot change capture dimensions through a preset");
        type.GetField("assistRunning",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(vm,false);
        vm.PreviewRoiPreset="125%";vm.PreviewRoiPreset="-50%";vm.PreviewRoiPreset="invalid";
        check(vm.PreviewRoiRectangle==before,"Invalid or unsupported percentage values leave the chosen ROI untouched");
        vm.UpdateDeviceInfo(new CameraInfo {Connected=true,DeviceId="ZWOptical_ASI.ROI.Test",XSize=1937,YSize=1097});Bind();
        Choose("50%");check(vm.PreviewRoiRectangle.Width==968 && vm.PreviewRoiRectangle.Height==548 && vm.PreviewRoiPreset=="50%",
            "Percentage sizes adapt to a different sensor and retain native ASI width/height alignment");
        Choose("100%");var aligned=vm.PreviewRoiRectangle;
        check(aligned.X==0 && aligned.Y==0 && aligned.Width==1936 && aligned.Height==1096 && vm.PreviewRoiPreset=="100%",
            "Full-frame ASI selection uses the largest aligned bounds without exceeding an odd-sized sensor");
        vm.SetRoiSizeCommand.Execute("50%");
        check(vm.PreviewRoiPreset=="50%","The shared ROI-size command accepts percentage presets too");
        vm.UpdateDeviceInfo(originalCamera);vm.PreviewCenterX=50;vm.PreviewCenterY=50;
        vm.PreviewRoiPreset="512";Bind();
    }
}
