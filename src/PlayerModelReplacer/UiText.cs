using System;
using BepInEx.Configuration;
using UnityEngine;

namespace MikuPlayerReplacer
{
    /// <summary>Which language the in-game MMS interface is drawn in.</summary>
    internal enum UiLanguageMode
    {
        Auto,
        Chinese,
        English
    }

    /// <summary>
    /// Picks the language for every piece of text MMS draws itself: the PDA model
    /// page, the F2 console and the first-run notice. The game's own UI is
    /// untouched.
    ///
    /// Call sites pass both strings - T("中文", "English") - instead of looking up
    /// a key in a table, so a missing translation is a visible gap in the source
    /// rather than a silent fallback at runtime. Everything English here is plain
    /// ASCII, which is what PrewarmAscii below reserves in the dynamic font.
    ///
    /// Auto follows the game's own language setting, read live rather than cached
    /// at start-up, so switching the language in the game's options changes the
    /// MMS interface with it. The system language is only the fallback, for the
    /// window before the game's Language object exists.
    /// </summary>
    internal static class UiText
    {
        private static ConfigEntry<UiLanguageMode> _mode;

        /// <summary>Printable ASCII, reserved for every UI font size and style.</summary>
        internal const string PrewarmAscii =
            " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ" +
            "[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~";

        internal static bool English
        {
            get
            {
                if (_mode != null && _mode.Value != UiLanguageMode.Auto)
                {
                    return _mode.Value == UiLanguageMode.English;
                }
                return AutoIsEnglish();
            }
        }

        /// <summary>The configured value, not the resolved language.</summary>
        internal static UiLanguageMode Mode
        {
            get { return _mode != null ? _mode.Value : UiLanguageMode.Auto; }
        }

        internal static void Bind(ConfigFile config)
        {
            _mode = config.Bind("Interface", "UiLanguage", UiLanguageMode.Auto,
                "Language of the MMS interface: the PDA model page, the MMS " +
                "console (F2) and the first-run notice. Auto follows the game's " +
                "own language setting, so switching the game language switches " +
                "this one too. Chinese and English force one language regardless. " +
                "Can also be changed in game from the MMS console (F2); a hand " +
                "edit here needs a restart.");
        }

        /// <summary>
        /// Stores a new mode. Nothing is cached, so the console picks the change
        /// up on its next frame; the caller saves the config file.
        /// </summary>
        internal static void SetMode(UiLanguageMode mode)
        {
            if (_mode != null)
            {
                _mode.Value = mode;
            }
        }

        /// <summary>
        /// The game's own language setting, falling back to the system language
        /// while the game's Language object does not exist yet or the current
        /// language is still empty.
        /// </summary>
        private static bool AutoIsEnglish()
        {
            try
            {
                if (Language.isNotQuitting)
                {
                    string language = Language.main.GetCurrentLanguage();
                    if (!string.IsNullOrEmpty(language))
                    {
                        return language.IndexOf("Chinese",
                            StringComparison.OrdinalIgnoreCase) < 0;
                    }
                }
            }
            catch (Exception)
            {
                // Fall through to the system language.
            }
            return SystemIsEnglish();
        }

        /// <summary>True when the system language is not one of the Chinese ones.</summary>
        internal static bool SystemIsEnglish()
        {
            switch (Application.systemLanguage)
            {
                case SystemLanguage.Chinese:
                case SystemLanguage.ChineseSimplified:
                case SystemLanguage.ChineseTraditional:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>The string for the active language; both are always given.</summary>
        internal static string T(string chinese, string english)
        {
            if (!English)
            {
                return chinese;
            }
            // An accidentally untranslated label must not put Chinese glyphs on an
            // English page, which prewarmed none of them.
            return string.IsNullOrEmpty(english) ? chinese : english;
        }
    }
}
