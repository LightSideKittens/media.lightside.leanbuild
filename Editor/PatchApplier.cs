using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace LightSide.LeanBuild
{
    /// <summary>
    /// Lays the project's patches over their packages for the length of one build and takes them off
    /// again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window is <see cref="OnPreprocessBuild"/>: the editor's own assemblies are already compiled
    /// and the player's are not, so a source edit made here reaches the player and nothing else.
    /// Measured on 6000.6 — a file rewritten in a package cache during preprocess appears in the player
    /// assembly, is absent from the editor assembly, and provokes no domain reload.
    /// </para>
    /// <para>
    /// This is why patches beat vendoring a package: the archive is small, it lives in the project, and
    /// a machine that re-downloads its packages on every run gets the patch applied again on every run.
    /// </para>
    /// <para>
    /// A build that fails never reaches <see cref="OnPostprocessBuild"/>, and patched files left inside a
    /// package are what the editor then compiles — a confusing state that reads as if the developer's own
    /// edits broke the project. So the undo is reached three ways: the postprocess callback, a delayed
    /// call scheduled the moment the patches go on, which runs on the first editor tick after the build
    /// returns whichever way it went, and the next editor load, from a record written to disk before
    /// anything is touched.
    /// </para>
    /// </remarks>
    internal sealed class PatchApplier : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        private const string WorkDirectory = "Library/LeanBuild";
        private const string PendingFile = WorkDirectory + "/pending-restore.txt";
        private const string BackupDirectory = WorkDirectory + "/backup";

        /// <summary>Runs before anything that reads the player's code, so the patched source is what it sees.</summary>
        public int callbackOrder => -1000;

        /// <inheritdoc/>
        public void OnPreprocessBuild(BuildReport report)
        {
            Restore();
            var patches = PatchStore.Paths();
            if (patches.Count == 0) return;

            var record = new StringBuilder();
            var applied = 0;
            foreach (var path in patches)
            {
                PackagePatch patch;
                try
                {
                    patch = PackagePatch.Read(path);
                }
                catch (Exception e) when (e is InvalidDataException or IOException)
                {
                    Debug.LogWarning($"[LeanBuild] Skipped patch '{Path.GetFileName(path)}': {e.Message}");
                    continue;
                }

                var target = PackageInfo.GetAllRegisteredPackages()
                    .FirstOrDefault(package => package.name == patch.Package);
                var refusal = patch.Refuse(target);
                if (refusal != null)
                {
                    Debug.LogWarning($"[LeanBuild] Skipped patch '{Path.GetFileName(path)}': {refusal}");
                    continue;
                }

                var concern = patch.Concern(target);
                if (concern != null)
                    Debug.LogWarning($"[LeanBuild] Patch '{Path.GetFileName(path)}': {concern}");

                var backup = Path.Combine(BackupDirectory, patch.Package);
                var replaced = new List<string>();
                var added = new List<string>();
                patch.Apply(target.resolvedPath, backup, replaced, added);

                foreach (var relative in replaced) record.AppendLine($"R\t{patch.Package}\t{relative}");
                foreach (var relative in added) record.AppendLine($"A\t{patch.Package}\t{relative}");
                Write(record.ToString());

                applied++;
                Debug.Log($"[LeanBuild] Patched {patch.Package}@{target.version} for this build: " +
                          string.Join(", ", patch.Entries));
            }

            if (applied == 0) return;
            EditorApplication.delayCall += Restore;
            Debug.Log($"[LeanBuild] {applied} patch(es) applied for this build.");
        }

        /// <inheritdoc/>
        public void OnPostprocessBuild(BuildReport report) => Restore();

        /// <summary>Undoes a build that died before it could undo itself, once the editor is ready.</summary>
        /// <remarks>Deferred because the package list is not yet queryable while load callbacks run.</remarks>
        [InitializeOnLoadMethod]
        private static void RestoreAfterLoad() => EditorApplication.delayCall += Restore;

        /// <summary>Puts every patched package file back the way the package had it.</summary>
        [MenuItem("Tools/LightSide/Lean Build/Undo applied patches")]
        internal static void Restore()
        {
            if (!File.Exists(PendingFile)) return;

            var packages = PackageInfo.GetAllRegisteredPackages().ToDictionary(package => package.name);
            var restored = 0;
            foreach (var line in File.ReadAllLines(PendingFile))
            {
                var parts = line.Split('\t');
                if (parts.Length != 3 || !packages.TryGetValue(parts[1], out var package)) continue;

                var live = Path.Combine(package.resolvedPath, parts[2]);
                if (parts[0] == "A")
                {
                    if (File.Exists(live)) File.Delete(live);
                }
                else
                {
                    var saved = Path.Combine(BackupDirectory, parts[1], parts[2]);
                    if (!File.Exists(saved)) continue;
                    File.Copy(saved, live, true);
                }
                restored++;
            }

            File.Delete(PendingFile);
            if (Directory.Exists(BackupDirectory)) Directory.Delete(BackupDirectory, true);
            if (restored > 0) Debug.Log($"[LeanBuild] Put back {restored} patched file(s).");
        }

        private static void Write(string record)
        {
            Directory.CreateDirectory(WorkDirectory);
            File.WriteAllText(PendingFile, record);
        }
    }
}
