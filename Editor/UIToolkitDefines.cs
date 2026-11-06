using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using PackageSource = UnityEditor.PackageManager.PackageSource;

namespace LightSide.Lean
{
    /// <summary>
    /// Makes the player's compilation of every installed package agree that UI Toolkit is absent, by taking the
    /// <c>versionDefines</c> entries that name its module out of their assembly definitions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A package with such an entry compiles its UI Toolkit code only while the module is in the project, and the
    /// module stays in the project: only the player is held away from it. Left alone, that entry keeps the code in
    /// and every use of it fails to compile. uGUI's panel bridge is the case every uGUI project meets.
    /// </para>
    /// <para>
    /// A package with no such gate keeps failing with a compiler error naming its file, which is the list of work
    /// the setting promises. <see cref="PackageSource.Embedded"/> and <see cref="PackageSource.Local"/> packages
    /// are left alone for the reason <see cref="PackagePatch.Refuse"/> gives.
    /// </para>
    /// </remarks>
    internal static class UIToolkitDefines
    {
        private const string Module = @"com\.unity\.modules\.uielements";
        private const string Entry = @"\{[^{}]*""name""\s*:\s*""" + Module + @"""[^{}]*\}";

        private static readonly Regex Entries = new(@",\s*" + Entry + "|" + Entry + @"\s*,?");

        private static readonly Regex EditorOnly = new(@"""includePlatforms""\s*:\s*\[\s*""Editor""\s*\]");

        /// <summary>
        /// Rewrites each player assembly definition that gates on the module, reporting its package, its path
        /// inside the package and its path on disk to <paramref name="rewriting"/> before writing it, and returns
        /// how many it rewrote.
        /// </summary>
        internal static int Remove(Action<string, string, string> rewriting)
        {
            var rewritten = 0;
            foreach (var package in PackageInfo.GetAllRegisteredPackages())
            {
                if (package.source is PackageSource.Embedded or PackageSource.Local) continue;

                foreach (var path in Directory.GetFiles(package.resolvedPath, "*.asmdef", SearchOption.AllDirectories))
                {
                    var text = File.ReadAllText(path);
                    if (EditorOnly.IsMatch(text) || !Entries.IsMatch(text)) continue;

                    var relative = path.Substring(package.resolvedPath.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    rewriting(package.name, relative, path);
                    File.WriteAllText(path, Entries.Replace(text, string.Empty));
                    Debug.Log($"[Lean] Compiled UI Toolkit out of {package.name}'s {Path.GetFileName(path)} for this build.");
                    rewritten++;
                }
            }
            return rewritten;
        }
    }
}
