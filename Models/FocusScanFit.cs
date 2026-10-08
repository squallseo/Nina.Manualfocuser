using System;
using System.Collections.Generic;
using System.Linq;
using NINA.Core.Enum;
using NINA.WPF.Base.Utility.AutoFocus;
using OxyPlot;
using OxyPlot.Series;

namespace Cwseo.NINA.ManualFocuser.Models {
    public sealed record FocusScanFit(int Position, double Predicted, double RSquared,
        string Method, Func<double,double> Evaluate) {
        public static FocusScanFit Calculate(IReadOnlyList<FocusPointStatistics> samples,FocusCurveModel model,double threshold) {
            bool signed=model==FocusCurveModel.LinearZeroCrossing;
            return Calculate(samples,signed,signed ? AFCurveFittingEnum.PARABOLIC : FocusCurveOptions.ToNina(model),threshold)
                with {Method=FocusCurveOptions.Label(model)};
        }
        public static FocusScanFit Calculate(IReadOnlyList<FocusPointStatistics> samples, bool signed,
            AFCurveFittingEnum method, double threshold) {
            var data = samples.OrderBy(p=>p.Position).ToArray();
            if (data.Select(p=>p.Position).Distinct().Count()<5) throw new InvalidOperationException("Autofocus needs at least five distinct measured positions.");
            double center = data[data.Length/2].Position;
            var points = data.Select(p=>new ScatterErrorPoint(p.Position-center,p.Median,0,p.Uncertainty)).ToList();
            Func<double,double> curve; double candidate,r2; string name;
            if (signed) {
                double weight = points.Sum(p=>1/(p.ErrorY*p.ErrorY));
                double mx=points.Sum(p=>p.X/(p.ErrorY*p.ErrorY))/weight, my=points.Sum(p=>p.Y/(p.ErrorY*p.ErrorY))/weight;
                double slope=points.Sum(p=>(p.X-mx)*(p.Y-my)/(p.ErrorY*p.ErrorY))/points.Sum(p=>(p.X-mx)*(p.X-mx)/(p.ErrorY*p.ErrorY));
                double intercept=my-slope*mx;
                curve=x=>slope*x+intercept; candidate=-intercept/slope;
                double total=points.Sum(p=>(p.Y-my)*(p.Y-my)/(p.ErrorY*p.ErrorY));
                r2=1-points.Sum(p=>Math.Pow(p.Y-curve(p.X),2)/(p.ErrorY*p.ErrorY))/total;
                name="Signed-error zero crossing";
            } else {
                switch(method) {
                    case AFCurveFittingEnum.PARABOLIC: {
                        var fit=new QuadraticFitting().Calculate(points); curve=fit.Fitting;candidate=fit.Minimum.X;r2=fit.RSquared;break;
                    }
                    case AFCurveFittingEnum.HYPERBOLIC: {
                        var fit=new HyperbolicFitting().Calculate(points);curve=fit.Fitting;candidate=fit.Minimum.X;r2=fit.RSquared;break;
                    }
                    case AFCurveFittingEnum.TRENDLINES:
                    case AFCurveFittingEnum.TRENDPARABOLIC:
                    case AFCurveFittingEnum.TRENDHYPERBOLIC: {
                        var trend=new TrendlineFitting().Calculate(points,"STARHFR");
                        candidate=trend.Intersection.X;r2=Math.Min(trend.LeftTrend.RSquared,trend.RightTrend.RSquared);
                        curve=x=>x<=trend.Intersection.X ? trend.LeftTrend.Slope*x+trend.LeftTrend.Offset : trend.RightTrend.Slope*x+trend.RightTrend.Offset;
                        if(method==AFCurveFittingEnum.TRENDPARABOLIC) {
                            var fit=new QuadraticFitting().Calculate(points);candidate=(candidate+fit.Minimum.X)/2;r2=Math.Min(r2,fit.RSquared);curve=fit.Fitting;
                        } else if(method==AFCurveFittingEnum.TRENDHYPERBOLIC) {
                            var fit=new HyperbolicFitting().Calculate(points);candidate=(candidate+fit.Minimum.X)/2;r2=Math.Min(r2,fit.RSquared);curve=fit.Fitting;
                        }
                        break;
                    }
                    default: throw new InvalidOperationException("Select a supported NINA autofocus curve fitting method.");
                }
                name="NINA "+method;
            }
            candidate+=center;
            if (!double.IsFinite(candidate) || !double.IsFinite(r2) || r2 < threshold)
                throw new InvalidOperationException($"Autofocus curve is unreliable (R² {r2:F3}; required {threshold:F3}). Check step size, ROI and exposure.");
            int position=checked((int)Math.Round(candidate));
            var left=data.Where(p=>p.Position<position).ToArray();var right=data.Where(p=>p.Position>position).ToArray();
            if(left.Length<2 || right.Length<2) throw new InvalidOperationException("The fitted focus is not bracketed by measurements on both sides. Adjust the starting position or NINA AF scan range.");
            double predicted=curve(position-center);
            var best=data.MinBy(p=>signed ? Math.Abs(p.Median) : p.Median);
            if(!double.IsFinite(predicted) || (!signed && (predicted<=0 || curve(data[0].Position-center)<=predicted || curve(data[^1].Position-center)<=predicted)))
                throw new InvalidOperationException("The curve has no supported focus minimum inside the measured range.");
            if(signed ? !left.Any(p=>Math.Sign(p.Median)!=Math.Sign(right[^1].Median) && Math.Abs(p.Median)>2*p.Uncertainty) :
                !left.Any(p=>p.Median-best.Median>2*Math.Sqrt(p.Uncertainty*p.Uncertainty+best.Uncertainty*best.Uncertainty)))
                throw new InvalidOperationException("Focus changes are smaller than measurement noise; the scan cannot establish a minimum.");
            if(!signed && !right.Any(p=>p.Median-best.Median>2*Math.Sqrt(p.Uncertainty*p.Uncertainty+best.Uncertainty*best.Uncertainty)))
                throw new InvalidOperationException("Focus changes on the other side are smaller than measurement noise.");
            return new(position,predicted,r2,name,x=>curve(x-center));
        }
    }
}
