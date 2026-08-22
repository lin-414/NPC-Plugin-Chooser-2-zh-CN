using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Cross-references game-relative asset paths (and archive file names) against the
/// top-level mod folders of the user's parent Mods folder (<c>Settings.ModsFolder</c>,
/// the mod manager's physical store) to name which installed mod(s) supply them.
///
/// <para>Under a mod manager the game's Data folder is a merged (virtual) view, so an
/// asset that resolves "from the data folder" cannot be attributed by its resolved
/// disk path — through the VFS everything appears to live in Data. The physical store
/// can: a loose asset is supplied by every mod folder shipping the same relative path
/// (the manager's profile order decides the winner, which this app cannot see), and an
/// archive-supplied asset by whichever mod folder ships the archive file at its root.</para>
///
/// <para>Consumers are the Mod Issues scanner and "Validate Output" — surfaces without
/// the mugshot pipeline's per-render time budget, which is why they can afford this
/// sweep where the tile badge only lists paths. Construct one instance per run: the
/// top-level directory list is snapshotted lazily on first use, and every query is
/// memoized, so the same hair texture referenced by a hundred NPCs costs one directory
/// sweep total. Thread-safe.</para>
/// </summary>
public sealed class ModsFolderAssetLocator
{
    private readonly string? _modsFolder;
    private readonly Lazy<IReadOnlyList<string>> _modDirs;
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _looseMemo =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _archiveMemo =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyList<string> Empty = Array.Empty<string>();

    public ModsFolderAssetLocator(string? modsFolder)
    {
        _modsFolder = string.IsNullOrWhiteSpace(modsFolder) ? null : modsFolder;
        _modDirs = new Lazy<IReadOnlyList<string>>(EnumerateModDirs,
            System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>False when no Mods folder is configured or it doesn't exist on disk —
    /// every query then returns empty, and callers should omit attribution rather
    /// than claim "no mod supplies this".</summary>
    public bool IsAvailable
    {
        get
        {
            try { return _modsFolder != null && Directory.Exists(_modsFolder); }
            catch { return false; }
        }
    }

    private IReadOnlyList<string> EnumerateModDirs()
    {
        try
        {
            if (!IsAvailable) return Empty;
            // Sorted so provider lists are deterministic across runs (raw enumeration
            // order is filesystem-dependent).
            return Directory.EnumerateDirectories(_modsFolder!)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Empty;
        }
    }

    /// <summary>Names (top-level folder names, not paths) of every mod folder that
    /// ships <paramref name="relativePath"/> as a loose file. Empty when none does,
    /// when the path is unusable, or when the Mods folder is unavailable.</summary>
    public IReadOnlyList<string> FindLooseProviders(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return Empty;
        string rel = relativePath.Replace('/', '\\').TrimStart('\\');
        return _looseMemo.GetOrAdd(rel, key =>
        {
            var dirs = _modDirs.Value;
            if (dirs.Count == 0) return Empty;
            List<string>? matches = null;
            foreach (var dir in dirs)
            {
                try
                {
                    if (File.Exists(Path.Combine(dir, key)))
                    {
                        (matches ??= new List<string>()).Add(Path.GetFileName(dir));
                    }
                }
                catch
                {
                    // A path with invalid characters (junk NIF strings) or an
                    // unreadable folder just doesn't attribute.
                }
            }
            return matches ?? Empty;
        });
    }

    /// <summary>Names of every mod folder shipping <paramref name="archiveFileName"/>
    /// (a bare .bsa file name) at its root — where the game (and the mod manager's
    /// VFS) picks archives up from. Empty when none does or the Mods folder is
    /// unavailable.</summary>
    public IReadOnlyList<string> FindArchiveProviders(string? archiveFileName)
    {
        if (string.IsNullOrWhiteSpace(archiveFileName)) return Empty;
        // Guard against full paths sneaking in — only the file name is meaningful
        // across machines/folders.
        string name;
        try { name = Path.GetFileName(archiveFileName); }
        catch { return Empty; }
        if (string.IsNullOrWhiteSpace(name)) return Empty;
        return _archiveMemo.GetOrAdd(name, key =>
        {
            var dirs = _modDirs.Value;
            if (dirs.Count == 0) return Empty;
            List<string>? matches = null;
            foreach (var dir in dirs)
            {
                try
                {
                    if (File.Exists(Path.Combine(dir, key)))
                    {
                        (matches ??= new List<string>()).Add(Path.GetFileName(dir));
                    }
                }
                catch
                {
                    // Same rationale as the loose sweep.
                }
            }
            return matches ?? Empty;
        });
    }

    /// <summary>Renders a provider list for prose: <c>'A'</c>, <c>'A' or 'B'</c>
    /// (multiple candidates mean the manager's profile order picks the winner —
    /// "or", not "and"), sampling past <paramref name="max"/> entries.</summary>
    public static string FormatProviderList(IReadOnlyList<string> providers, int max = 4)
    {
        if (providers.Count == 0) return string.Empty;
        if (providers.Count == 1) return $"'{providers[0]}'";
        var shown = providers.Take(max).Select(p => $"'{p}'").ToList();
        string joined = string.Join(", ", shown.Take(shown.Count - 1)) + " or " + shown[^1];
        int remaining = providers.Count - shown.Count;
        return remaining > 0 ? $"{joined} (+{remaining} more)" : joined;
    }
}
