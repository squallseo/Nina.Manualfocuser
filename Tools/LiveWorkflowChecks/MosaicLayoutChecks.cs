using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;

internal static class MosaicLayoutChecks {
    public static async Task Run(Action<bool,string> check) {
        Console.WriteLine("Mosaic regression runtime: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        var dimensions = new[] { (4788,3192,512), (9576,6388,512), (1281,1201,512), (2048,1536,512), (1300,1400,333), (3,3,512) };
        int plans = 0;
        for (int batch = 0; batch < 80; batch++) {
            for (int iteration = 0; iteration < 1000; iteration++) {
                var (width,height,tileSize) = dimensions[batch < 40 ? 0 : iteration % dimensions.Length];
                var layout = FocusAberrationMosaic.Plan(width,height,tileSize);
                int size = Math.Min(tileSize, Math.Min(width,height)/3);
                for (int i = 0; i < 9; i++) {
                    var tile = layout.Tiles[i];
                    int col=i%3, row=i/3;
                    // Independent endpoint expectations, rather than comparing
                    // a bitmap to coordinates produced by the same faulty plan.
                    int x = col == 0 ? 0 : col == 1 ? (width-size)/2 : width-size;
                    int y = row == 0 ? 0 : row == 1 ? (height-size)/2 : height-size;
                    int displayX = col == 0 ? 0 : col == 1 ? size+8 : size+size+16;
                    int displayY = row == 0 ? 0 : row == 1 ? size+8 : size+size+16;
                    if (tile.X != x || tile.Y != y || tile.DisplayX != displayX || tile.DisplayY != displayY)
                        throw new Exception($"Incorrect layout after {plans} plans, {width}x{height}, tile {i}: {tile}");
                }
                plans++;
            }
            // Allow tiered compilation to replace the initial machine code.
            await Task.Delay(30);
        }
        check(plans == 80000,"80,000 hot mosaic layouts preserve all nine coordinates across six source/tile sizes");
    }
}
