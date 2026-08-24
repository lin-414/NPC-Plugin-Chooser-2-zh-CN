// VM_Validate.cs
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Localization;
using NPC_Plugin_Chooser_2.Views;
using ReactiveUI;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// Backs the Validate tab. Runs <see cref="OutputValidator"/> against the real (untrimmed)
/// deployed load order to confirm the generated output actually wins in-game for each NPC
/// with an appearance selection: the conflict-winning record, the deployed FaceGen .nif,
/// and any SkyPatcher overrides.
/// </summary>
public class VM_Validate : ReactiveObject, IDisposable
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly Settings _model;
    private readonly OutputValidator _outputValidator;
    private readonly Lazy<VM_Run> _lazyRunVm;
    private readonly CompositeDisposable _disposables = new();

    public ReactiveCommand<Unit, Unit> ValidateOutputCommand { get; }

    // Passthrough to VM_Run, which owns the Analyze Masters flow (its narration goes to the
    // Run tab log and it is disabled while a patch run is active). Lazy because VM_Run
    // injects this VM for NotifyPatchRunCompleted — a direct reference would be a ctor cycle.
    public ReactiveCommand<Unit, Unit> AnalyzeMastersCommand => _lazyRunVm.Value.AnalyzeMastersCommand;

    // True once a patch run in THIS session wrote output. Under a mod manager (which is how
    // nearly everyone runs N.P.C.2) the virtual file system was built at launch, so files the
    // run just wrote are invisible to this process — validating against them reports missing
    // FaceGen/plugins/lost conflicts that don't exist in the real game. Set by VM_Run; consumed
    // by the confirmation gate in ValidateOutputAsync.
    private bool _outputWrittenThisSession;

    // Suppresses the gate for repeat clicks after the user has chosen to proceed. Re-armed by
    // the next patch run, so a fresh run always warns again.
    private bool _staleOutputWarningAcknowledged;

    public VM_Validate(
        EnvironmentStateProvider environmentStateProvider,
        Settings settingsModel,
        OutputValidator outputValidator,
        Lazy<VM_Run> lazyRunVm)
    {
        _environmentStateProvider = environmentStateProvider;
        _model = settingsModel;
        _outputValidator = outputValidator;
        _lazyRunVm = lazyRunVm;

        ValidateOutputCommand = ReactiveCommand.CreateFromTask(ValidateOutputAsync).DisposeWith(_disposables);
    }

    /// <summary>
    /// Called by <see cref="VM_Run"/> after any run that reached patching. Arms the
    /// "relaunch before validating" confirmation on the Validate Output button.
    /// </summary>
    public void NotifyPatchRunCompleted()
    {
        _outputWrittenThisSession = true;
        _staleOutputWarningAcknowledged = false;
    }

    // Opens the "choose NPCs" dialog, runs OutputValidator against the real (untrimmed)
    // deployed load order on a background thread with a cancellable progress window, then
    // shows the findings table. See OutputValidator for the three checks performed.
    private async Task ValidateOutputAsync()
    {
        if (_environmentStateProvider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid)
        {
            ScrollableMessageBox.ShowWarning(
                GetTranslation("theGameEnvironmentIs", "The game environment is not valid. Resolve it on the Settings page (a working load order and data folder are required) before validating output."),
                GetTranslation("validateOutput", "Validate Output"));
            return;
        }

        var selections = _model.SelectedAppearanceMods;
        if (selections == null || selections.Count == 0)
        {
            ScrollableMessageBox.ShowWarning(GetTranslation("msg_noAppearanceSelectionsMade", "No appearance selections have been made yet, so there is nothing to validate."), GetTranslation("validateOutput", "Validate Output"));
            return;
        }

        // A patch run in this session wrote output the mod manager's VFS hasn't picked up yet,
        // so the validator would be reading a stale Data folder. Warned about BEFORE the deploy
        // readiness probe below: with a stale VFS that probe is itself unreliable (it can fail
        // to see the just-written plugin and blame the user for not installing it).
        if (_outputWrittenThisSession && !_staleOutputWarningAcknowledged)
        {
            var proceed = ScrollableMessageBox.Confirm(
                "The patcher has been run since N.P.C.2 was launched.\n\n" +
                "If you launched N.P.C.2 through a mod manager (MO2, Vortex, etc.), the mod manager built " +
                "its virtual file system when the app started, so the output this run just wrote is not " +
                "yet visible to N.P.C.2. Validating now can report problems — missing FaceGen, a missing " +
                "output plugin, lost conflicts — that don't actually exist in your game.\n\n" +
                "To validate reliably: close N.P.C.2, make sure the generated output is installed and " +
                "enabled in your mod manager, then relaunch N.P.C.2 through the mod manager and validate.\n\n" +
                "Validate anyway?",
                GetTranslation("validateOutput", "Validate Output"), MessageBoxImage.Warning);

            if (!proceed) return;
            _staleOutputWarningAcknowledged = true;
        }

        // Fail fast: confirm this app's output is actually deployed & active BEFORE the user
        // invests effort picking NPCs in the scope dialog. Building the untrimmed load order
        // can take a moment, so run it off the UI thread (the command's IsExecuting disables
        // the button meanwhile). The same gate still runs inside Validate() as a backstop.
        var readiness = await Task.Run(() => _outputValidator.CheckDeployReadiness());
        if (!readiness.Ok)
        {
            ScrollableMessageBox.ShowWarning(readiness.BlockReason ?? GetTranslation("validationCouldNotRun", "Validation could not run."), GetTranslation("validateOutput", "Validate Output"));
            return;
        }

        var items = BuildValidationScopeItems(selections);

        var scopeVm = new VM_ValidationScopeWindow(items);
        var scopeWindow = new ValidationScopeWindow { DataContext = scopeVm };
        TrySetOwner(scopeWindow);
        bool? scopeResult = scopeWindow.ShowDialog();
        var chosen = scopeVm.GetChosenFormKeys();
        scopeVm.Dispose();

        if (scopeResult != true) return;
        if (chosen.Count == 0)
        {
            ScrollableMessageBox.ShowWarning(GetTranslation("msg_noNpcsSelectedToValidate", "No NPCs were selected to validate."), GetTranslation("validateOutput", "Validate Output"));
            return;
        }

        var progressVm = new VM_ProgressWindow
        {
            Title = GetTranslation("validatingOutput", "Validating Output"),
            StatusMessage = "Preparing...",
            IsIndeterminate = true,
            ProgressMaximum = chosen.Count
        };
        var progressWindow = new ProgressWindow { ViewModel = progressVm };
        TrySetOwner(progressWindow);
        progressWindow.Show();

        using var cts = new CancellationTokenSource();
        using var cancelSub = progressVm.WhenAnyValue(x => x.IsCancellationRequested)
            .Where(requested => requested)
            .Subscribe(_ => { try { cts.Cancel(); } catch { /* already disposed */ } });

        var progress = new Progress<(int current, int total, string message)>(p =>
        {
            if (p.total > 0)
            {
                progressVm.IsIndeterminate = false;
                progressVm.ProgressMaximum = p.total;
                progressVm.ProgressValue = p.current;
            }
            else
            {
                progressVm.IsIndeterminate = true;
            }
            progressVm.StatusMessage = p.message;
        });

        ValidationRunResult? result = null;
        try
        {
            result = await Task.Run(() => _outputValidator.Validate(chosen, progress, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // User cancelled — fall through and close the progress window.
        }
        catch (Exception ex)
        {
            progressWindow.Close();
            progressVm.Dispose();
            ScrollableMessageBox.ShowError(string.Format(GetTranslation("validationFailed", "Validation failed:\n{0}"), ExceptionLogger.GetExceptionStack(ex)), GetTranslation("validateOutput", "Validate Output"));
            return;
        }

        progressWindow.Close();
        progressVm.Dispose();

        if (result == null) return; // cancelled

        if (result.Blocked)
        {
            ScrollableMessageBox.ShowWarning(result.BlockReason ?? GetTranslation("validationCouldNotRun", "Validation could not run."), GetTranslation("validateOutput", "Validate Output"));
            return;
        }

        var resultsVm = new VM_ValidationResultsWindow(result);
        var resultsWindow = new ValidationResultsWindow { DataContext = resultsVm };
        resultsWindow.Closed += (_, _) => resultsVm.Dispose(); // modeless: dispose VM subscriptions on close
        TrySetOwner(resultsWindow);
        resultsWindow.Show();
    }

    private List<VM_ValidationScopeItem> BuildValidationScopeItems(
        Dictionary<FormKey, (string ModName, FormKey NpcFormKey)> selections)
    {
        var items = new List<VM_ValidationScopeItem>(selections.Count);
        var linkCache = _environmentStateProvider.LinkCache;
        foreach (var kvp in selections)
        {
            string displayName;
            if (linkCache != null && linkCache.TryResolve<INpcGetter>(kvp.Key, out var npc) && npc != null)
            {
                displayName = Auxilliary.GetLogString(npc, _model.LocalizationLanguage);
            }
            else
            {
                displayName = kvp.Key.ToString();
            }
            items.Add(new VM_ValidationScopeItem(kvp.Key, displayName, kvp.Value.ModName));
        }
        return items;
    }

    private void TrySetOwner(Window window)
    {
        try
        {
            var mainWindow = Application.Current?.MainWindow;
            if (mainWindow != null && mainWindow != window)
            {
                window.Owner = mainWindow;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not set window owner: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }

    private static string GetTranslation(string key, string fallback) =>
        TranslationServiceProvider.GetService()?.GetString(key) ?? fallback;

}
