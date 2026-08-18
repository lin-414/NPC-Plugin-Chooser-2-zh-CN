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
using Mutagen.Bethesda.Skyrim;
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
/// Rescan All ignores them. Both honor the toolbar's searchable scan-target box
/// (default <see cref="AllModsScanTarget"/> = every eligible mod).
/// </summary>
public class VM_ModIssues : ReactiveObject, ISearchFilterHost, IDisposable
{
    private readonly ModIssueScanner _scanner;
    private readonly ModIssuesCache _cache;
    private readonly Settings _settings;
    private readonly VM_Mods _modsViewModel;
    private readonly VM_Run _runViewModel;
    private readonly VM_ModsMenuMugshot.Factory _mugshotFactory;
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly CompositeDisposable _disposables = new();

    private readonly List<VM_ModIssueEntry> _allEntries = new();
    private bool _ensureLoadedRan;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _mugshotLoadingCts;
    private VM_ModIssueEntry? _lastLoadedEntry;
    private IssueTypeFilterOption? _lastLoadedTypeFilter;
    private bool _lastLoadedIncludeOutfitOnly = true;
    private IDisposable? _tileLoadRepackSubscription;

    /// <summary>The most recent full result set (cache load or scan), kept so
    /// severity/ignore toggle flips can rebuild the entries without re-reading
    /// the cache from disk.</summary>
    private IReadOnlyDictionary<string, ModIssueScanResult> _lastResults =
        new Dictionary<string, ModIssueScanResult>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Settings.ModIssuesIgnored grouped by mod name for O(entries-per-mod)
    /// matching instead of a whole-list scan per issue.</summary>
    private Dictionary<string, List<ModIssueIgnoreEntry>> _ignoreLookup = new(StringComparer.OrdinalIgnoreCase);

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

    // --- Scan target (searchable combo in the scan toolbar) ---

    /// <summary>Sentinel first entry of the scan-target box: scan every eligible mod.</summary>
    public const string AllModsScanTarget = "ALL";

    /// <summary>Raw text of the editable scan-target ComboBox. Session-local,
    /// deliberately not persisted so every launch starts back at ALL.</summary>
    [Reactive] public string ScanTargetText { get; set; } = AllModsScanTarget;

    /// <summary>Dropdown contents: ALL plus the eligible mod names, narrowed to
    /// substring matches while <see cref="ScanTargetText"/> is a partial search
    /// string (see <see cref="RebuildScanTargetOptions"/>).</summary>
    public ObservableCollection<string> ScanTargetOptions { get; } = new();

    /// <summary>Eligible mod display names (sorted), snapshotted so per-keystroke
    /// filtering doesn't re-probe ~every mod folder on disk. Uses the settings-only
    /// eligibility half; <see cref="ScanAsync"/> still applies the full predicate.</summary>
    private List<string> _scanTargetNames = new();

    /// <summary>Display toggle (deliberately NOT reset by Ctrl+Shift+C): when
    /// off, mods and NPC tiles whose only problems are outfit/headgear-related
    /// are hidden, leaving just face/body defects.</summary>
    [Reactive] public bool IncludeOutfitOnlyIssues { get; set; } = true;

    /// <summary>Show Note-severity findings — real but subtle in game
    /// (secondary texture slots that even vanilla meshes reference without
    /// shipping). Off by default so the headline view stays actionable.</summary>
    [Reactive] public bool ShowNotes { get; set; }

    /// <summary>Show rows the user has ignored (greyed; Unignore available from
    /// the row's context menu).</summary>
    [Reactive] public bool ShowIgnored { get; set; }

    /// <summary>Collapse the results table to one row per distinct problem with
    /// an NPC count (shared body/head NIFs repeat one missing file across every
    /// NPC — measured ~9.5× row reduction). Clicking a mugshot tile still shows
    /// that NPC's individual rows.</summary>
    [Reactive] public bool GroupByFile { get; set; } = true;

    [Reactive] public string ShowNotesLabel { get; set; } = "Show notes";
    [Reactive] public string ShowIgnoredLabel { get; set; } = "Show ignored";

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
    public ReactiveCommand<Unit, Unit> CancelMugshotLoadCommand { get; }

    // --- Right panel (mugshots) ---
    public ObservableCollection<VM_ModsMenuMugshot> CurrentModNpcMugshots { get; } = new();
    [Reactive] public bool IsLoadingMugshots { get; private set; }

    // --- Right panel (issue table) ---

    /// <summary>One row of the results table under the mugshots. In grouped mode
    /// one row stands for every NPC sharing a distinct problem (NpcCount > 1)
    /// and carries all of their issues so the ignore commands can act on the
    /// whole group. Plugin is the scanned mod's own plugin(s) whose record
    /// produced a record-graded verdict (multi-plugin mods only; empty
    /// otherwise).</summary>
    public sealed record IssueRow(string NpcDisplayName, string NpcFormKey, string Category,
        string TypeDisplay, string Plugin, string AffectedPath, string Location, string Referencer,
        string ProvidedBy, string Detail, int NpcCount, bool IsNote, bool IsIgnored,
        IReadOnlyList<ModIssue> Issues);

    public ObservableCollection<IssueRow> IssueTableRows { get; } = new();
    [Reactive] public string IssueTableHeaderText { get; set; } = string.Empty;

    /// <summary>Non-empty when the current table contains outfit rows: points
    /// users at the outfit mod ("Provided by") instead of the replacer.</summary>
    [Reactive] public string OutfitHintText { get; set; } = string.Empty;

    /// <summary>Eligible mods with no cache entry — absence must not read as
    /// "clean". Shown greyed under the left list with a per-mod Scan action.</summary>
    public ObservableCollection<string> UnscannedModNames { get; } = new();

    // Row context-menu commands (parameter = the row, via the PlacementTarget.Tag
    // recipe in the view). File scope hides every occurrence of the path in this
    // mod; all-mods scope hides it everywhere (vanilla-recurring files like the
    // mouth subsurface maps); exact scope pins the NPC (and NIF/shape for
    // texture rows).
    public ReactiveCommand<IssueRow, Unit> IgnoreFileCommand { get; }
    public ReactiveCommand<IssueRow, Unit> IgnoreFileAllModsCommand { get; }
    public ReactiveCommand<IssueRow, Unit> IgnoreExactCommand { get; }
    public ReactiveCommand<IssueRow, Unit> UnignoreCommand { get; }

    // Copy-to-clipboard commands for the row's NPC (single-NPC rows only).
    public ReactiveCommand<IssueRow, Unit> CopyNpcNameCommand { get; }
    public ReactiveCommand<IssueRow, Unit> CopyNpcEditorIdCommand { get; }
    public ReactiveCommand<IssueRow, Unit> CopyNpcFormIdCommand { get; }
    public ReactiveCommand<IssueRow, Unit> CopyNpcFormKeyCommand { get; }

    /// <summary>Left-list context menu: rescan just this mod, ignoring its cache entry.</summary>
    public ReactiveCommand<VM_ModIssueEntry, Unit> RescanModCommand { get; }

    /// <summary>Scan one not-yet-scanned mod from the unscanned list.</summary>
    public ReactiveCommand<string, Unit> ScanSingleModCommand { get; }

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
        VM_ModsMenuMugshot.Factory mugshotFactory,
        EnvironmentStateProvider environmentStateProvider)
    {
        _scanner = scanner;
        _cache = cache;
        _settings = settings;
        _modsViewModel = modsViewModel;
        _runViewModel = runViewModel;
        _mugshotFactory = mugshotFactory;
        _environmentStateProvider = environmentStateProvider;

        var filterOptions = new List<IssueTypeFilterOption> { new("All issue types", null) };
        filterOptions.AddRange(Enum.GetValues<ModIssueType>()
            .Select(t => new IssueTypeFilterOption(VM_ModIssueEntry.GetIssueTypeDisplayName(t), t)));
        AvailableIssueTypeFilters = filterOptions;
        SelectedIssueTypeFilter = filterOptions[0];

        RefreshScanTargetNames();
        RebuildScanTargetOptions();

        var canScan = Observable.CombineLatest(
            this.WhenAnyValue(x => x.IsScanning),
            runViewModel.WhenAnyValue(r => r.IsRunning),
            (scanning, patching) => !scanning && !patching);
        ScanCommand = ReactiveCommand.CreateFromTask(() => ScanFromToolbarAsync(ignoreCache: false), canScan);
        RescanAllCommand = ReactiveCommand.CreateFromTask(() => ScanFromToolbarAsync(ignoreCache: true), canScan);
        CancelScanCommand = ReactiveCommand.Create(() => { _scanCts?.Cancel(); },
            this.WhenAnyValue(x => x.IsScanning));
        ExportCsvCommand = ReactiveCommand.Create(ExportCsv,
            this.WhenAnyValue(x => x.IsScanning, scanning => !scanning));

        RescanModCommand = ReactiveCommand.CreateFromTask<VM_ModIssueEntry>(
            entry => ScanAsync(ignoreCache: true, onlyMods: new[] { entry.DisplayName }), canScan);
        ScanSingleModCommand = ReactiveCommand.CreateFromTask<string>(
            name => ScanAsync(ignoreCache: false, onlyMods: new[] { name }), canScan);

        IgnoreFileCommand = ReactiveCommand.Create<IssueRow>(row => ApplyIgnore(row, exact: false));
        IgnoreFileAllModsCommand = ReactiveCommand.Create<IssueRow>(row => ApplyIgnore(row, exact: false, allMods: true));
        IgnoreExactCommand = ReactiveCommand.Create<IssueRow>(row => ApplyIgnore(row, exact: true));
        UnignoreCommand = ReactiveCommand.Create<IssueRow>(Unignore);
        CopyNpcNameCommand = ReactiveCommand.Create<IssueRow>(row => CopyNpcField(row, CopyField.Name));
        CopyNpcEditorIdCommand = ReactiveCommand.Create<IssueRow>(row => CopyNpcField(row, CopyField.EditorId));
        CopyNpcFormIdCommand = ReactiveCommand.Create<IssueRow>(row => CopyNpcField(row, CopyField.FormId));
        CopyNpcFormKeyCommand = ReactiveCommand.Create<IssueRow>(row => CopyNpcField(row, CopyField.FormKey));
        foreach (var cmd in new IReactiveCommand[]
                     { RescanModCommand, ScanSingleModCommand, IgnoreFileCommand, IgnoreFileAllModsCommand,
                       IgnoreExactCommand, UnignoreCommand, CopyNpcNameCommand, CopyNpcEditorIdCommand,
                       CopyNpcFormIdCommand, CopyNpcFormKeyCommand })
        {
            cmd.ThrownExceptions
                .Subscribe(ex => Debug.WriteLine($"VM_ModIssues command error: {ExceptionLogger.GetExceptionStack(ex)}"))
                .DisposeWith(_disposables);
        }

        // Severity / ignore toggles change which issues exist at all, so the
        // entries themselves are rebuilt from the last results (selection is
        // restored by name inside RebuildEntries).
        this.WhenAnyValue(x => x.ShowNotes, x => x.ShowIgnored)
            .Skip(1)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RebuildEntries(_lastResults))
            .DisposeWith(_disposables);

        // Mirrors VM_Mods.CancelMugshotLoadCommand: the load CTS token flows
        // into both the population task and the generation batch, so one Cancel
        // stops tile creation and every in-flight/queued render.
        CancelMugshotLoadCommand = ReactiveCommand.Create(() =>
        {
            _mugshotLoadingCts?.Cancel();
            IsLoadingMugshots = false; // Set UI state immediately for responsiveness
        });

        foreach (var cmd in new[] { ScanCommand, RescanAllCommand, CancelScanCommand, ExportCsvCommand, CancelMugshotLoadCommand })
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

        // Scan-target text → narrowed dropdown. Throttled so the list mutates
        // between keystrokes rather than on every one.
        this.WhenAnyValue(x => x.ScanTargetText)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(150), RxApp.MainThreadScheduler)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RebuildScanTargetOptions())
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
                x => x.IncludeOutfitOnlyIssues, x => x.TableNpcFilter, x => x.GroupByFile)
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

        // Mods discovered after construction (VM_Mods population) should be
        // offerable as scan targets by the time the tab is first shown.
        RefreshScanTargetOptions();

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

    // --- Scan target resolution ---

    private void RefreshScanTargetNames() => _scanTargetNames = _settings.ModSettings
        .Where(ModIssueScanner.IsEligibleBySettings)
        .Select(m => m.DisplayName)
        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>True when the text is a completed choice — ALL or an exact mod
    /// name — rather than a partial search string. The view uses this to avoid
    /// popping the dropdown open when an item was just picked.</summary>
    public bool IsResolvedScanTarget(string? text)
    {
        text = text?.Trim();
        return string.IsNullOrEmpty(text)
               || text.Equals(AllModsScanTarget, StringComparison.OrdinalIgnoreCase)
               || _scanTargetNames.Any(n => n.Equals(text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Rebuilds the dropdown: ALL + every eligible mod while the text is
    /// a resolved choice (so reopening after a pick offers the full list again),
    /// or just the substring matches while the user is mid-search.</summary>
    private void RebuildScanTargetOptions()
    {
        var text = ScanTargetText?.Trim() ?? string.Empty;
        var visible = IsResolvedScanTarget(text)
            ? _scanTargetNames
            : _scanTargetNames.Where(n => n.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();

        // Removing the ComboBox's selected item during Clear() can push an empty
        // string through the two-way Text binding (classic editable-combo filter
        // trap); snapshot and restore so the user's text survives the rebuild.
        var keep = ScanTargetText;
        ScanTargetOptions.Clear();
        ScanTargetOptions.Add(AllModsScanTarget);
        foreach (var name in visible) ScanTargetOptions.Add(name);
        if (!string.Equals(ScanTargetText, keep, StringComparison.Ordinal)) ScanTargetText = keep;
    }

    /// <summary>Dropdown-open / tab-activation refresh: re-snapshot the eligible
    /// mod names (mods may have been added or removed) and rebuild the options.</summary>
    public void RefreshScanTargetOptions()
    {
        RefreshScanTargetNames();
        RebuildScanTargetOptions();
    }

    /// <summary>Resolves the scan-target text to the mods a toolbar scan covers:
    /// null = every eligible mod (ALL or empty text); otherwise the exact-named
    /// mod, or every mod whose name contains the text — the same match rule the
    /// dropdown displays. An empty list means nothing matches.</summary>
    internal static IReadOnlyCollection<string>? ResolveScanTarget(
        string? scanTargetText, IReadOnlyList<string> eligibleModNames)
    {
        var text = scanTargetText?.Trim();
        if (string.IsNullOrEmpty(text) ||
            text.Equals(AllModsScanTarget, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var exact = eligibleModNames
            .Where(n => n.Equals(text, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count > 0) return exact;

        return eligibleModNames
            .Where(n => n.Contains(text, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Toolbar Scan / Rescan All entry point: applies the scan-target
    /// box before delegating to <see cref="ScanAsync"/>.</summary>
    private async Task ScanFromToolbarAsync(bool ignoreCache)
    {
        RefreshScanTargetNames();
        var onlyMods = ResolveScanTarget(ScanTargetText, _scanTargetNames);
        if (onlyMods is { Count: 0 })
        {
            ScanStatusMessage = $"No eligible mod matches '{ScanTargetText?.Trim()}' — nothing scanned.";
            return;
        }
        await ScanAsync(ignoreCache, onlyMods);
    }

    private async Task ScanAsync(bool ignoreCache, IReadOnlyCollection<string>? onlyMods = null)
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
                             .Where(m => onlyMods == null ||
                                         onlyMods.Contains(m.DisplayName, StringComparer.OrdinalIgnoreCase))
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
            // From the cache rather than `results` so a single-mod rescan keeps
            // every other mod's row.
            RebuildEntries(_cache.GetAllRaw());
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
        _lastResults = results;
        RefreshIgnoreLookup();
        var previousSelection = SelectedEntry?.DisplayName;

        _allEntries.Clear();
        int scannedCount = 0;
        int noteTotal = 0;
        int ignoredTotal = 0;
        DateTime? newestScan = null;
        foreach (var (displayName, result) in results)
        {
            scannedCount++;
            if (newestScan == null || result.ScanTimeUtc > newestScan) newestScan = result.ScanTimeUtc;
            if (result.Issues.Count == 0) continue;

            var vm = _modsViewModel.AllModSettings.FirstOrDefault(m =>
                m.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase));
            if (vm == null) continue; // Mod removed since the scan.

            var visible = FilterVisible(displayName, result.Issues, ref noteTotal, ref ignoredTotal);
            if (visible.Count == 0) continue; // everything filtered out (notes/ignored)

            _allEntries.Add(new VM_ModIssueEntry(displayName, vm, result, visible, _modsViewModel));
        }
        _allEntries.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        ShowNotesLabel = noteTotal > 0 || ShowNotes ? $"Show notes ({noteTotal})" : "Show notes";
        ShowIgnoredLabel = ignoredTotal > 0 || ShowIgnored ? $"Show ignored ({ignoredTotal})" : "Show ignored";

        // Eligible mods absent from the results: absence must not read as clean.
        UnscannedModNames.Clear();
        foreach (var name in _settings.ModSettings
                     .Where(ModIssueScanner.IsEligible)
                     .Select(m => m.DisplayName)
                     .Where(nm => !results.ContainsKey(nm))
                     .OrderBy(nm => nm, StringComparer.OrdinalIgnoreCase))
        {
            UnscannedModNames.Add(name);
        }

        LastScanText = newestScan.HasValue ? $"Last scan: {newestScan.Value.ToLocalTime():g}" : string.Empty;
        StatusSummaryText = scannedCount == 0
            ? "No scan results yet — press Scan."
            : _allEntries.Count == 0
                ? $"Scan complete — no issues found in {scannedCount} scanned mod{(scannedCount == 1 ? "" : "s")}."
                : $"{_allEntries.Count} of {scannedCount} scanned mods have issues.";
        if (UnscannedModNames.Count > 0 && scannedCount > 0)
        {
            StatusSummaryText += $" · {UnscannedModNames.Count} eligible mod{(UnscannedModNames.Count == 1 ? "" : "s")} not yet scanned (see below).";
        }

        ApplyFilters();

        if (previousSelection != null)
        {
            SelectedEntry = FilteredModEntries.FirstOrDefault(e =>
                e.DisplayName.Equals(previousSelection, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The severity/ignore display filter: drops ignored issues (unless
    /// Show ignored) and Note-severity issues (unless Show notes), while
    /// counting both totals for the toggle labels.</summary>
    private List<ModIssue> FilterVisible(string modName, List<ModIssue> issues,
        ref int noteTotal, ref int ignoredTotal)
    {
        var visible = new List<ModIssue>(issues.Count);
        foreach (var issue in issues)
        {
            if (IsIgnored(modName, issue))
            {
                ignoredTotal++;
                if (!ShowIgnored) continue;
            }
            else if (issue.Severity == ModIssueSeverity.Note)
            {
                noteTotal++;
                if (!ShowNotes) continue;
            }
            visible.Add(issue);
        }
        return visible;
    }

    private void RefreshIgnoreLookup()
    {
        // Global entries live under the "*" bucket and are consulted for every mod.
        _ignoreLookup = _settings.ModIssuesIgnored
            .GroupBy(e => e.AllMods ? "*" : e.ModName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    private bool IsIgnored(string modName, ModIssue issue)
        => (_ignoreLookup.TryGetValue(modName, out var entries) &&
            entries.Any(e => e.Matches(modName, issue))) ||
           (_ignoreLookup.TryGetValue("*", out var globals) &&
            globals.Any(e => e.Matches(modName, issue)));

    private void ApplyIgnore(IssueRow? row, bool exact, bool allMods = false)
    {
        var entry = SelectedEntry;
        if (row == null || entry == null || row.Issues.Count == 0) return;

        if (!exact)
        {
            // File scope: one entry per (type, path) — covers every NPC (and, with
            // allMods, every mod).
            var first = row.Issues[0];
            var candidate = new ModIssueIgnoreEntry
            {
                ModName = entry.DisplayName,
                AllMods = allMods,
                Type = first.Type,
                AffectedPath = first.AffectedPath,
            };
            if (!_settings.ModIssuesIgnored.Any(e =>
                    e.NpcFormKey.IsNull &&
                    e.AllMods == allMods &&
                    (allMods || e.ModName.Equals(candidate.ModName, StringComparison.OrdinalIgnoreCase)) &&
                    e.Type == candidate.Type &&
                    e.AffectedPath.Equals(candidate.AffectedPath, StringComparison.OrdinalIgnoreCase)))
            {
                _settings.ModIssuesIgnored.Add(candidate);
            }
        }
        else
        {
            foreach (var issue in row.Issues)
            {
                if (IsIgnored(entry.DisplayName, issue)) continue;
                _settings.ModIssuesIgnored.Add(new ModIssueIgnoreEntry
                {
                    ModName = entry.DisplayName,
                    Type = issue.Type,
                    AffectedPath = issue.AffectedPath,
                    NpcFormKey = issue.NpcFormKey,
                    NifPath = issue.NifPath,
                    ShapeName = issue.ShapeName,
                });
            }
        }

        RebuildEntries(_lastResults);
    }

    private void Unignore(IssueRow? row)
    {
        var entry = SelectedEntry;
        if (row == null || entry == null || row.Issues.Count == 0) return;

        _settings.ModIssuesIgnored.RemoveAll(e =>
            row.Issues.Any(i => e.Matches(entry.DisplayName, i)));

        RebuildEntries(_lastResults);
    }

    private enum CopyField { Name, EditorId, FormId, FormKey }

    /// <summary>Copies one identity field of the row's NPC to the clipboard.
    /// Single-NPC rows only (grouped rows carry no single FormKey). EditorID is
    /// resolved lazily from the live link cache rather than stored in the scan
    /// cache; FormID is the runtime load-order ID via the same index mapping the
    /// spawn-bat generator uses (ESL-aware).</summary>
    private void CopyNpcField(IssueRow? row, CopyField field)
    {
        if (row == null || string.IsNullOrEmpty(row.NpcFormKey)) return;
        if (!FormKey.TryFactory(row.NpcFormKey, out var fk)) return;

        string? text = null;
        switch (field)
        {
            case CopyField.Name:
                text = row.NpcDisplayName;
                break;

            case CopyField.FormKey:
                text = fk.ToString();
                break;

            case CopyField.EditorId:
                if (_environmentStateProvider.LinkCache?.TryResolve<INpcGetter>(fk, out var npc) == true)
                {
                    text = npc?.EditorID;
                }
                break;

            case CopyField.FormId:
                var indices = PatchVerifyRunner.BuildLoadOrderIndices(
                    _environmentStateProvider, new System.Text.StringBuilder());
                if (indices.TryGetValue(fk.ModKey, out var idx))
                {
                    text = idx.IsLight
                        ? $"FE{idx.Index:X3}{fk.ID & 0xFFF:X3}"
                        : $"{idx.Index:X2}{fk.ID & 0xFFFFFF:X6}";
                }
                break;
        }

        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CopyNpcField clipboard error: {ex.Message}");
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
            int noteTotal = 0, ignoredTotal = 0;
            var visible = vm != null
                ? FilterVisible(displayName, result.Issues, ref noteTotal, ref ignoredTotal)
                : new List<ModIssue>();
            if (vm != null && visible.Count > 0)
            {
                newEntry = new VM_ModIssueEntry(displayName, vm, result, visible, _modsViewModel);

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
            !entry.VisibleIssues.Any(i =>
                (i.NpcDisplayName?.Contains(NpcSearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                i.NpcFormKey.ToString().Contains(NpcSearchText, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (SelectedIssueTypeFilter?.Value is { } type && !entry.CountsByType.ContainsKey(type))
        {
            return false;
        }

        if (!IncludeOutfitOnlyIssues && entry.VisibleIssues.All(i => i.IsOutfitIssue))
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
                    // Multi-plugin mods: the caption's top line names the plugin(s)
                    // whose record verdicts hit this NPC (empty = line hidden).
                    vm.ScanRecordPluginText = string.Join(" · ", issues
                        .Select(i => i.RecordPluginName)
                        .Where(p => !string.IsNullOrEmpty(p))
                        .Distinct(StringComparer.OrdinalIgnoreCase));
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
    /// selected mod's visible issues, honoring the issue-type filter, the
    /// outfit toggle, the per-NPC tile filter, and the grouped/flat toggle.
    /// UI thread.</summary>
    private void RebuildIssueTable()
    {
        IssueTableRows.Clear();

        var entry = SelectedEntry;
        if (entry == null)
        {
            IssueTableHeaderText = string.Empty;
            OutfitHintText = string.Empty;
            return;
        }

        var typeFilter = SelectedIssueTypeFilter?.Value;
        IEnumerable<ModIssue> filtered = entry.VisibleIssues;
        if (typeFilter != null) filtered = filtered.Where(i => i.Type == typeFilter);
        if (!IncludeOutfitOnlyIssues) filtered = filtered.Where(i => !i.IsOutfitIssue);
        if (TableNpcFilter is { } npcFilter) filtered = filtered.Where(i => i.NpcFormKey.Equals(npcFilter));
        var issues = filtered.ToList();

        // Grouped mode collapses to one row per distinct problem; a tile filter
        // means "show me THIS NPC's rows", so it always renders flat.
        bool grouped = GroupByFile && TableNpcFilter == null;
        string? filteredNpcName = null;
        int distinctProblems = 0;

        IssueRow MakeRow(ModIssue issue, IReadOnlyList<ModIssue> groupIssues, int npcCount)
        {
            string location = issue.ShapeName != null
                ? $"{issue.ShapeName} in {Path.GetFileName(issue.NifPath ?? string.Empty)}"
                : issue.NifPath ?? string.Empty;
            bool ignored = IsIgnored(entry.DisplayName, issue);
            string npcDisplay;
            string detail;
            if (npcCount > 1)
            {
                npcDisplay = $"{npcCount} NPCs";

                static string Sample(IEnumerable<ModIssue> source)
                {
                    var names = source
                        .Select(i => i.NpcDisplayName ?? i.NpcFormKey.ToString())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(nm => nm, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var sample = string.Join(", ", names.Take(8));
                    if (names.Count > 8) sample += $", … +{names.Count - 8} more";
                    return sample;
                }

                // A FaceGen path belongs to exactly ONE NPC — the appearance terminus whose
                // FormKey is encoded in the filename — so several NPCs on one such path are
                // by construction a Traits-template group. Say so, instead of a flat name
                // list that reads like 14 independent problems.
                var templateFk = TryParseFaceGenSubjectKey(issue.AffectedPath);
                var ownerIssue = templateFk is { } tfk
                    ? groupIssues.FirstOrDefault(i => i.NpcFormKey.Equals(tfk))
                    : null;
                if (templateFk is { } templateKey && ownerIssue != null)
                {
                    var users = groupIssues.Where(i => !i.NpcFormKey.Equals(templateKey)).ToList();
                    detail = $"Affects {ownerIssue.NpcDisplayName ?? templateKey.ToString()} ({templateKey}) " +
                             $"and the following {npcCount - 1} NPCs that use it as an appearance template: {Sample(users)}";
                }
                else if (templateFk is { } sharedKey)
                {
                    // The template itself is not among this mod's NPCs; the members all inherit
                    // from it.
                    detail = $"Affects {npcCount} NPCs that share the appearance template {sharedKey}: {Sample(groupIssues)}";
                }
                else
                {
                    detail = $"Affects {npcCount} NPCs: {Sample(groupIssues)}";
                }
                if (!string.IsNullOrEmpty(issue.Detail)) detail += "\n" + issue.Detail;
            }
            else
            {
                npcDisplay = issue.NpcFormKey.IsNull
                    ? "(mod-level)"
                    : issue.NpcDisplayName ?? issue.NpcFormKey.ToString();
                detail = issue.Detail ?? string.Empty;
            }

            return new IssueRow(
                npcDisplay,
                npcCount > 1 || issue.NpcFormKey.IsNull ? string.Empty : issue.NpcFormKey.ToString(),
                issue.IsOutfitIssue ? "Outfit" : "NPC",
                VM_ModIssueEntry.GetIssueTypeDisplayName(issue.Type),
                issue.RecordPluginName ?? string.Empty,
                issue.AffectedPath,
                npcCount > 1 && groupIssues.Select(i => i.NifPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
                    ? $"{groupIssues.Select(i => i.NifPath).Distinct(StringComparer.OrdinalIgnoreCase).Count()} locations"
                    : location,
                issue.ReferencingRecord ?? string.Empty,
                issue.SourceModName ?? string.Empty,
                detail,
                npcCount,
                issue.Severity == ModIssueSeverity.Note,
                ignored,
                groupIssues);
        }

        if (grouped)
        {
            var groups = issues
                .GroupBy(i => (i.Severity, i.Type, i.IsOutfitIssue, Path: i.AffectedPath.ToLowerInvariant(),
                    Source: i.SourceModName ?? string.Empty, Plugin: i.RecordPluginName ?? string.Empty))
                // (grouping key includes the path, so a template group's FaceGen file
                // collapses to one row — MakeRow decodes the terminus FormKey from it —
                // and the record plugin, so per-plugin verdicts with differing Details
                // never merge into one row)
                .OrderBy(g => g.Key.Severity)
                .ThenBy(g => g.Key.Type)
                .ThenBy(g => g.Key.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            distinctProblems = groups.Count;
            foreach (var g in groups)
            {
                var members = g.ToList();
                int npcCount = members
                    .Select(i => i.NpcFormKey)
                    .Where(fk => !fk.IsNull)
                    .Distinct()
                    .Count();
                IssueTableRows.Add(MakeRow(members[0], members, Math.Max(npcCount, 1)));
            }
        }
        else
        {
            foreach (var issue in issues
                         .OrderBy(i => i.Severity)
                         .ThenBy(i => i.NpcDisplayName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(i => i.Type)
                         .ThenBy(i => i.AffectedPath, StringComparer.OrdinalIgnoreCase))
            {
                filteredNpcName ??= issue.NpcDisplayName;
                IssueTableRows.Add(MakeRow(issue, new[] { issue }, 1));
            }
        }

        OutfitHintText = issues.Any(i => i.IsOutfitIssue)
            ? "Outfit issues usually come from the outfit/armor mod (see 'Provided by'), not from this appearance replacer."
            : string.Empty;

        int issueCount = issues.Count;
        IssueTableHeaderText = TableNpcFilter != null
            ? $"{issueCount} issue{(issueCount == 1 ? "" : "s")} for {filteredNpcName ?? "selected NPC"} — click the tile again to show the whole mod"
            : grouped
                ? $"{distinctProblems} distinct problem{(distinctProblems == 1 ? "" : "s")} ({issueCount} issue{(issueCount == 1 ? "" : "s")}) in {entry.DisplayName} — click a mugshot to filter to that NPC"
                : $"{issueCount} issue{(issueCount == 1 ? "" : "s")} in {entry.DisplayName} — click a mugshot to filter to that NPC";
    }

    /// <summary>Decodes the NPC FormKey a FaceGen asset path is keyed by
    /// (…facegeom\&lt;plugin&gt;\&lt;formid8&gt;.nif or …facetint\…\&lt;formid8&gt;.dds — the
    /// inverse of <see cref="Auxilliary.GetFaceGenSubPathStrings"/>). Null for any
    /// non-FaceGen path, so callers can use it as a "is this a per-NPC FaceGen
    /// file" test too.</summary>
    internal static FormKey? TryParseFaceGenSubjectKey(string? affectedPath)
    {
        if (string.IsNullOrWhiteSpace(affectedPath)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(affectedPath,
            @"facegendata[\\/](?:facegeom|facetint)[\\/]([^\\/]+)[\\/]([0-9a-fA-F]{8})\.(?:nif|dds)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        if (!ModKey.TryFromNameAndExtension(m.Groups[1].Value, out var modKey)) return null;
        if (!uint.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.HexNumber, null, out var id))
            return null;
        // FaceGen filenames zero-pad the local FormID to 8 digits; mask off anything
        // above the 6-digit record ID (injected-record files carry a 00 prefix).
        return new FormKey(modKey, id & 0xFFFFFF);
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
            // New columns appended at the END so existing spreadsheets/pivots
            // built on the original schema keep working.
            writer.WriteLine("Mod,Category,IssueType,NpcFormKey,NpcName,AffectedPath,Shape,Nif,Referencer,Detail,Severity,ProvidedBy,Ignored,RecordPlugin");
            foreach (var (modName, result) in _lastResults.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var issue in result.Issues)
                {
                    bool ignored = IsIgnored(modName, issue);
                    if (ignored && !ShowIgnored) continue; // export mirrors the display rule

                    writer.WriteLine(string.Join(",", new[]
                    {
                        Csv(modName),
                        issue.IsOutfitIssue ? "Outfit" : "NPC",
                        Csv(VM_ModIssueEntry.GetIssueTypeDisplayName(issue.Type)),
                        Csv(issue.NpcFormKey.IsNull ? "" : issue.NpcFormKey.ToString()),
                        Csv(issue.NpcDisplayName ?? ""),
                        Csv(issue.AffectedPath),
                        Csv(issue.ShapeName ?? ""),
                        Csv(issue.NifPath ?? ""),
                        Csv(issue.ReferencingRecord ?? ""),
                        Csv(issue.Detail ?? ""),
                        issue.Severity == ModIssueSeverity.Note ? "Note" : "Issue",
                        Csv(issue.SourceModName ?? ""),
                        ignored ? "yes" : "",
                        Csv(issue.RecordPluginName ?? ""),
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
