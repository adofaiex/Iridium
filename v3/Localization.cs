using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Iridium
{
    public static class Localization
    {
        private static readonly Dictionary<string, Dictionary<string, string>> languages = [];
        private static readonly Dictionary<string, string> languageDisplayNames = [];
        private static bool loaded = false;
        private static readonly List<string> _availableLanguages = [];
        public static List<string> AvailableLanguages
        {
            get
            {
                if (!loaded) Load();
                return _availableLanguages;
            }
        }

        public static string GetDisplayName(string langId)
        {
            if (!loaded) Load();
            return languageDisplayNames.TryGetValue(langId, out string name) ? name : langId;
        }

        public static void Load()
        {
            try
            {
                languages.Clear();
                languageDisplayNames.Clear();
                _availableLanguages.Clear();
                string langDir = Path.Combine(ResourceLoader.ResourcesPath, "lang");
                
                if (Directory.Exists(langDir))
                {
                    string[] files = Directory.GetFiles(langDir, "*.json");
                    foreach (string file in files)
                    {
                        try
                        {
                            string langId = Path.GetFileNameWithoutExtension(file);
                            string json = File.ReadAllText(file);
                            var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                            if (dict != null && dict.Count > 0)
                            {
                                languages[langId] = dict;
                                _availableLanguages.Add(langId);
                                
                                if (dict.TryGetValue("displayName", out string displayName))
                                {
                                    languageDisplayNames[langId] = displayName;
                                }
                                else
                                {
                                    languageDisplayNames[langId] = langId;
                                }

                                Main.Logger?.Log($"Loaded language: {langId} ({languageDisplayNames[langId]}) - {dict.Count} keys");
                            }
                        }
                        catch (Exception ex)
                        {
                            Main.Logger?.Error($"Failed to load language file {file}: {ex.Message}");
                        }
                    }
                }

                if (languages.Count == 0)
                {
                    Main.Logger?.Warning("No language files found, using empty 'en' fallback.");
                    languages["en"] = [];
                    languageDisplayNames["en"] = "English";
                    _availableLanguages.Add("en");
                }
            }
            catch (Exception ex)
            {
                Main.Logger?.Error($"Critical error in Localization.Load: {ex.Message}");
                if (languages.Count == 0) languages["en"] = [];
            }
            finally
            {
                loaded = true;
            }
        }

        private static string _lastLanguage = "en";
        private static Dictionary<string, string>? _currentDict;

        public static string Get(string key)
        {
            if (!loaded) Load();

            string currentLang = Main.Settings?.language ?? "en";

            if (_currentDict == null || _lastLanguage != currentLang)
            {
                languages.TryGetValue(currentLang, out _currentDict);
                _lastLanguage = currentLang;
            }

            if (_currentDict != null && _currentDict.TryGetValue(key, out string value))
                return value;

            // Fallback to 'en' or the first available language
            if (languages.TryGetValue("en", out var enDict) && enDict.TryGetValue(key, out string enValue))
            {
                return enValue;
            }

            return key;
        }

        public static string Get(string key, params object[] args)
        {
            try
            {
                string format = Get(key);
                return string.Format(format, args);
            }
            catch (Exception ex)
            {
                Main.Logger?.Error($"Localization.Get format error for key {key}: {ex.Message}");
                return key;
            }
        }
    }
}
