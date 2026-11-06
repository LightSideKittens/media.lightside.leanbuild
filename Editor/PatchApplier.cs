using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace LightSide.Lean
{
    /// <summary>
    /// Lays the project's patches over their packages: <see cref="PatchStore.Permanent"/>'s for good, and
    /// <see cref="PatchStore.Build"/>'s and <see cref="UIToolkitDefines"/>'s rewrites for the length of one build.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window for a build patch is <see cref="OnPreprocessBuild"/>: the editor's own assemblies are
    /// already compiled and the player's are not, so a source edit made here reaches the player and
    /// nothing else. Measured on 6000.6 — a file rewritten in a package cache during preprocess appears in
    /// the player assembly, is absent from the editor assembly, and provokes no domain reload.
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
    /// <para>
    /// The two stores meet in one place on purpose. An undo puts back what a build patch found, which for
    /// a package that a permanent patch also covers is the permanently patched file — so the undo has to
    /// run before the permanent pass, and the permanent pass before the build patches that may overwrite
    /// the same files. Split across callbacks whose order Unity does not define, that sequence would be a
    /// race.
    /// </para>
    /// </remarks>
    internal sealed class PatchApplier : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        private const string PendingFile = LeanPaths.Work + "/pending-restore.txt";
        private const string BackupDirectory = LeanPaths.Work + "/backup";

        /// <summary>Runs before anything that reads the player's code, so the patched source is what it sees.</summary>
        public int callbackOrder => -1000;

        /// <inheritdoc/>
        public void OnPreprocessBuild(BuildReport report)
        {
            Restore();
            if (Directory.Exists(BackupDirectory)) Directory.Delete(BackupDirectory, true);
            PermanentPatches.Apply();
            EditorApplication.delayCall += Restore;

            var applied = 0;
            foreach (var stored in PatchStore.Build.Read())
            {
                var name = Path.GetFileName(stored.Path);
                if (!stored.Usable)
                {
                    Debug.LogWarning($"[Lean] Skipped patch '{name}': {stored.Problem}");
                    continue;
                }
                if (stored.Concern != null)
                    Debug.LogWarning($"[Lean] Patch '{name}': {stored.Concern}");

                var package = stored.Patch.Package;
                stored.Patch.Apply(stored.Target.resolvedPath, Path.Combine(BackupDirectory, package),
                    (replaced, relative) => Record($"{(replaced ? 'R' : 'A')}\t{package}\t{relative}"));

                applied++;
                Debug.Log($"[Lean] Patched {stored.Patch.Package}@{stored.Target.version} for this build: " +
                          string.Join(", ", stored.Patch.Entries));
            }

            if (applied > 0) Debug.Log($"[Lean] {applied} patch(es) applied for this build.");

            if (LeanSettings.StripUIToolkit) UIToolkitDefines.Remove(Preserve);
        }

        /// <inheritdoc/>
        public void OnPostprocessBuild(BuildReport report) => Restore();

        private static void Preserve(string package, string relative, string live)
        {
            Record($"R\t{package}\t{relative}");
            var saved = Path.Combine(BackupDirectory, package, relative);
            if (File.Exists(saved)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            File.Copy(live, saved);
        }

        /// <summary>Puts every file a build patch replaced back the way the package had it.</summary>
        [MenuItem("Tools/LightSide/Lean/Undo this build's patches")]
        internal static void Restore()
        {
            LeanPaths.Adopt();
            if (!File.Exists(PendingFile)) return;

            var packages = PackageInfo.GetAllRegisteredPackages().ToDictionary(package => package.name);
            var restored = 0;
            foreach (var line in File.ReadAllLines(PendingFile).Reverse())
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

            if (Directory.Exists(BackupDirectory)) Directory.Delete(BackupDirectory, true);
            File.Delete(PendingFile);
            if (restored > 0) Debug.Log($"[Lean] Put back {restored} patched file(s).");
        }

        private static void Record(string line)
        {
            Directory.CreateDirectory(LeanPaths.Work);
            File.AppendAllText(PendingFile, line + "\n");
        }
    }

    /// <summary>The one thing the package does on a domain reload, so its two stores keep their order.</summary>
    /// <remarks>
    /// <para>
    /// Undoes a build that died before it could undo itself, then puts the permanent patches back on. A
    /// project with neither costs one <c>File.Exists</c> and one <c>Directory.Exists</c>.
    /// </para>
    /// <para>
    /// This is an asset postprocessor rather than an <see cref="InitializeOnLoadMethodAttribute"/> because
    /// a callback taking <c>didDomainReload</c> is the only load-time hook that meets all three
    /// conditions: Unity runs it after asset import finishes, where the package list is queryable at last;
    /// it runs on every domain reload, even one that imported nothing; and it runs synchronously, which a
    /// <c>-batchmode</c> run that quits after one method never promised the next editor tick would.
    /// </para>
    /// </remarks>
    internal sealed class PatchReload : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved,
            string[] movedFrom, bool didDomainReload)
        {
            if (!didDomainReload) return;
            PatchApplier.Restore();
            PermanentPatches.InEditor();
        }
    }
}
