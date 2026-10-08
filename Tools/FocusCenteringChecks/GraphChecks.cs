using System.Reflection;
using System.IO;
using Cwseo.NINA.ManualFocuser.Dockables;
using Cwseo.NINA.ManualFocuser.Models;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Annotations;
using OxyPlot.Series;

internal static class GraphChecks {
    public static void Run(Action<bool,string> check) {
        Exception failure=null;
        var thread=new Thread(()=>{try{RenderAndHitTest(check);CheckMovement(check).GetAwaiter().GetResult();}catch(Exception e){failure=e;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure!=null)throw new Exception("Graph control checks failed",failure);
    }
    private static void RenderAndHitTest(Action<bool,string> check) {
        var model=new PlotModel{Title="Focus position — drag the X-axis, release to move",Background=OxyColor.FromRgb(28,28,28),
            TextColor=OxyColors.White,TitleColor=OxyColors.White,PlotAreaBorderColor=OxyColors.Gray};
        var axis=new LinearAxis{Key="FocusPosition",Position=AxisPosition.Bottom,Title="Focuser position",MinimumPadding=.08,MaximumPadding=.08};
        model.Axes.Add(axis);model.Axes.Add(new LinearAxis{Position=AxisPosition.Left,Title="HFR (px)"});
        var points=new BoxPlotSeries{BoxWidth=750,Fill=OxyColors.SlateGray,Stroke=OxyColors.White};
        for(int position=15000;position<=25000;position+=2500){double y=2+Math.Pow((position-20000)/4000.0,2);points.Items.Add(new BoxPlotItem(position,y-.12,y-.05,y,y+.05,y+.12));}
        model.Series.Add(points);
        model.Annotations.Add(new LineAnnotation{Type=LineAnnotationType.Vertical,X=18000,Text="Current: 18000",Color=OxyColors.Cyan,TextColor=OxyColors.Cyan,LineStyle=LineStyle.Dash,StrokeThickness=1.5});
        model.Annotations.Add(new LineAnnotation{Type=LineAnnotationType.Vertical,X=21000,Text="Move → 21000",Color=OxyColors.Gold,TextColor=OxyColors.Gold,StrokeThickness=2});
        string path="bin/graph-focus-control.png";
        OxyPlot.Wpf.PngExporter.Export(model,path,1000,600);
        var area=model.PlotArea;
        check(FocusGraphHitTest.TryPosition(model,new ScreenPoint(axis.Transform(17250),area.Bottom+8),600,true,out double target) && Math.Abs(target-17250)<1e-6,
            "X-axis click maps screen pixels to the actual focus coordinate");
        check(!FocusGraphHitTest.TryPosition(model,new ScreenPoint(axis.Transform(17250),area.Top+20),600,true,out _),"Box/curve hover and tracking cannot trigger a focuser move");
        check(!FocusGraphHitTest.TryPosition(model,new ScreenPoint(area.Left-10,area.Bottom+8),600,true,out _),"Y-axis and chart margins cannot start a graph drag");
        check(!FocusGraphHitTest.TryPosition(model,new ScreenPoint(area.Right,601),600,true,out _),"A gesture outside the graph cannot start a focus drag");
        check(FocusGraphHitTest.TryPosition(model,new ScreenPoint(area.Right+1000,area.Top),600,false,out target) && Math.Abs(target-axis.ActualMaximum)<1e-6,
            "Captured drag clamps at the visible chart edge");
        axis.Zoom(18000,22000);OxyPlot.Wpf.PngExporter.Export(model,path,500,350);area=model.PlotArea;
        check(FocusGraphHitTest.TryPosition(model,new ScreenPoint(axis.Transform(20500),area.Bottom+8),350,true,out target) && Math.Abs(target-20500)<1e-6,
            "Zoom and resize use the rendered axis transform rather than stale bounds");
        axis.Reset();OxyPlot.Wpf.PngExporter.Export(model,path,1000,600);
        check(!FocusGraphHitTest.TryPosition(new PlotModel(),new ScreenPoint(10,10),100,true,out _),"An unrendered chart cannot issue a position");
    }
    private static async Task CheckMovement(Action<bool,string> check) {
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,"Database","Migration"));
        var profile=Fake.Of<IProfile>((m,a)=>Fake.Unexpected(m));
        var profiles=Fake.Of<IProfileService>((m,a)=>m.Name=="get_ActiveProfile"?profile:m.Name.StartsWith("add_")||m.Name.StartsWith("remove_")?null:Fake.Unexpected(m));
        var info=new FocuserInfo{Connected=true,DeviceId="Graph.Test",Position=10000};
        var cameraInfo=new CameraInfo();bool cameraBusy=false;
        int moves=0,lastTarget=-1;
        var motorFinish=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var device=Fake.Of<IFocuser>((m,a)=>m.Name=="get_MaxStep"?50000:Fake.Unexpected(m));
        async Task<int> Move(int target,CancellationToken token){moves++;lastTarget=target;await motorFinish.Task;token.ThrowIfCancellationRequested();info.Position=target;return target;}
        var motor=Fake.Of<IFocuserMediator>((m,a)=>m.Name switch{"GetInfo"=>info,"GetDevice"=>device,"MoveFocuser"=>Move((int)a[0],(CancellationToken)a[1]),"RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var camera=Fake.Of<ICameraMediator>((m,a)=>m.Name switch{"GetInfo"=>cameraInfo,"IsFreeToCapture"=>!cameraBusy,"RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var mount=Fake.Of<ITelescopeMediator>((m,a)=>m.Name switch{"GetInfo"=>new TelescopeInfo(),"RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var guider=Fake.Of<IGuiderMediator>((m,a)=>m.Name switch{"GetInfo"=>new GuiderInfo(),"RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var wheel=Fake.Of<IFilterWheelMediator>((m,a)=>m.Name is "RegisterConsumer" or "RemoveConsumer"?null:Fake.Unexpected(m));
        var imaging=Fake.Of<IImagingMediator>((m,a)=>m.Name is "add_ImagePrepared" or "remove_ImagePrepared"?null:Fake.Unexpected(m));
        var status=Fake.Of<IApplicationStatusMediator>((m,a)=>m.Name=="StatusUpdate"?null:Fake.Unexpected(m));
        using var vm=new ManualFocuserDockableVM(profiles,camera,imaging,wheel,motor,mount,guider,null,null,null,null,null,status);
        typeof(ManualFocuserDockableVM).GetProperty(nameof(vm.FocuserInfo)).SetValue(vm,info);
        typeof(ManualFocuserDockableVM).GetProperty(nameof(vm.CameraInfo)).SetValue(vm,cameraInfo);vm.FocusMode="Auto";
        check(vm.SelectGraphPosition(-100) && vm.GraphSelectionPosition==0,"Graph targets respect the focuser lower limit");
        check(vm.SelectGraphPosition(100000) && vm.GraphSelectionPosition==50000,"Graph targets respect the public focuser MaxStep limit");
        check(!vm.SelectGraphPosition(double.NaN) && !vm.SelectGraphPosition(double.PositiveInfinity) && moves==0,"Invalid coordinates and target previews issue no motor command");
        vm.CancelGraphSelection();check(!vm.GraphSelectionVisible,"Canceled graph preview hides the target marker");
        check(await vm.MoveFocusFromGraphAsync(10000)==0 && moves==0,"Selecting the current position does not command a redundant move");
        cameraInfo.Connected=true;cameraBusy=true;
        check(!vm.SelectGraphPosition(20000),"Another camera workflow blocks manual graph movement");
        cameraBusy=false;cameraInfo.IsExposing=true;
        check(!vm.SelectGraphPosition(20000),"A running host exposure blocks graph movement");
        cameraInfo.IsExposing=false;cameraInfo.LiveViewEnabled=true;
        check(!vm.SelectGraphPosition(20000),"An external live view blocks graph movement");
        cameraInfo.LiveViewEnabled=false;
        var move=vm.MoveFocusFromGraphAsync(20000);
        check(vm.IsMoving && moves==1 && lastTarget==20000 && !move.IsCompleted,"Release submits one absolute move and holds the movement lock");
        check(await vm.MoveFocusFromGraphAsync(21000)==0 && moves==1,"A second graph release cannot overlap the motor command");
        motorFinish.SetResult(20000);
        check(await move==1 && !vm.IsMoving && vm.GraphCurrentPosition==20000,"Completed graph movement reports the actual focuser position and unlocks controls");
        motorFinish=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        move=vm.MoveFocusFromGraphAsync(22000);vm.HaltFocuserCommand.Execute(null);
        check(!move.IsCompleted && vm.IsMoving,"Halt retains the motor lock while native movement cleanup is pending");
        motorFinish.SetResult(22000);
        check(await move==0 && !vm.IsMoving && info.Position==20000,"Canceled graph movement drains cleanup without claiming the target was reached");
    }
}
