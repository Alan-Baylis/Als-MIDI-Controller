using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// The ONE way a file is replaced in this project.
    ///
    /// "Write a temp, delete the target, move the temp over it" has a window between
    /// the delete and the move in which NEITHER file exists. Kill the process there —
    /// a crash, a power cut, Task Manager — and the session is simply gone.
    ///
    /// File.Replace closes that window: the swap is a single filesystem operation and
    /// it hands the old contents to a backup on the way past, so the cost of a
    /// safety net is one API call. Where Replace cannot be used (the destination does
    /// not exist yet, or the platform refuses it) the old move is used, but the
    /// backup is still made FIRST.
    ///
    /// Nothing here catches its own exceptions: callers already report failures to
    /// the user, and a save that silently did nothing is worse than one that says so.
    /// </summary>
    public static class AtomicFile
    {
        public const string TempSuffix   = ".tmp";
        public const string BackupSuffix = ".bak";

        /// <summary>
        /// Writes text so that the destination is, at every instant, either the old
        /// file or the new one. Creates the folder. The previous contents are left in
        /// "&lt;path&gt;.bak".
        /// </summary>
        public static void WriteText(string path, string contents)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("No path given.", nameof(path));

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string temp   = path + TempSuffix;
            string backup = path + BackupSuffix;

            // Flush(true) pushes the bytes past the OS cache and onto the device. Without
            // it the file can be complete as far as C# is concerned and empty on disk
            // after a power loss — which is precisely the failure this exists to stop.
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write,
                                               FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(true);
            }

            if (!File.Exists(path))
            {
                File.Move(temp, path);     // nothing to replace, nothing to back up
                return;
            }

            try
            {
                File.Replace(temp, path, backup, ignoreMetadataErrors: true);
            }
            catch (Exception e) when (e is PlatformNotSupportedException ||
                                      e is IOException ||
                                      e is UnauthorizedAccessException)
            {
                // Replace refuses across volumes and on some network shares. Back up
                // by MOVE rather than delete, so the old file still exists throughout.
                Swap(temp, path, backup);
            }
        }

        private static void Swap(string temp, string path, string backup)
        {
            if (File.Exists(backup)) File.Delete(backup);

            File.Move(path, backup);       // the old file is now safe under another name

            try
            {
                File.Move(temp, path);
            }
            catch
            {
                // Put it back. Better the previous save than no save at all.
                try { if (!File.Exists(path)) File.Move(backup, path); } catch { }
                throw;
            }
        }

        /// <summary>
        /// Decides what should actually be read for <paramref name="path"/>.
        ///
        /// A ".tmp" beside a file means a save was interrupted. If it is NEWER than the
        /// file and it parses, it is the better copy and is promoted (the older one
        /// becoming the .bak). If it does not parse it is kept as ".tmp.bad" as
        /// evidence. If it is older it is stale rubbish from a previous run.
        ///
        /// <paramref name="isValid"/> is the caller's parser — this class knows nothing
        /// about session or layout shapes. Pass null to skip the check.
        ///
        /// <paramref name="note"/> is a user-facing message, or null when nothing
        /// unusual happened. Never throws: a recovery that fails must not stop a load.
        /// </summary>
        public static string ResolveForRead(string path, Func<string, bool> isValid,
                                            out string note)
        {
            note = null;

            if (string.IsNullOrWhiteSpace(path)) return path;

            string temp = path + TempSuffix;

            try
            {
                if (!File.Exists(temp)) return path;

                bool exists = File.Exists(path);
                bool newer  = !exists ||
                              File.GetLastWriteTimeUtc(temp) > File.GetLastWriteTimeUtc(path);

                string leaf = Path.GetFileName(path);

                if (!newer)
                {
                    File.Delete(temp);
                    return path;
                }

                if (isValid != null && !isValid(temp))
                {
                    string bad = path + TempSuffix + ".bad";
                    if (File.Exists(bad)) File.Delete(bad);
                    File.Move(temp, bad);

                    note = $"A save of \"{leaf}\" was interrupted and the partial file is " +
                           $"unreadable. It has been kept as \"{Path.GetFileName(bad)}\" " +
                           "and the previous good file is being used instead.";
                    return path;
                }

                if (exists)
                {
                    string backup = path + BackupSuffix;
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(path, backup);
                }

                File.Move(temp, path);

                note = $"A save of \"{leaf}\" was interrupted last time. The newer copy " +
                       "has been recovered" +
                       (exists ? $"; the older one is beside it as \"{leaf}{BackupSuffix}\"." : ".");

                return path;
            }
            catch (Exception e)
            {
                note = $"Could not check for an interrupted save of " +
                       $"\"{Path.GetFileName(path)}\": {e.Message}";
                return path;
            }
        }

        /// <summary>
        /// Renames a file out of the way instead of letting it be overwritten:
        /// "session.json" → "session.corrupt-20260921-2253.json". Returns the new path,
        /// or null if it could not be moved.
        /// </summary>
        public static string SetAside(string path, string tag)
        {
            try
            {
                if (!File.Exists(path)) return null;

                string dir  = Path.GetDirectoryName(path) ?? "";
                string stem = Path.GetFileNameWithoutExtension(path);
                string ext  = Path.GetExtension(path);

                string target = Path.Combine(dir,
                    $"{stem}.{tag}-{DateTime.Now:yyyyMMdd-HHmmss}{ext}").Replace('\\', '/');

                // Same second, same name. One suffix is enough; nobody corrupts three
                // files inside a second without a much larger problem.
                if (File.Exists(target)) target += "-2";

                File.Move(path, target);
                return target;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AtomicFile] Could not set \"{path}\" aside: {e.Message}");
                return null;
            }
        }

        /// <summary>True if a usable backup sits beside this file.</summary>
        public static bool HasBackup(string path) =>
            !string.IsNullOrWhiteSpace(path) && File.Exists(path + BackupSuffix);

        /// <summary>
        /// Copies "&lt;path&gt;.bak" back into place. The backup is COPIED, not moved, so a
        /// failed recovery does not also destroy the backup.
        /// </summary>
        public static bool RestoreBackup(string path)
        {
            try
            {
                string backup = path + BackupSuffix;
                if (!File.Exists(backup)) return false;

                File.Copy(backup, path, overwrite: true);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AtomicFile] Could not restore \"{path}{BackupSuffix}\": " +
                                 e.Message);
                return false;
            }
        }
    }
}
