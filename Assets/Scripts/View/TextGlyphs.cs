using System.Collections.Generic;
using System.Text;
using TMPro;

namespace LaunchpadStudio
{
    /// <summary>
    /// Makes code-written text fit the FONT it is about to be drawn in.
    ///
    /// TextMeshPro draws a character only if the label's font asset (or one of its
    /// fallbacks) has a glyph for it. Anything else becomes an empty box, silently —
    /// which is how the Layouts browser's "▸" current-row marker went missing: it is
    /// not in LiberationSans at all.
    ///
    /// Fit swaps each character the font CANNOT draw for a plain-ASCII stand-in
    /// ("—" → "-", "…" → "...", "→" → "->", "▸" → ">"). Characters the font can draw
    /// are left alone, so a better font — or a v2 theme with one — gets the proper
    /// punctuation back automatically, with no code change.
    ///
    /// Display only. The notice history, the log file and the clipboard keep the
    /// original text; only what is painted on a label is adjusted.
    ///
    /// Characters with no stand-in (a Japanese file name, say) are passed through
    /// unchanged: guessing would be worse than the box, and the real fix for those is
    /// a fallback font on the font asset, not a substitution.
    /// </summary>
    public static class TextGlyphs
    {
        private static readonly Dictionary<char, string> StandIns = new Dictionary<char, string>
        {
            { '\u2014', "-"   },   // — em dash
            { '\u2013', "-"   },   // – en dash
            { '\u2026', "..." },   // … ellipsis
            { '\u2192', "->"  },   // → right arrow
            { '\u2190', "<-"  },   // ← left arrow
            { '\u25B8', ">"   },   // ▸ small right triangle
            { '\u25BA', ">"   },   // ► right pointer
            { '\u203A', ">"   },   // › single right angle quote
            { '\u2022', "*"   },   // • bullet
            { '\u2018', "'"   },   // ‘
            { '\u2019', "'"   },   // ’
            { '\u201C', "\""  },   // “
            { '\u201D', "\""  },   // ”
            { '\u00D7', "x"   },   // × multiplication sign
        };

        /// <summary>
        /// (font, character) → drawable. Looking a glyph up can ADD it to a dynamic
        /// atlas, so the answer is worth keeping; the ticker calls this for every
        /// notice. Keyed by font so two labels in different fonts get their own answers.
        /// </summary>
        private static readonly Dictionary<(int, char), bool> Cache =
            new Dictionary<(int, char), bool>();

        /// <summary>
        /// Returns <paramref name="text"/> with every character <paramref name="label"/>'s
        /// font cannot draw replaced by its ASCII stand-in. Plain ASCII is returned as-is
        /// without any lookup, which is nearly every string in the app.
        /// </summary>
        public static string Fit(TMP_Text label, string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            if (label == null || label.font == null || IsAscii(text)) return text;

            var font = label.font;
            StringBuilder builder = null;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                bool keep = c < 128 ||
                            char.IsSurrogate(c) ||
                            !StandIns.TryGetValue(c, out _) ||
                            CanDraw(font, c);

                if (keep)
                {
                    builder?.Append(c);
                    continue;
                }

                // First substitution: copy everything before it, then carry on.
                if (builder == null) builder = new StringBuilder(text, 0, i, text.Length + 8);
                builder.Append(StandIns[c]);
            }

            return builder != null ? builder.ToString() : text;
        }

        private static bool IsAscii(string text)
        {
            foreach (char c in text)
                if (c >= 128) return false;
            return true;
        }

        private static bool CanDraw(TMP_FontAsset font, char c)
        {
            var key = (font.GetInstanceID(), c);
            if (Cache.TryGetValue(key, out bool known)) return known;

            // searchFallbacks: the asset's own fallback list. tryAddCharacter: a DYNAMIC
            // font (or fallback) adds the glyph to its atlas if its source font has it.
            bool drawable = font.HasCharacter(c, true, true);

            // TMP also consults the project-wide fallbacks in TMP Settings.
            if (!drawable && TMP_Settings.fallbackFontAssets != null)
                foreach (var fallback in TMP_Settings.fallbackFontAssets)
                    if (fallback != null && fallback.HasCharacter(c, true, true))
                    {
                        drawable = true;
                        break;
                    }

            Cache[key] = drawable;
            return drawable;
        }
    }
}