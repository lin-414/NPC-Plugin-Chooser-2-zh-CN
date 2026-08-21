using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Autofac;
using FluentAssertions;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.Memory;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using NPC_Plugin_Chooser_2.Views;
using Splat;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// Reproduces the app's real startup sequence against the REAL <see cref="MainWindow"/> XAML
/// and the REAL <see cref="VM_MainWindow"/> graph: <c>Show()</c>, then
/// <c>InitializeApplicationState(isStartup: true)</c> on the same callstack (mirroring
/// <c>App.OnStartup</c>), then a dispatcher pump so Loaded / WhenActivated / bindings run.
/// The startup-selected tab's RadioButton must end up checked with its underline visible —
/// this pins the "no tab is underlined on startup" regression.
///
/// Uses the Invalid environment, under which startup forces the Settings tab — the assertion
/// targets SettingsRadioButton, so no Skyrim install is needed.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class MainWindowStartupUnderlineTests
{
    private readonly WpfStaFixture _sta;
    private readonly ITestOutputHelper _output;

    public MainWindowStartupUnderlineTests(WpfStaFixture sta, ITestOutputHelper output)
    {
        _sta = sta;
        _output = output;
    }

    private static Settings BuildSettings() => new() { SkyrimRelease = SkyrimRelease.SkyrimSE };

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
    public async Task StartupSelectedTab_IsCheckedAndUnderlined()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var guard = new StaticStateGuard(immediateSchedulers: false);
            FrontendVmHarness.InstallStaMainThreadScheduler();

            var settings = BuildSettings(); // Settings.TabStyle defaults to "Underline"
            using var harness = new FrontendVmHarness(NpcChooserTestEnvironment.Invalid(), settings);

            var vmMain = harness.Container.Resolve<VM_MainWindow>();

            // The real MainWindow ctor resolves its VM + Settings through Splat.
            Locator.CurrentMutable.Register(() => vmMain, typeof(VM_MainWindow));
            Locator.CurrentMutable.Register(() => settings, typeof(Settings));
            try
            {
                var window = new MainWindow
                {
                    // Keep the test window off-screen and unfocused; Loaded still fires.
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                };
                try
                {
                    window.Show();                                    // App.OnStartup line order:
                    vmMain.InitializeApplicationState(isStartup: true); // Show() first, then init.
                    DoEvents();

                    string[] names =
                    {
                        "NpcsRadioButton", "ModsRadioButton", "ModIssuesRadioButton",
                        "SummaryRadioButton", "SettingsRadioButton", "RunRadioButton",
                        "ValidateRadioButton",
                    };
                    foreach (var name in names)
                    {
                        var rb = (RadioButton)window.FindName(name)!;
                        var underline = rb.Template?.FindName("Underline", rb) as FrameworkElement;
                        _output.WriteLine(
                            $"{name}: IsChecked={rb.IsChecked} IsEnabled={rb.IsEnabled} " +
                            $"underlineFound={underline != null} underlineOpacity={underline?.Opacity.ToString() ?? "n/a"}");
                    }
                    _output.WriteLine(
                        $"VM: Npcs={vmMain.IsNpcsTabSelected} Mods={vmMain.IsModsTabSelected} " +
                        $"ModIssues={vmMain.IsModIssuesTabSelected} Summary={vmMain.IsSummaryTabSelected} " +
                        $"Settings={vmMain.IsSettingsTabSelected} Run={vmMain.IsRunTabSelected} " +
                        $"Validate={vmMain.IsValidateTabSelected} AreOtherTabsEnabled={vmMain.AreOtherTabsEnabled} " +
                        $"TabStyle={vmMain.TabStyle}");

                    // Invalid environment => startup forces the Settings tab.
                    vmMain.IsSettingsTabSelected.Should().BeTrue("startup with an invalid environment lands on Settings");

                    var settingsRb = (RadioButton)window.FindName("SettingsRadioButton")!;
                    settingsRb.IsChecked.Should().BeTrue("the startup-selected tab's radio must be checked");

                    var settingsUnderline = settingsRb.Template?.FindName("Underline", settingsRb) as FrameworkElement;
                    settingsUnderline.Should().NotBeNull("TabStyle=Underline must have applied the underline template");
                    settingsUnderline!.Opacity.Should().Be(1.0, "the checked tab's underline indicator must be visible");
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Locator.CurrentMutable.UnregisterCurrent(typeof(VM_MainWindow));
                Locator.CurrentMutable.UnregisterCurrent(typeof(Settings));
            }
        });
    }
}
