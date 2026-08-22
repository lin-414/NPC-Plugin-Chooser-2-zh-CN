using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Names the installed mod(s) supplying a mugshot tile's data-folder-fallback
/// assets — the blue badge's paths — the same way the Mod Issues scan's
/// out-of-scope rows do, but at DISPLAY time: the PNG stamp stays paths-only
/// (provider names would go stale inside metadata, and the render pipeline
/// keeps its time budget), so the tooltip attributes freshly against the
/// current setup whenever a tile loads.
///
/// <para>Resolution mirrors the scanner/patcher rule: a loose file in any
/// Mods-folder mod wins the attribution; otherwise the winning ENABLED
/// archive (<see cref="BsaHandler.LocateWinningEnabledArchive"/> — later
/// plugin wins, the game's own rule) names the archive, and the mod folder(s)
/// shipping that archive file are the suppliers. The archive index is ensured
/// lazily once per environment (usually a no-op: the startup render pre-warm
/// already widened it to the enabled load order).</para>
///
/// <para>Thread-safety: <see cref="GetProviders"/> is meant for background
/// threads (first calls sweep the Mods folder / may index archives); results
/// memoize per path, so the same shared hair texture across a hundred tiles
/// costs one lookup. The memo and index latch reset when the Mods folder
/// setting changes or the game environment rebuilds.</para>
/// </summary>
public sealed class DataFolderAssetAttributor
{
    private readonly Settings _settings;
    private readonly BsaHandler _bsaHandler;
    private readonly EnvironmentStateProvider _env;

    private readonly object _stateLock = new();
    private ModsFolderAssetLocator _locator = new(null);
    private string _locatorKey = string.Empty;
    private bool _archivesEnsured;
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _providersByPath =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyList<string> Empty = Array.Empty<string>();

    public DataFolderAssetAttributor(
        Settings settings, BsaHandler bsaHandler, EnvironmentStateProvider env)
    {
        _settings = settings;
        _bsaHandler = bsaHandler;
        _env = env;

        // A rebuilt environment changes both halves of the answer (enabled load
        // order ranks the winning archive; new plugins may need indexing), so
        // drop everything and re-derive lazily. Singleton — subscription lives
        // for the app's life.
        _env.OnEnvironmentUpdated.Subscribe(_ =>
        {
            lock (_stateLock) { _archivesEnsured = false; }
            _providersByPath.Clear();
        });
    }

    /// <summary>Mod folder names supplying <paramref name="relPath"/> (a
    /// data-relative game path from the blue badge's stamp). Empty when no Mods
    /// folder is configured or nothing attributable ships it. Call from a
    /// background thread — first-time lookups touch disk.</summary>
    public IReadOnlyList<string> GetProviders(string relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return Empty;
        string rel = relPath.Replace('/', '\\').TrimStart('\\').Trim();

        var locator = GetCurrentLocator();
        if (!locator.IsAvailable) return Empty; // not memoized: the setting may be filled in later

        return _providersByPath.GetOrAdd(rel, key =>
            ResolveProvidersCore(key, locator, WinningEnabledArchiveFileName));
    }

    /// <summary>The attribution rule, isolated from session state for tests:
    /// loose providers win (the engine reads loose before archives); otherwise
    /// the winning enabled archive's file name maps to the mod folder(s)
    /// shipping it.</summary>
    internal static IReadOnlyList<string> ResolveProvidersCore(
        string relPath, ModsFolderAssetLocator locator, Func<string, string?> winningArchiveFileName)
    {
        var loose = locator.FindLooseProviders(relPath);
        if (loose.Count > 0) return loose;

        string? archiveName;
        try { archiveName = winningArchiveFileName(relPath); }
        catch { archiveName = null; }
        return string.IsNullOrEmpty(archiveName) ? Empty : locator.FindArchiveProviders(archiveName);
    }

    private ModsFolderAssetLocator GetCurrentLocator()
    {
        string key = _settings.ModsFolder ?? string.Empty;
        lock (_stateLock)
        {
            if (!key.Equals(_locatorKey, StringComparison.OrdinalIgnoreCase))
            {
                _locator = new ModsFolderAssetLocator(key);
                _locatorKey = key;
                _providersByPath.Clear();
            }
            return _locator;
        }
    }

    private string? WinningEnabledArchiveFileName(string relPath)
    {
        EnsureEnabledArchivesIndexed();
        var winner = _bsaHandler.LocateWinningEnabledArchive(relPath, GetEnabledLoadOrderKeysAscending());
        return winner == null ? null : Path.GetFileName(winner.Value.BsaPath);
    }

    private IReadOnlyList<ModKey> GetEnabledLoadOrderKeysAscending()
    {
        var loadOrder = _env.LoadOrder;
        return loadOrder == null
            ? Array.Empty<ModKey>()
            : loadOrder.ListedOrder.Where(l => l.Enabled).Select(l => l.ModKey).ToList();
    }

    /// <summary>AssetHandler's <c>EnsureLoadOrderArchivesIndexedForRun</c> pattern:
    /// index the enabled load order's data-folder archives once per environment so
    /// <see cref="BsaHandler.LocateWinningEnabledArchive"/> can rank them. Usually a
    /// no-op — the startup render pre-warm widens the same index — and
    /// <see cref="BsaHandler.CacheContainsModKey"/> filters already-indexed keys
    /// either way.</summary>
    private void EnsureEnabledArchivesIndexed()
    {
        if (_archivesEnsured) return;
        lock (_stateLock)
        {
            if (_archivesEnsured) return;
            try
            {
                var enabledKeys = GetEnabledLoadOrderKeysAscending();
                if (enabledKeys.Count == 0) return; // environment unresolved — retry later
                var toIndex = enabledKeys
                    .Where(k => !_env.BaseGamePlugins.Contains(k))
                    .Where(k => !_env.CreationClubPlugins.Contains(k))
                    .Where(k => !_bsaHandler.CacheContainsModKey(k))
                    .ToList();
                if (toIndex.Count > 0)
                {
                    _bsaHandler.EnsureDataFolderArchivesIndexed(toIndex, _env.SkyrimVersion.ToGameRelease());
                }
                _archivesEnsured = true;
            }
            catch
            {
                // Attribution is a nicety; a failed index attempt must never
                // break tooltips. Leave the latch unset so a later call retries.
            }
        }
    }

    /// <summary>Composes the blue badge's tooltip: the user-approved header + one
    /// line per asset, suffixed " [from X, Y]" when <paramref name="providersByPath"/>
    /// names supplier(s) for that path (same bracket convention as the scan
    /// tooltip's "[from …]"). Pass null providers for the immediate, paths-only
    /// text; the async enrichment re-composes with the lookups filled in. Shared
    /// by both mugshot tile VMs so the wording cannot drift. Internal for tests.</summary>
    internal static string ComposeNoticeText(
        string modDisplayName,
        IReadOnlyList<string> paths,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? providersByPath)
    {
        var sb = new StringBuilder();
        sb.Append("The following assets were loaded from your data folder because they were not found in this mod's Corresponding Mod Folders. Whichever mod these assets come from must stay activated, or else that mod needs to be added to ")
          .Append(modDisplayName)
          .Append("'s Corresponding Mod Folders:");
        foreach (var p in paths)
        {
            sb.Append('\n').Append(p);
            if (providersByPath != null && providersByPath.TryGetValue(p, out var providers) &&
                providers.Count > 0)
            {
                sb.Append("  [from ")
                  .Append(string.Join(", ", providers.Take(3)))
                  .Append(providers.Count > 3 ? ", …]" : "]");
            }
        }
        return sb.ToString();
    }
}
