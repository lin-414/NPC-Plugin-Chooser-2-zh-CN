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

        public async Task<string?> GetDescriptionAsync(FormKey npcFormKey, string? displayName, string? editorId)
        {
            // 1. Check conditions
            string? overrideDescription = null;
            if (!_settings.ShowNpcDescriptions || npcFormKey.IsNull || 
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
                                    string cacheKey = npcFormKey.ToString();
                                    string? cachedEnOnly = null;
                                    lock (_cacheLock)
                                    {
                                        if (_cache.TryGetValue(cacheKey, out var cached))
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
                                            // zh-CN UI but only the English text is cached: remember the English
                                            // text so the fetch below can be skipped and only translation re-run.
                                            cachedEnOnly = cached.En;
                                        }
                                    }
                                    if (cachedEnOnly != null)
                                    {
                                        return await FinalizeAsync(cacheKey, cachedEnOnly);
                                    }

                        Stopwatch sw = Stopwatch.StartNew();
                        string? finalDescription = null;

            // --- 3. Try UESP First ---
            string uespSearchTerm = $"Skyrim:{searchTermRaw}";
            string encodedUespSearchTerm = WebUtility.UrlEncode(uespSearchTerm);
            Debug.WriteLine($"[DescProvider] Attempting UESP for: \"{uespSearchTerm}\"");
            try
            {
                 string? uespUrl = await SearchWikiAsync($"https://en.uesp.net/w/api.php?action=query&list=search&srsearch={encodedUespSearchTerm}&format=json&srlimit=1", "https://en.uesp.net/wiki/");
                 if (!string.IsNullOrEmpty(uespUrl))
                 {
                     Debug.WriteLine($"[DescProvider] Found UESP URL: {uespUrl}");
                     string? rawUespDesc = await FetchAndParseDescriptionAsync(uespUrl, WikiSite.UESP);
                     if (ValidateDescription(rawUespDesc, searchKeywords)) // Validate before assigning
                         {
                             finalDescription = rawUespDesc; // Assign if valid
                             Debug.WriteLine($"[DescProvider] Success: Valid UESP description found ({sw.ElapsedMilliseconds}ms).");
                             return await FinalizeAsync(cacheKey, finalDescription); // *** UESP success: translate + cache ***
                         }
                     else if(rawUespDesc != null) { // Description was fetched but failed validation
                         Debug.WriteLine($"[DescProvider] UESP description failed validation against keywords: {string.Join(", ", searchKeywords)}");
                     }
                     else { // Fetch/Parse failed
                         Debug.WriteLine($"[DescProvider] UESP fetch/parse yielded no description.");
                     }
                 }
                 else { Debug.WriteLine($"[DescProvider] UESP search returned no URL for \"{uespSearchTerm}\"."); }
            }
            catch (Exception ex) { Debug.WriteLine($"[DescProvider] Error during UESP processing for \"{uespSearchTerm}\": {ex.Message}"); }

            // --- 4. Try Fandom ONLY if UESP failed ---
            if (finalDescription == null) // Check if UESP attempt was unsuccessful
            {
                 sw.Restart(); // Restart timer for Fandom attempt
                 string fandomSearchTerm = searchTermRaw;
                 string encodedFandomSearchTerm = WebUtility.UrlEncode(fandomSearchTerm);
                 Debug.WriteLine($"[DescProvider] UESP failed, Attempting Fandom for: \"{fandomSearchTerm}\"");
                 try
                 {
                      string? fandomUrl = await SearchWikiAsync($"https://elderscrolls.fandom.com/api.php?action=query&list=search&srsearch={encodedFandomSearchTerm}&format=json&srlimit=1", "https://elderscrolls.fandom.com/wiki/");
                      if (!string.IsNullOrEmpty(fandomUrl))
                      {
                           Debug.WriteLine($"[DescProvider] Found Fandom URL: {fandomUrl}");
                           string? rawFandomDesc = await FetchAndParseDescriptionAsync(fandomUrl, WikiSite.Fandom);
                           if (ValidateDescription(rawFandomDesc, searchKeywords)) // Validate before assigning
                           {
                                finalDescription = rawFandomDesc; // Assign if valid
                                Debug.WriteLine($"[DescProvider] Success: Valid Fandom description found ({sw.ElapsedMilliseconds}ms).");
                                return await FinalizeAsync(cacheKey, finalDescription); // *** Fandom success: translate + cache ***
                           }
                           else if(rawFandomDesc != null) { // Description was fetched but failed validation
                                Debug.WriteLine($"[DescProvider] Fandom description failed validation against keywords: {string.Join(", ", searchKeywords)}");
                           }
                            else { // Fetch/Parse failed
                                Debug.WriteLine($"[DescProvider] Fandom fetch/parse yielded no description.");
                            }
                      }
                       else { Debug.WriteLine($"[DescProvider] Fandom search returned no URL for \"{fandomSearchTerm}\"."); }
                 }
                 catch (Exception ex) { Debug.WriteLine($"[DescProvider] Error during Fandom processing for \"{fandomSearchTerm}\": {ex.Message}"); }
            }

            // --- 5. Return Result ---
            sw.Stop();
            if (finalDescription != null) {
                // This point should theoretically not be reached if returns are immediate, but as safety.
                                 Debug.WriteLine($"[DescProvider] Returning description for '{searchTermRaw}'. Total time: {sw.ElapsedMilliseconds}ms");
                                return await FinalizeAsync(cacheKey, finalDescription);
            } else {
                 Debug.WriteLine($"[DescProvider] No valid description found for '{searchTermRaw}' after trying both sites.");
                return null;
            }
        }

        // --- FinalizeAsync Method ---
        // Shared exit point for every successful description fetch: translate it when the
        // UI is Chinese, then cache the English original (always) and the translation (when
        // produced) both in memory and on disk, so repeated views are instant and consume
        // no wiki/translation requests.
        private async Task<string?> FinalizeAsync(string cacheKey, string? englishDescription)
        {
            if (string.IsNullOrWhiteSpace(englishDescription)) return englishDescription;

            string? result = await TranslateIfNeededAsync(englishDescription);

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
        private async Task<string?> TranslateIfNeededAsync(string? description)
        {
            if (string.IsNullOrWhiteSpace(description)) return description;

            // Only translate when UI is Chinese and the description is still English.
            // NOTE: do NOT use description.Any(ch => ch > 127) to detect "already Chinese";
            // English text regularly contains Unicode punctuation (curly quotes, em dashes),
            // which would falsely skip translation. Check for actual CJK ideographs instead.
            bool uiIsChinese = !string.IsNullOrWhiteSpace(_settings.UiLanguage) &&
                               _settings.UiLanguage.Equals("zh-CN", StringComparison.OrdinalIgnoreCase);
            bool alreadyChinese = description.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);
            if (!uiIsChinese || alreadyChinese)
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


        // --- SearchWikiAsync remains the same ---
        private async Task<string?> SearchWikiAsync(string apiUrl, string baseWikiUrl)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(7));
                using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                using var response = await _httpClient.SendAsync(request, cts.Token);
                response.EnsureSuccessStatusCode();

                string jsonResponse = await response.Content.ReadAsStringAsync(cts.Token);
                 if (string.IsNullOrWhiteSpace(jsonResponse) || !jsonResponse.Contains("\"search\":", StringComparison.OrdinalIgnoreCase)) {
                     Debug.WriteLine($"[DescProvider][Search] Response from {apiUrl} invalid or no 'search' field.");
                     return null;
                 }

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var searchResult = JsonSerializer.Deserialize<MediaWikiQueryResult>(jsonResponse, options);
                if (searchResult?.Query?.Search == null || !searchResult.Query.Search.Any()) {
                     Debug.WriteLine($"[DescProvider][Search] JSON parsed but 'search' array is null or empty from {apiUrl}");
                     return null;
                }

                var firstHit = searchResult.Query.Search.FirstOrDefault();
                if (firstHit != null && !string.IsNullOrWhiteSpace(firstHit.Title)) {
                    // MediaWiki titles often need spaces replaced with underscores for URLs
                    return baseWikiUrl + firstHit.Title.Replace(' ', '_');
                } else { Debug.WriteLine($"[DescProvider][Search] No valid title found in first search result from {apiUrl}"); }
            }
            catch (HttpRequestException ex) { Debug.WriteLine($"[DescProvider][Search] HTTP Error: {apiUrl} - {ex.StatusCode} {ex.Message}"); }
            catch (TaskCanceledException ex) { Debug.WriteLine($"[DescProvider][Search] Timeout: {apiUrl} - {ex.Message}"); }
            catch (JsonException ex) { Debug.WriteLine($"[DescProvider][Search] JSON Error: {apiUrl} - {ex.Message}"); }
            catch (Exception ex) { Debug.WriteLine($"[DescProvider][Search] Unexpected Error: {apiUrl} - {ex.Message}"); }
            return null;
        }

        // --- FetchAndParseDescriptionAsync remains the same (extracts first sentence) ---
        private async Task<string?> FetchAndParseDescriptionAsync(string pageUrl, WikiSite site)
        {
             try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
                using var response = await _httpClient.SendAsync(request, cts.Token);

                if (response.StatusCode == HttpStatusCode.NotFound) {
                    Debug.WriteLine($"[DescProvider][Parse] Page not found (404): {pageUrl}");
                    return null;
                }
                response.EnsureSuccessStatusCode();

                string htmlContent = await response.Content.ReadAsStringAsync(cts.Token);
                if (string.IsNullOrWhiteSpace(htmlContent)) {
                     Debug.WriteLine($"[DescProvider][Parse] Empty HTML content received from {pageUrl}");
                     return null;
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
                        return null; // Return null if word count is too low
                    }
                    // *** END NEW ***

                    Debug.WriteLine($"[DescProvider][Parse] Successfully processed description for {pageUrl}. Word count: {words.Length}.");
                    return finalSentenceOrCleanedParagraph; // Return the validated description
                }
                else
                {
                    Debug.WriteLine($"[DescProvider][Parse] Could not extract description content from {pageUrl} (Site: {site}).");
                }
            }
            catch (HttpRequestException ex) { Debug.WriteLine($"[DescProvider][Parse] HTTP Error: {pageUrl} - {ex.StatusCode} {ex.Message}"); }
            catch (TaskCanceledException ex) { Debug.WriteLine($"[DescProvider][Parse] Timeout: {pageUrl} - {ex.Message}"); }
            catch (Exception ex) { Debug.WriteLine($"[DescProvider][Parse] Unexpected Error: {pageUrl} - {ex.Message}"); }
            return null;
        }

        // --- Helper classes for MediaWiki API JSON Deserialization ---
        private class MediaWikiQueryResult { [JsonPropertyName("query")] public MediaQuery? Query { get; set; } }
        private class MediaQuery { [JsonPropertyName("searchinfo")] public SearchInfo? SearchInfo { get; set; } [JsonPropertyName("search")] public List<SearchItem>? Search { get; set; } }
        private class SearchInfo { [JsonPropertyName("totalhits")] public int TotalHits { get; set; } }
        private class SearchItem { [JsonPropertyName("ns")] public int Ns { get; set; } [JsonPropertyName("title")] public string? Title { get; set; } [JsonPropertyName("pageid")] public int PageId { get; set; } [JsonPropertyName("snippet")] public string? Snippet { get; set; } }
            }

            /// <summary>One cached NPC description entry (persisted to DescriptionCache/descriptions.json).
            /// En is the UESP/Fandom English source; Zh is the zh-CN translation produced for Chinese UIs,
            /// null while not yet translated (a later view will translate just the cached English text).</summary>
            public sealed class CachedNpcDescription
            {
                public string? En { get; set; }
                public string? Zh { get; set; }
            }
}