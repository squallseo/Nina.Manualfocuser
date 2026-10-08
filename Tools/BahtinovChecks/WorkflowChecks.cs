using Cwseo.NINA.ManualFocuser.Models;

internal static class WorkflowChecks {
    public static async Task Run(Action<bool, string> check) {
        var moves = new List<int>();
        Task<int> Move(int p, CancellationToken t) { moves.Add(p); return Task.FromResult(p); }
        int adaptiveReads = 0;
        var result = await BahtinovFocusRunner.RunAsync(1000, 100, 2, Move, (p, t) => { adaptiveReads++; return Task.FromResult((p - 1030) / 50.0); }, default);
        check(adaptiveReads < 6, "Adaptive mask prediction reduces the previous six measurements");
        Console.WriteLine($"Adaptive Bahtinov measurements: {adaptiveReads}; previous scan: 6");
        check(result.Position == 1030 && Math.Abs(result.Error) < .5, "Motor workflow verifies interpolated zero");
        check(moves.All(p => p >= 800 && p <= 1200) && moves[^2] < moves[^1], "All moves bounded and final approach matches scan direction");
        async Task<bool> Fails(Func<Task> run) { try { await run(); return false; } catch (InvalidOperationException) { return true; } }
        moves.Clear();
        check(await Fails(async () => await BahtinovFocusRunner.RunAsync(1000, 100, 2, Move, (p, t) => Task.FromResult(double.NaN), default)) && moves.Count == 0,
            "Invalid preflight issues no motor command");
        moves.Clear();
        check(await Fails(async () => await BahtinovFocusRunner.RunAsync(1000, 100, 2, Move, (p, t) => Task.FromResult(2.0), default))
            && moves.Count<=4 && moves.All(p=>p>=800&&p<=1200),
            "Flat mask error stops after bounded larger slope probes");
        foreach(int initialStep in new[]{600,2500}) {
            moves.Clear();
            var scaled=await BahtinovFocusRunner.RunAsync(20000,initialStep,8,Move,
                (p,t)=>Task.FromResult((p-16600)/6000.0),default,()=>.12);
            check(Math.Abs(scaled.Error)<=.25 && moves.All(p=>p>=20000-initialStep*8 && p<=20000+initialStep*8),
                $"Mask AF with {initialStep} initial step detects usable slope and verifies zero within original bounds");
            if(initialStep==600)check(moves.Any(p=>Math.Abs(p-20000)>600),"Small mask changes trigger a larger calibration movement");
        }
        moves.Clear();
        var noisyProbe=await BahtinovFocusRunner.RunAsync(20000,600,8,Move,
            (p,t)=>Task.FromResult((p-16600)/6000.0+(p==20600?-.2:0)),default,()=>.12);
        check(Math.Abs(noisyProbe.Error)<=.25 && moves.Any(p=>Math.Abs(p-20000)>=2400),
            "A noisy reversed small mask change requires larger evidence before zero prediction");
        moves.Clear();
        check(await Fails(async()=>await BahtinovFocusRunner.RunAsync(20000,600,4,Move,
            (p,t)=>Task.FromResult(2+.03*Math.Sin(p)),default,()=>.2))
            && moves.All(p=>p>=17600&&p<=22400) && moves.Count<=5,
            "Noise-only mask changes never invent a direction or verified zero");
        moves.Clear();
        check(await Fails(async()=>await BahtinovFocusRunner.RunAsync(20000,600,4,Move,
            (p,t)=>Task.FromResult(2.0),default,()=>double.NaN)) && moves.Count==0,
            "Invalid mask uncertainty stops before a motor command");
        moves.Clear();
        int targetReads = 0;
        check(await Fails(async () => await BahtinovFocusRunner.RunAsync(1000, 100, 2, Move, (p, t) => Task.FromResult(p == 1030 && ++targetReads > 1 ? 2.0 : (p - 1030) / 50.0), default)),
            "Failed final measurement cannot report autofocus success");
        foreach (int focus in new[] { 825, 973, 1049, 1175 }) foreach (int sign in new[] { -1, 1 }) {
            moves.Clear(); int sampleNumber = 0;
            var adaptive = await BahtinovFocusRunner.RunAsync(1000,100,3,Move,(p,t)=> {
                double distance=(p-focus)/50.0;
                return Task.FromResult(sign*(distance+.015*distance*distance*distance)+.02*Math.Sin(++sampleNumber));
            },default);
            check(Math.Abs(adaptive.Error)<=.25 && Math.Abs(adaptive.Position-focus)<=13,
                $"Nonlinear noisy mask slope converges at {focus}, polarity {sign}");
            check(moves.All(p=>p>=700&&p<=1300),"Adaptive mask prediction stays within original bounds");
        }
        moves.Clear();
        var alreadyFocused = await BahtinovFocusRunner.RunAsync(1000,100,3,Move,(p,t)=>Task.FromResult(.1),default);
        check(alreadyFocused.Position==1000 && moves.Count==0,"Already focused star is confirmed without unnecessary movement");
        moves.Clear();
        check(await Fails(async()=>await BahtinovFocusRunner.RunAsync(1000,100,2,Move,(p,t)=>Task.FromResult((p-1500)/50.0),default))
            && moves.All(p=>p>=800&&p<=1200),"Outside-range zero cannot expand the configured motor limits");
        moves.Clear();
        using var cancellation = new CancellationTokenSource();
        bool cancelled = false;
        try {
            await BahtinovFocusRunner.RunAsync(1000, 100, 2, (p, t) => { moves.Add(p); cancellation.Cancel(); return Task.FromResult(p); },
                (p, t) => Task.FromResult(-2.0), cancellation.Token);
        } catch (OperationCanceledException) { cancelled = true; }
        check(cancelled && moves.Count == 1, "Cancellation after a move prevents further captures and moves");
        moves.Clear();
        check(await Fails(async () => await BahtinovFocusRunner.RunAsync(1000, 100, 2, (p, t) => Task.FromResult(p + 1), (p, t) => Task.FromResult(-2.0), default)),
            "Focuser position mismatch aborts the workflow");
    }
}
