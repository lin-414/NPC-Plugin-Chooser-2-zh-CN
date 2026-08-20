// BackEnd/NpcDescriptionProvider.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net; // Required for WebUtility
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions; // Required for Regex\using System.IO;
using System.Threading;
using System.Threading.Tasks;
// using System.Web; // Use System.Net if available
using HtmlAgilityPack;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd
{
    public class NpcDescriptionProvider
    {
        private readonly HttpClient _httpClient;
        private readonly Settings _settings;
        private const string UserAgent = "NPC Plugin Chooser 2 (https://github.com/Piranha91/NPC-Plugin-Chooser-2; piranha9191@example.com)";
        
        private readonly Dictionary<FormKey, string> _overrideDescriptions = new(); // master overrides

        // --- Description cache (memory + disk) ---
        // Reduces wiki scrapes and translation API calls: the fetched English text is
        // always cached; the zh-CN translation is cached alongside it once produced.
        // A hit with a translation returns instantly (no network); a hit with only the
        // English text re-runs just the translation step, not the wiki scrape.
        private readonly Dictionary<string, CachedNpcDescription> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _cacheLock = new();
        private readonly SemaphoreSlim _diskWriteGate = new(1, 1);
        private string _cacheFilePath = "";

        private static readonly HashSet<string> BaseGamePlugins = new(StringComparer.OrdinalIgnoreCase)
        {
            "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"
        };

        // Common articles/words to ignore when validating description
        private static readonly HashSet<string> IgnoredWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "is", "of", "in", "on", "at", "skyrim", "with", "and", "to", "who" // Added a few more
        };

        private enum WikiSite { UESP, Fandom }

        public NpcDescriptionProvider(IHttpClientFactory httpClientFactory, Settings settings)
        {
            _httpClient = httpClientFactory.CreateClient("WikiClient");
            if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0) { _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent); }
            if (_httpClient.Timeout == TimeSpan.FromSeconds(100)) { _httpClient.Timeout = TimeSpan.FromSeconds(30); }
            _settings = settings;
            Initialize();
        }
        
        public void Initialize()
        {
            try
            {
                string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!;
                string overridesDir = Path.Combine(exeDir, "DescriptionOverrides");

                bool newDir = false;
                if (!Directory.Exists(overridesDir))
                {
                    Directory.CreateDirectory(overridesDir);
                    newDir = true;
                }

                // If newly created or empty, seed with example file
                if (newDir || !Directory.GetFiles(overridesDir).Any())
                {
                    string samplePath = Path.Combine(overridesDir, "ExampleOverride.json");
                    var sampleDict = new Dictionary<FormKey, string>
                    {
                        { FormKey.Factory("123456:ModName.esp"), "Description goes here." }
                    };
                    JSONhandler<Dictionary<FormKey, string>>.SaveJSONFile(sampleDict, samplePath, out _, out _);
                }

                // Load all override files into master dictionary
                _overrideDescriptions.Clear();
                foreach (string jsonPath in Directory.EnumerateFiles(overridesDir, "*.json"))
                {
                    var currentDictionary = JSONhandler<Dictionary<FormKey, string>>.LoadJSONFile(jsonPath, out _, out _);
                    if (currentDictionary != null)
                    {
                        foreach (var kvp in currentDictionary)
                        {
                            _overrideDescriptions[kvp.Key] = kvp.Value; // last file wins on duplicate
                        }
                    }
                }
            }
            catch (Exception ex)
                        {
                            Debug.WriteLine($"[DescProvider][Initialize] Error: {ex.Message}");
                        }

                        // Load persisted description cache (previous session's scrapes + translations).
                        try
                        {
                            string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!;
                            string cacheDir = Path.Combine(exeDir, "DescriptionCache");
                            Directory.CreateDirectory(cacheDir);
                            _cacheFilePath = Path.Combine(cacheDir, "descriptions.json");
                            var persisted = JSONhandler<Dictionary<string, CachedNpcDescription>>.LoadJSONFile(_cacheFilePath, out _, out _);
                            if (persisted != null)
                            {
                                lock (_cacheLock)
                                {
                                    foreach (var kvp in persisted)
                                    {
                                        _cache[kvp.Key] = kvp.Value;
                                    }
                                }
                                Debug.WriteLine($"[DescProvider][Init] Loaded {persisted.Count} cached descriptions from disk.");
                            }
                        }
                        catch (Exception ex)
                                    {
                                        Debug.WriteLine($"[DescProvider][Initialize] Cache load failed (non-fatal): {ex.Message}");
                                    }
                                }

                                /// <summary>Whether this NPC is one the description provider will serve (base-game
                                /// NPCs, plus mod NPCs that carry a DescriptionOverrides entry). Used by the
                                /// pre-cache batch to skip NPCs that would just immediately return null.</summary>
                                public bool IsEligibleNpc(FormKey npcFormKey)
                                    => !npcFormKey.IsNull &&
                                       (BaseGamePlugins.Contains(npcFormKey.ModKey.FileName) || _overrideDescriptions.ContainsKey(npcFormKey));

                                public bool HasCachedEn(FormKey npcFormKey)
                                                                {
                                                                    lock (_cacheLock)
                                                                    {
                                                                        return _cache.TryGetValue(npcFormKey.ToString(), out var entry) &&
                                                                               !string.IsNullOrWhiteSpace(entry.En);
                                                                    }
                                                                }

                                                                /// <summary>Whether a zh-CN translation already exists in the cache for this NPC
                                                                /// (memory + disk merged at Initialize). Lets the pre-cache batch count completed
                                                                /// entries without issuing any network request.</summary>
                                                                public bool HasCachedZh(FormKey npcFormKey)
                                                                {
                                                                    lock (_cacheLock)
                                                                    {
                                                                        return _cache.TryGetValue(npcFormKey.ToString(), out var entry) &&
                                                                               !string.IsNullOrWhiteSpace(entry.Zh);
                                                                    }
                                }

        public async Task<string?> GetDescriptionAsync(FormKey npcFormKey, string? displayName, string? editorId, bool forceTranslate = false)
                {
                    // 1. Check conditions. forceTranslate (used by the pre-cache batch pass)
                    // bypasses the ShowNpcDescriptions UI toggle — the batch's whole job is to
                    // populate the cache regardless of what the description panel shows.
                    string? overrideDescription = null;
                    if ((!_settings.ShowNpcDescriptions && !forceTranslate) || npcFormKey.IsNull || 
                        (!BaseGamePlugins.Contains(npcFormKey.ModKey.FileName) && 
                         !_overrideDescriptions.TryGetValue(npcFormKey, out overrideDescription)))
            {
                return null;
            }
            
            if (overrideDescription is not null)
            {
                return overrideDescription; // skip API look-ups if override present
            }

            // 2. Determine base search term raw and keywords for validation
            // Chinese-localization scenario: displayName may be Chinese (e.g. "阿尔瓦克"),
            // which cannot match English wiki pages. Prefer EditorID (always English,
            // e.g. "AelaTheHuntress"), split on CamelCase boundaries for search + validation.
            string? displaySearchTerm = !string.IsNullOrWhiteSpace(displayName) ? displayName.Split('[')[0].Trim() : null;
            string? editorSearchTerm = !string.IsNullOrWhiteSpace(editorId) ? editorId.Split('[')[0].Trim() : null;

            bool displayNameIsAscii = displaySearchTerm != null && displaySearchTerm.All(ch => ch <= 127);

            // Search term: use the ASCII display name when available (wiki page titles use it),
            // otherwise fall back to the (English) EditorID with CamelCase split into words.
            string? searchTermRaw = displayNameIsAscii ? displaySearchTerm
                : editorSearchTerm != null ? SplitCamelCase(editorSearchTerm)
                : displaySearchTerm;
            if (string.IsNullOrWhiteSpace(searchTermRaw)) return null;

            // Validation keywords: union of EditorID words and ASCII display-name words,
            // minus ignored filler words. EditorID words alone are enough in the
            // Chinese-localization case (display name contributes nothing).
            var searchKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (editorSearchTerm != null)
            {
                foreach (string word in SplitCamelCase(editorSearchTerm)
                    .Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => !IgnoredWords.Contains(w)))
                {
                    searchKeywords.Add(word);
                }
            }
            if (displayNameIsAscii && displaySearchTerm != null)
            {
                foreach (string word in displaySearchTerm
                    .Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => !IgnoredWords.Contains(w)))
                {
                    searchKeywords.Add(word);
                }
            }

            if (!searchKeywords.Any()) {
                            Debug.WriteLine($"[DescProvider] No significant keywords found for '{searchTermRaw}'. Cannot validate.");
                            return null; // Cannot validate if no keywords
                        }

                        // 2.5 Cache check: a fully cached entry (English + translation when the UI is
                                    // Chinese) returns instantly with zero network requests. A cache entry with only
                                    // the English text re-runs just the translation step below (see FinalizeAsync).
                                    // When forceTranslate is set (pre-cache batch), a zh-CN entry is a completed
                                    // job (skip); an English-only entry re-runs only the translation step.
                                    string cacheKey = npcFormKey.ToString();
                                    string? cachedEnOnly = null;
                                    lock (_cacheLock)
                                    {
                                        if (_cache.TryGetValue(cacheKey, out var cached))
                                        {
                                            if (forceTranslate)
                                            {
                                                if (!string.IsNullOrEmpty(cached.Zh))
                                                {
                                                    Debug.WriteLine($"[DescProvider] Cache hit (force zh-CN) for '{cacheKey}': skipping.");
                                                    return cached.Zh;
                                                }
                                                cachedEnOnly = cached.En;
                                            }
                                            else
                                            {
                                                bool uiChinese = !string.IsNullOrWhiteSpace(_settings.UiLanguage) &&
                                                                 _settings.UiLanguage.Equals("zh-CN", StringComparison.OrdinalIgnoreCase);
                                                if (uiChinese && !string.IsNullOrEmpty(cached.Zh))
                                                {
                                                    Debug.WriteLine($"[DescProvider] Cache hit (zh-CN) for '{cacheKey}': returning instantly.");
                                                    return cached.Zh;
                                                }
                                                if (!uiChinese && !string.IsNullOrEmpty(cached.En))
                                                {
                                                    Debug.WriteLine($"[DescProvider] Cache hit (en) for '{cacheKey}': returning instantly.");
                                                    return cached.En;
                                                }
                                                cachedEnOnly = cached.En;
                                            }
                                        }
                                    }
                                    if (cachedEnOnly != null)
                                    {
                                        return await FinalizeAsync(cacheKey, cachedEnOnly, forceTranslate);
                                    }

                        Stopwatch sw = Stopwatch.StartNew();

                                                // --- 3+4. Try UESP, then Fandom, with keyword validation (shared with the CSV export path) ---
                                                                                                var rawResult = await FetchRawEnglishAsync(searchTermRaw, searchKeywords);
                                                                                                sw.Stop();
                                                                                                if (rawResult.Description != null)
                                                                                                {
                                                                                                    Debug.WriteLine($"[DescProvider] Raw English fetched for '{searchTermRaw}' ({sw.ElapsedMilliseconds}ms). Finalizing.");
                                                                                                    return await FinalizeAsync(cacheKey, rawResult.Description, forceTranslate);
                                                                                                }

                                                Debug.WriteLine($"[DescProvider] No valid description found for '{searchTermRaw}' after trying both sites.");
                                                return null;
                                }

                                // Shared by GetDescriptionAsync (translate on demand) and GetEnglishDescriptionAsync (CSV
                                                                // export): search UESP first, then Fandom, validate the extracted first sentence against
                                                                // the NPC's keywords, and return the raw ENGLISH text. Returns NetworkError=true
                                                                // when the LAST attempt failed at the transport level (timeout / rate-limit /
                                                                // connection) — the counterpoint to "page simply not found", which the caller
                                                                // reports differently. Both sites get one retry after a 2s pause, since the
                                                                // wikis rate-limit bursts and a single transient failure currently marks ~all
                                                                // NPCs in a batch as failed.
                                                                private async Task<(string? Description, bool NetworkError)> FetchRawEnglishAsync(string searchTermRaw, HashSet<string> searchKeywords)
                                                                {
                                                                    Stopwatch sw = Stopwatch.StartNew();
                                                                    string? finalDescription = null;
                                                                    bool lastAttemptWasNetwork = true; // default: classify total failure as network if neither site was reached cleanly

                                                                    // --- 3. Try UESP First (max 3 attempts: network failures retry with backoff 3s then 10s) ---
                                                                                                                                                                                                            string uespSearchTerm = $"Skyrim:{searchTermRaw}";
                                                                                                                                                                                                            string encodedUespSearchTerm = WebUtility.UrlEncode(uespSearchTerm);
                                                                                                                                                                                                            Debug.WriteLine($"[DescProvider] Attempting UESP for: \"{uespSearchTerm}\"");
                                                                                                                                                                                                            for (int attempt = 0; attempt < 2 && finalDescription == null; attempt++)
                                                                                                                                                                                                            {
                                                                                                                                                                                                                if (attempt > 0)
                                                                                                                                                                                                                {
                                                                                                                                                                                                                    Debug.WriteLine($"[DescProvider] UESP network failure — retrying in {(attempt == 1 ? 2 : 5)}s (attempt {attempt + 1}/2).");
                                                                                                                                                                                                                    await Task.Delay(attempt == 1 ? 2000 : 5000).ConfigureAwait(false);
                                                                                                                                                                                                                }
                                                                        bool uespBlocked = false; // no point retrying when the query failed, not the network
                                                                        try
                                                                        {
                                                                            var uespSearch = await SearchWikiAsync($"https://en.uesp.net/w/api.php?action=query&list=search&srsearch={encodedUespSearchTerm}&format=json&srlimit=1", "https://en.uesp.net/wiki/");
                                                                                                                                                        if (uespSearch.NetworkError) { lastAttemptWasNetwork = true; continue; } // transient — retry
                                                                                                                                                        lastAttemptWasNetwork = false;
                                                                                                                                                        string? uespUrl = uespSearch.Url;
                                                                                                                                                        if (!string.IsNullOrEmpty(uespUrl))
                                                                                                                                                        {
                                    Debug.WriteLine($"[DescProvider] Found UESP URL: {uespUrl}");
                                                                            // Try the MediaWiki extracts API first — UESP pages are
                                                                            // behind Cloudflare, so direct HTML fetching often returns a
                                                                            // "Just a moment..." Challenge page instead of the real content.
                                                                            // The extracts API endpoint is whitelisted and returns plain text.
                                                                            string uespPageTitle = uespUrl.Replace("https://en.uesp.net/wiki/", "").Replace("_", " ");
                                                                            var uespExtract = await FetchExtractViaApiAsync(uespPageTitle, WikiSite.UESP);
                                                                            string? rawUespDesc = uespExtract.Description;
                                                                            if (uespExtract.NetworkError) { lastAttemptWasNetwork = true; }
                                                                            // Fall back to the rendered page if the extracts API gave nothing.
                                                                            if (string.IsNullOrWhiteSpace(rawUespDesc))
                                                                            {
                                                                                var uespFetch = await FetchAndParseDescriptionAsync(uespUrl, WikiSite.UESP);
                                                                                if (uespFetch.NetworkError) { lastAttemptWasNetwork = true; continue; } // transient — retry
                                                                                rawUespDesc = uespFetch.Description;
                                                                            }
                                                                            if (ValidateDescription(rawUespDesc, searchKeywords)) // Validate before assigning
                                                                                {
                                                                                    finalDescription = rawUespDesc; // Assign if valid
                                                                                    Debug.WriteLine($"[DescProvider] Success: Valid UESP description found ({sw.ElapsedMilliseconds}ms).");
                                                                                }
                                                                                else if (rawUespDesc != null)
                                                                                {
                                                                                    Debug.WriteLine($"[DescProvider] UESP description failed validation against keywords: {string.Join(", ", searchKeywords)}");
                                                                                    uespBlocked = true; // page exists but text doesn't fit — Fandom next
                                                                                }
                                                                                else
                                                                                {
                                                                                    Debug.WriteLine($"[DescProvider] UESP fetch/parse yielded no description.");
                                                                                    uespBlocked = true;
                                                                                }
                                                                            }
                                                                            else
                                                                            {
                                                                                Debug.WriteLine($"[DescProvider] UESP search returned no URL for \"{uespSearchTerm}\".");
                                                                                uespBlocked = true;
                                                                            }
                                                                        }
                                                                        catch (Exception ex) { lastAttemptWasNetwork = true; Debug.WriteLine($"[DescProvider] Error during UESP processing for \"{uespSearchTerm}\": {ex.Message}"); }
                                                                        if (uespBlocked) break;
                                                                    }

                                                                    // --- 4. Try Fandom ONLY if UESP failed (same retry policy) ---
                                                                    if (finalDescription == null)
                                                                    {
                                                                        sw.Restart();
                                                                        string fandomSearchTerm = searchTermRaw;
                                                                        string encodedFandomSearchTerm = WebUtility.UrlEncode(fandomSearchTerm);
                                                                        Debug.WriteLine($"[DescProvider] UESP failed, Attempting Fandom for: \"{fandomSearchTerm}\"");
                                                                        for (int attempt = 0; attempt < 2 && finalDescription == null; attempt++)
                                                                                                                                                                                                                        {
                                                                                                                                                                                                                            if (attempt > 0)
                                                                                                                                                                                                                            {
                                                                                                                                                                                                                                Debug.WriteLine($"[DescProvider] Fandom network failure — retrying in {(attempt == 1 ? 2 : 5)}s (attempt {attempt + 1}/2).");
                                                                                                                                                                                                                                await Task.Delay(attempt == 1 ? 2000 : 5000).ConfigureAwait(false);
                                                                                                                                                                                                                            }
                                                                            bool fandomBlocked = false;
                                                                            try
                                                                            {
                                                                                var fandomSearch = await SearchWikiAsync($"https://elderscrolls.fandom.com/api.php?action=query&list=search&srsearch={encodedFandomSearchTerm}&format=json&srlimit=1", "https://elderscrolls.fandom.com/wiki/");
                                                                                                                                                                if (fandomSearch.NetworkError) { lastAttemptWasNetwork = true; continue; }
                                                                                                                                                                lastAttemptWasNetwork = false;
                                                                                                                                                                string? fandomUrl = fandomSearch.Url;
                                                                                                                                                                if (!string.IsNullOrEmpty(fandomUrl))
                                                                                                                                                                {
                                    Debug.WriteLine($"[DescProvider] Found Fandom URL: {fandomUrl}");
                                                                            // Try the MediaWiki extracts API first — Fandom pages are
                                                                            // behind Cloudflare too, so the extracts API is more reliable.
                                                                            string fandomPageTitle = fandomUrl.Replace("https://elderscrolls.fandom.com/wiki/", "").Replace("_", " ");
                                                                            var fandomExtract = await FetchExtractViaApiAsync(fandomPageTitle, WikiSite.Fandom);
                                                                            string? rawFandomDesc = fandomExtract.Description;
                                                                            if (fandomExtract.NetworkError) { lastAttemptWasNetwork = true; }
                                                                            // Fall back to the rendered page if the extracts API gave nothing.
                                                                            if (string.IsNullOrWhiteSpace(rawFandomDesc))
                                                                            {
                                                                                var fandomFetch = await FetchAndParseDescriptionAsync(fandomUrl, WikiSite.Fandom);
                                                                                if (fandomFetch.NetworkError) { lastAttemptWasNetwork = true; continue; }
                                                                                rawFandomDesc = fandomFetch.Description;
                                                                            }
                                                                            if (ValidateDescription(rawFandomDesc, searchKeywords))
                                                                                    {
                                                                                        finalDescription = rawFandomDesc;
                                                                                        Debug.WriteLine($"[DescProvider] Success: Valid Fandom description found ({sw.ElapsedMilliseconds}ms).");
                                                                                    }
                                                                                    else if (rawFandomDesc != null)
                                                                                    {
                                                                                        Debug.WriteLine($"[DescProvider] Fandom description failed validation against keywords: {string.Join(", ", searchKeywords)}");
                                                                                        fandomBlocked = true;
                                                                                    }
                                                                                    else
                                                                                    {
                                                                                        Debug.WriteLine($"[DescProvider] Fandom fetch/parse yielded no description.");
                                                                                        fandomBlocked = true;
                                                                                    }
                                                                                }
                                                                                else
                                                                                {
                                                                                    Debug.WriteLine($"[DescProvider] Fandom search returned no URL for \"{fandomSearchTerm}\".");
                                                                                    fandomBlocked = true;
                                                                                }
                                                                            }
                                                                            catch (Exception ex) { lastAttemptWasNetwork = true; Debug.WriteLine($"[DescProvider] Error during Fandom processing for \"{fandomSearchTerm}\": {ex.Message}"); }
                                                                            if (fandomBlocked) break;
                                                                        }
                                                                    }

                                                                    // --- 5. Return Result ---
                                                                    sw.Stop();
                                                                    if (finalDescription == null)
                                                                    {
                                                                        Debug.WriteLine($"[DescProvider] No valid description found for '{searchTermRaw}' after trying both sites.");
                                                                    }
                                                                    return (finalDescription, lastAttemptWasNetwork);
                                                                }

                                /// <summary>Fetches the ENGLISH description for an NPC without translating it and without
                                                                /// being gated by the ShowNpcDescriptions UI toggle; used by the CSV export flow. Serves
                                                                /// from the cache when the English text is already known, otherwise scrapes UESP/Fandom
                                                                /// and caches the English text for later (the translation, if any, is left untouched).
                                                                /// NetworkError distinguishes \"transient failure, retry later\" from \"no such page\".</summary>
                                                                public async Task<(string? Description, bool NetworkError)> GetEnglishDescriptionAsync(FormKey npcFormKey, string? displayName, string? editorId)
                                                                {
                                                                    if (npcFormKey.IsNull) return (null, false);

                                                                    string? overrideDescription = null;
                                                                    if (!BaseGamePlugins.Contains(npcFormKey.ModKey.FileName) &&
                                                                        !_overrideDescriptions.TryGetValue(npcFormKey, out overrideDescription))
                                                                    {
                                                                        return (null, false); // not an eligible NPC
                                                                    }
                                                                    if (overrideDescription is not null)
                                                                    {
                                                                        return (overrideDescription, false); // master override — no wiki look-up
                                                                    }

                                                                    string cacheKey = npcFormKey.ToString();
                                                                    lock (_cacheLock)
                                                                    {
                                                                        if (_cache.TryGetValue(cacheKey, out var cached) && !string.IsNullOrWhiteSpace(cached.En))
                                                                        {
                                                                            return (cached.En, false); // English already cached (from a prior view or pre-translate run)
                                                                        }
                                                                    }

                                                                    // Build search term + validation keywords exactly like GetDescriptionAsync does.
                                                                    string? displaySearchTerm = !string.IsNullOrWhiteSpace(displayName) ? displayName.Split('[')[0].Trim() : null;
                                                                    string? editorSearchTerm = !string.IsNullOrWhiteSpace(editorId) ? editorId.Split('[')[0].Trim() : null;
                                                                    bool displayNameIsAscii = displaySearchTerm != null && displaySearchTerm.All(ch => ch <= 127);
                                                                    string? searchTermRaw = displayNameIsAscii ? displaySearchTerm
                                                                        : editorSearchTerm != null ? SplitCamelCase(editorSearchTerm)
                                                                        : displaySearchTerm;
                                                                    if (string.IsNullOrWhiteSpace(searchTermRaw)) return (null, false);

                                                                    var searchKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                                                    if (editorSearchTerm != null)
                                                                    {
                                                                        foreach (string word in SplitCamelCase(editorSearchTerm)
                                                                            .Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries)
                                                                            .Where(w => !IgnoredWords.Contains(w)))
                                                                        {
                                                                            searchKeywords.Add(word);
                                                                        }
                                                                    }
                                                                    if (displayNameIsAscii && displaySearchTerm != null)
                                                                    {
                                                                        foreach (string word in displaySearchTerm
                                                                            .Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries)
                                                                            .Where(w => !IgnoredWords.Contains(w)))
                                                                        {
                                                                            searchKeywords.Add(word);
                                                                        }
                                                                    }
                                                                    if (!searchKeywords.Any()) return (null, false);

                                                                    var rawResult = await FetchRawEnglishAsync(searchTermRaw, searchKeywords);

                                                                    if (!string.IsNullOrWhiteSpace(rawResult.Description))
                                                                    {
                                                                        // Cache the English text (never overwrite an existing translation).
                                                                        lock (_cacheLock)
                                                                        {
                                                                            _cache.TryGetValue(cacheKey, out var existing);
                                                                            existing ??= new CachedNpcDescription();
                                                                            existing.En = rawResult.Description;
                                                                            _cache[cacheKey] = existing;
                                                                        }
                                                                        _ = PersistCacheAsync();
                                                                    }
                                                                    return rawResult;
                                                                }

                                /// <summary>zh-CN translation from the cache, or null when none exists yet.</summary>
                                public string? GetCachedZh(FormKey npcFormKey)
                                {
                                    lock (_cacheLock)
                                    {
                                        return _cache.TryGetValue(npcFormKey.ToString(), out var entry) ? entry.Zh : null;
                                    }
                                }

                                /// <summary>Bulk-imports user-provided translations (from the CSV round-trip) into the
                                /// cache. Only non-empty Chinese values are applied. Returns the number imported.</summary>
                                public int ImportTranslations(IEnumerable<(string CacheKey, string? English, string Chinese)> entries)
                                {
                                    int imported = 0;
                                    lock (_cacheLock)
                                    {
                                        foreach (var (cacheKey, english, chinese) in entries)
                                        {
                                            if (string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(chinese)) continue;
                                            _cache.TryGetValue(cacheKey, out var existing);
                                            existing ??= new CachedNpcDescription();
                                            if (!string.IsNullOrWhiteSpace(english) && string.IsNullOrWhiteSpace(existing.En))
                                            {
                                                existing.En = english; // CSV carries the English too; adopt it if unknown
                                            }
                                            existing.Zh = chinese;
                                            _cache[cacheKey] = existing;
                                            imported++;
                                        }
                                    }
                                    if (imported > 0)
                                    {
                                        _ = PersistCacheAsync();
                                    }
                                    return imported;
                                }

        // --- FinalizeAsync Method ---
        // Shared exit point for every successful description fetch: translate it when the
        // UI is Chinese, then cache the English original (always) and the translation (when
        // produced) both in memory and on disk, so repeated views are instant and consume
        // no wiki/translation requests.
        private async Task<string?> FinalizeAsync(string cacheKey, string? englishDescription, bool forceTranslate = false)
                {
                    if (string.IsNullOrWhiteSpace(englishDescription)) return englishDescription;

                    string? result = await TranslateIfNeededAsync(englishDescription, forceTranslate);

            lock (_cacheLock)
            {
                _cache.TryGetValue(cacheKey, out var existing);
                existing ??= new CachedNpcDescription();
                existing.En = englishDescription;
                // Only store the translation when it actually differs from the source,
                // i.e. a real translation succeeded (failed translations fall back to the
                // English text and leave Zh null, so the next view retries just translation).
                if (result != null && !result.Equals(englishDescription, StringComparison.OrdinalIgnoreCase))
                {
                    existing.Zh = result;
                }
                _cache[cacheKey] = existing;
            }
            _ = PersistCacheAsync(); // fire-and-forget disk write, never blocks the UI
            return result;
        }

        // --- PersistCacheAsync Method ---
        // Writes the whole in-memory cache to DescriptionCache/descriptions.json, serialized
        // through a semaphore so concurrent fire-and-forget calls cannot interleave writes.
        private async Task PersistCacheAsync()
        {
            try
            {
                await _diskWriteGate.WaitAsync();
                if (string.IsNullOrEmpty(_cacheFilePath)) return;
                Dictionary<string, CachedNpcDescription> snapshot;
                lock (_cacheLock)
                {
                    snapshot = new Dictionary<string, CachedNpcDescription>(_cache, StringComparer.OrdinalIgnoreCase);
                }
                JSONhandler<Dictionary<string, CachedNpcDescription>>.SaveJSONFile(snapshot, _cacheFilePath, out _, out _);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DescProvider][Cache] Persist failed (non-fatal): {ex.Message}");
            }
            finally
            {
                _diskWriteGate.Release();
            }
        }

        // --- TranslateIfNeededAsync Method ---
        // When the UI language is Chinese (zh-CN), translate fetched English descriptions
        // via Google's free translate endpoint, so Chinese-localized users can read them.
        // Falls back to the English original on any failure (network, parse, non-Chinese UI).
        private async Task<string?> TranslateIfNeededAsync(string? description, bool forceTranslate = false)
                {
                    if (string.IsNullOrWhiteSpace(description)) return description;

                    // Only translate when UI is Chinese (or the pre-cache batch explicitly asks
                    // for it) and the description is still English.
                    // NOTE: do NOT use description.Any(ch => ch > 127) to detect "already Chinese";
                    // English text regularly contains Unicode punctuation (curly quotes, em dashes),
                    // which would falsely skip translation. Check for actual CJK ideographs instead.
                    bool uiIsChinese = !string.IsNullOrWhiteSpace(_settings.UiLanguage) &&
                                       _settings.UiLanguage.Equals("zh-CN", StringComparison.OrdinalIgnoreCase);
                    bool alreadyChinese = description.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);
                    if ((!uiIsChinese && !forceTranslate) || alreadyChinese)
                        return description; // not needed, or already Chinese

            try
                        {
                            // Keep URL within safe length; ~900 chars covers several sentences
                            string text = description.Length > 900 ? description.Substring(0, 900) : description;
                            string encoded = Uri.EscapeDataString(text);
                            string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=zh-CN&dt=t&q={encoded}";

                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                            using var request = new HttpRequestMessage(HttpMethod.Get, url);
                            request.Headers.Referrer = new Uri("https://translate.google.com/");
                            // Google's free endpoint is more lenient with browser-like User-Agents;
                            // the app's own UserAgent ("WikiClient") can get 429/blocked.
                            request.Headers.UserAgent.Clear();
                            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
                            using var response = await _httpClient.SendAsync(request, cts.Token);
                            response.EnsureSuccessStatusCode();

                            string json = await response.Content.ReadAsStringAsync(cts.Token);
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                            {
                                // Response shape: [[["中文","English",null,...], ...], null, "en", ...]
                                string translated = string.Concat(doc.RootElement[0].EnumerateArray()
                                    .Where(seg => seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String)
                                    .Select(seg => seg[0].GetString()));

                                if (!string.IsNullOrWhiteSpace(translated))
                                {
                                    Debug.WriteLine($"[DescProvider][Translate] Google OK: \"{translated.Substring(0, Math.Min(translated.Length, 60))}...\"");
                                    return translated;
                                }
                            }
                            // JSON did not parse as a translation (e.g. network interference page);
                            // fall through to the fallback endpoint below instead of returning English.
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[DescProvider][Translate] Google endpoint failed: {ex.Message}. Trying fallback endpoint.");
                        }

                        // --- Fallback endpoint: MyMemory (free, no key, reachable from CN networks) ---
                        // Some networks block/poison translate.googleapis.com with an HTML page,
                        // so on any Google failure we retry via api.mymemory.translated.net.
                        try
                        {
                            string text = description.Length > 1200 ? description.Substring(0, 1200) : description;
                            string url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair=en|zh-CN";

                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                            using var request = new HttpRequestMessage(HttpMethod.Get, url);
                            request.Headers.Referrer = new Uri("https://mymemory.translated.net/");
                            request.Headers.UserAgent.Clear();
                            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
                            using var response = await _httpClient.SendAsync(request, cts.Token);
                            response.EnsureSuccessStatusCode();

                            string json = await response.Content.ReadAsStringAsync(cts.Token);
                            using var doc = JsonDocument.Parse(json);
                            // Response shape: {"responseData":{"translatedText":"你好世界","match":1}, ...}
                            if (doc.RootElement.TryGetProperty("responseData", out var rd) &&
                                rd.TryGetProperty("translatedText", out var tt) &&
                                tt.ValueKind == JsonValueKind.String)
                            {
                                string translated = tt.GetString()!;
                                // MyMemory may echo the input on failure with match==0; guard against that.
                                bool looksReal = translated.Length > 0 && !translated.Equals(description, StringComparison.OrdinalIgnoreCase);
                                if (looksReal)
                                {
                                    Debug.WriteLine($"[DescProvider][Translate] MyMemory OK: \"{translated.Substring(0, Math.Min(translated.Length, 60))}...\"");
                                    return translated;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[DescProvider][Translate] MyMemory endpoint failed: {ex.Message}. Returning English description.");
                        }

                        return description; // fall back to the English original on any failure
        }

        // --- SplitCamelCase Method ---
        // "AelaTheHuntress" -> "Aela The Huntress" so MediaWiki search can tokenize it and
        // validation keywords are split into real words. Uses regex lookarounds so no
        // characters are consumed/replaced.
        private static string SplitCamelCase(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return Regex.Replace(input, @"(?<=[a-z0-9])(?=[A-Z])", " ");
        }

        // --- ValidateDescription Method ---
        private bool ValidateDescription(string? description, HashSet<string> keywords)
        {
            if (string.IsNullOrWhiteSpace(description) || !keywords.Any())
            {
                return false;
            }

            // Check if the description contains at least one significant keyword (case-insensitive)
            foreach (string keyword in keywords)
            {
                 // Use OrdinalIgnoreCase for case-insensitive comparison
                if (description.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return true; // Found at least one keyword
                }
            }

            // If no keyword was found
            Debug.WriteLine($"[DescProvider] Validation failed: Description '{description.Substring(0, Math.Min(description.Length, 50))}...' did not contain keywords: {string.Join(", ", keywords)}");
            return false;
        }


        // --- SearchWikiAsync: MediaWiki search; NetworkError is set when the request itself
                // failed (timeout / 5xx / transport), so callers can distinguish "transient network
                // problem worth retrying" from "query simply found no page".
                private async Task<(string? Url, bool NetworkError)> SearchWikiAsync(string apiUrl, string baseWikiUrl)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                                                using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                        using var response = await _httpClient.SendAsync(request, cts.Token);
                        response.EnsureSuccessStatusCode();

                        string jsonResponse = await response.Content.ReadAsStringAsync(cts.Token);
                         if (string.IsNullOrWhiteSpace(jsonResponse) || !jsonResponse.Contains("\"search\":", StringComparison.OrdinalIgnoreCase)) {
                             Debug.WriteLine($"[DescProvider][Search] Response from {apiUrl} invalid or no 'search' field.");
                             return (null, false);
                         }

                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        var searchResult = JsonSerializer.Deserialize<MediaWikiQueryResult>(jsonResponse, options);
                        if (searchResult?.Query?.Search == null || !searchResult.Query.Search.Any()) {
                             Debug.WriteLine($"[DescProvider][Search] JSON parsed but 'search' array is null or empty from {apiUrl}");
                             return (null, false);
                        }

                        var firstHit = searchResult.Query.Search.FirstOrDefault();
                        if (firstHit != null && !string.IsNullOrWhiteSpace(firstHit.Title)) {
                            // MediaWiki titles often need spaces replaced with underscores for URLs
                            return (baseWikiUrl + firstHit.Title.Replace(' ', '_'), false);
                        } else { Debug.WriteLine($"[DescProvider][Search] No valid title found in first search result from {apiUrl}"); }
                    }
                    catch (HttpRequestException ex) { Debug.WriteLine($"[DescProvider][Search] HTTP Error: {apiUrl} - {ex.StatusCode} {ex.Message}"); return (null, true); }
                    catch (TaskCanceledException ex) { Debug.WriteLine($"[DescProvider][Search] Timeout: {apiUrl} - {ex.Message}"); return (null, true); }
                    catch (JsonException ex) { Debug.WriteLine($"[DescProvider][Search] JSON Error: {apiUrl} - {ex.Message}"); }
                    catch (Exception ex) { Debug.WriteLine($"[DescProvider][Search] Unexpected Error: {apiUrl} - {ex.Message}"); return (null, true); }
                    return (null, false);
                }

        // --- FetchAndParseDescriptionAsync (extracts first sentence); NetworkError marks
        // transport-level failures (timeout / 5xx / connection) so the caller can retry,
        // distinct from "page found but nothing usable extracted".
        private async Task<(string? Description, bool NetworkError)> FetchAndParseDescriptionAsync(string pageUrl, WikiSite site)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                                using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
                using var response = await _httpClient.SendAsync(request, cts.Token);

                if (response.StatusCode == HttpStatusCode.NotFound) {
                    Debug.WriteLine($"[DescProvider][Parse] Page not found (404): {pageUrl}");
                    return (null, false);
                }
                response.EnsureSuccessStatusCode();

                string htmlContent = await response.Content.ReadAsStringAsync(cts.Token);
                if (string.IsNullOrWhiteSpace(htmlContent)) {
                    Debug.WriteLine($"[DescProvider][Parse] Empty HTML content received from {pageUrl}");
                    return (null, false);
                }

                var htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(htmlContent);
                string? rawDescription = null;

                // --- Extraction Logic ---
                if (site == WikiSite.Fandom)
                {
                    var metaNode = htmlDoc.DocumentNode.SelectSingleNode("//meta[@name='description']");
                    if (metaNode != null)
                    {
                        rawDescription = metaNode.GetAttributeValue("content", null);
                        if (!string.IsNullOrWhiteSpace(rawDescription))
                        {
                            Debug.WriteLine($"[DescProvider][Parse] Fandom: Found meta description.");
                            var match = Regex.Match(rawDescription, @"^\s*Not to be confused with .*?\.\s*(.*)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                            rawDescription = match.Success ? match.Groups[1].Value : rawDescription;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(rawDescription)) // Fallback
                    {
                        Debug.WriteLine($"[DescProvider][Parse] Fandom: Meta description failed, trying main content p.");
                        var pNode = htmlDoc.DocumentNode.SelectSingleNode("//div[contains(@class,'mw-parser-output')]/p[normalize-space() and not(@class='caption')]");
                        if (pNode != null) { rawDescription = pNode.InnerText; }
                    }
                }
                else // UESP
                {
                    var pNode = htmlDoc.DocumentNode.SelectSingleNode("//div[@id='mw-content-text']//p[normalize-space()]");
                    if (pNode != null && pNode.FirstChild?.Name == "i") {
                        var nextPNode = pNode.SelectSingleNode("following-sibling::p[normalize-space()]");
                        if (nextPNode != null) {
                            Debug.WriteLine("[DescProvider][Parse] UESP: First paragraph was italic/disambig, using next.");
                            rawDescription = nextPNode.InnerText;
                        } else { Debug.WriteLine("[DescProvider][Parse] UESP: First paragraph was italic/disambig, but no next paragraph found."); }
                    } else if (pNode != null) {
                        rawDescription = pNode.InnerText;
                    }
                }
                // --- End Extraction Logic ---

                if (!string.IsNullOrWhiteSpace(rawDescription))
                {
                    // General cleanup
                    string cleaned = WebUtility.HtmlDecode(rawDescription).Trim();
                    cleaned = Regex.Replace(cleaned, @"\[\d+\]|\[src\]|\[.*?\]", "").Trim();
                    cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");

                    // Extract first sentence
                    string finalSentenceOrCleanedParagraph;
                    var sentenceMatch = Regex.Match(cleaned, @"^([^.!?]+[.!?])");
                    if (sentenceMatch.Success)
                    {
                        finalSentenceOrCleanedParagraph = sentenceMatch.Groups[1].Value.Trim();
                    }
                    else
                    {
                        Debug.WriteLine($"[DescProvider][Parse] Could not extract first sentence from '{cleaned.Substring(0, Math.Min(cleaned.Length, 50))}...'. Using cleaned paragraph.");
                        finalSentenceOrCleanedParagraph = cleaned.Length > 300 ? cleaned.Substring(0, 300) + "..." : cleaned; // Use cleaned paragraph as fallback
                    }

                    // *** NEW: Word Count Validation ***
                    string[] words = finalSentenceOrCleanedParagraph.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length < 5)
                    {
                        Debug.WriteLine($"[DescProvider][Validation] Description rejected due to low word count ({words.Length}): '{finalSentenceOrCleanedParagraph}'");
                        return (null, false); // Return null if word count is too low
                    }
                    // *** END NEW ***

                    Debug.WriteLine($"[DescProvider][Parse] Successfully processed description for {pageUrl}. Word count: {words.Length}.");
                    return (finalSentenceOrCleanedParagraph, false); // Return the validated description
                }
                else
                {
                    Debug.WriteLine($"[DescProvider][Parse] Could not extract description content from {pageUrl} (Site: {site}).");
                }
            }
            catch (HttpRequestException ex) { Debug.WriteLine($"[DescProvider][Parse] HTTP Error: {pageUrl} - {ex.StatusCode} {ex.Message}"); return (null, true); }
            catch (TaskCanceledException ex) { Debug.WriteLine($"[DescProvider][Parse] Timeout: {pageUrl} - {ex.Message}"); return (null, true); }
            catch (Exception ex) { Debug.WriteLine($"[DescProvider][Parse] Unexpected Error: {pageUrl} - {ex.Message}"); return (null, true); }
            return (null, false);
        }

        // --- Helper classes for MediaWiki API JSON Deserialization ---
        private class MediaWikiQueryResult { [JsonPropertyName("query")] public MediaQuery? Query { get; set; } }
        private class MediaQuery { [JsonPropertyName("searchinfo")] public SearchInfo? SearchInfo { get; set; } [JsonPropertyName("search")] public List<SearchItem>? Search { get; set; } }
        private class SearchInfo { [JsonPropertyName("totalhits")] public int TotalHits { get; set; } }
        private class SearchItem { [JsonPropertyName("ns")] public int Ns { get; set; } [JsonPropertyName("title")] public string? Title { get; set; } [JsonPropertyName("pageid")] public int PageId { get; set; } [JsonPropertyName("snippet")] public string? Snippet { get; set; } }

        // --- FetchExtractViaApiAsync: bypass Cloudflare by using the MediaWiki extracts API ---
        // UESP and Elderscrolls Fandom serve their HTML pages behind Cloudflare (the 250 failed NPCs
        // all returned "Just a moment..." Challenge pages instead of wiki content). The search API
        // endpoint is whitelisted and works, but the rendered page is blocked. The `prop=extracts`
        // API endpoint is also whitelisted and returns plain-text content with no Cloudflare
        // challenge, so we use it directly when a wiki search succeeds, falling back to page HTML
        // only if the API gives us nothing.
        private async Task<(string? Description, bool NetworkError)> FetchExtractViaApiAsync(string pageTitle, WikiSite site)
        {
            string baseUrl = site == WikiSite.UESP
                ? "https://en.uesp.net/w/api.php"
                : "https://elderscrolls.fandom.com/api.php";
            string encodedTitle = WebUtility.UrlEncode(pageTitle);
            string apiUrl = $"{baseUrl}?action=query&prop=extracts&explaintext=1&titles={encodedTitle}&format=json";

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                using var response = await _httpClient.SendAsync(request, cts.Token);
                response.EnsureSuccessStatusCode();

                string jsonResponse = await response.Content.ReadAsStringAsync(cts.Token);
                if (string.IsNullOrWhiteSpace(jsonResponse))
                {
                    Debug.WriteLine($"[DescProvider][Extract] Empty JSON from {apiUrl}");
                    return (null, false);
                }

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var query = JsonSerializer.Deserialize<MediaWikiExtractResult>(jsonResponse)?.Query;
                if (query?.Pages == null || query.Pages.Count == 0)
                {
                    Debug.WriteLine($"[DescProvider][Extract] No pages returned from {apiUrl}");
                    return (null, false);
                }

                string? rawExtract = query.Pages.Values.FirstOrDefault()?.Extract;
                if (string.IsNullOrWhiteSpace(rawExtract))
                {
                    Debug.WriteLine($"[DescProvider][Extract] Page found but extract is empty for '{pageTitle}'");
                    return (null, false);
                }

                // Take first sentence like FetchAndParseDescriptionAsync does
                var sentenceMatch = Regex.Match(rawExtract, @"^([^.!?]+[.!?])");
                string firstSentence = sentenceMatch.Success ? sentenceMatch.Groups[1].Value.Trim() : rawExtract.Trim();
                if (firstSentence.Length > 400)
                {
                    firstSentence = firstSentence.Substring(0, 400) + "...";
                }

                Debug.WriteLine($"[DescProvider][Extract] Extracted description from {apiUrl}: '{firstSentence.Substring(0, Math.Min(firstSentence.Length, 80))}...'");
                return (firstSentence, false);
            }
            catch (HttpRequestException ex)
            {
                Debug.WriteLine($"[DescProvider][Extract] HTTP Error: {apiUrl} - {ex.StatusCode} {ex.Message}");
                return (null, true);
            }
            catch (TaskCanceledException ex)
            {
                Debug.WriteLine($"[DescProvider][Extract] Timeout: {apiUrl} - {ex.Message}");
                return (null, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DescProvider][Extract] Unexpected Error: {apiUrl} - {ex.Message}");
                return (null, true);
            }
        }

        // Helper for MediaWiki extracts API
        private class MediaWikiExtractResult { [JsonPropertyName("query")] public MediaExtractQuery? Query { get; set; } }
        private class MediaExtractQuery { [JsonPropertyName("pages")] public Dictionary<int, MediaExtractPage>? Pages { get; set; } }
        private class MediaExtractPage { [JsonPropertyName("extract")] public string? Extract { get; set; } }

            /// <summary>One cached NPC description entry (persisted to DescriptionCache/descriptions.json).
            /// En is the UESP/Fandom English source; Zh is the zh-CN translation produced for Chinese UIs,
            /// null while not yet translated (a later view will translate just the cached English text).</summary>
            }

            public sealed class CachedNpcDescription
            {
                public string? En { get; set; }
                public string? Zh { get; set; }
            }
}