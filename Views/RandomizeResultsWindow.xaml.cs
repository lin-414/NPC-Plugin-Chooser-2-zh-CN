using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.Views;

/// <summary>
/// The randomize run's completion dialog when issues were encountered: summary and notes on
/// top, then a sortable/filterable table of the NPCs the run could not place or had to leave
/// out. Informational only — the run has already happened, so the single button is Close.
/// A clean run never opens this; it keeps the plain completion message box.
/// </summary>
public partial class RandomizeResultsWindow : Window
{
    public RandomizeResultsWindow()
    {
        InitializeComponent();
    }

    /// <summary>Shows the dialog modally over the active window.</summary>
    public static void ShowResults(string summaryText, IReadOnlyList<string> notes,
        IReadOnlyList<RandomizeIssueRow> rows)
    {
        var vm = new VM_RandomizeResultsWindow(summaryText, notes, rows);
        var window = new RandomizeResultsWindow { DataContext = vm };
        window.AppendCandidateFailureColumns(vm.MaxCandidateFailures);
        TrySetOwner(window);
        window.ShowDialog();
        vm.Dispose();
    }

    /// <summary>
    /// Appends one fixed-width column per tried-and-rejected candidate ("Candidate 1..N", cells
    /// are "Mod: reason"). Columns are positional because the mods differ per NPC; the VM pads
    /// every row's failure list to N, so the index bindings always resolve. Fixed widths are what
    /// push the grid past its viewport and make it scroll horizontally instead of crushing the
    /// star-sized base columns.
    /// </summary>
    private void AppendCandidateFailureColumns(int count)
    {
        if (count <= 0) return;

        // Wrap long reasons; derive from the theme's implicit TextBlock style when present so the
        // appended columns keep matching the XAML-declared ones.
        var wrap = TryFindResource(typeof(TextBlock)) is Style baseStyle
            ? new Style(typeof(TextBlock), baseStyle)
            : new Style(typeof(TextBlock));
        wrap.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));

        for (int i = 0; i < count; i++)
        {
            ResultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = $"Candidate {i + 1}",
                Binding = new Binding($"CandidateFailures[{i}]"),
                Width = new DataGridLength(300),
                ElementStyle = wrap,
            });
        }
    }

    private static void TrySetOwner(Window window)
    {
        try
        {
            var owner = Application.Current?.MainWindow
                        ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
            if (owner != null && owner != window && owner.IsLoaded)
            {
                window.Owner = owner;
            }
        }
        catch (System.Exception ex)
        {
            Debug.WriteLine($"Could not set window owner: {ex.Message}");
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
