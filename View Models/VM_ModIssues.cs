using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Views;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// View model behind the Mod Issues tab: a Mods-tab-like display filtered to
/// mods whose render-free asset scan (<see cref="ModIssueScanner"/>) found
/// problems. The left panel lists affected mods with per-type counts; the right
/// panel shows mugshot tiles for only the affected NPCs, annotated with the
/// specific missing files. Results persist in <see cref="ModIssuesCache"/> and
/// populate the tab on open without a scan; Scan re-uses valid cache entries,
/// Rescan All ignores them.
/// </summary>
public class VM_ModIssues : ReactiveObject, ISearchFilterHost, IDisposable
{
    private readonly ModIssueScanner _scanner;
    private readonly ModIssuesCache _cache;
    private readonly Settings _settings;
    private readonly VM_Mods _modsViewModel;
    private readonly VM_Run _runViewModel;
    private readonly VM_ModsMenuMugshot.Factory _mugshotFactory;
    private readonly CompositeDisposable _disposables = new();

    private readonly List<VM_ModIssueEntry> _allEntries = new();
    private bool _ensureLoadedRan;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _mugshotLoadingCts;
    private VM_ModIssueEntry? _lastLoadedEntry;
    private IssueTypeFilterOption? _lastLoadedTypeFilter;
    private bool _lastLoadedIncludeOutfitOnly = true;
    private IDisposable? _tileLoadRepackSubscription;

    /// <summary>Per-load auto-generation state, mirroring VM_Mods'
    /// MugshotGenerationBatch: bounds the parallel renders and keeps
    /// IsLoadingMugshots up until the last kicked tile completes.</summary>
    private sealed class IssueMugshotBatch
    {
        public required SemaphoreSlim Gate { get; init; }
        public required CancellationToken Token { get; init; }
        public readonly HashSet<VM_ModsMenuMugshot> Kicked = new();
        public int ActiveCount;
    }

    // --- Left panel ---
    public ObservableCollection<VM_ModIssueEntry> FilteredModEntries { get; } = new();
    [Reactive] public VM_ModIssueEntry? SelectedEntry { get; set; }
    [Reactive] public string StatusSummaryText { get; set; } = "No scan results yet — press Scan.";

    // --- Filters ---
    [Reactive] public string NameFilterText { get; set; } = string.Empty;
    [Reactive] public string NpcSearchText { get; set; } = string.Empty;

    /// <summary>ComboBox bridge item — a concrete SelectedItem avoids the WPF
    /// null-SelectedValue blank-render trap for the "All" option.</summary>
    public sealed record IssueTypeFilterOption(string Label, ModIssueType? Value)
    {
        public override string ToString() => Label;
    }

    public IReadOnlyList<IssueTypeFilterOption> AvailableIssueTypeFilters { get; }
    [Reactive] public IssueTypeFilterOption SelectedIssueTypeFilter { get; set; }

    /// <summary>Display toggle (deliberately NOT reset by Ctrl+Shift+C): when
    /// off, mods and NPC tiles whose only problems are outfit/headgear-related
    /// are hidden, leaving just face/body defects.</summary>
    [Reactive] public bool IncludeOutfitOnlyIssues { get; set; } = true;

    // --- Scan state ---
    [Reactive] public bool IsScanning { get; private set; }
    [Reactive] public double ScanProgressValue { get; set; }
    [Reactive] public double ScanProgressMaximum { get; set; } = 1;
    [Reactive] public string ScanStatusMessage { get; set; } = string.Empty;
    [Reactive] public string ScanEtaText { get; set; } = string.Empty;
    [Reactive] public string LastScanText { get; set; } = string.Empty;

    public ReactiveCommand<Unit, Unit> ScanCommand { get; }
    public ReactiveCommand<Unit, Unit> RescanAllCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelScanCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportCsvCommand { get; }

    // --- Right panel (mugshots) ---
    public ObservableCollection<VM_ModsMenuMugshot> CurrentModNpcMugshots { get; } = new();
    [Reactive] public bool IsLoadingMugshots { get; private set; }

    // --- Right panel (issue table) ---

    /// <summary>One row of the results table under the mugshots.</summary>
    public sealed record IssueRow(string NpcDisplayName, string NpcFormKey, string Category,
        string TypeDisplay, string AffectedPath, string Location, string Referencer, string Detail);

    public ObservableCollection<IssueRow> IssueTableRows { get; } = new();
    [Reactive] public string IssueTableHeaderText { get; set; } = string.Empty;

    /// <summary>When set (by clicking a mugshot tile), the table shows only that
    /// NPC's issues. Clicking the same tile again clears it.</summary>
    [Reactive] public FormKey? TableNpcFilter { get; set; }

    public void ToggleTableNpcFilter(FormKey npcKey)
    {
        TableNpcFilter = TableNpcFilter.HasValue && TableNpcFilter.Value.Equals(npcKey)
            ? null
            : npcKey;
    }

    // --- Zoom / packing (mirrors VM_Mods; session-local, not persisted) ---
    private const double MinZoomPercentage = 1.0;
    private const double MaxZoomPercentage = 1000.0;
    private const double ZoomStepPercentage = 2.5;
    [Reactive] public double ZoomLevel { get; set; } = 100.0;
    [Reactive] public bool IsZoomLocked { get; set; }
    [Reactive] public bool HasUserManuallyZoomed { get; set; }
    public ReactiveCommand<Unit, Unit> ZoomInCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomOutCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetZoomCommand { get; }

    private readonly Subject<Unit> _refreshMugshotSizesSubject = new();
    public IObservable<Unit> RefreshMugshotSizesObservable => _refreshMugshotSizesSubject.AsObservable();

    public bool NormalizeImageDimensions => _settings.NormalizeImageDimensions;
    public int MaxMugshotsToFit => _settings.MaxMugshotsToFit;
    public double LeftPanelWidth { get; set; } // session-local splitter memory

    public CancellationToken GetCurrentMugshotLoadToken() => _mugshotLoadingCts?.Token ?? CancellationToken.None;

    public VM_ModIssues(
        ModIssueScanner scanner,
        ModIssuesCache cache,
        Settings settings,
        VM_Mods modsViewModel,
        VM_Run runViewModel,
        VM_ModsMenuMugshot.Factory mugshotFactory)
    {
        _scanner = scanner;
        _cache = cache;
        _settings = settings;
        _modsViewModel = modsViewModel;
        _runViewModel = runViewModel;
        _mugshotFactory = mugshotFactory;

        var filterOptions = new List<IssueTypeFilterOption> { new("All issue types", null) };
        filterOptions.AddRange(Enum.GetValues<ModIssueType>()
            .Select(t => new IssueTypeFilterOption(VM_ModIssueEntry.GetIssueTypeDisplayName(t), t)));
        AvailableIssueTypeFilters = filterOptions;
        SelectedIssueTypeFilter = filterOptions[0];

        var canScan = Observable.CombineLatest(
            this.WhenAnyValue(x => x.IsScanning),
            runViewModel.WhenAnyValue(r => r.IsRunning),
            (scanning, patching) => !scanning && !patching);
        ScanCommand = ReactiveCommand.CreateFromTask(() => ScanAsync(ignoreCache: false), canScan);
        RescanAllCommand = ReactiveCommand.CreateFromTask(() => ScanAsync(ignoreCache: true), canScan);
        CancelScanCommand = ReactiveCommand.Create(() => { _scanCts?.Cancel(); },
            this.WhenAnyValue(x => x.IsScanning));
        ExportCsvCommand = ReactiveCommand.Create(ExportCsv,
            this.WhenAnyValue(x => x.IsScanning, scanning => !scanning));

        foreach (var cmd in new[] { ScanCommand, RescanAllCommand, CancelScanCommand, ExportCsvCommand })
        {
            cmd.ThrownExceptions
                .Subscribe(ex => Debug.WriteLine($"VM_ModIssues command error: {ExceptionLogger.GetExceptionStack(ex)}"))
                .DisposeWith(_disposables);
        }

        ZoomInCommand = ReactiveCommand.Create(() =>
        {
            HasUserManuallyZoomed = true;
            ZoomLevel = Math.Min(MaxZoomPercentage, ZoomLevel + ZoomStepPercentage);
        });
        ZoomOutCommand = ReactiveCommand.Create(() =>
        {
            HasUserManuallyZoomed = true;
            ZoomLevel = Math.Max(MinZoomPercentage, ZoomLevel - ZoomStepPercentage);
        });
        ResetZoomCommand = ReactiveCommand.Create(() =>
        {
            IsZoomLocked = false;
            HasUserManuallyZoomed = false;
            _refreshMugshotSizesSubject.OnNext(Unit.Default);
        });

        this.WhenAnyValue(x => x.ZoomLevel)
            .Skip(1)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (IsZoomLocked || HasUserManuallyZoomed)
                {
                    _refreshMugshotSizesSubject.OnNext(Unit.Default);
                }
            })
            .DisposeWith(_disposables);

        // Filters → left list.
        this.WhenAnyValue(x => x.NameFilterText, x => x.NpcSearchText, x => x.SelectedIssueTypeFilter,
                x => x.IncludeOutfitOnlyIssues)
            .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyFilters())
            .DisposeWith(_disposables);

        // Selection (and the two filters that also narrow tiles) → right panel.
        // Throttled so the transient null that ApplyFilters' Clear/restore pushes
        // through the SelectedItem binding collapses away instead of blanking and
        // reloading the tiles; the no-op guard then skips true non-changes (e.g.
        // a filter edit that kept the same selection).
        this.WhenAnyValue(x => x.SelectedEntry, x => x.SelectedIssueTypeFilter, x => x.IncludeOutfitOnlyIssues)
            .Throttle(TimeSpan.FromMilliseconds(50), RxApp.MainThreadScheduler)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(tuple =>
            {
                if (ReferenceEquals(tuple.Item1, _lastLoadedEntry) &&
                    Equals(tuple.Item2, _lastLoadedTypeFilter) &&
                    tuple.Item3 == _lastLoadedIncludeOutfitOnly)
                {
                    return;
                }
                LoadIssueMugshots(tuple.Item1);
            })
            .DisposeWith(_disposables);

        // Any change affecting the issue table's row set rebuilds it.
        this.WhenAnyValue(x => x.SelectedEntry, x => x.SelectedIssueTypeFilter,
                x => x.IncludeOutfitOnlyIssues, x => x.TableNpcFilter)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RebuildIssueTable())
            .DisposeWith(_disposables);
    }

    /// <summary>Called on first tab activation: loads the cache from disk,
    /// populates the list without scanning, and marks stale entries in the
    /// background. Cheap on subsequent calls.</summary>
    public void EnsureLoaded()
    {
        if (_ensureLoadedRan) return;
        _ensureLoadedRan = true;

        _ = Task.Run(() =>
        {
            try
            {
                _cache.Load();
                var raw = _cache.GetAllRaw();
                Application.Current.Dispatcher.Invoke(() => RebuildEntries(raw));
                MarkStaleEntries(raw);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VM_ModIssues.EnsureLoaded failed: {ExceptionLogger.GetExceptionStack(ex)}");
            }
        });
    }

    private void MarkStaleEntries(IReadOnlyDictionary<string, ModIssueScanResult> raw)
    {
        foreach (var (displayName, result) in raw)
        {
            var vm = _modsViewModel.AllModSettings.FirstOrDefault(m =>
                m.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase));
            if (vm == null) continue;

            bool stale;
            try
            {
                var snapshot = vm.GenerateSnapshot();
                var trees = ModIssuesCache.BuildLooseAssetTrees(vm.CorrespondingFolderPaths);
                stale = !ModIssuesCache.IsEntryValid(result, snapshot, trees);
            }
            catch
            {
                stale = true;
            }

            if (!stale) continue;
            Application.Current.Dispatcher.Invoke(() =>
            {
                var entry = _allEntries.FirstOrDefault(e =>
                    e.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase));
                if (entry != null) entry.IsStale = true;
            });
        }
    }

    private async Task ScanAsync(bool ignoreCache)
    {
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        IsScanning = true;
        ScanStatusMessage = "Preparing scan…";
        ScanEtaText = string.Empty;
        ScanProgressValue = 0;

        try
        {
            // Sync in-memory mod edits to the persisted models the scanner reads
            // (same force-sync the mugshot generator's AG path performs).
            _modsViewModel.SaveModSettingsToModel();

            // Snapshot generation enumerates mod folders — run off the UI thread.
            var targets = await Task.Run(() =>
            {
                var list = new List<ModIssueScanner.ModScanTarget>();
                foreach (var model in _settings.ModSettings
                             .Where(ModIssueScanner.IsEligible)
                             .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    var vm = _modsViewModel.AllModSettings.FirstOrDefault(m =>
                        m.DisplayName.Equals(model.DisplayName, StringComparison.OrdinalIgnoreCase));
                    list.Add(new ModIssueScanner.ModScanTarget(model, vm?.GenerateSnapshot()));
                }
                return list;
            }, ct);

            // Progress units are NPCs (see ModIssueScanner.ProgressInfo) so big
            // and small mods advance the bar proportionally to their real cost;
            // the authoritative total arrives with each report. The ETA records
            // one item per NPC — cache-hit NPCs count as ~0s, which is correct
            // (remaining cached mods will also cost ~0) — with a window wide
            // enough that one large cached mod doesn't wipe out the recent
            // real-work history.
            var eta = new EtaCalculator(windowSize: 200);
            int lastCompleted = 0;
            var itemStopwatch = Stopwatch.StartNew();
            var progress = new Progress<ModIssueScanner.ProgressInfo>(p =>
            {
                ScanProgressMaximum = Math.Max(1, p.Total);
                ScanProgressValue = p.Completed;
                ScanStatusMessage = p.CurrentLabel;
                if (p.Completed > lastCompleted)
                {
                    // Attribute the elapsed time evenly across the NPCs that
                    // completed since the last report (per-NPC reports batch by
                    // 10; cache hits advance a whole mod at once).
                    double seconds = itemStopwatch.Elapsed.TotalSeconds / (p.Completed - lastCompleted);
                    for (int i = lastCompleted; i < p.Completed; i++) eta.RecordItem(seconds);
                    lastCompleted = p.Completed;
                    itemStopwatch.Restart();
                    var estimate = eta.Estimate(p.Total - p.Completed);
                    ScanEtaText = estimate.HasValue ? $"ETA: {estimate.Value:hh\\:mm\\:ss}" : string.Empty;
                }
            });

            var results = await _scanner.RunAsync(targets, ignoreCache, progress, ct,
                onModCompleted: (name, result) =>
                    Application.Current.Dispatcher.InvokeAsync(() => UpsertEntry(name, result)));

            // Consistency pass over the incremental upserts: recomputes the
            // summary/last-scan text and drops rows for mods removed mid-scan.
            RebuildEntries(results);
            ScanStatusMessage = "Scan complete.";
        }
        catch (OperationCanceledException)
        {
            // Completed mods were persisted by the scanner; refresh from cache.
            RebuildEntries(_cache.GetAllRaw());
            ScanStatusMessage = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            ScanStatusMessage = "Scan failed — see debug log.";
            Debug.WriteLine($"VM_ModIssues.ScanAsync failed: {ExceptionLogger.GetExceptionStack(ex)}");
            ScrollableMessageBox.ShowWarning(
                $"The mod issue scan failed:\n{ExceptionLogger.GetExceptionStack(ex)}", "Scan Error");
        }
        finally
        {
            IsScanning = false;
            ScanEtaText = string.Empty;
        }
    }

    private void RebuildEntries(IReadOnlyDictionary<string, ModIssueScanResult> results)
    {
        var previousSelection = SelectedEntry?.DisplayName;

        _allEntries.Clear();
        int scannedCount = 0;
        DateTime? newestScan = null;
        foreach (var (displayName, result) in results)
        {
            scannedCount++;
            if (newestScan == null || result.ScanTimeUtc > newestScan) newestScan = result.ScanTimeUtc;
            if (result.Issues.Count == 0) continue;

            var vm = _modsViewModel.AllModSettings.FirstOrDefault(m =>
                m.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase));
            if (vm == null) continue; // Mod removed since the scan.

            _allEntries.Add(new VM_ModIssueEntry(displayName, vm, result, _modsViewModel));
        }
        _allEntries.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        LastScanText = newestScan.HasValue ? $"Last scan: {newestScan.Value.ToLocalTime():g}" : string.Empty;
        StatusSummaryText = scannedCount == 0
            ? "No scan results yet — press Scan."
            : _allEntries.Count == 0
                ? $"Scan complete — no issues found in {scannedCount} scanned mod{(scannedCount == 1 ? "" : "s")}."
                : $"{_allEntries.Count} of {scannedCount} scanned mods have issues.";

        ApplyFilters();

        if (previousSelection != null)
        {
            SelectedEntry = FilteredModEntries.FirstOrDefault(e =>
                e.DisplayName.Equals(previousSelection, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Applies one mod's finished scan result to the list while the scan is
    /// still running: replaces (or removes, if now clean) any existing row for
    /// that mod and inserts the new one in sorted position, without rebuilding
    /// the collections — so the user's selection and scroll position survive.
    /// UI thread only.
    /// </summary>
    private void UpsertEntry(string displayName, ModIssueScanResult result)
    {
        var oldEntry = _allEntries.FirstOrDefault(e =>
            e.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase));
        bool wasSelected = oldEntry != null && ReferenceEquals(SelectedEntry, oldEntry);
        if (oldEntry != null)
        {
            _allEntries.Remove(oldEntry);
            FilteredModEntries.Remove(oldEntry);
        }

        VM_ModIssueEntry? newEntry = null;
        if (result.Issues.Count > 0)
        {
            var vm = _modsViewModel.AllModSettings.FirstOrDefault(m =>
                m.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase));
            if (vm != null)
            {
                newEntry = new VM_ModIssueEntry(displayName, vm, result, _modsViewModel);

                int allIdx = _allEntries.FindIndex(e =>
                    string.Compare(e.DisplayName, displayName, StringComparison.OrdinalIgnoreCase) > 0);
                if (allIdx < 0) _allEntries.Add(newEntry);
                else _allEntries.Insert(allIdx, newEntry);

                if (EntryPassesFilters(newEntry))
                {
                    int filteredIdx = 0;
                    while (filteredIdx < FilteredModEntries.Count &&
                           string.Compare(FilteredModEntries[filteredIdx].DisplayName, displayName,
                               StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        filteredIdx++;
                    }
                    FilteredModEntries.Insert(filteredIdx, newEntry);
                }
            }
        }

        // Re-point the selection at the replacement so the mugshot panel
        // refreshes with the new data; a mod that came back clean leaves the
        // selection cleared (the binding already nulled it on removal).
        if (wasSelected && newEntry != null && FilteredModEntries.Contains(newEntry))
        {
            SelectedEntry = newEntry;
        }

        StatusSummaryText = $"{_allEntries.Count} mod{(_allEntries.Count == 1 ? "" : "s")} with issues so far…";
    }

    private bool EntryPassesFilters(VM_ModIssueEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(NameFilterText) &&
            !entry.DisplayName.Contains(NameFilterText, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(NpcSearchText) &&
            !entry.Result.Issues.Any(i =>
                (i.NpcDisplayName?.Contains(NpcSearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                i.NpcFormKey.ToString().Contains(NpcSearchText, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (SelectedIssueTypeFilter?.Value is { } type && !entry.CountsByType.ContainsKey(type))
        {
            return false;
        }

        if (!IncludeOutfitOnlyIssues && entry.Result.Issues.All(i => i.IsOutfitIssue))
        {
            return false;
        }

        return true;
    }

    private void ApplyFilters()
    {
        var result = _allEntries.Where(EntryPassesFilters).ToList();

        // Clearing the ObservableCollection makes the ListBox's two-way
        // SelectedItem binding push null into SelectedEntry, so capture the
        // selection first and restore it after repopulating.
        var previousSelection = SelectedEntry;
        FilteredModEntries.Clear();
        foreach (var entry in result) FilteredModEntries.Add(entry);

        SelectedEntry = previousSelection != null && result.Contains(previousSelection)
            ? previousSelection
            : null;
    }

    /// <inheritdoc/>
    public void ClearSearchFilters()
    {
        NameFilterText = string.Empty;
        NpcSearchText = string.Empty;
        SelectedIssueTypeFilter = AvailableIssueTypeFilters[0];
    }

    // --- Right panel ---

    private void DisposeAndClearMugshots()
    {
        foreach (var vm in CurrentModNpcMugshots) vm.Dispose();
        CurrentModNpcMugshots.Clear();
    }

    private void LoadIssueMugshots(VM_ModIssueEntry? entry)
    {
        _mugshotLoadingCts?.Cancel();
        _mugshotLoadingCts?.Dispose();
        _mugshotLoadingCts = new CancellationTokenSource();
        var token = _mugshotLoadingCts.Token;

        // Selecting a different mod invalidates any per-NPC table filter.
        if (!ReferenceEquals(entry, _lastLoadedEntry)) TableNpcFilter = null;

        _lastLoadedEntry = entry;
        _lastLoadedTypeFilter = SelectedIssueTypeFilter;
        _lastLoadedIncludeOutfitOnly = IncludeOutfitOnlyIssues;

        _tileLoadRepackSubscription?.Dispose();
        _tileLoadRepackSubscription = null;

        DisposeAndClearMugshots();
        if (entry == null)
        {
            IsLoadingMugshots = false;
            return;
        }

        IsLoadingMugshots = true;
        if (!IsZoomLocked) HasUserManuallyZoomed = false;

        var typeFilter = SelectedIssueTypeFilter?.Value;
        bool includeOutfitOnly = IncludeOutfitOnlyIssues;

        // Per-load auto-generation batch (VM_Mods pattern): placeholder tiles
        // render their mugshot through the tile's own priority pipeline, bounded
        // by the renderer's parallelism ceiling.
        int maxParallel = Math.Max(1, _settings.MaxParallelPortraitRenders);
        var batch = new IssueMugshotBatch
        {
            Gate = new SemaphoreSlim(maxParallel, maxParallel),
            Token = token,
        };

        _ = Task.Run(async () =>
        {
            try
            {
                var modSettingVm = entry.SourceVm;

                // Affected NPCs (narrowed by the issue-type filter and, when the
                // outfit toggle is off, to NPCs with at least one non-outfit
                // issue), ordered by display name.
                var affected = entry.IssuesByNpc
                    .Where(kv => typeFilter == null || kv.Value.Any(i => i.Type == typeFilter))
                    .Where(kv => includeOutfitOnly || kv.Value.Any(i => !i.IsOutfitIssue))
                    .Select(kv => (NpcFormKey: kv.Key,
                        Issues: (IReadOnlyList<ModIssue>)(typeFilter == null
                            ? kv.Value
                            : kv.Value.Where(i => i.Type == typeFilter).ToList()),
                        DisplayName: modSettingVm.NpcFormKeysToDisplayName.TryGetValue(kv.Key, out var dn)
                            ? dn
                            : kv.Value.FirstOrDefault()?.NpcDisplayName ?? kv.Key.ToString()))
                    .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (token.IsCancellationRequested) return;

                // Existing mugshot images for this mod, same source folders the
                // Mods tab consults (curated + AutoGen + FaceFinder caches).
                var existingMugshots = new Dictionary<FormKey, string>();
                var candidateFolders = (modSettingVm.MugShotFolderPaths ?? Enumerable.Empty<string>())
                    .Concat(new[]
                    {
                        BatchMugshotGenerator.GetAutoGenModFolder(_settings, modSettingVm.DisplayName),
                        BatchMugshotGenerator.GetFaceFinderModFolder(_settings, modSettingVm.DisplayName),
                    })
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(Directory.Exists);

                foreach (var imagePath in candidateFolders
                             .SelectMany(folder => Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
                             .Where(f => VM_Mods.MugshotNameRegex.IsMatch(Path.GetFileName(f))))
                {
                    if (token.IsCancellationRequested) return;
                    var match = VM_Mods.MugshotNameRegex.Match(Path.GetFileName(imagePath));
                    var hexPart = match.Groups["hex"].Value;
                    var pluginName = new DirectoryInfo(Path.GetDirectoryName(imagePath)!).Name;
                    var tail6 = hexPart.Length >= 6 ? hexPart[^6..] : hexPart;
                    if (FormKey.TryFactory($"{tail6}:{pluginName}", out var npcFormKey) &&
                        !existingMugshots.ContainsKey(npcFormKey))
                    {
                        existingMugshots[npcFormKey] = imagePath;
                    }
                }

                var vms = new List<VM_ModsMenuMugshot>(affected.Count);
                foreach (var (npcFormKey, issues, displayName) in affected)
                {
                    if (token.IsCancellationRequested) return;
                    string imagePath = existingMugshots.TryGetValue(npcFormKey, out var path)
                        ? path
                        : VM_Mods.FullPlaceholderPath;

                    bool isAmbiguous = modSettingVm.AmbiguousNpcFormKeys.Contains(npcFormKey);
                    var availableModKeys = modSettingVm.AvailablePluginsForNpcs.TryGetValue(npcFormKey, out var keys)
                        ? keys
                        : new List<ModKey>();
                    var currentSource = modSettingVm.NpcPluginDisambiguation.TryGetValue(npcFormKey, out var source)
                        ? (ModKey?)source
                        : availableModKeys.FirstOrDefault();

                    var vm = _mugshotFactory(imagePath, npcFormKey, displayName, _modsViewModel,
                        isAmbiguous, availableModKeys, currentSource, modSettingVm, token);
                    var baseIssues = issues.Where(i => !i.IsOutfitIssue).ToList();
                    var outfitIssues = issues.Where(i => i.IsOutfitIssue).ToList();
                    if (baseIssues.Count > 0)
                        vm.ApplyScanIssueOverlay(VM_ModIssueEntry.BuildNpcIssueText(baseIssues));
                    if (outfitIssues.Count > 0)
                        vm.ApplyScanOutfitIssueOverlay(VM_ModIssueEntry.BuildNpcIssueText(outfitIssues));
                    vms.Add(vm);
                }

                // Repack whenever tile images finish their async loads: the
                // packer only sizes tiles whose dimensions are known, so the
                // passes fired during population see zeros for still-loading
                // images and would otherwise leave them at natural size forever.
                _tileLoadRepackSubscription = Observable
                    .Merge(vms.Select(v => v.WhenAnyValue(x => x.MugshotSource).Select(_ => Unit.Default)))
                    .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        if (!token.IsCancellationRequested) _refreshMugshotSizesSubject.OnNext(Unit.Default);
                    });

                // Add in dispatcher batches so a large broken mod stays responsive.
                const int addBatchSize = 100;
                for (int i = 0; i < vms.Count; i += addBatchSize)
                {
                    if (token.IsCancellationRequested) return;
                    var slice = vms.Skip(i).Take(addBatchSize).ToList();
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        foreach (var vm in slice) CurrentModNpcMugshots.Add(vm);
                        _refreshMugshotSizesSubject.OnNext(Unit.Default);
                    }, System.Windows.Threading.DispatcherPriority.Background, token);
                }

                // Population done — kick auto-generation for placeholder tiles.
                // IsLoadingMugshots stays up until the last kicked tile finishes
                // (or clears now if there is nothing to generate).
                await Application.Current.Dispatcher.InvokeAsync(
                    () => TriggerMugshotGeneration(batch),
                    System.Windows.Threading.DispatcherPriority.Background, token);
            }
            catch (OperationCanceledException) { /* superseded load */ }
            catch (Exception ex)
            {
                Debug.WriteLine($"VM_ModIssues.LoadIssueMugshots failed: {ExceptionLogger.GetExceptionStack(ex)}");
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested) IsLoadingMugshots = false;
                });
            }
        }, token);
    }

    /// <summary>Kicks bounded auto-generation (via each tile's own
    /// LoadRealImageAsync priority pipeline) for placeholder tiles. Mirrors
    /// VM_Mods.TriggerAsyncMugshotGeneration. UI thread.</summary>
    private void TriggerMugshotGeneration(IssueMugshotBatch batch)
    {
        if (batch.Token.IsCancellationRequested) return;

        var newTiles = new List<VM_ModsMenuMugshot>();
        foreach (var vm in CurrentModNpcMugshots)
        {
            if (!vm.HasMugshot && batch.Kicked.Add(vm)) newTiles.Add(vm);
        }

        if (newTiles.Count == 0)
        {
            if (Volatile.Read(ref batch.ActiveCount) == 0) IsLoadingMugshots = false;
            return;
        }

        Interlocked.Add(ref batch.ActiveCount, newTiles.Count);
        IsLoadingMugshots = true;

        foreach (var mugshotVm in newTiles)
        {
            var vmCapture = mugshotVm;
            _ = Task.Run(async () =>
            {
                bool acquired = false;
                try
                {
                    await batch.Gate.WaitAsync(batch.Token);
                    acquired = true;
                    await vmCapture.LoadRealImageAsync();
                }
                catch (OperationCanceledException) { /* load superseded */ }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Mod Issues mugshot generation failed: {ExceptionLogger.GetExceptionStack(ex)}");
                }
                finally
                {
                    if (acquired) batch.Gate.Release();
                    OnTileGenerationComplete(batch);
                }
            });
        }
    }

    private void OnTileGenerationComplete(IssueMugshotBatch batch)
    {
        if (Interlocked.Decrement(ref batch.ActiveCount) != 0) return;

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (batch.Token.IsCancellationRequested) return;
            if (Volatile.Read(ref batch.ActiveCount) != 0) return;
            IsLoadingMugshots = false;
            _refreshMugshotSizesSubject.OnNext(Unit.Default);
        });
    }

    /// <summary>Rebuilds the results table under the mugshot panel from the
    /// selected mod's issues, honoring the issue-type filter, the outfit
    /// toggle, and the per-NPC tile filter. UI thread.</summary>
    private void RebuildIssueTable()
    {
        IssueTableRows.Clear();

        var entry = SelectedEntry;
        if (entry == null)
        {
            IssueTableHeaderText = string.Empty;
            return;
        }

        var typeFilter = SelectedIssueTypeFilter?.Value;
        IEnumerable<ModIssue> issues = entry.Result.Issues;
        if (typeFilter != null) issues = issues.Where(i => i.Type == typeFilter);
        if (!IncludeOutfitOnlyIssues) issues = issues.Where(i => !i.IsOutfitIssue);
        if (TableNpcFilter is { } npcFilter) issues = issues.Where(i => i.NpcFormKey.Equals(npcFilter));

        string? filteredNpcName = null;
        foreach (var issue in issues
                     .OrderBy(i => i.NpcDisplayName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(i => i.Type)
                     .ThenBy(i => i.AffectedPath, StringComparer.OrdinalIgnoreCase))
        {
            filteredNpcName ??= issue.NpcDisplayName;
            string location = issue.ShapeName != null
                ? $"{issue.ShapeName} in {Path.GetFileName(issue.NifPath ?? string.Empty)}"
                : issue.NifPath ?? string.Empty;
            IssueTableRows.Add(new IssueRow(
                issue.NpcFormKey.IsNull ? "(mod-level)" : issue.NpcDisplayName ?? issue.NpcFormKey.ToString(),
                issue.NpcFormKey.IsNull ? string.Empty : issue.NpcFormKey.ToString(),
                issue.IsOutfitIssue ? "Outfit" : "NPC",
                VM_ModIssueEntry.GetIssueTypeDisplayName(issue.Type),
                issue.AffectedPath,
                location,
                issue.ReferencingRecord ?? string.Empty,
                issue.Detail ?? string.Empty));
        }

        IssueTableHeaderText = TableNpcFilter != null
            ? $"{IssueTableRows.Count} issue{(IssueTableRows.Count == 1 ? "" : "s")} for {filteredNpcName ?? "selected NPC"} — click the tile again to show the whole mod"
            : $"{IssueTableRows.Count} issue{(IssueTableRows.Count == 1 ? "" : "s")} in {entry.DisplayName} — click a mugshot to filter to that NPC";
    }

    // --- CSV export ---

    private void ExportCsv()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            FileName = $"ModIssues_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            using var writer = new StreamWriter(dialog.FileName, append: false);
            writer.WriteLine("Mod,Category,IssueType,NpcFormKey,NpcName,AffectedPath,Shape,Nif,Referencer,Detail");
            foreach (var entry in _allEntries)
            {
                foreach (var issue in entry.Result.Issues)
                {
                    writer.WriteLine(string.Join(",", new[]
                    {
                        Csv(entry.DisplayName),
                        issue.IsOutfitIssue ? "Outfit" : "NPC",
                        Csv(VM_ModIssueEntry.GetIssueTypeDisplayName(issue.Type)),
                        Csv(issue.NpcFormKey.IsNull ? "" : issue.NpcFormKey.ToString()),
                        Csv(issue.NpcDisplayName ?? ""),
                        Csv(issue.AffectedPath),
                        Csv(issue.ShapeName ?? ""),
                        Csv(issue.NifPath ?? ""),
                        Csv(issue.ReferencingRecord ?? ""),
                        Csv(issue.Detail ?? ""),
                    }));
                }
            }
        }
        catch (Exception ex)
        {
            ScrollableMessageBox.ShowWarning($"Failed to export CSV:\n{ex.Message}", "Export Error");
        }
    }

    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    public void Dispose()
    {
        _disposables.Dispose();
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _mugshotLoadingCts?.Cancel();
        _mugshotLoadingCts?.Dispose();
        _tileLoadRepackSubscription?.Dispose();
        DisposeAndClearMugshots();
        _refreshMugshotSizesSubject.Dispose();
    }
}
