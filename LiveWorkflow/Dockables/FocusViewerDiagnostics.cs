using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NINA.Core.Utility;
using ImageView = NINA.WPF.Base.View.ImageView;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    // Bounded trace for MF Image only. Capture bitmap data and layout metadata,
    // never render a live WPF visual: doing so can freeze the host's image display.
    internal sealed class FocusViewerDiagnostics {
        internal static readonly string RootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA", "ManualFocuser", "ViewerDiagnostics");
        internal static string DirectoryPath { get; } = Path.Combine(RootPath, "session-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId);
        private static readonly ConditionalWeakTable<ImageView, FocusViewerDiagnostics> observers = new();
        private static bool initialized;
        private readonly ImageView viewer;
        private readonly DispatcherTimer debounce;
        private readonly DispatcherTimer heartbeat;
        private readonly HashSet<string> reasons = new();
        private INotifyPropertyChanged context;
        private bool attached, wantSnapshot;
        private long sequence;
        private DateTime lastSnapshot;
        private int lastAnomalyKind;
        private static readonly JsonSerializerOptions jsonOptions = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        private sealed record WriteJob(string Json, BitmapSource Source);
        private static readonly Channel<WriteJob> writes = Channel.CreateBounded<WriteJob>(new BoundedChannelOptions(64) {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true
        });
        private static readonly Task writer = Task.Run(WriteLoop);

        internal static void Initialize() {
            var application = Application.Current;
            if (application == null) return;
            try {
                // NINA composes plugin parts on a worker thread. Application.Exit
                // and WPF handlers belong to the application's UI dispatcher.
                // Never synchronously wait for UI work during plugin composition.
                if (!application.Dispatcher.CheckAccess()) {
                    if (!application.Dispatcher.HasShutdownStarted)
                        application.Dispatcher.BeginInvoke(new Action(Initialize), DispatcherPriority.ContextIdle);
                    return;
                }
                if (initialized || application.Dispatcher.HasShutdownStarted) return;
                application.Exit += (_, _) => {
                    writes.Writer.TryComplete(); writer.Wait(TimeSpan.FromMilliseconds(500));
                };
                initialized = true;
                Logger.Info("MF Image diagnostics enabled (source bitmaps only): " + DirectoryPath + "; module=" + typeof(FocusViewerDiagnostics).Assembly.ManifestModule.ModuleVersionId);
            } catch (Exception error) {
                // Diagnostics must never prevent the focusing plugin from loading.
                Logger.Error("MF viewer diagnostics initialization failed", error);
            }
        }
        internal static void Observe(ImageView viewer) {
            if (viewer == null) return;
            try {
                if (!viewer.Dispatcher.CheckAccess()) {
                    if (!viewer.Dispatcher.HasShutdownStarted)
                        viewer.Dispatcher.BeginInvoke(new Action(() => Observe(viewer)), DispatcherPriority.ContextIdle);
                    return;
                }
                if (!IsOwnedViewer(viewer)) return;
                observers.GetValue(viewer, v => new FocusViewerDiagnostics(v)).Attach();
            } catch (Exception error) { Logger.Error("MF viewer diagnostics attachment failed", error); }
        }
        private FocusViewerDiagnostics(ImageView viewer) {
            this.viewer = viewer;
            debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.ContextIdle, (_, _) => {
                debounce.Stop(); Capture();
            }, viewer.Dispatcher);
            debounce.Stop();
            heartbeat = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => {
                if (IsInspector && viewer.IsVisible) { reasons.Add("inspector-heartbeat"); Capture(); }
            }, viewer.Dispatcher);
            heartbeat.Stop();
            viewer.Loaded += (_, _) => Attach();
            viewer.Unloaded += (_, _) => Detach();
            viewer.DataContextChanged += (_, _) => { Detach(); Attach(); };
        }
        private static bool IsOwnedViewer(ImageView view) => view.DataContext is ManualFocuserDockableVM && view.ImageAreaContent is FocusRoiOverlay;
        private bool IsRelevant => IsOwnedViewer(viewer);
        private bool IsInspector => viewer.DataContext is ManualFocuserDockableVM vm && vm.HasDisplayInspectorTiles;
        private void Attach() {
            if (attached || !IsRelevant) return;
            attached = true; SubscribeContext();
            viewer.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnClick), true);
            viewer.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel), true);
            viewer.PreviewKeyDown += OnKey;
            if (viewer.FindName("PART_ScaleTransform") is ScaleTransform zoom) zoom.Changed += OnZoom;
            if (viewer.FindName("PART_ScrollViewer") is ScrollViewer scroll) scroll.ScrollChanged += OnScroll;
            heartbeat.Start(); Queue("attach", true);
        }
        private void Detach() {
            if (!attached) return;
            attached = false;
            if (context != null) PropertyChangedEventManager.RemoveHandler(context, OnProperty, string.Empty);
            context = null;
            viewer.RemoveHandler(Button.ClickEvent, new RoutedEventHandler(OnClick));
            viewer.RemoveHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel));
            viewer.PreviewKeyDown -= OnKey;
            if (viewer.FindName("PART_ScaleTransform") is ScaleTransform zoom) zoom.Changed -= OnZoom;
            if (viewer.FindName("PART_ScrollViewer") is ScrollViewer scroll) scroll.ScrollChanged -= OnScroll;
            debounce.Stop(); heartbeat.Stop(); reasons.Clear(); wantSnapshot = false;
        }
        private void SubscribeContext() {
            if (context != null) PropertyChangedEventManager.RemoveHandler(context, OnProperty, string.Empty);
            context = viewer.DataContext as INotifyPropertyChanged;
            if (context != null) PropertyChangedEventManager.AddHandler(context, OnProperty, string.Empty);
        }
        private void OnProperty(object sender, PropertyChangedEventArgs e) {
            if (e.PropertyName is "IsAberrationInspector" or "ImageRotation" or "ImageFlip" or "PreviewAutoStretch") Queue(e.PropertyName, true);
            // Frame arrivals must not keep restarting the debounce during streaming.
        }
        private void OnClick(object sender, RoutedEventArgs e) => Queue("button:" + (e.OriginalSource as FrameworkElement)?.Name, true);
        private void OnWheel(object sender, MouseWheelEventArgs e) => Queue("wheel:" + e.Delta, true);
        private void OnZoom(object sender, EventArgs e) => Queue("zoom", true);
        private void OnScroll(object sender, ScrollChangedEventArgs e) {
            if (Math.Abs(e.HorizontalChange) + Math.Abs(e.VerticalChange) > 0) Queue("scroll", false);
        }
        private void OnKey(object sender, KeyEventArgs e) {
            if (e.Key == Key.F12 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) {
                Queue("manual-snapshot", true); lastSnapshot = DateTime.MinValue; Capture(); e.Handled = true;
            }
        }
        private void Queue(string reason, bool snapshot) {
            if (!viewer.Dispatcher.CheckAccess()) { viewer.Dispatcher.BeginInvoke(new Action(() => Queue(reason, snapshot))); return; }
            if (!attached || !IsRelevant) return;
            reasons.Add(reason); wantSnapshot |= snapshot;
            // Keep a timestamped action history even when a burst is coalesced.
            Enqueue(new { Utc = DateTime.UtcNow, Event = "action", Viewer = ViewerName, Reason = reason, Inspector = IsInspector,
                Zoom = (viewer.FindName("PART_ScaleTransform") as ScaleTransform)?.ScaleX }, null);
            if (!debounce.IsEnabled) debounce.Start();
        }
        private string ViewerName => "MF Image";
        private static object SizeOf(FrameworkElement element) => element == null ? null : new {
            element.Name, Width = element.ActualWidth, Height = element.ActualHeight, element.Visibility, element.IsVisible,
            DesiredWidth = element.DesiredSize.Width, DesiredHeight = element.DesiredSize.Height,
            element.Opacity,
            Clip = VisualTreeHelper.GetClip(element)?.Bounds.ToString(), element.ClipToBounds,
            Scaling = RenderOptions.GetBitmapScalingMode(element).ToString()
        };
        private void Capture() {
            if (!attached || !IsRelevant) return;
            try {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var image = viewer.Image;
                if (image == null) { reasons.Clear(); wantSnapshot = false; return; }
                var canvas = viewer.FindName("PART_Canvas") as Canvas;
                var scroll = viewer.FindName("PART_ScrollViewer") as ScrollViewer;
                var zoom = viewer.FindName("PART_ScaleTransform") as ScaleTransform;
                var vm = viewer.DataContext as ManualFocuserDockableVM;
                bool displayedInspector = vm.HasDisplayInspectorTiles;
                var overlay = viewer.ImageAreaContent as FocusRoiOverlay;
                var surface = overlay?.FindName("PreviewSurface") as FrameworkElement;
                var tileControl = overlay?.FindName("InspectorTiles") as ItemsControl;
                var tileImages = tileControl == null ? Array.Empty<Image>() : VisualChildren(tileControl).OfType<Image>().ToArray();
                var pixels = PixelGrid(image, IsInspector);
                bool missingSource = displayedInspector && Enumerable.Range(0,3).All(row => pixels[row*3+2].Max == 0) &&
                    pixels.Where(p => p.Tile%3 != 2).Any(p => p.Max > 0);
                bool badBounds = canvas != null && (Math.Abs(canvas.ActualWidth - image.Width) > 1 || Math.Abs(canvas.ActualHeight - image.Height) > 1);
                bool missingContainers = vm?.HasDisplayInspectorTiles == true &&
                    (tileImages.Length != 9 || tileImages.Any(tile => tile.Visibility != Visibility.Visible || tile.Opacity == 0 || tile.ActualWidth < 1 || tile.ActualHeight < 1));
                bool anomaly = missingSource || badBounds || missingContainers;
                int anomalyKind = (missingSource ? 1 : 0) | (badBounds ? 2 : 0) | (missingContainers ? 4 : 0);
                BitmapSource source = null;
                bool snapshotRequested = wantSnapshot || anomaly;
                if ((wantSnapshot || anomaly) && (DateTime.UtcNow - lastSnapshot > TimeSpan.FromSeconds(3) || anomalyKind != 0 && anomalyKind != lastAnomalyKind)) {
                    // Never capture the viewer or any ancestor/descendant visual.
                    // Freeze a private copy only when the source is still mutable;
                    // the background encoder must not touch the display's state.
                    source = image;
                    if (!source.IsFrozen) { source = source.CloneCurrentValue(); source.Freeze(); }
                    lastSnapshot = DateTime.UtcNow;
                }
                var clips = new List<object>();
                for (DependencyObject node = surface ?? (DependencyObject)canvas; node != null && clips.Count < 12; node = VisualTreeHelper.GetParent(node))
                    if (node is FrameworkElement element) clips.Add(SizeOf(element));
                Enqueue(new {
                    Utc = DateTime.UtcNow, Event = "state", Viewer = ViewerName, ViewerId = RuntimeHelpers.GetHashCode(viewer), Sequence = ++sequence,
                    Reasons = reasons.ToArray(), Module = typeof(FocusViewerDiagnostics).Assembly.ManifestModule.ModuleVersionId,
                    PluginVersion = typeof(FocusViewerDiagnostics).Assembly.GetName().Version.ToString(), Process = Environment.ProcessId,
                    RenderTier = RenderCapability.Tier >> 16, Dpi = VisualTreeHelper.GetDpi(viewer).DpiScaleX,
                    InspectorRequested = vm.IsAberrationInspector, InspectorDisplayed = displayedInspector,
                    ImageId = RuntimeHelpers.GetHashCode(image), PixelWidth = image.PixelWidth, PixelHeight = image.PixelHeight, Format = image.Format.ToString(), image.IsFrozen,
                    ZoomX = zoom?.ScaleX, ZoomY = zoom?.ScaleY, Rotation = viewer.ImageRotation, Flip = viewer.ImageFlip,
                    Viewport = scroll == null ? null : new { scroll.ViewportWidth, scroll.ViewportHeight, scroll.ExtentWidth, scroll.ExtentHeight, scroll.HorizontalOffset, scroll.VerticalOffset },
                    Canvas = SizeOf(canvas), NativeImage = SizeOf(viewer.FindName("PART_Image") as FrameworkElement), Surface = SizeOf(surface), Clips = clips,
                    FocusFrame = vm?.GetFocusViewerTrace(), Pixels = pixels,
                    TileContainers = tileImages.Select(tile => new { Bounds = SizeOf(tile), PixelWidth = (tile.Source as BitmapSource)?.PixelWidth,
                        PixelHeight = (tile.Source as BitmapSource)?.PixelHeight, SourceId = tile.Source == null ? 0 : RuntimeHelpers.GetHashCode(tile.Source),
                        CanvasPoint = canvas == null ? null : tile.TransformToAncestor(canvas).Transform(new Point()).ToString() }).ToArray(),
                    Generator = tileControl?.ItemContainerGenerator.Status.ToString(), MissingSourcePixels = missingSource,
                    BoundsMismatch = badBounds, MissingTileContainers = missingContainers, Anomaly = anomaly,
                    CaptureMilliseconds = timer.Elapsed.TotalMilliseconds,
                    SnapshotKind = source == null ? null : "source-bitmap; not screen capture"
                }, source);
                lastAnomalyKind = anomalyKind;
                reasons.Clear(); wantSnapshot = snapshotRequested && source == null;
            } catch (Exception error) {
                reasons.Clear(); wantSnapshot = false;
                Enqueue(new { Utc = DateTime.UtcNow, Event = "capture-error", Viewer = ViewerName, Error = error.ToString() }, null);
            }
        }
        private sealed record PixelSample(int Tile, int X, int Y, double Mean, int Min, int Max);
        internal static object TilePixelTrace(BitmapSource bitmap) => PixelGrid(bitmap, false)[4];
        private static PixelSample[] PixelGrid(BitmapSource bitmap, bool mosaic) {
            var result = new PixelSample[9];
            for (int row = 0; row < 3; row++) for (int col = 0; col < 3; col++) {
                int x = (int)(bitmap.PixelWidth * (col + .5) / 3), y = (int)(bitmap.PixelHeight * (row + .5) / 3);
                result[row*3+col] = ReadPixelSample(bitmap,row*3+col,x,y);
            }
            return result;
        }
        private static PixelSample ReadPixelSample(BitmapSource bitmap,int tile,int x,int y) {
            int left = Math.Clamp(x-8,0,bitmap.PixelWidth-1),top = Math.Clamp(y-8,0,bitmap.PixelHeight-1);
            int width = Math.Min(17,bitmap.PixelWidth-left),height = Math.Min(17,bitmap.PixelHeight-top);
            BitmapSource sample = new CroppedBitmap(bitmap,new Int32Rect(left,top,width,height));
            if (sample.Format != PixelFormats.Bgra32) sample = new FormatConvertedBitmap(sample,PixelFormats.Bgra32,null,0);
            var bytes = new byte[width*height*4];sample.CopyPixels(bytes,width*4,0);
            long sum=0;int min=255,max=0;
            for(int i=0;i<bytes.Length;i+=4){int value=(bytes[i]+bytes[i+1]+bytes[i+2])/3;sum+=value;min=Math.Min(min,value);max=Math.Max(max,value);}
            return new PixelSample(tile,x,y,sum/(double)(width*height),min,max);
        }
        private static IEnumerable<DependencyObject> VisualChildren(DependencyObject parent) {
            for (int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) {
                var child = VisualTreeHelper.GetChild(parent,i); yield return child;
                foreach (var nested in VisualChildren(child)) yield return nested;
            }
        }
        private static void Enqueue(object state, BitmapSource source) {
            try { writes.Writer.TryWrite(new WriteJob(JsonSerializer.Serialize(state, jsonOptions), source)); }
            catch (Exception error) { Logger.Error("MF viewer trace serialization failed", error); }
        }
        private static async Task WriteLoop() {
            int snapshot = 0;
            bool firstWrite = true;
            await foreach (var job in writes.Reader.ReadAllAsync()) {
                try {
                    Directory.CreateDirectory(DirectoryPath);
                    if (firstWrite) {
                        firstWrite = false;
                        // Retain three trace sessions. Only our exact session-name
                        // pattern inside our resolved diagnostic root is eligible.
                        string boundary = Path.GetFullPath(RootPath) + Path.DirectorySeparatorChar;
                        foreach (var old in new DirectoryInfo(RootPath).GetDirectories("session-*").OrderByDescending(d => d.CreationTimeUtc).Skip(3)) {
                            string full = Path.GetFullPath(old.FullName);
                            if (full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) && full != Path.GetFullPath(DirectoryPath) &&
                                System.Text.RegularExpressions.Regex.IsMatch(old.Name, @"^session-\d{8}-\d{6}-\d+$")) Directory.Delete(full, true);
                        }
                    }
                    string path = Path.Combine(DirectoryPath, "viewer.jsonl");
                    if (File.Exists(path) && new FileInfo(path).Length > 8*1024*1024) {
                        string previous = Path.Combine(DirectoryPath,"viewer.1.jsonl");
                        if (File.Exists(previous)) File.Move(previous,Path.Combine(DirectoryPath,"viewer.2.jsonl"),true);
                        File.Move(path,Path.Combine(DirectoryPath,"viewer.1.jsonl"),true);
                    }
                    string prefix = null;
                    if (job.Source != null) {
                        prefix = "snapshot-" + (++snapshot % 12).ToString("D2");
                        SavePng(job.Source,Path.Combine(DirectoryPath,prefix+"-bitmap.png"));
                        File.WriteAllText(Path.Combine(DirectoryPath,prefix+".json"),job.Json);
                    }
                    await File.AppendAllTextAsync(path,job.Json+Environment.NewLine);
                    if (prefix != null) await File.AppendAllTextAsync(path,JsonSerializer.Serialize(new { Utc=DateTime.UtcNow, Event="snapshot-files", Prefix=prefix })+Environment.NewLine);
                } catch (Exception error) { Logger.Error("MF viewer trace file write failed", error); }
            }
        }
        private static void SavePng(BitmapSource image, string path) {
            if (image == null) { if (File.Exists(path)) File.Delete(path); return; }
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(path); encoder.Save(stream);
        }
    }

    public partial class ManualFocuserDockableVM {
        internal object GetFocusViewerTrace() {
            var entry = LiveDisplayImage != null && previewPixels.TryGetValue(LiveDisplayImage, out var found) ? found : null;
            var raw = entry?.Raw;
            return new { FocusMode, IsFocusAssistRunning, PreviewRoiWidth, PreviewRoiHeight, PreviewCenterX, PreviewCenterY,
                PreviewAutoStretch, PreviewStretchStrength, IsAberrationInspector, CachedInspector = entry?.Inspector,
                Raw = raw == null ? null : new { raw.Width, raw.Height, raw.Stride, raw.Left, raw.Top, Count=raw.Pixels.Length,
                    SensorWidth=raw.SourceImage?.Properties.Width, SensorHeight=raw.SourceImage?.Properties.Height,
                    Samples=Enumerable.Range(0,9).Select(i=>raw.Sample(Math.Min(raw.Width*raw.Height-1,(int)(raw.Height*(i/3+.5)/3)*raw.Width+(int)(raw.Width*(i%3+.5)/3)))).ToArray() },
                TileSources = LiveDisplayInspectorTiles?.Select((tile,i)=>new { Tile=i,tile.X,tile.Y, tile.Bitmap.PixelWidth,tile.Bitmap.PixelHeight,
                    Format=tile.Bitmap.Format.ToString(),tile.Bitmap.IsFrozen, SourceId=RuntimeHelpers.GetHashCode(tile.Bitmap),
                    Pixels=FocusViewerDiagnostics.TilePixelTrace(tile.Bitmap) }).ToArray()
            };
        }
    }
}
