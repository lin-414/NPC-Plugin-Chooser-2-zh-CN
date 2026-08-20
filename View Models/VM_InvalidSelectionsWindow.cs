using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Text;
using System.Windows;
using NPC_Plugin_Chooser_2.BackEnd;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// Backs the pre-run "Invalid Selections Found" dialog: the rejections the screening pass
/// produced, one sortable/filterable table row per rejected NPC (Issue, Mod, NPC, FormKey,
/// Missing Record — the same columns the spreadsheet export always had), plus text/spreadsheet
/// export of the full list. Replaced the issue → mod → NPC tree at the user's direction
/// (2026-08-19): at load-order scale the tree was too busy to parse, where a grid sorts and
/// filters. The window itself owns the go/no-go answer (DialogResult); this type is purely the
/// presentation of what would be skipped.
/// </summary>
public sealed class VM_InvalidSelectionsWindow : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private readonly IReadOnlyList<Validator.InvalidSelection> _entries;

    /// <summary>The rows the filter currently admits, in screening order (the DataGrid's
    /// user-initiated column sorts act on top). Bound straight to
    /// <see cref="Validator.InvalidSelection"/> — the record already carries every column.</summary>
    public ObservableCollection<Validator.InvalidSelection> FilteredRows { get; } = new();

    [Reactive] public string FilterText { get; set; } = string.Empty;

    public string SummaryText { get; }

    public ReactiveCommand<Unit, Unit> CopyTextCommand { get; }
    public ReactiveCommand<Unit, Unit> CopySheetCommand { get; }

    private static readonly string[] SheetHeaders = { "Issue", "Mod", "NPC", "FormKey", "Missing Record" };

    public VM_InvalidSelectionsWindow(IReadOnlyList<Validator.InvalidSelection> entries)
    {
        _entries = entries ?? Array.Empty<Validator.InvalidSelection>();

        int npcCount = _entries.Count;
        int modCount = _entries.Select(e => e.ModName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int issueCount = _entries.Select(e => e.Reason).Distinct(StringComparer.Ordinal).Count();
        SummaryText =
            $"Found {npcCount} invalid NPC selection{(npcCount == 1 ? string.Empty : "s")} " +
            $"across {modCount} mod{(modCount == 1 ? string.Empty : "s")} and " +
            $"{issueCount} issue{(issueCount == 1 ? string.Empty : "s")}. " +
            "These NPCs will be skipped.";

        ApplyFilter();

        this.WhenAnyValue(x => x.FilterText)
            .Throttle(TimeSpan.FromMilliseconds(150), RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyFilter())
            .DisposeWith(_disposables);

        CopyTextCommand = ReactiveCommand.Create(CopyText).DisposeWith(_disposables);
        CopySheetCommand = ReactiveCommand.Create(CopySheet).DisposeWith(_disposables);
    }

    private void ApplyFilter()
    {
        FilteredRows.Clear();
        IEnumerable<Validator.InvalidSelection> query = _entries;
        var s = FilterText?.Trim();
        if (!string.IsNullOrEmpty(s))
        {
            query = query.Where(e =>
                e.Reason.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                e.ModName.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                e.NpcDescription.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                e.NpcFormKey.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                e.Detail.Contains(s, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var e in query) FilteredRows.Add(e);
    }

    /// <summary>The indented report — the same text the old message box showed, so anything a
    /// user has previously pasted into a bug report still looks familiar. Always the FULL list,
    /// not the filtered view: the copy is the durable record.</summary>
    private void CopyText() => SetClipboard(Validator.FormatInvalidSelectionsReport(_entries));

    /// <summary>One flat tab-separated row per rejected NPC, headers included: pastes straight
    /// into Excel as five columns matching the grid. Always the FULL list, not the filtered
    /// view. The last column is empty for rejections that carry no per-NPC detail.</summary>
    private void CopySheet()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join("\t", SheetHeaders));
        foreach (var e in _entries)
        {
            sb.AppendLine(string.Join("\t",
                new[] { e.Reason, e.ModName, e.NpcDescription, e.NpcFormKey, e.Detail }.Select(CleanTsv)));
        }
        SetClipboard(sb.ToString());
    }

    private static void SetClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch { /* clipboard can be transiently locked by another process; ignore */ }
    }

    private static string CleanTsv(string? s) =>
        (s ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    public void Dispose() => _disposables.Dispose();
}
