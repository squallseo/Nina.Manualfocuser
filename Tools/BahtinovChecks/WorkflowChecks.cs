using Cwseo.NINA.ManualFocuser.Models;

internal static class WorkflowChecks {
    public static async Task Run(Action<bool, string> check) {
        var moves = new List<int>();
        Task<int> Move(int p, CancellationToken t) { moves.Add(p); return Task.FromResult(p); }
        var result = await BahtinovFocusRunner.RunAsync(1000, 100, 2, Move, (p, t) => Task.FromResult((p - 1030) / 50.0), default);
        check(result.Position == 1030 && Math.Abs(result.Error) < .5, "Motor workflow verifies interpolated zero");
        check(moves.All(p => p >= 800 && p <= 1200) && moves[^2] < moves[^1], "All moves bounded and final approach matches scan direction");
        async Task<bool> Fails(Func<Task> run) { try { await run(); return false; } catch (InvalidOperationException) { return true; } }
        moves.Clear();
        check(await Fails(async () => await BahtinovFocusRunner.RunAsync(1000, 100, 2, Move, (p, t) => Task.FromResult(double.NaN), default)) && moves.Count == 0,
            "Invalid preflight issues no motor command");
        moves.Clear();
        check(await Fails(async () => await BahtinovFocusRunner.RunAsync(1000, 100, 2, Move, (p, t) => Task.FromResult(2.0), default)) && moves.Count == 5,
            "Missing bracket stops at scan bound without extrapolation");
        moves.Clear();
        int readings = 0;
        check(await Fails(async () => await BahtinovFocusRunner.RunAsync(1000, 100, 2, Move, (p, t) => Task.FromResult(++readings == 6 ? 2.0 : (p - 1030) / 50.0), default)),
            "Failed final measurement cannot report autofocus success");
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
