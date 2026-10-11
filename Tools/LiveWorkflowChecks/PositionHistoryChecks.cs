using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;

internal static class PositionHistoryChecks {
    public static void Run(Action<bool,string> check) {
        var history = new PositionHfrHistory();
        check(double.IsNaN(history.MinimumPoint().X) && history.MovementPoints().Length==0,"Empty manual history has no minimum or movement arrow");
        history.Record(12000,4);
        check(history.MinimumPoint().X==12000 && history.MovementPoints().Length==0,"One measurement shows a minimum without inventing a previous focus position");
        history.Record(9000,2);
        check(history.MovementPoints()[0].X==12000 && history.MovementPoints()[1].X==9000,"Arrow preserves measurement order when focus moves to a smaller position");
        history.Record(9000,6);
        check(history.MinimumPoint().Y==4 && history.MovementPoints()[0].X==12000 && history.MovementPoints()[1].Y==4,
            "Repeated captures update the plotted median while retaining the last focus movement");
        history.Record(11000,3);
        check(history.MinimumPoint().X==11000 && history.MinimumPoint().Y==3,"Minimum marker follows the lowest plotted position median");
        history.Record(9000,1);
        check(history.MovementPoints()[0].X==11000 && history.MovementPoints()[1].X==9000 && history.MinimumPoint().X==9000,
            "Returning to an existing position updates the arrow and minimum");
        history.Record(5000,double.NaN);history.Record(-1,1);history.Record(6000,0);
        check(history.MovementPoints()[1].X==9000 && history.DisplayPoints().Length==3,"Invalid measurements do not advance the arrow or alter the minimum");
        history.Clear();
        check(double.IsNaN(history.MinimumPoint().X) && history.MovementPoints().Length==0,"Clearing history resets arrow endpoints and minimum");
        history.Record(7000,5);
        check(history.MovementPoints().Length==0 && history.MinimumPoint().X==7000,"Capture after Clear starts without an arrow to the previous session");
    }
}
