using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LaunchpadStudio
{
    public readonly struct LibraryEntry
    {
        public readonly string Path;
        public readonly string Name;
        public readonly bool   IsDirectory;

        public LibraryEntry(string path, string name, bool isDirectory)
        {
            Path = path;
            Name = name;
            IsDirectory = isDirectory;
        }
    }

    public sealed class DirectoryListing
    {
        public string Path;
        public readonly List<LibraryEntry> Folders = new List<LibraryEntry>();
        public readonly List<LibraryEntry> Files   = new List<LibraryEntry>();
    }

    /// <summary>
    /// Reads one directory at a time (Explorer-style navigation), rather than
    /// caching the whole tree.
    ///
    /// The extension filter is now a PARAMETER rather than a constant, because the same
    /// walker serves the audio library, the layout browser, the lightshow browser and
    /// the theme browser. Null means "every file".
    /// </summary>
    public static class LibraryScanner
    {
        // Kept in sync with AudioFileLoader.GuessAudioType — this list is what the
        // library, the pad inspector's Load, and the folder picker all show.
        public static readonly string[] Extensions = { ".wav", ".ogg", ".aif", ".aiff", ".mp3" };
        public static bool IsAudioFile(string path) => HasExtension(path, Extensions);

        /// <summary>
        /// Null or empty filter = everything passes.
        ///
        /// Matched against the END OF THE FILE NAME, not against Path.GetExtension:
        /// GetExtension("Kit.layout.json") is ".json", so a filter of ".layout.json"
        /// matched nothing and the layout browser listed an empty folder. Suffix
        /// matching handles both shapes with one rule.
        /// </summary>
        public static bool HasExtension(string path, string[] extensions)
        {
            if (extensions == null || extensions.Length == 0) return true;
            if (string.IsNullOrEmpty(path)) return false;

            string name = System.IO.Path.GetFileName(path).ToLowerInvariant();

            for (int i = 0; i < extensions.Length; i++)
            {
                if (string.IsNullOrEmpty(extensions[i])) continue;

                // Tolerate "json" as well as ".json": a hand-typed filter should not
                // fail silently over a missing dot.
                string want = extensions[i].ToLowerInvariant();
                if (want[0] != '.') want = "." + want;

                if (name.Length > want.Length && name.EndsWith(want, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        public static bool TryRead(string directory, out DirectoryListing listing) =>
            TryRead(directory, Extensions, out listing);

        public static bool TryRead(string directory, string[] extensions,
                                   out DirectoryListing listing)
        {
            listing = null;

            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return false;

            var result = new DirectoryListing { Path = NormalizeDir(directory) };

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(directory))
                {
                    // Skip hidden/system folders so the list isn't cluttered.
                    var attr = File.GetAttributes(dir);
                    if ((attr & FileAttributes.Hidden) != 0) continue;

                    result.Folders.Add(new LibraryEntry(
                        NormalizeDir(dir), System.IO.Path.GetFileName(dir), true));
                }

                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (!HasExtension(file, extensions)) continue;

                    result.Files.Add(new LibraryEntry(
                        file.Replace('\\', '/'), System.IO.Path.GetFileName(file), false));
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Could not read \"{directory}\": {e.Message}");
                return false;
            }

            result.Folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            result.Files.Sort((a, b)   => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            listing = result;
            return true;
        }

        /// <summary>True if <paramref name="candidate"/> is <paramref name="root"/> or below it.</summary>
        public static bool IsWithin(string candidate, string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            string c = NormalizeDir(candidate);
            string r = NormalizeDir(root);
            return c.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                   c.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizeDir(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string p = System.IO.Path.GetFullPath(path).Replace('\\', '/');
            return p.Length > 1 && p.EndsWith("/") ? p.TrimEnd('/') : p;
        }
    }
}