using System;
using UnityEngine.Localization.Settings;

namespace MateEngineQoL.Bridge
{
    /// <summary>
    /// Looks up fork UI strings in the "QoL" string table (Assets/MateEngineQoL/Localization), falling back to the
    /// English entry when the current language has none yet, and to the key itself if the table is missing.
    /// Entries are created and edited with MateEngine/QoL/Update QoL String Table.
    /// </summary>
    public static class QolText
    {
        public const string Table = "QoL";

        public static string Get(string key)
        {
            try
            {
                var db = LocalizationSettings.StringDatabase;
                string s = db.GetTable(Table)?.GetEntry(key)?.GetLocalizedString();
                if (!string.IsNullOrEmpty(s)) return s;

                var english = LocalizationSettings.AvailableLocales.GetLocale("en");
                if (english != null)
                {
                    s = db.GetTable(Table, english)?.GetEntry(key)?.GetLocalizedString();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch (Exception)
            {
                // Localization not initialized or table missing: show the key so the gap is visible.
            }
            return key;
        }

        public static string Format(string key, params object[] args)
        {
            string format = Get(key);
            try { return string.Format(format, args); }
            catch (FormatException) { return format; }
        }
    }
}
