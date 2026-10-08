using System;
using System.Linq;
using OxyPlot;
using OxyPlot.Axes;

namespace Cwseo.NINA.ManualFocuser.Models {
    public static class FocusGraphHitTest {
        public static bool TryPosition(PlotModel model,ScreenPoint screen,double height,bool requireAxis,out double position) {
            position=double.NaN;
            if(model==null) return false;
            var area=model.PlotArea;
            if(area.Width<=0 || area.Height<=0 || (requireAxis &&
                (screen.X<area.Left || screen.X>area.Right || screen.Y<area.Bottom || screen.Y>height))) return false;
            if(model.Axes.FirstOrDefault(a=>a.Key=="FocusPosition") is not LinearAxis axis) return false;
            position=axis.InverseTransform(Math.Clamp(screen.X,area.Left,area.Right));
            return double.IsFinite(position);
        }
    }
}
