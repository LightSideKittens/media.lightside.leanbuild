using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace LightSide.Lean
{
    /// <summary>
    /// Holds <see cref="PatchStore.Permanent"/>'s patches on their packages at all times — in the editor,
    /// in batch mode and in a build — rather than for the length of one build.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A registry package's files live under <c>Library/PackageCache</c>, which Package Manager lays down
    /// again whenever it re-resolves, so a patch that lasts is one that is put back rather than one that
    /// is written once. The store is re-read on every domain reload and whenever the registered packages
    /// change; a pass that finds every file already carrying its patch writes nothing, says nothing and
    /// costs a digest of each patched file.
    /// </para>
    /// <para>
    /// This is what a build-time patch cannot do. A package whose own source does not compile against the
    /// project — one reaching for a type the project's fork of another package does not have — fails the
    /// editor, not only the player, and the editor is compiled long before any build callback runs.
    /// </para>
    /// <para>
    /// No public callback runs between a package being laid down and its first compilation, so on a
    /// machine whose package cache is cold — a fresh checkout on a build agent — that first compilation
    /// reads the unpatched files and its errors are real. The patch lands immediately afterwards and the
    /// editor compiles again; the second compilation is the one the project is judged by. The player is
    /// never affected either way: <see cref="PatchApplier"/> applies this store before the player's own
    /// code is compiled.
    /// </para>
    /// </remarks>
    internal static class PermanentPatches
    {
        private const string AttemptKey = "LightSide.Lean.Reapplied.";
        private const int Attempts = 10;

        /// <summary>Catches a package change that lays files down without reloading the domain.</summary>
        /// <remarks>Every other re-resolve is already covered by <see cref="PatchReload"/>. Registered
        /// again after each domain reload, because Package Manager drops its subscribers on every one.</remarks>
        [InitializeOnLoadMethod]
        private static void Hook() => Events.registeredPackages += _ => InEditor();

        /// <summary>Applies the store and has the editor compile what changed.</summary>
        /// <remarks>
        /// Refreshing costs a domain reload, so it is asked for only when a file was actually written; the
        /// reload runs this again, finds every patch in place and stops there. It is asked for on the next
        /// editor tick because the caller can be inside asset import, where starting another one is not
        /// allowed — the files are on disk by then either way, which is what a build and a batch run need.
        /// </remarks>
        internal static void InEditor()
        {
            if (Apply() == 0) return;
            EditorApplication.delayCall += () => AssetDatabase.Refresh();
        }

        /// <summary>
        /// Writes every patch whose package is not already carrying it, and returns how many files it
        /// wrote. Nothing is backed up: a permanent patch is never taken off.
        /// </summary>
        internal static int Apply()
        {
            var written = 0;
            foreach (var stored in PatchStore.Permanent.Read())
            {
                if (!stored.Usable || stored.Patch.AlreadyApplied(stored.Target.resolvedPath)) continue;
                if (!Allowed(stored)) continue;

                written += stored.Patch.Apply(stored.Target.resolvedPath);
                if (stored.Concern != null)
                    Debug.LogWarning($"[Lean] Patch '{Name(stored)}': {stored.Concern}");
                Debug.Log($"[Lean] Patched {stored.Patch.Package}@{stored.Target.version}: " +
                          string.Join(", ", stored.Patch.Entries));
            }
            return written;
        }

        /// <summary>
        /// Tells the pass that <paramref name="package"/> is about to be laid down again on purpose, so
        /// putting its patch back does not count as a round of the fight <see cref="Allowed"/> watches for.
        /// </summary>
        internal static void ExpectRestore(string package) => SessionState.EraseInt(AttemptKey + package);

        /// <summary>Whether this patch may be written over its package again.</summary>
        /// <remarks>
        /// An editor that puts a package's own files back as fast as this lays the patch over them would
        /// reload the domain for as long as it is open, and never become usable again. Unity does restore
        /// an altered immutable package by itself, so the pass gives up on one it has had to repeat this
        /// many times unasked, and says which package it was. The count belongs to the editor session, so
        /// restarting clears it.
        /// </remarks>
        private static bool Allowed(StoredPatch stored)
        {
            var key = AttemptKey + stored.Patch.Package;
            var attempt = SessionState.GetInt(key, 0) + 1;
            SessionState.SetInt(key, attempt);

            if (attempt <= Attempts) return true;
            if (attempt == Attempts + 1)
                Debug.LogError(
                    $"[Lean] Unity has put {stored.Patch.Package}'s own files back {Attempts} times in " +
                    "this editor session without being asked, so its permanent patch is not being written " +
                    "again. The package will not hold a patch while something keeps restoring it — " +
                    "restart the Editor to try again, or install it from disk, where its files are the " +
                    "project's own.");
            return false;
        }

        private static string Name(StoredPatch stored) => Path.GetFileName(stored.Path);
    }
}
