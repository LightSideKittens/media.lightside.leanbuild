using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using PackageSource = UnityEditor.PackageManager.PackageSource;

namespace LightSide.LeanBuild
{
    /// <summary>
    /// One archive of replacement files for an installed package: entry paths are relative to the
    /// package root, and <see cref="ManifestEntry"/> records the package, the version it was written
    /// against, and the digest each replaced file had at that moment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whole files, not diffs, so a replacement written against one revision of a file undoes whatever
    /// upstream changed in that file later. Nothing is refused over a version number: a package changes
    /// version far more often than it changes any given file, and refusing would throw away the ordinary
    /// case — a patch release that never touched the patched file — to guard against the rare one. A
    /// version that has moved is reported and the patch is applied.
    /// </para>
    /// <para>
    /// The obvious stronger check is unavailable. Detecting that upstream rewrote a file would need the
    /// digest it had before the patch, and by the time a patch is authored the developer has already
    /// overwritten that file in place — the original is gone. The digest recorded here is of the
    /// replacement, which only tells us whether a file already carries it.
    /// </para>
    /// </remarks>
    internal sealed class PackagePatch
    {
        internal const string ManifestEntry = ".leanbuild-patch.json";

        private Manifest manifest;

        private PackagePatch(string path) => Path = path;

        /// <summary>Where the archive lives.</summary>
        internal string Path { get; }

        /// <summary>Package the archive replaces files in.</summary>
        internal string Package => manifest.package;

        /// <summary>Version of that package the replacement was written against.</summary>
        internal string Version => manifest.version;

        /// <summary>Paths inside the package this archive replaces.</summary>
        internal IReadOnlyList<string> Entries => manifest.files.Select(file => file.path).ToArray();

        /// <summary>Reads one archive.</summary>
        /// <exception cref="InvalidDataException">The archive is unreadable, carries no manifest, or
        /// carries no files.</exception>
        internal static PackagePatch Read(string path)
        {
            var patch = new PackagePatch(path);
            using var archive = ZipFile.OpenRead(path);

            var entry = archive.GetEntry(ManifestEntry)
                        ?? throw new InvalidDataException(
                            $"'{System.IO.Path.GetFileName(path)}' has no {ManifestEntry}, so the package " +
                            "it was written for is unknown.");
            using (var reader = new StreamReader(entry.Open()))
                patch.manifest = JsonUtility.FromJson<Manifest>(reader.ReadToEnd());

            if (patch.manifest == null || string.IsNullOrEmpty(patch.manifest.package) ||
                patch.manifest.files == null || patch.manifest.files.Length == 0)
                throw new InvalidDataException(
                    $"{ManifestEntry} must name a package and the files it replaces.");

            foreach (var file in patch.manifest.files) Safe(file.path);
            return patch;
        }

        /// <summary>Why this patch cannot be applied to <paramref name="target"/>, or null when it can.</summary>
        /// <remarks>Only two things stop a patch: a package that is not here, and a package whose source
        /// belongs to the project, where a permanent edit is the honest fix.</remarks>
        internal string Refuse(PackageInfo target)
        {
            if (target == null) return $"'{Package}' is not installed in this project.";
            if (target.source is PackageSource.Embedded or PackageSource.Local)
                return $"'{Package}' is {target.source} — its source belongs to this project, " +
                       "so edit it in place instead of patching it for one build.";
            return null;
        }

        /// <summary>
        /// What the reader should know before this patch is applied to <paramref name="target"/>, or null
        /// when there is nothing to say. A version that has moved on is worth a look and is not an error.
        /// </summary>
        internal string Concern(PackageInfo target) =>
            target.version == Version
                ? null
                : $"written against {Package}@{Version}, installed is {target.version} — " +
                  "if upstream changed these files since, this patch puts the older ones back.";

        /// <summary>
        /// Writes the archive's files into <paramref name="root"/>, copying each original it overwrites
        /// into <paramref name="backup"/> under the same relative path first. Files the package did not
        /// have are recorded in <paramref name="added"/> so they can be deleted again.
        /// </summary>
        internal void Apply(string root, string backup, ICollection<string> replaced, ICollection<string> added)
        {
            using var archive = ZipFile.OpenRead(Path);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName == ManifestEntry || entry.FullName.EndsWith("/", StringComparison.Ordinal))
                    continue;
                var relative = Safe(entry.FullName);
                var destination = System.IO.Path.Combine(root, relative);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);

                if (File.Exists(destination))
                {
                    var saved = System.IO.Path.Combine(backup, relative);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(saved)!);
                    File.Copy(destination, saved, true);
                    replaced.Add(relative);
                }
                else added.Add(relative);

                entry.ExtractToFile(destination, true);
            }
        }

        /// <summary>Whether <paramref name="root"/> already carries exactly this patch's files.</summary>
        internal bool AlreadyApplied(string root) => manifest.files.All(file =>
        {
            var live = System.IO.Path.Combine(root, Safe(file.path));
            return File.Exists(live) && Digest(live) == file.sha;
        });

        /// <summary>Content digest of one file, as recorded in a manifest and recomputed on disk.</summary>
        internal static string Digest(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }

        /// <summary>Refuses an entry path that would escape the package root.</summary>
        /// <exception cref="InvalidDataException">The path is absolute, rooted, or walks upwards.</exception>
        private static string Safe(string entry)
        {
            var relative = (entry ?? string.Empty).Replace('\\', '/').TrimStart('/');
            if (relative.Length == 0 || System.IO.Path.IsPathRooted(relative) ||
                relative.Split('/').Any(part => part == ".."))
                throw new InvalidDataException($"'{entry}' does not stay inside the package.");
            return relative.Replace('/', System.IO.Path.DirectorySeparatorChar);
        }

        [Serializable]
        internal sealed class PatchedFile
        {
            public string path;
            public string sha;
        }

        [Serializable]
        internal sealed class Manifest
        {
            public string package;
            public string version;
            public PatchedFile[] files;
        }
    }
}
