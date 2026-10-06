using System.Windows;
using Cwseo.NINA.ManualFocuser.Dockables;

int passed = 0;
void Check(bool condition, string description) {
    if (!condition) throw new Exception(description);
    passed++; Console.WriteLine("PASS " + description);
}
var roi = new Rect(100, 100, 512, 256);
Check(RoiInteraction.HitTest(roi, new Point(300, 200)) == RoiHandle.Move, "Interior is movable");
Check(RoiInteraction.HitTest(roi, new Point(50, 50)) == RoiHandle.Draw, "Outside starts a new rectangle");
foreach (var (point, expected) in new[] {
    (new Point(100,200), RoiHandle.Left), (new Point(612,200), RoiHandle.Right),
    (new Point(300,100), RoiHandle.Top), (new Point(300,356), RoiHandle.Bottom),
    (new Point(100,100), RoiHandle.Left|RoiHandle.Top), (new Point(612,100), RoiHandle.Right|RoiHandle.Top),
    (new Point(100,356), RoiHandle.Left|RoiHandle.Bottom), (new Point(612,356), RoiHandle.Right|RoiHandle.Bottom)
}) Check(RoiInteraction.HitTest(roi, point) == expected, $"Handle {expected} is distinguished");
Check(RoiInteraction.HitTest(new Rect(0,0,8,8), new Point(4,4)) == RoiHandle.Move, "Small overview ROI still has a movable interior");
Rect Drag(RoiHandle handle, double dx, double dy) => RoiInteraction.Drag(roi, new Point(0,0), new Point(dx,dy), handle, 9600,6422);
var moved = Drag(RoiHandle.Move, 230,140);
Check(moved == new Rect(330,240,512,256), "Moving preserves rectangular dimensions and pointer offset");
Check(Drag(RoiHandle.Move,-99999,-99999) == new Rect(0,0,512,256), "Move clamps at top and left sensor edges");
Check(Drag(RoiHandle.Move,99999,99999) == new Rect(9088,6166,512,256), "Move clamps at bottom and right sensor edges");
var resized = Drag(RoiHandle.Left|RoiHandle.Top, -50,-30);
Check(resized.Right == roi.Right && resized.Bottom == roi.Bottom && resized.Size == new Size(562,286), "Corner resize keeps opposite corner anchored");
resized = Drag(RoiHandle.Right,100,90);
Check(resized.Left == roi.Left && resized.Top == roi.Top && resized.Width == 612 && resized.Height == 256, "Edge resize changes only one axis");
Check(Drag(RoiHandle.Left|RoiHandle.Top,99999,99999).Size == new Size(32,32), "Crossing the opposite corner stops at minimum dimensions");
Check(Drag(RoiHandle.Right|RoiHandle.Bottom,99999,99999).Size == new Size(9500,6322), "Resize allows full sensor bounds beyond the old 2048 limit");
var drawn = RoiInteraction.Drag(roi,new Point(800,600),new Point(300,200),RoiHandle.Draw,9600,6422);
Check(drawn == new Rect(300,200,500,400), "New rectangle supports reverse direction drawing");
drawn = RoiInteraction.Drag(roi,new Point(9599,6421),new Point(9600,6422),RoiHandle.Draw,9600,6422);
Check(drawn == new Rect(9568,6390,32,32), "Tiny drawing at sensor boundary stays valid");
Console.WriteLine($"{passed} ROI interaction checks passed; no hardware accessed.");
