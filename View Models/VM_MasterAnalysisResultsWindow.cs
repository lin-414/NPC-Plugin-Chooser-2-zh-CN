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
using Microsoft.Win32;
using NPC_Plugin_Chooser_2.BackEnd;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// Backs the Analyze Masters results window: one sortable/filterable table row per reference
/// found (Master, Record, Appearance Mod, Reference), matching the tabular format of the
/// Validate Output and pre-run invalid-selections windows. A master with no references still
/// gets a row — "this master may be unnecessary" is the finding the feature exists to surface.
/// The old indented text report remains available via Copy Text, so anything a user has
/// previously pasted into a bug report still looks familiar.
/// </summary>
public sealed class VM_MasterAnalysisResultsWindow : ReactiveObject, IDisposable
{
    /// <summary>One table row. AppearanceMod is only populated for NPC source records with a
    /// selection; the no-references placeholder row carries an empty Record.</summary>
    public sealed record MasterAnalysisRow(string Master, string SourceRecord, string AppearanceMod, string Reference);

    private readonly CompositeDisposable _disposables = new();
    private readonly List<MasterAnalysisRow> _allRows;
    private readonly string _textReport;

    public ObservableCollection<MasterAnalysisRow> FilteredRows { get; } = new();

    [Reactive] public string FilterText { get; set; } = string.Empty;

    public string SummaryText { get; }

    public ReactiveCommand<Unit, Unit> CopyTextCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyTsvCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCsvCommand { get; }

    private static readonly string[] Headers = { "Master", "Record", "Appearance Mod", "Reference" };

    public VM_MasterAnalysisResultsWindow(MasterAnalysisResult result, string textReport)
    {
        _textReport = textReport;

        _allRows = new List<MasterAnalysisRow>();
        int mastersWithoutReferences = 0;
        foreach (var master in result.AnalyzedMasters)
        {
            var references = result.ReferencesByMaster.GetValueOrDefault(master) ?? new List<MasterReference>();
            if (references.Count == 0)
            {
                mastersWithoutReferences++;
                _allRows.Add(new MasterAnalysisRow(master.FileName, string.Empty, string.Empty,
                    "(No references found. This master may be unnecessary, or references may be in record types not analyzed.)"));
                continue;
            }

            foreach (var reference in references)
            {
                _allRows.Add(new MasterAnalysisRow(
                    master.FileName,
                    reference.SourceRecord,
                    reference.AppearanceModInfo ?? string.Empty,
                    reference.ReferencePath));
            }
        }

        int totalReferences = _allRows.Count - mastersWithoutReferences;
        SummaryText =
            $"Analyzed {result.AnalyzedMasters.Count} master(s) in {result.TargetPlugin.FileName}: " +
            $"{totalReferences} reference(s) found." +
            (mastersWithoutReferences > 0 ? $" {mastersWithoutReferences} master(s) had no references." : string.Empty);

        ApplyFilter();

        this.WhenAnyValue(x => x.FilterText)
            .Throttle(TimeSpan.FromMilliseconds(150), RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyFilter())
            .DisposeWith(_disposables);

        CopyTextCommand = ReactiveCommand.Create(CopyText).DisposeWith(_disposables);
        CopyTsvCommand = ReactiveCommand.Create(CopyTsv).DisposeWith(_disposables);
        SaveCsvCommand = ReactiveCommand.Create(SaveCsv).DisposeWith(_disposables);
    }

    private void ApplyFilter()
    {
        FilteredRows.Clear();
        IEnumerable<MasterAnalysisRow> query = _allRows;
        var s = FilterText?.Trim();
        if (!string.IsNullOrEmpty(s))
        {
            query = query.Where(r =>
                r.Master.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                r.SourceRecord.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                r.AppearanceMod.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                r.Reference.Contains(s, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var r in query) FilteredRows.Add(r);
    }

    /// <summary>The classic indented report (grouped by master, then source record). Always the
    /// full analysis, independent of the filter — the copy is the durable record.</summary>
    private void CopyText() => SetClipboard(_textReport);

    private void CopyTsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join("\t", Headers));
        foreach (var r in FilteredRows)
        {
            sb.AppendLine(string.Join("\t", Row(r).Select(CleanTsv)));
        }
        SetClipboard(sb.ToString());
    }

    private void SaveCsv()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save Master Analysis Report",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = "MasterAnalysisReport.csv"
        };
        if (dlg.ShowDialog() != true) return;

        // Deliberately NOT the filtered view: the saved file is the archive.
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Headers.Select(CsvField)));
        foreach (var r in _allRows)
        {
            sb.AppendLine(string.Join(",", Row(r).Select(CsvField)));
        }
        // UTF-8 BOM so Excel detects encoding correctly.
        System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
    }

    private static string[] Row(MasterAnalysisRow r) =>
        new[] { r.Master, r.SourceRecord, r.AppearanceMod, r.Reference };

    private static void SetClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch { /* clipboard can be transiently locked by another process; ignore */ }
    }

    private static string CleanTsv(string? s) =>
        (s ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static string CsvField(string? s)
    {
        s ??= string.Empty;
        if (s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r'))
        {
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        return s;
    }

    public void Dispose() => _disposables.Dispose();
}
