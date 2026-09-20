using System;
using System.IO;
using UnityEngine;

namespace LightSide.Lean
{
    /// <summary>
    /// Where Lean writes inside a project, and how it takes over what it wrote while it was called Lean
    /// Build.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything under <c>ProjectSettings</c> is the project's own and belongs in version control; a
    /// build reads it, so a checkout without it builds differently. Everything under <c>Library</c> is
    /// Lean's to rebuild, except the record of a build's patches, which is the only thing that can undo a
    /// build that died — which is why the rename moves that folder too.
    /// </para>
    /// <para>
    /// The move runs once per editor session, before the first read of any of these paths, and never
    /// writes over something already at the new name. A patch archive is the only copy of a developer's
    /// edits to a package file, and a settings file left behind reads as off rather than as missing — a
    /// build that quietly comes out at full size. Nothing reads the old names afterwards.
    /// </para>
    /// </remarks>
    internal static class LeanPaths
    {
        /// <summary>The project's Lean switches.</summary>
        internal const string Settings = "ProjectSettings/LightSideLean.asset";

        /// <summary>Archives laid over their packages for the length of a build.</summary>
        internal const string BuildPatches = "ProjectSettings/LightSideLeanPatches";

        /// <summary>Archives that stay on their packages.</summary>
        internal const string PermanentPatches = "ProjectSettings/LightSideLeanPermanentPatches";

        /// <summary>Lean's own scratch folder.</summary>
        internal const string Work = "Library/LightSideLean";

        private const string WasSettings = "ProjectSettings/LeanBuild.asset";
        private const string WasPatches = "ProjectSettings/LeanBuildPatches";
        private const string WasWork = "Library/LeanBuild";
        private const string WasIdentifier = "LightSide.LeanBuild::LightSide.LeanBuild.LeanBuildSettings";
        private const string Identifier = "LightSide.Lean::LightSide.Lean.LeanSettings";

        private static bool adopted;

        /// <summary>Moves whatever is still under the former names, once per editor session.</summary>
        internal static void Adopt()
        {
            if (adopted) return;
            adopted = true;

            Move(WasPatches, BuildPatches, Directory.Move);
            Move(WasWork, Work, Directory.Move);
            Move(WasSettings, Settings, Retype);
        }

        /// <summary>
        /// Moves the settings file, naming the class this version reads it into.
        /// </summary>
        /// <remarks>A <see cref="UnityEditor.ScriptableSingleton{T}"/> whose serialized class no longer
        /// matches loads as a fresh one with every value at its default, so the identifier the file
        /// carries is part of what has to be renamed.</remarks>
        private static void Retype(string from, string to)
        {
            File.WriteAllText(to, File.ReadAllText(from).Replace(WasIdentifier, Identifier));
            File.Delete(from);
        }

        private static void Move(string from, string to, Action<string, string> move)
        {
            if (!Present(from) || Present(to)) return;
            try
            {
                move(from, to);
                Debug.Log($"[Lean] Moved '{from}' to '{to}', where this version of the package reads it.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Debug.LogError($"[Lean] '{from}' could not be moved to '{to}': {e.Message}. Until it is " +
                               "moved by hand, what it holds is not in use.");
            }
        }

        private static bool Present(string path) => File.Exists(path) || Directory.Exists(path);
    }
}
