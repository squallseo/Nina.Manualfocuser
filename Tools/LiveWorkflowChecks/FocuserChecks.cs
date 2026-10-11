using System.Reflection;
using Cwseo.NINA.ManualFocuser.Dockables;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;

internal static class FocuserChecks {
    public static void Run(Action<bool,string> check) {
        Exception failure=null;
        var thread=new Thread(()=>{try{Verify(check).GetAwaiter().GetResult();}catch(Exception e){failure=e;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure!=null)throw new Exception("Focuser checks failed",failure);
    }
    private static async Task Verify(Action<bool,string> check) {
        var profiles=Fake.Of<IProfileService>((m,a)=>m.Name=="get_ActiveProfile"?Fake.Of<IProfile>((n,b)=>n.Name=="get_TelescopeSettings"?Fake.Properties<ITelescopeSettings>(new(){["NoSync"]=true}):Fake.Unexpected(n)):
            m.Name.StartsWith("add_")||m.Name.StartsWith("remove_")?null:Fake.Unexpected(m));
        var camera=Fake.Of<ICameraMediator>((m,a)=>m.Name switch {"GetInfo"=>new CameraInfo(),"RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var info=new FocuserInfo{Connected=true,DeviceId="fake.motor",Position=10000};
        var driver=Fake.Of<IFocuser>((m,a)=>m.Name=="get_MaxStep"?20000:Fake.Unexpected(m));
        int calls=0;bool slow=false,miss=false;
        TaskCompletionSource<int> finished=null;
        CancellationToken token=default;
        var motor=Fake.Of<IFocuserMediator>((m,a)=>m.Name switch {
            "GetInfo"=>info,"GetDevice"=>driver,"RegisterConsumer" or "RemoveConsumer"=>null,
            "MoveFocuser"=>Move((int)a[0],(CancellationToken)a[1]),
            "MoveFocuserRelative"=>Move(info.Position+(int)a[0],(CancellationToken)a[1]),_=>Fake.Unexpected(m)});
        Task<int> Move(int position,CancellationToken ct) {
            calls++;token=ct;
            if(slow){finished=new(TaskCreationOptions.RunContinuationsAsynchronously);return finished.Task;}
            info.Position=position;return Task.FromResult(miss?position-1:position);
        }
        var mount=Fake.Of<ITelescopeMediator>((m,a)=>m.Name switch {"GetInfo"=>new TelescopeInfo(),"RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var guider=Fake.Of<IGuiderMediator>((m,a)=>m.Name switch {"GetInfo"=>new GuiderInfo(),"RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var wheel=Fake.Of<IFilterWheelMediator>((m,a)=>m.Name is "RegisterConsumer" or "RemoveConsumer"?null:Fake.Unexpected(m));
        var imaging=Fake.Of<IImagingMediator>((m,a)=>m.Name is "add_ImagePrepared" or "remove_ImagePrepared"?null:Fake.Unexpected(m));
        var status=Fake.Of<IApplicationStatusMediator>((m,a)=>m.Name=="StatusUpdate"?null:Fake.Unexpected(m));
        using var vm=new ManualFocuserDockableVM(profiles,camera,imaging,wheel,motor,mount,guider,null,null,null,status);
        vm.UpdateDeviceInfo(info);
        var invoke=typeof(ManualFocuserDockableVM).GetMethod("MoveStandaloneAsync",BindingFlags.NonPublic|BindingFlags.Instance);
        Task<int> Run(int? relative)=>(Task<int>)invoke.Invoke(vm,new object[]{relative});
        check(vm.MoveINCommand.CanExecute(null),"Connected idle focuser permits manual movement");
        check(await Run(600)==1 && info.Position==10600 && !vm.IsMoving,"Relative move completes and updates position");
        int before=calls;bool rejected=false;
        try{await Run(20000);}catch(InvalidOperationException){rejected=true;}
        check(rejected && calls==before && !vm.IsMoving,"Travel limit rejects an out-of-range target before motor motion");
        slow=true;var pending=Run(-600);
        check(vm.IsMoving && !vm.MoveOUTCommand.CanExecute(null) && !vm.CanConfigureLive,"Active motor blocks duplicate moves and ROI changes");
        check(await Run(600)==0 && calls==before+1,"Overlapping direct invocation cannot start another motor task");
        vm.HaltFocuserCommand.Execute(null);
        check(token.IsCancellationRequested && !pending.IsCompleted && vm.IsMoving,"Stop requests cancellation and waits for driver completion");
        finished.SetResult(10000);bool canceled=false;
        try{await pending;}catch(OperationCanceledException){canceled=true;}
        check(canceled && !vm.IsMoving,"Canceled movement unlocks controls after cleanup");
        slow=false;miss=true;rejected=false;
        try{await Run(-600);}catch(InvalidOperationException){rejected=true;}
        check(rejected && !vm.IsMoving,"Driver-reported wrong position is surfaced as movement failure");
        info.Connected=false;before=calls;
        check(!vm.MoveINCommand.CanExecute(null) && await Run(600)==0 && calls==before,"Disconnected motor cannot be commanded");
    }
}
