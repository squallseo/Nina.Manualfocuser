using System;
using System.Collections.Generic;
using System.Linq;
using NINA.Core.Enum;

namespace Cwseo.NINA.ManualFocuser.Models {
    public enum FocusCurveModel { Hyperbolic, Parabolic, Trendlines, TrendHyperbolic, TrendParabolic, LinearZeroCrossing }
    public sealed record FocusCurveChoice(FocusCurveModel Model,string Label);
    public static class FocusCurveOptions {
        public static IReadOnlyList<FocusCurveChoice> Choices { get; }=Array.AsReadOnly(new[] {
            new FocusCurveChoice(FocusCurveModel.Hyperbolic,"Hyperbolic (symmetric)"),
            new FocusCurveChoice(FocusCurveModel.Parabolic,"Parabolic"),
            new FocusCurveChoice(FocusCurveModel.Trendlines,"Trend lines"),
            new FocusCurveChoice(FocusCurveModel.TrendHyperbolic,"Hyperbolic + trend lines"),
            new FocusCurveChoice(FocusCurveModel.TrendParabolic,"Parabolic + trend lines")
        });
        public static bool IsWidthModel(FocusCurveModel model) => Choices.Any(c=>c.Model==model);
        public static FocusCurveModel Parse(string setting,FocusCurveModel fallback) =>
            Enum.TryParse<FocusCurveModel>(setting,true,out var value) && IsWidthModel(value) ? value : fallback;
        public static FocusCurveModel Resolve(string method,string hfrSetting,string spikeSetting) => method switch {
            "Linear" or "HFR" => Parse(hfrSetting,FocusCurveModel.Hyperbolic),
            "Spike" => Parse(spikeSetting,FocusCurveModel.Parabolic),
            "Bahtinov" => FocusCurveModel.LinearZeroCrossing,
            _ => throw new ArgumentException("Unknown autofocus measurement method.",nameof(method))
        };
        public static string Label(FocusCurveModel model) => model==FocusCurveModel.LinearZeroCrossing
            ? "Linear (zero crossing)" : Choices.First(c=>c.Model==model).Label;
        public static AFCurveFittingEnum ToNina(FocusCurveModel model) => model switch {
            FocusCurveModel.Hyperbolic => AFCurveFittingEnum.HYPERBOLIC,
            FocusCurveModel.Parabolic => AFCurveFittingEnum.PARABOLIC,
            FocusCurveModel.Trendlines => AFCurveFittingEnum.TRENDLINES,
            FocusCurveModel.TrendHyperbolic => AFCurveFittingEnum.TRENDHYPERBOLIC,
            FocusCurveModel.TrendParabolic => AFCurveFittingEnum.TRENDPARABOLIC,
            _ => throw new ArgumentException("This model does not fit a positive focus width.",nameof(model))
        };
    }
}
