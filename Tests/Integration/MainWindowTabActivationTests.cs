using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FluentAssertions;
using ReactiveUI;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// Pins the startup-ordering behaviour behind the main window's tab RadioButtons.
///
/// The app's startup sequence is: <c>mainWindow.Show()</c>, then (still on the same
/// dispatcher callstack) <c>VM_MainWindow.InitializeApplicationState(isStartup: true)</c>
/// sets the startup tab's <c>Is*TabSelected</c>. The window's <c>WhenActivated</c> block —
/// which contains the <c>this.Bind(vm.Is*TabSelected, v.*RadioButton.IsChecked)</c> calls —
/// only runs when WPF raises <c>Loaded</c>, a QUEUED dispatcher operation that fires after
/// the startup code yields. So the two-way binds always attach AFTER the VM has already
/// chosen the startup tab, and whether the chosen tab's radio ends up checked (= underlined)
/// depends entirely on the binds' attach-time initial synchronisation. This test reproduces
/// that ordering against a minimal ReactiveWindow and asserts the VM's choice survives and
/// reaches the radio.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class MainWindowTabActivationTests
{
    private readonly WpfStaFixture _sta;
    private readonly ITestOutputHelper _output;

    public MainWindowTabActivationTests(WpfStaFixture sta, ITestOutputHelper output)
    {
        _sta = sta;
        _output = output;
    }

    private sealed class TabVm : ReactiveObject
    {
        private bool _isA;
        public bool IsA { get => _isA; set => this.RaiseAndSetIfChanged(ref _isA, value); }

        private bool _isB;
        public bool IsB { get => _isB; set => this.RaiseAndSetIfChanged(ref _isB, value); }

        private bool _isC;
        public bool IsC { get => _isC; set => this.RaiseAndSetIfChanged(ref _isC, value); }
    }

    private sealed class TabWindow : ReactiveWindow<TabVm>
    {
        public RadioButton A { get; } = new() { GroupName = "TestTabs", Content = "A" };
        public RadioButton B { get; } = new() { GroupName = "TestTabs", Content = "B" };
        public RadioButton C { get; } = new() { GroupName = "TestTabs", Content = "C" };

        public bool Activated;
        public Exception? ActivationError;

        public TabWindow(TabVm vm)
        {
            ViewModel = vm;
            var panel = new StackPanel();
            panel.Children.Add(A);
            panel.Children.Add(B);
            panel.Children.Add(C);
            Content = panel;

            // Keep the test window out of the way; Loaded still fires.
            ShowInTaskbar = false;
            ShowActivated = false;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;
            Width = 120;
            Height = 60;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -32000;
            Top = -32000;

            this.WhenActivated(d =>
            {
                Activated = true;
                try
                {
                    this.Bind(ViewModel, vm2 => vm2.IsA, v => v.A.IsChecked).DisposeWith(d);
                    this.Bind(ViewModel, vm2 => vm2.IsB, v => v.B.IsChecked).DisposeWith(d);
                    this.Bind(ViewModel, vm2 => vm2.IsC, v => v.C.IsChecked).DisposeWith(d);
                }
                catch (Exception ex)
                {
                    ActivationError = ex;
                }
            });
        }
    }

    /// <summary>Pumps the STA dispatcher until everything queued at Loaded priority and above has run.</summary>
    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public async Task VmTabChosenBeforeLoaded_ReachesRadio_AndSurvives()
    {
        await _sta.RunOnStaAsync(() =>
        {
            var previousScheduler = RxSchedulers.MainThreadScheduler;
            RxSchedulers.MainThreadScheduler =
                new System.Reactive.Concurrency.DispatcherScheduler(Dispatcher.CurrentDispatcher);
            try
            {
                var vm = new TabVm();
                var window = new TabWindow(vm);
                try
                {
                    window.Show();      // Loaded is queued, not yet raised — mirrors App.OnStartup
                    vm.IsA = true;      // mirrors InitializeApplicationState(isStartup: true)
                    DoEvents();         // dispatcher pumps: Loaded -> WhenActivated -> binds attach

                    _output.WriteLine(
                        $"Activated={window.Activated} ActivationError={window.ActivationError?.Message ?? "none"} " +
                        $"A.IsChecked={window.A.IsChecked} vm.IsA={vm.IsA} vm.IsB={vm.IsB} vm.IsC={vm.IsC}");

                    window.Activated.Should().BeTrue("WhenActivated must run when the window loads");
                    window.ActivationError.Should().BeNull();
                    vm.IsA.Should().BeTrue("attaching the binds must not stomp the VM's startup tab choice");
                    window.A.IsChecked.Should().BeTrue("the startup tab chosen before Loaded must end up checked");
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                RxSchedulers.MainThreadScheduler = previousScheduler;
            }
        });
    }

    [Fact]
    public async Task VmTabChosenAfterLoaded_ReachesRadio()
    {
        await _sta.RunOnStaAsync(() =>
        {
            var previousScheduler = RxSchedulers.MainThreadScheduler;
            RxSchedulers.MainThreadScheduler =
                new System.Reactive.Concurrency.DispatcherScheduler(Dispatcher.CurrentDispatcher);
            try
            {
                var vm = new TabVm();
                var window = new TabWindow(vm);
                try
                {
                    window.Show();
                    DoEvents();         // binds attach first
                    vm.IsA = true;      // the "user clicked later" era
                    DoEvents();

                    _output.WriteLine(
                        $"Activated={window.Activated} ActivationError={window.ActivationError?.Message ?? "none"} " +
                        $"A.IsChecked={window.A.IsChecked} vm.IsA={vm.IsA}");

                    window.A.IsChecked.Should().BeTrue("a VM tab change after the binds attach must reach the radio");
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                RxSchedulers.MainThreadScheduler = previousScheduler;
            }
        });
    }
}
