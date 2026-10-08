using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cwseo.NINA.ManualFocuser.Models {
    public static class FocusCurveScanRunner {
        public static int? Extension(IReadOnlyList<FocusPointStatistics> points,bool signed,int step,int sidePoints,int maximum) {
            var sorted=points.OrderBy(p=>p.Position).ToArray();
            var best=sorted.MinBy(p=>signed?Math.Abs(p.Median):p.Median);
            if(sorted.Max(p=>p.Median)-sorted.Min(p=>p.Median)<=2*sorted.Max(p=>p.Uncertainty))
                throw new InvalidOperationException("Focus changes are smaller than measurement noise. Adjust AF step size rather than increasing the scan range.");
            long next;
            if(sorted.Count(p=>p.Position<best.Position)<sidePoints) next=(long)sorted[0].Position-step;
            else if(sorted.Count(p=>p.Position>best.Position)<sidePoints) next=(long)sorted[^1].Position+step;
            else return null;
            if(next<0 || next>maximum) throw new InvalidOperationException("The focus minimum cannot be bracketed within the focuser's travel limits.");
            return (int)next;
        }
        public static int[] Plan(int origin,int step,int offsets,int maximum=int.MaxValue) {
            if(origin<0 || step<1 || offsets<2 || offsets>12 || maximum<origin) throw new ArgumentException("Invalid autofocus scan settings (at least two offset steps are required).");
            var positions=Enumerable.Range(-offsets,2*offsets+1).Select(i=>(long)origin+i*(long)step)
                .Where(p=>p>=0 && p<=maximum).OrderByDescending(p=>p).Select(p=>(int)p).ToArray();
            if(positions.Length<5) throw new InvalidOperationException("The autofocus range contains fewer than five positions within the focuser limits.");
            return positions;
        }
        // Acquisition completes before moving. Only CPU analysis overlaps the move;
        // at most one position's analysis is outstanding, including on failure/Stop.
        public static async Task<IReadOnlyList<FocusPointStatistics>> ScanAsync(int[] positions,
            Func<int,CancellationToken,Task<int>> move,
            Func<int,CancellationToken,Task<Task<FocusPointStatistics>>> acquire,
            Action<FocusPointStatistics> report,CancellationToken cancellation) {
            using var stop=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var result=new List<FocusPointStatistics>();
            await Move(positions[0]);
            for(int i=0;i<positions.Length;i++) {
                stop.Token.ThrowIfCancellationRequested();
                var analysis=await acquire(positions[i],stop.Token);
                Task<int> moving=null;
                try {
                    if(i+1<positions.Length) moving=Move(positions[i+1]);
                    if(moving!=null && await Task.WhenAny(analysis,moving)==moving) await moving;
                    var point=await analysis;
                    stop.Token.ThrowIfCancellationRequested();
                    result.Add(point);report(point);
                    if(moving!=null) await moving;
                } catch {
                    stop.Cancel();
                    try { await analysis; } catch { }
                    if(moving!=null) {try{await moving;}catch{}}
                    throw;
                }
            }
            return result;
            async Task<int> Move(int position) {
                int actual=await move(position,stop.Token);
                stop.Token.ThrowIfCancellationRequested();
                if(actual!=position) throw new InvalidOperationException($"Focuser did not reach the requested position {position} (reported {actual}).");
                return actual;
            }
        }
    }
}
