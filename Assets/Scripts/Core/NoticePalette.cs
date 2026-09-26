using System;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// Notice colours, in ONE place — the ticker and the log window paint from these,
    /// so a warning can never be amber in one and grey in the other.
    ///
    /// Static rather than a ScriptableObject because there is exactly one scheme and
    /// nothing in the app has ever wanted a second. The values are now SETTABLE so the
    /// session file can carry them; Changed lets any open view repaint.
    /// </summary>
    public static class NoticePalette
    {
        public static readonly Color DefaultInfo    = new Color32(210, 210, 210, 255);
        public static readonly Color DefaultSuccess = new Color32(120, 220, 130, 255);
        public static readonly Color DefaultWarning = new Color32(240, 190,  70, 255);
        public static readonly Color DefaultError   = new Color32(240,  90,  90, 255);

        /// <summary>Raised when any of the four colours changes.</summary>
        public static event Action Changed;

        private static Color _info    = DefaultInfo;
        private static Color _success = DefaultSuccess;
        private static Color _warning = DefaultWarning;
        private static Color _error   = DefaultError;

        public static Color Info    { get => _info;    set => Assign(ref _info,    value); }
        public static Color Success { get => _success; set => Assign(ref _success, value); }
        public static Color Warning { get => _warning; set => Assign(ref _warning, value); }
        public static Color Error   { get => _error;   set => Assign(ref _error,   value); }

        private static void Assign(ref Color field, Color value)
        {
            if (field == value) return;
            field = value;
            Changed?.Invoke();
        }

        public static void ResetToDefaults()
        {
            _info    = DefaultInfo;
            _success = DefaultSuccess;
            _warning = DefaultWarning;
            _error   = DefaultError;
            Changed?.Invoke();
        }

        public static Color ColourFor(NoticeLevel level)
        {
            switch (level)
            {
                case NoticeLevel.Success: return Success;
                case NoticeLevel.Warning: return Warning;
                case NoticeLevel.Error:   return Error;
                default:                  return Info;
            }
        }

        /// <summary>Row tint behind the text. Errors and warnings get a faint wash.</summary>
        public static Color RowFor(NoticeLevel level)
        {
            switch (level)
            {
                case NoticeLevel.Error:   return new Color(0.45f, 0.10f, 0.10f, 0.28f);
                case NoticeLevel.Warning: return new Color(0.45f, 0.33f, 0.05f, 0.22f);
                case NoticeLevel.Success: return new Color(0.10f, 0.35f, 0.15f, 0.18f);
                default:                  return new Color(1f, 1f, 1f, 0.06f);
            }
        }

        /// <summary>"WARN", for the level column. Fixed width reads as a column.</summary>
        public static string Tag(NoticeLevel level)
        {
            switch (level)
            {
                case NoticeLevel.Success: return "OK  ";
                case NoticeLevel.Warning: return "WARN";
                case NoticeLevel.Error:   return "ERR ";
                default:                  return "INFO";
            }
        }
    }
}