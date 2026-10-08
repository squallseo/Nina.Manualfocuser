using System;
using System.Windows;
using System.Windows.Input;
using OxyPlot.Wpf;
using Cwseo.NINA.ManualFocuser.Models;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class ManualFocuserDockableView {
        private Plot graphDragPlot;
        private ManualFocuserDockableVM graphDragVm;
        private bool IsPositionGraph(Plot plot) => plot==ManualFocusPlot ||
            (plot==PreviewFocusPlot && DataContext is ManualFocuserDockableVM vm && vm.AutoFocusPreviewVisible);
        private bool TryGraphPosition(Plot plot,Point screen,bool requireAxis,out double position) {
            position=double.NaN;
            if(!OperatingSystem.IsWindowsVersionAtLeast(7)) return false;
            if(!IsPositionGraph(plot) || plot.ActualModel is not { } model) return false;
            return FocusGraphHitTest.TryPosition(model,new OxyPlot.ScreenPoint(screen.X,screen.Y),plot.ActualHeight,requireAxis,out position);
        }
        private void CancelGraphDrag() {
            var plot=graphDragPlot;graphDragPlot=null;
            graphDragVm?.CancelGraphSelection();graphDragVm=null;
            if(plot!=null) {plot.ReleaseMouseCapture();plot.Cursor=Cursors.Arrow;}
        }
        private void OnFocusGraphDown(object sender,MouseButtonEventArgs e) {
            if(sender is not Plot plot || DataContext is not ManualFocuserDockableVM vm ||
                !TryGraphPosition(plot,e.GetPosition(plot),true,out double position) || !vm.SelectGraphPosition(position)) return;
            graphDragPlot=plot;graphDragVm=vm;
            if(!plot.CaptureMouse()) {CancelGraphDrag();return;}
            plot.Focus();plot.Cursor=Cursors.SizeWE;e.Handled=true;
        }
        private void OnFocusGraphMove(object sender,MouseEventArgs e) {
            if(sender is not Plot plot || DataContext is not ManualFocuserDockableVM vm) return;
            if(graphDragPlot==plot) {
                if(graphDragVm!=vm || e.LeftButton!=MouseButtonState.Pressed || !vm.CanMoveFromGraph) {CancelGraphDrag();return;}
                if(TryGraphPosition(plot,e.GetPosition(plot),false,out double target)) vm.SelectGraphPosition(target);
                e.Handled=true;
            } else plot.Cursor=vm.CanMoveFromGraph && TryGraphPosition(plot,e.GetPosition(plot),true,out _) ? Cursors.SizeWE : Cursors.Arrow;
        }
        private async void OnFocusGraphUp(object sender,MouseButtonEventArgs e) {
            if(sender is not Plot plot || graphDragPlot!=plot) return;
            var vm=graphDragVm;
            bool commit=DataContext==vm && vm.CanMoveFromGraph && TryGraphPosition(plot,e.GetPosition(plot),false,out double position)
                && vm.SelectGraphPosition(position);
            int target=vm.GraphSelectionPosition;
            CancelGraphDrag();e.Handled=true;
            if(commit) await vm.MoveFocusFromGraphAsync(target);
        }
        private void OnFocusGraphCaptureLost(object sender,MouseEventArgs e) {
            if(sender==graphDragPlot) CancelGraphDrag();
        }
        private void OnFocusGraphKeyDown(object sender,KeyEventArgs e) {
            if(e.Key==Key.Escape && graphDragPlot!=null) {CancelGraphDrag();e.Handled=true;}
        }
    }
}
