using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using Splat;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace NPC_Plugin_Chooser_2.Views
{
    /// <summary>
    /// Code-behind for the Mod Issues tab. The zoom / packing / splitter wiring
    /// mirrors <see cref="ModsView"/> (same ImagePacker flow) against
    /// <see cref="VM_ModIssues"/>'s session-local zoom state.
    /// </summary>
    public partial class ModIssuesView : ReactiveUserControl<VM_ModIssues>
    {
        private readonly CompositeDisposable _viewBindings = new();
        private readonly Subject<SizeChangedEventArgs> _sizeChangedSubject = new();
        private const double MinZoomPercentage = 1.0;
        private const double MaxZoomPercentage = 1000.0;
        private const double ZoomStepPercentage = 2.5;
        private bool _isInitialLayout = true;
        private bool _userHasAdjustedSplitter = false;

        public ModIssuesView()
        {
            InitializeComponent();

            if (this.DataContext == null)
            {
                try
                {
                    ViewModel = Locator.Current.GetService<VM_ModIssues>();
                    DataContext = this.ViewModel;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error resolving VM_ModIssues: {ex.Message}");
                }
            }

            this.WhenActivated(d =>
            {
                _viewBindings.Clear();
                d.DisposeWith(_viewBindings);
                if (ViewModel == null) return;

                // Zoom TextBox: VM -> View (formatted) and View -> VM (throttled).
                this.WhenAnyValue(x => x.ViewModel.ZoomLevel)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Select(val => val.ToString("F2", CultureInfo.InvariantCulture))
                    .BindTo(this, v => v.ZoomPercentageTextBox.Text)
                    .DisposeWith(d);

                Observable.FromEventPattern<TextChangedEventArgs>(ZoomPercentageTextBox,
                        nameof(ZoomPercentageTextBox.TextChanged))
                    .Select(ep => ((TextBox)ep.Sender).Text)
                    .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(text =>
                    {
                        if (ViewModel == null) return;
                        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
                        {
                            ViewModel.HasUserManuallyZoomed = true;
                            double clamped = Math.Max(MinZoomPercentage, Math.Min(MaxZoomPercentage, result));
                            if (Math.Abs(ViewModel.ZoomLevel - clamped) > 0.001)
                            {
                                ViewModel.ZoomLevel = clamped;
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(text))
                        {
                            ZoomPercentageTextBox.Text =
                                ViewModel.ZoomLevel.ToString("F2", CultureInfo.InvariantCulture);
                        }
                    })
                    .DisposeWith(d);

                _sizeChangedSubject
                    .Throttle(TimeSpan.FromMilliseconds(250), RxApp.MainThreadScheduler)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        if (ViewModel != null && !ViewModel.IsZoomLocked)
                        {
                            ViewModel.HasUserManuallyZoomed = false;
                        }
                        RefreshMugshotImageSizes();
                    })
                    .DisposeWith(d);

                Observable.FromEventPattern<DragCompletedEventArgs>(ColumnSplitter, nameof(GridSplitter.DragCompleted))
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        _userHasAdjustedSplitter = true;
                        if (ViewModel != null)
                        {
                            ViewModel.LeftPanelWidth = LeftColumnForModList.ActualWidth;
                        }
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (MainContentGridForSplitter.IsLoaded)
                            {
                                MainContentGridForSplitter.InvalidateMeasure();
                                MainContentGridForSplitter.InvalidateArrange();
                                MainContentGridForSplitter.UpdateLayout();
                            }
                            Dispatcher.BeginInvoke(new Action(RefreshMugshotImageSizes), DispatcherPriority.Background);
                        }), DispatcherPriority.ContextIdle);
                    })
                    .DisposeWith(d);

                this.BindCommand(ViewModel, vm => vm.ZoomInCommand, v => v.ZoomInButton).DisposeWith(d);
                this.BindCommand(ViewModel, vm => vm.ZoomOutCommand, v => v.ZoomOutButton).DisposeWith(d);
                this.Bind(ViewModel, vm => vm.IsZoomLocked, v => v.LockZoomCheckBox.IsChecked).DisposeWith(d);
                this.BindCommand(ViewModel, vm => vm.ResetZoomCommand, v => v.ResetZoomButton).DisposeWith(d);

                ViewModel.RefreshMugshotSizesObservable
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => RefreshMugshotImageSizes())
                    .DisposeWith(d);

                if (ViewModel.CurrentModNpcMugshots != null && ViewModel.CurrentModNpcMugshots.Any())
                {
                    RefreshMugshotImageSizes();
                }
            });
        }

        private void ModIssuesView_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isInitialLayout && !_userHasAdjustedSplitter)
            {
                AdjustLeftColumnWidth();
                _isInitialLayout = false;
            }
        }

        private void AdjustLeftColumnWidth()
        {
            if (MainContentGridForSplitter.ActualWidth > 0 && LeftColumnForModList != null)
            {
                ApplyLeftColumnWidth();
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_isInitialLayout && !_userHasAdjustedSplitter &&
                        MainContentGridForSplitter.ActualWidth > 0 && LeftColumnForModList != null)
                    {
                        ApplyLeftColumnWidth();
                        _isInitialLayout = false;
                    }
                }), DispatcherPriority.Loaded);
            }
        }

        private void ApplyLeftColumnWidth()
        {
            double availableWidth = MainContentGridForSplitter.ActualWidth;
            double savedWidth = ViewModel?.LeftPanelWidth ?? 0;
            double targetWidth = savedWidth > 0 ? savedWidth : availableWidth * 0.3;

            double maxWidth = availableWidth - RightPanelColumn.MinWidth - ColumnSplitter.ActualWidth;
            if (maxWidth > LeftColumnForModList.MinWidth && targetWidth > maxWidth) targetWidth = maxWidth;
            if (targetWidth < LeftColumnForModList.MinWidth) targetWidth = LeftColumnForModList.MinWidth;
            if (targetWidth > LeftColumnForModList.MaxWidth) targetWidth = LeftColumnForModList.MaxWidth;

            LeftColumnForModList.Width = new GridLength(targetWidth, GridUnitType.Pixel);
        }

        private void MugshotScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            _sizeChangedSubject.OnNext(e);
        }

        private void MugshotScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ViewModel?.CurrentModNpcMugshots == null || !ViewModel.CurrentModNpcMugshots.Any()) return;
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                double change = (e.Delta > 0 ? 1 : -1) * ZoomStepPercentage;
                ViewModel.HasUserManuallyZoomed = true;
                ViewModel.ZoomLevel = Math.Max(MinZoomPercentage,
                    Math.Min(MaxZoomPercentage, ViewModel.ZoomLevel + change));
                e.Handled = true;
            }
        }

        /// <summary>
        /// Wheel routing for the Details cell's internal ScrollViewer: let the cell
        /// scroll while it has somewhere to go in the wheel's direction, otherwise
        /// mark the event handled and re-raise it from the cell so it bubbles to the
        /// results DataGrid — a bare ScrollViewer would swallow the wheel even when
        /// it cannot scroll, freezing table scrolling whenever the cursor rests on a
        /// Details cell.
        /// </summary>
        private void DetailsCell_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer viewer) return;

            bool cellCanScroll = e.Delta < 0
                ? viewer.VerticalOffset < viewer.ScrollableHeight
                : viewer.VerticalOffset > 0;
            if (cellCanScroll && viewer.ScrollableHeight > 0) return;

            e.Handled = true;
            viewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = viewer,
            });
        }

        private void ZoomPercentageTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ViewModel == null || sender is not TextBox textBox) return;
            double change = (e.Delta > 0 ? 1 : -1) * ZoomStepPercentage;
            ViewModel.HasUserManuallyZoomed = true;
            ViewModel.ZoomLevel = Math.Max(MinZoomPercentage,
                Math.Min(MaxZoomPercentage, ViewModel.ZoomLevel + change));
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            textBox.CaretIndex = textBox.Text.Length;
            textBox.SelectAll();
            e.Handled = true;
        }

        /// <summary>Plain left-click on a tile toggles the issue table's
        /// per-NPC filter. Modifier-clicks keep their existing meanings
        /// (Ctrl+RClick fullscreen etc. live on the right button).</summary>
        private void MugshotItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            if (sender is not FrameworkElement element || element.DataContext is not VM_ModsMenuMugshot vm)
                return;
            ViewModel?.ToggleTableNpcFilter(vm.NpcFormKey);
            e.Handled = true;
        }

        private void MugshotItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement element || element.DataContext is not VM_ModsMenuMugshot vm)
                return;

            if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                if (vm.Show3DPreviewCommand.CanExecute.FirstAsync().Wait())
                {
                    vm.Show3DPreviewCommand.Execute(Unit.Default).Subscribe().DisposeWith(_viewBindings);
                }
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (vm.ToggleFullScreenCommand.CanExecute.FirstAsync().Wait())
                {
                    vm.ToggleFullScreenCommand.Execute(Unit.Default).Subscribe().DisposeWith(_viewBindings);
                }
                e.Handled = true;
            }
        }

        private void RefreshMugshotImageSizes()
        {
            if (ViewModel?.CurrentModNpcMugshots == null) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ViewModel?.CurrentModNpcMugshots == null || !MugshotScrollViewer.IsLoaded) return;

                MugshotScrollViewer.UpdateLayout();
                MugshotsItemsControl.UpdateLayout();

                double availableWidth = MugshotScrollViewer.ActualWidth;
                double availableHeight = MugshotScrollViewer.ActualHeight;
                if (MugshotScrollViewer.ComputedVerticalScrollBarVisibility == Visibility.Visible)
                {
                    availableHeight = MugshotScrollViewer.ViewportHeight;
                    availableWidth -= SystemParameters.VerticalScrollBarWidth;
                }
                availableWidth = Math.Max(0, availableWidth);

                var imagesForPacker = ViewModel.CurrentModNpcMugshots;

                if (ViewModel.IsZoomLocked || ViewModel.HasUserManuallyZoomed)
                {
                    var visibleImages = imagesForPacker
                        .Where(img => img.IsVisible && img.OriginalDipDiagonal > 0)
                        .ToList();
                    if (!visibleImages.Any())
                    {
                        foreach (var img in imagesForPacker) { img.ImageWidth = 0; img.ImageHeight = 0; }
                        return;
                    }

                    double averageDiagonal = visibleImages.Sum(img => img.OriginalDipDiagonal) / visibleImages.Count;
                    double userZoomFactor = ViewModel.ZoomLevel / 100.0;

                    foreach (var img in imagesForPacker)
                    {
                        if (img.IsVisible && img.OriginalDipDiagonal > 0)
                        {
                            double scale = (averageDiagonal / img.OriginalDipDiagonal) * userZoomFactor;
                            img.ImageWidth = img.OriginalDipWidth * scale;
                            img.ImageHeight = img.OriginalDipHeight * scale;
                        }
                        else
                        {
                            img.ImageWidth = 0;
                            img.ImageHeight = 0;
                        }
                    }
                }
                else
                {
                    try
                    {
                        if (availableHeight > 0 && availableWidth > 0)
                        {
                            var imagePacker = Locator.Current.GetService<ImagePacker>();
                            if (imagePacker == null) return;

                            var cancellationToken = ViewModel.GetCurrentMugshotLoadToken();
                            var tempCollection =
                                new ObservableCollection<IHasMugshotImage>(imagesForPacker.Cast<IHasMugshotImage>());

                            double packerScaleFactor = imagePacker.FitOriginalImagesToContainer(
                                tempCollection,
                                availableHeight,
                                availableWidth,
                                5,
                                ViewModel.NormalizeImageDimensions,
                                ViewModel.MaxMugshotsToFit,
                                cancellationToken);

                            ViewModel.ZoomLevel = packerScaleFactor * 100.0;
                        }
                        else
                        {
                            foreach (var img in imagesForPacker)
                            {
                                img.ImageWidth = 0;
                                img.ImageHeight = 0;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // superseded load
                    }
                }
            }), DispatcherPriority.Background);
        }
    }
}
