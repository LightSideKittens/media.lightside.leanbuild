using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor.PackageManager;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace LightSide.LeanBuild
{
    /// <summary>One dropped file, resolved to the package it belongs to.</summary>
    internal readonly struct PatchSource
    {
        internal PatchSource(PackageInfo package, string relative, string file)
        {
            Package = package;
            Relative = relative;
            File = file;
        }

        /// <summary>The package that owns the file.</summary>
        internal PackageInfo Package { get; }

        /// <summary>Where the file sits inside that package, with forward slashes.</summary>
        internal string Relative { get; }

        /// <summary>The file on disk to read the replacement from.</summary>
        internal string File { get; }
    }

    /// <summary>The project's patch archives: the folder is the list, and it belongs in version control.</summary>
    /// <remarks>
    /// Kept beside the project's other settings rather than under <c>Assets</c> so the archives are not
    /// imported, and in the project rather than in a package so a continuous-integration checkout carries
    /// them. There is no separate registry of which patches are active: a patch is active because it is
    /// in the folder, which is one fewer thing to fall out of step.
    /// </remarks>
    internal static class PatchStore
    {
        internal const string Directory = "ProjectSettings/LeanBuildPatches";

        /// <summary>Every archive in the folder, by name; empty when the folder is absent.</summary>
        internal static IReadOnlyList<string> Paths() =>
            System.IO.Directory.Exists(Directory)
                ? System.IO.Directory.GetFiles(Directory, "*.zip")
                    .OrderBy(path => path, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();

        /// <summary>
        /// Resolves a path as it arrives from a drag — the asset path the Project window supplies, or the
        /// filesystem path a file manager supplies — into the package that owns it. Null when the file
        /// belongs to no installed package.
        /// </summary>
        internal static PatchSource? Resolve(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            var asset = path.Replace('\\', '/');
            if (asset.StartsWith("Packages/", StringComparison.Ordinal))
            {
                var package = PackageInfo.FindForAssetPath(asset);
                if (package == null) return null;
                var relative = asset.Substring(package.assetPath.Length).TrimStart('/');
                var file = Path.Combine(package.resolvedPath, relative.Replace('/', Path.DirectorySeparatorChar));
                return System.IO.File.Exists(file) ? new PatchSource(package, relative, file) : null;
            }

            var full = Path.GetFullPath(path);
            if (!System.IO.File.Exists(full)) return null;
            PackageInfo best = null;
            foreach (var package in PackageInfo.GetAllRegisteredPackages())
            {
                var root = Path.GetFullPath(package.resolvedPath);
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (best == null || root.Length > Path.GetFullPath(best.resolvedPath).Length) best = package;
            }
            if (best == null) return null;
            var inside = full.Substring(Path.GetFullPath(best.resolvedPath).Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');
            return new PatchSource(best, inside, full);
        }

        /// <summary>
        /// Adds <paramref name="sources"/> to the archive for the package that owns them, creating it
        /// when this is the first patched file and replacing an entry that is already there. Returns
        /// where the archive lives.
        /// </summary>
        /// <exception cref="ArgumentException">Nothing to add, or the files do not all come from one
        /// package.</exception>
        internal static string Add(IReadOnlyList<PatchSource> sources)
        {
            if (sources == null || sources.Count == 0)
                throw new ArgumentException("No files to patch.", nameof(sources));

            var package = sources[0].Package;
            foreach (var source in sources)
                if (source.Package.name != package.name)
                    throw new ArgumentException(
                        "Every file in one patch must come from the same package.", nameof(sources));

            System.IO.Directory.CreateDirectory(Directory);
            var path = PathFor(package.name, package.version);

            var files = Read(path)?.files?.ToList() ?? new List<PackagePatch.PatchedFile>();
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                foreach (var source in sources)
                {
                    archive.GetEntry(source.Relative)?.Delete();
                    archive.CreateEntryFromFile(source.File, source.Relative);
                    files.RemoveAll(file => file.path == source.Relative);
                    files.Add(new PackagePatch.PatchedFile
                    {
                        path = source.Relative,
                        sha = PackagePatch.Digest(source.File),
                    });
                }
                Write(archive, new PackagePatch.Manifest
                {
                    package = package.name,
                    version = package.version,
                    files = files.OrderBy(file => file.path, StringComparer.Ordinal).ToArray(),
                });
            }
            return path;
        }

        /// <summary>Drops one file from a patch, and the patch itself once it holds no files.</summary>
        internal static void Remove(string path, string relative)
        {
            var manifest = Read(path);
            if (manifest == null) return;

            var files = manifest.files.Where(file => file.path != relative).ToArray();
            if (files.Length == 0)
            {
                Remove(path);
                return;
            }

            using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
            archive.GetEntry(relative)?.Delete();
            manifest.files = files;
            Write(archive, manifest);
        }

        /// <summary>
        /// Deletes a patched package's files so Unity restores the untouched ones, and asks it to.
        /// </summary>
        /// <remarks>
        /// The fix a developer makes lives in the archive from the moment it is captured, so the copy they
        /// edited inside the package folder has done its job and is only in the way — it is what the editor
        /// compiles, and it is not what a colleague or a build machine will have. Removing the folder is
        /// how Unity is asked to lay the package down again; it does the same by itself whenever it
        /// re-resolves.
        /// </remarks>
        /// <exception cref="InvalidOperationException">The package's source belongs to the project, where
        /// there is nothing to restore it from.</exception>
        internal static void RestorePackage(PackageInfo package)
        {
            if (package.source is PackageSource.Embedded or PackageSource.Local)
                throw new InvalidOperationException(
                    $"'{package.name}' is {package.source} — its files are this project's own, " +
                    "so Unity has no untouched copy to put back.");

            var root = Path.GetFullPath(package.resolvedPath);
            if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
            Client.Resolve();
        }

        /// <summary>Drops a whole patch.</summary>
        internal static void Remove(string path)
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }

        private static string PathFor(string package, string version) =>
            Path.Combine(Directory, $"{package}@{version}.zip");

        private static PackagePatch.Manifest Read(string path)
        {
            if (!System.IO.File.Exists(path)) return null;
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry(PackagePatch.ManifestEntry);
            if (entry == null) return null;
            using var reader = new StreamReader(entry.Open());
            return JsonUtility.FromJson<PackagePatch.Manifest>(reader.ReadToEnd());
        }

        private static void Write(ZipArchive archive, PackagePatch.Manifest manifest)
        {
            archive.GetEntry(PackagePatch.ManifestEntry)?.Delete();
            var entry = archive.CreateEntry(PackagePatch.ManifestEntry);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(JsonUtility.ToJson(manifest, true));
        }
    }
}
