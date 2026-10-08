using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

internal static class CurveChartPreview {
    public static void Write(IEnumerable<BoxPlotItem> boxes,IEnumerable<BoxPlotItem> verified,IEnumerable<DataPoint> curve,double width,string path) {
        var points=boxes.ToArray();var final=verified.ToArray();var fit=curve.ToArray();
        Exception failure=null;
        var thread=new Thread(()=>{try{
            var primary=OxyColor.FromRgb(230,230,230);var yellow=OxyColor.FromRgb(255,213,79);
            var model=new PlotModel {Title="Synthetic autofocus — multi-frame HFR statistics",TitleColor=primary,
                Background=OxyColor.FromRgb(28,28,28),PlotAreaBorderColor=OxyColors.Gray,TextColor=primary};
            model.Axes.Add(new LinearAxis {Position=AxisPosition.Bottom,Title="Focuser position",MajorStep=2500,MinimumPadding=.08,MaximumPadding=.08,TitleColor=primary,TextColor=primary});
            model.Axes.Add(new LinearAxis {Position=AxisPosition.Left,Title="HFR (px)",MinimumPadding=.08,MaximumPadding=.08,TitleColor=primary,TextColor=primary});
            var distribution=new BoxPlotSeries {BoxWidth=width,Fill=OxyColors.SlateGray,Stroke=primary,MedianThickness=2};
            foreach(var point in points)distribution.Items.Add(point);model.Series.Add(distribution);
            var line=new LineSeries {Color=yellow,StrokeThickness=2};line.Points.AddRange(fit);model.Series.Add(line);
            var confirmation=new BoxPlotSeries {BoxWidth=width,Fill=yellow,Stroke=primary,MedianThickness=2};
            foreach(var point in final)confirmation.Items.Add(point);model.Series.Add(confirmation);
            OxyPlot.Wpf.PngExporter.Export(model,path,1000,600);
        }catch(Exception e){failure=e;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure!=null)throw failure;
    }
}
