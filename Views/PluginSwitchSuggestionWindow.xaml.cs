using System.Windows;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.Views;

/// <summary>
/// Post-scan dialog offering to re-pin NPCs whose current source plugin dark-faces
/// while a sibling plugin of the same mod grades clean. Pure view: the caller
/// (VM_ModIssues.OfferPluginSwitches) applies the checked proposals when
/// ShowDialog returns true.
/// </summary>
public partial class PluginSwitchSuggestionWindow : Window
{
    public PluginSwitchSuggestionWindow()
    {
        InitializeComponent();
    }

    private VM_PluginSwitchSuggestions? Vm => DataContext as VM_PluginSwitchSuggestions;

    private void CheckAll_Click(object sender, RoutedEventArgs e) => Vm?.SetAll(true);

    private void UncheckAll_Click(object sender, RoutedEventArgs e) => Vm?.SetAll(false);

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
