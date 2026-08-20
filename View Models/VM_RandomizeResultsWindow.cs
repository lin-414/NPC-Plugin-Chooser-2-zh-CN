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
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>One NPC the randomize run could not place or had to leave out, as a table row.
/// <paramref name="Detail"/> is what differs between NPCs under a shared <paramref name="Issue"/>
/// (e.g. the surviving selection); empty when there is nothing to add.</summary>
public sealed record RandomizeIssueRow(string Issue, string Npc, string FormKey, string Detail)
{
    /// <summary>For exhausted-pool rows: why each tried candidate was rejected, one
    /// "Mod: reason" entry per candidate, in the order they were tried. Rendered as one grid
    /// column per entry (the window appends the columns at open, padded across rows so
    /// positional index bindings always resolve).</summary>
    public IReadOnlyList<string> CandidateFailures { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Backs the "Randomize Complete with Warnings" dialog: the run's summary and notes on top, then
/// one sortable/filterable table row per NPC the run could not place (candidate pool exhausted)
/// or had to leave out (base record not in the load order). Replaced the bullet-list message box
/// at the user's direction (2026-08-19), matching the pre-run invalid-selections table. Only
/// shown when issue rows exist — a clean run keeps the plain completion message box.
/// </summary>
public sealed class VM_RandomizeResultsWindow : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private readonly IReadOnlyList<RandomizeIssueRow> _rows;

    /// <summary>The rows the filter currently admits, in run order (the DataGrid's
    /// user-initiated column sorts act on top).</summary>
    public ObservableCollection<RandomizeIssueRow> FilteredRows { get; } = new();

    [Reactive] public string FilterText { get; set; } = string.Empty;

    public string SummaryText { get; }
    public string NotesText { get; }
    public bool HasNotes { get; }

    /// <summary>The widest row's candidate-failure count — how many "Candidate N" columns the
    /// window appends to the grid.</summary>
    public int MaxCandidateFailures { get; }

    public ReactiveCommand<Unit, Unit> CopySheetCommand { get; }

    private static readonly string[] SheetHeaders = { "Issue", "NPC", "FormKey", "Detail" };

    public VM_RandomizeResultsWindow(string summaryText, IReadOnlyList<string> notes,
        IReadOnlyList<RandomizeIssueRow> rows)
    {
        var source = rows ?? Array.Empty<RandomizeIssueRow>();
        MaxCandidateFailures = source.Count == 0 ? 0 : source.Max(r => r.CandidateFailures.Count);

        // Pad every row's failure list to the widest, so the positional "CandidateFailures[i]"
        // column bindings always resolve to a real (empty) cell instead of a binding error.
        _rows = source
            .Select(r => r.CandidateFailures.Count == MaxCandidateFailures
                ? r
                : r with
                {
                    CandidateFailures = r.CandidateFailures
                        .Concat(Enumerable.Repeat(string.Empty,
                            MaxCandidateFailures - r.CandidateFailures.Count))
                        .ToList(),
                })
            .ToList();
        SummaryText = summaryText;
        NotesText = notes is { Count: > 0 }
            ? string.Join(Environment.NewLine + Environment.NewLine, notes)
            : string.Empty;
        HasNotes = NotesText.Length > 0;

        ApplyFilter();

        this.WhenAnyValue(x => x.FilterText)
            .Throttle(TimeSpan.FromMilliseconds(150), RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyFilter())
            .DisposeWith(_disposables);

        CopySheetCommand = ReactiveCommand.Create(CopySheet).DisposeWith(_disposables);
    }

    private void ApplyFilter()
    {
        FilteredRows.Clear();
        IEnumerable<RandomizeIssueRow> query = _rows;
        var s = FilterText?.Trim();
        if (!string.IsNullOrEmpty(s))
        {
            query = query.Where(r =>
                r.Issue.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                r.Npc.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                r.FormKey.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                r.Detail.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                r.CandidateFailures.Any(f => f.Contains(s, StringComparison.OrdinalIgnoreCase)));
        }
        foreach (var r in query) FilteredRows.Add(r);
    }

    /// <summary>One flat tab-separated row per NPC, headers included: pastes straight into Excel
    /// with columns matching the grid, per-candidate failure columns included. Always the FULL
    /// list, not the filtered view.</summary>
    private void CopySheet()
    {
        var headers = SheetHeaders
            .Concat(Enumerable.Range(1, MaxCandidateFailures).Select(i => $"Candidate {i}"));

        var sb = new StringBuilder();
        sb.AppendLine(string.Join("\t", headers));
        foreach (var r in _rows)
        {
            sb.AppendLine(string.Join("\t",
                new[] { r.Issue, r.Npc, r.FormKey, r.Detail }
                    .Concat(r.CandidateFailures)
                    .Select(CleanTsv)));
        }
        try { Clipboard.SetText(sb.ToString()); }
        catch { /* clipboard can be transiently locked by another process; ignore */ }
    }

    private static string CleanTsv(string? s) =>
        (s ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    public void Dispose() => _disposables.Dispose();
}
