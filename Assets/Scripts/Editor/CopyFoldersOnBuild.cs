using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace LaunchpadStudio
{
    /// <summary>
    /// Copies user-visible folders from the PROJECT ROOT into the build, next to the
    /// executable, so they exist on a first run.
    ///
    /// Why not StreamingAssets: in a build that is buried at <App>_Data/StreamingAssets,
    /// which is exactly where a user will never look to drop in a new sample. A folder
    /// beside the .exe is one a user can find, and Manager resolves a relative Library
    /// Root ("Samples") against that same place — project folder in the editor, exe
    /// folder in a build — so one Inspector value works in both.
    ///
    /// Files are copied over, never deleted: rebuilding into a folder where you have
    /// added samples by hand keeps them.
    /// </summary>
    public class CopyFoldersOnBuild : IPostprocessBuildWithReport
    {
        /// <summary>
        /// Project-root folders to ship. Add "Layouts" to ship starter kits as well —
        /// a layout stores its paths relative to the library root, so kits built on
        /// Samples load correctly in the build.
        /// </summary>
        private static readonly string[] Folders = { "Samples", "Layouts" };

        public int callbackOrder => 0;

        public void OnPostprocessBuild(BuildReport report)
        {
            string output = report.summary.outputPath;
            string dest;

            switch (report.summary.platform)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                case BuildTarget.StandaloneLinux64:
                    // outputPath is the .exe itself; its folder is what
                    // Directory.GetParent(Application.dataPath) returns at runtime.
                    dest = Path.GetDirectoryName(output);
                    break;

                case BuildTarget.StandaloneOSX:
                    // At runtime dataPath is X.app/Contents, whose parent is X.app —
                    // so the resolver looks INSIDE the bundle. Match it for now; a
                    // user-visible Mac location is version-2.0 platform work.
                    dest = output;
                    break;

                default:
                    Debug.Log($"[CopyFoldersOnBuild] {report.summary.platform}: nothing copied " +
                              "(standalone platforms only).");
                    return;
            }

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;

            foreach (var folder in Folders)
            {
                string source = Path.Combine(projectRoot, folder);

                if (!Directory.Exists(source))
                {
                    Debug.LogWarning($"[CopyFoldersOnBuild] \"{source}\" does not exist, so the " +
                                     $"build has no {folder} folder. A first run will show " +
                                     "\"not found\" in the library.");
                    continue;
                }

                string target = Path.Combine(dest, folder);
                int copied = CopyTree(source, target);

                Debug.Log($"[CopyFoldersOnBuild] {folder}: {copied} file(s) → \"{target}\".");
            }
        }

        private static int CopyTree(string source, string target)
        {
            Directory.CreateDirectory(target);
            int count = 0;

            foreach (var file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file);

                // Hidden files (.DS_Store, desktop.ini-style) and half-written temps
                // have no business in a shipped folder.
                if (name.StartsWith(".") || name.EndsWith(".tmp")) continue;
                if ((File.GetAttributes(file) & FileAttributes.Hidden) != 0) continue;

                File.Copy(file, Path.Combine(target, name), overwrite: true);
                count++;
            }

            foreach (var dir in Directory.GetDirectories(source))
            {
                if ((File.GetAttributes(dir) & FileAttributes.Hidden) != 0) continue;
                count += CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)));
            }

            return count;
        }
    }
}