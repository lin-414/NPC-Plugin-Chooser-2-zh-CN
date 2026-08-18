using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd
{
    /// <summary>
    /// Disk-persisted cache of Mod Issues scan results, one entry per mod keyed
    /// by DisplayName, written to ModIssuesCache.json next to the exe (the
    /// RaceEvalCache.json convention — deliberately NOT Settings.json, which is
    /// large and saved on a different cadence). An entry is a valid hit only
    /// when the file-format version matches, the mod's
    /// <see cref="ModStateSnapshot"/> compares equal, the general loose-asset
    /// tree aggregates compare equal, and the scan ran to completion.
    /// </summary>
    public sealed class ModIssuesCache
    {
        public const string CacheFileName = "ModIssuesCache.json";

        private readonly object _gate = new();
        private ModIssuesCacheFile _data = new();
        private bool _loaded;

        public string CacheFilePath { get; }

        public ModIssuesCache()
        {
            CacheFilePath = Path.Combine(AppContext.BaseDirectory, CacheFileName);
        }

        /// <summary>Test seam.</summary>
        public ModIssuesCache(string cacheFilePath)
        {
            CacheFilePath = cacheFilePath;
        }

        /// <summary>Loads the cache file if present. Idempotent; a version
        /// mismatch or unreadable file yields an empty cache.</summary>
        public void Load()
        {
            lock (_gate)
            {
                if (_loaded) return;
                _loaded = true;
                if (!File.Exists(CacheFilePath)) return;

                var loaded = JSONhandler<ModIssuesCacheFile>.LoadJSONFile(
                    CacheFilePath, out bool success, out _);
                if (success && loaded != null && loaded.Version == ModIssuesCacheFile.CurrentVersion)
                {
                    _data = loaded;
                }
            }
        }

        /// <summary>All raw entries (valid or stale) — used to populate the tab on
        /// open, before any scan. Returns a snapshot copy of the dictionary.</summary>
        public IReadOnlyDictionary<string, ModIssueScanResult> GetAllRaw()
        {
            lock (_gate)
            {
                return new Dictionary<string, ModIssueScanResult>(_data.Mods, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Returns true (and the cached result) when the entry for
        /// <paramref name="displayName"/> is still valid against the mod's current
        /// on-disk state.
        /// </summary>
        public bool TryGetValid(string displayName, ModStateSnapshot? currentSnapshot,
            IReadOnlyList<LooseAssetTreeSnapshot> currentTrees, out ModIssueScanResult result)
        {
            lock (_gate)
            {
                result = null!;
                if (!_data.Mods.TryGetValue(displayName, out var entry)) return false;
                if (!IsEntryValid(entry, currentSnapshot, currentTrees)) return false;
                result = entry;
                return true;
            }
        }

        /// <summary>Pure validity check, exposed for tests and for the tab's
        /// stale-marking pass (which wants the entry regardless).</summary>
        public static bool IsEntryValid(ModIssueScanResult entry, ModStateSnapshot? currentSnapshot,
            IReadOnlyList<LooseAssetTreeSnapshot> currentTrees)
        {
            if (!entry.ScanCompleted) return false;

            // Snapshot presence must agree: a mod whose snapshot can't be produced
            // (folders gone) never validates against an entry that had one, and
            // vice versa.
            if (entry.Snapshot == null != (currentSnapshot == null)) return false;
            if (entry.Snapshot != null && !entry.Snapshot.Equals(currentSnapshot)) return false;

            if (entry.LooseAssetTrees.Count != currentTrees.Count) return false;
            // Order-insensitive: tree lists are built from folder enumeration whose
            // order is stable in practice, but don't depend on it.
            foreach (var tree in entry.LooseAssetTrees)
            {
                if (!currentTrees.Any(t => t.Equals(tree))) return false;
            }

            return true;
        }

        public void Store(string displayName, ModIssueScanResult result)
        {
            lock (_gate)
            {
                _data.Mods[displayName] = result;
            }
        }

        /// <summary>Drops entries not in <paramref name="liveDisplayNames"/> (mod removed
        /// from the settings, or no longer scan-eligible — e.g. its folders were deleted).
        /// Returns the number of entries removed so the caller can persist when a prune
        /// was the only change.</summary>
        public int Prune(IEnumerable<string> liveDisplayNames)
        {
            var live = new HashSet<string>(liveDisplayNames, StringComparer.OrdinalIgnoreCase);
            lock (_gate)
            {
                var dead = _data.Mods.Keys.Where(k => !live.Contains(k)).ToList();
                foreach (var key in dead)
                {
                    _data.Mods.Remove(key);
                }
                return dead.Count;
            }
        }

        public Task SaveAsync()
        {
            ModIssuesCacheFile snapshot;
            lock (_gate)
            {
                // Shallow copy is enough: entries are replaced wholesale by Store,
                // never mutated in place after being handed to the cache.
                snapshot = new ModIssuesCacheFile
                {
                    Version = ModIssuesCacheFile.CurrentVersion,
                    Mods = new Dictionary<string, ModIssueScanResult>(_data.Mods, StringComparer.OrdinalIgnoreCase),
                };
            }

            return Task.Run(() =>
            {
                JSONhandler<ModIssuesCacheFile>.SaveJSONFile(snapshot, CacheFilePath, out _, out _);
            });
        }

        /// <summary>
        /// Builds the general loose-asset tree aggregates (meshes\ and textures\
        /// under each mod folder) that widen <see cref="ModStateSnapshot"/> for
        /// invalidation. Missing subtrees contribute no entry, so
        /// installing/removing a whole tree also invalidates (count changes).
        /// </summary>
        public static List<LooseAssetTreeSnapshot> BuildLooseAssetTrees(IEnumerable<string> modFolders)
        {
            var result = new List<LooseAssetTreeSnapshot>();
            foreach (var folder in modFolders)
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                foreach (var sub in new[] { "meshes", "textures" })
                {
                    string root;
                    try { root = Path.Combine(folder, sub); }
                    catch { continue; }
                    if (!Directory.Exists(root)) continue;

                    int count = 0;
                    long bytes = 0;
                    DateTime maxWrite = DateTime.MinValue;
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                        {
                            var info = new FileInfo(file);
                            count++;
                            bytes += info.Length;
                            if (info.LastWriteTimeUtc > maxWrite) maxWrite = info.LastWriteTimeUtc;
                        }
                    }
                    catch
                    {
                        // Tree partially unreadable — record what was seen; the
                        // aggregates still change when the tree changes.
                    }

                    result.Add(new LooseAssetTreeSnapshot
                    {
                        Root = root,
                        FileCount = count,
                        TotalBytes = bytes,
                        MaxLastWriteUtc = maxWrite,
                    });
                }
            }
            return result;
        }
    }
}
