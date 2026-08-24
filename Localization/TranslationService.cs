using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace NPC_Plugin_Chooser_2.Localization
{
    public class TranslationService
    {
        private readonly Dictionary<string, string> _translations = new();
        private string _currentLanguage = "en";
        private string _basePath;

        public event Action? LanguageChanged;

        public TranslationService()
        {
            _basePath = AppDomain.CurrentDomain.BaseDirectory;
        }

        /// <summary>
        /// Initialize the translation service with the specified language.
        /// Called once at startup after the DI container is built.
        /// </summary>
        public void Initialize(string language = "en")
        {
            _currentLanguage = language;
            LoadLanguageFile(language);
        }

        /// <summary>
        /// Switch the UI language at runtime. All LocExtension bindings
        /// refresh automatically via LocSource.
        /// </summary>
        public void SetLanguage(string language)
        {
            if (language == _currentLanguage) return;
            _currentLanguage = language;
            LoadLanguageFile(language);
            LanguageChanged?.Invoke();
        }

        public string CurrentLanguage => _currentLanguage;

        /// <summary>
        /// Get a translated string by key. Returns the key itself if not found.
        /// </summary>
        public string GetString(string key)
        {
            if (_translations.TryGetValue(key, out var value))
                return value;
            return key; // Fallback: show the key name
        }

        /// <summary>
        /// Try to get a translated string. Returns false if the key is missing.
        /// </summary>
        public bool TryGetString(string key, out string? value)
        {
            return _translations.TryGetValue(key, out value);
        }

        private void LoadLanguageFile(string language)
        {
            _translations.Clear();

            // Always load English as the base fallback
            LoadFromFile("en");

            // If the target language is not English, load it on top (overrides)
            if (language != "en")
            {
                LoadFromFile(language);
            }
        }

        private void LoadFromFile(string language)
        {
            // Search in Localization/ subdirectory and the base directory
            string[] searchPaths = new[]
            {
                Path.Combine(_basePath, "Localization", $"strings.{language}.json"),
                Path.Combine(_basePath, $"strings.{language}.json"),
                Path.Combine(_basePath, "Localization", "strings.json"),
                Path.Combine(_basePath, "strings.json"),
            };

            string? filePath = searchPaths.FirstOrDefault(File.Exists);
            if (filePath == null)
            {
                System.Diagnostics.Debug.WriteLine($"[TranslationService] No translation file found for: {language}");
                return;
            }

            try
            {
                var json = File.ReadAllText(filePath);
                var entries = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                if (entries != null)
                {
                    foreach (var kvp in entries)
                    {
                        _translations[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TranslationService] Failed to load {filePath}: {ex.Message}");
            }
        }
    }
}