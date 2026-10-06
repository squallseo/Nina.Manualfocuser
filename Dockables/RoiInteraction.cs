using System;
using System.Windows;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    [Flags]
    public enum RoiHandle { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8, Move = 16, Draw = 32 }

    public static class RoiInteraction {
        public static RoiHandle HitTest(Rect rectangle, Point point) {
            var hit = rectangle; hit.Inflate(6, 6);
            if (!hit.Contains(point)) return RoiHandle.Draw;
            double tx = Math.Min(6, rectangle.Width / 3), ty = Math.Min(6, rectangle.Height / 3);
            RoiHandle handle = RoiHandle.None;
            if (Math.Abs(point.X - rectangle.Left) <= tx) handle |= RoiHandle.Left;
            else if (Math.Abs(point.X - rectangle.Right) <= tx) handle |= RoiHandle.Right;
            if (Math.Abs(point.Y - rectangle.Top) <= ty) handle |= RoiHandle.Top;
            else if (Math.Abs(point.Y - rectangle.Bottom) <= ty) handle |= RoiHandle.Bottom;
            return handle != RoiHandle.None ? handle : rectangle.Contains(point) ? RoiHandle.Move : RoiHandle.Draw;
        }

        // Coordinates are sensor pixels; resizing keeps the opposite edge anchored.
        public static Rect Drag(Rect original, Point start, Point current, RoiHandle handle, int width, int height) {
            double minW = Math.Min(32, width), minH = Math.Min(32, height);
            double maxW = width, maxH = height;
            double dx = current.X - start.X, dy = current.Y - start.Y;
            if (handle == RoiHandle.Move)
                return new Rect(Math.Clamp(original.X + dx, 0, width - original.Width),
                    Math.Clamp(original.Y + dy, 0, height - original.Height), original.Width, original.Height);
            double l = original.Left, r = original.Right, t = original.Top, b = original.Bottom;
            if (handle == RoiHandle.Draw) {
                l = Math.Clamp(Math.Min(start.X, current.X), 0, width - minW);
                t = Math.Clamp(Math.Min(start.Y, current.Y), 0, height - minH);
                r = l + Math.Clamp(Math.Abs(dx), minW, Math.Min(maxW, width - l));
                b = t + Math.Clamp(Math.Abs(dy), minH, Math.Min(maxH, height - t));
            } else {
                if (handle.HasFlag(RoiHandle.Left)) l = Math.Clamp(l + dx, Math.Max(0, r - maxW), r - minW);
                if (handle.HasFlag(RoiHandle.Right)) r = Math.Clamp(r + dx, l + minW, Math.Min(width, l + maxW));
                if (handle.HasFlag(RoiHandle.Top)) t = Math.Clamp(t + dy, Math.Max(0, b - maxH), b - minH);
                if (handle.HasFlag(RoiHandle.Bottom)) b = Math.Clamp(b + dy, t + minH, Math.Min(height, t + maxH));
            }
            return new Rect(l, t, r - l, b - t);
        }
    }
}
