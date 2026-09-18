using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace LightSide.LeanBuild
{
    /// <summary>The patch list and the drop target that authors one, as they appear in the settings page.</summary>
    /// <remarks>
    /// Authoring is a drop, not a form, because the path a file already sits at names its package, its
    /// version and its place inside the package. The developer fixes the file where the compiler pointed
    /// them, drops it here, and the archive is written from what the path already says.
    /// </remarks>
    internal static class PatchSection
    {
        /// <summary>Builds the section under <paramref name="parent"/>.</summary>
        internal static void Build(VisualElement parent)
        {
            var card = new VisualElement();
            card.AddToClassList("leanbuild__card");
            parent.Add(card);

            card.Add(Label("Patches", "leanbuild__title"));
            card.Add(Label(
                "A failing build names the files that keep UI Toolkit in the player. Fix one where it lives, " +
                "drop it here, and Lean Build writes a patch pinned to that package's installed version. " +
                "Patches are applied for the length of a build and taken off afterwards, so a machine " +
                "that downloads its packages fresh every run is patched every run.",
                "leanbuild__lead"));

            var list = new VisualElement();
            list.AddToClassList("leanbuild__list");
            card.Add(BuildDropTarget(() => Refresh(list)));

            var restoreAll = new Button(RestoreAll)
            {
                text = "Restore every patched package",
                tooltip = "Deletes the files of every package a patch covers so Unity lays them down " +
                          "again. Use it after editing a package in place: the patch already carries " +
                          "your change, and the copy inside the package is what the editor compiles.",
            };
            restoreAll.AddToClassList("leanbuild__wide");
            card.Add(restoreAll);

            card.Add(list);
            Refresh(list);
        }

        private static VisualElement BuildDropTarget(Action changed)
        {
            var drop = Label("Drop fixed package files here", "leanbuild__drop");
            drop.pickingMode = PickingMode.Position;
            drop.RegisterCallback<DragUpdatedEvent>(_ =>
            {
                DragAndDrop.visualMode = Resolve(DragAndDrop.paths).Count > 0
                    ? DragAndDropVisualMode.Copy
                    : DragAndDropVisualMode.Rejected;
            });
            drop.RegisterCallback<DragPerformEvent>(_ =>
            {
                DragAndDrop.AcceptDrag();
                var sources = Resolve(DragAndDrop.paths);
                if (sources.Count == 0)
                {
                    Debug.LogWarning("[LeanBuild] Those files are not inside an installed package, " +
                                     "so there is nothing to patch.");
                    return;
                }

                foreach (var group in sources.GroupBy(source => source.Package.name))
                {
                    try
                    {
                        var path = PatchStore.Add(group.ToArray());
                        Debug.Log($"[LeanBuild] Wrote {path} for {group.Key}@{group.First().Package.version}: " +
                                  string.Join(", ", group.Select(source => source.Relative)));
                    }
                    catch (Exception e) when (e is ArgumentException or IOException)
                    {
                        Debug.LogWarning($"[LeanBuild] Could not write a patch for {group.Key}: {e.Message}");
                    }
                }
                changed();
            });
            return drop;
        }

        private static List<PatchSource> Resolve(IEnumerable<string> paths)
        {
            var sources = new List<PatchSource>();
            foreach (var path in paths ?? Array.Empty<string>())
                if (PatchStore.Resolve(path) is { } source)
                    sources.Add(source);
            return sources;
        }

        private static void Refresh(VisualElement list)
        {
            list.Clear();
            var paths = PatchStore.Paths();
            if (paths.Count == 0)
            {
                list.Add(Label("No patches in this project.", "leanbuild__empty"));
                return;
            }

            foreach (var path in paths) list.Add(BuildPatch(path, () => Refresh(list)));
        }

        private static VisualElement BuildPatch(string path, Action changed)
        {
            var block = new VisualElement();
            block.AddToClassList("leanbuild__patch");

            PackagePatch patch;
            try
            {
                patch = PackagePatch.Read(path);
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                var name = Path.GetFileName(path);
                block.Add(Row(Label($"{name} — unreadable: {e.Message}", "leanbuild__subtitle"),
                    Remove($"Delete {name}", () =>
                    {
                        if (!Confirm($"Delete {name}?\n\nIt cannot be read, so what it holds cannot be " +
                                     "shown here — it may still carry edits of yours."))
                            return;
                        PatchStore.Remove(path);
                        changed();
                    })));
                return block;
            }

            var entries = patch.Entries;
            var header = new VisualElement();
            header.AddToClassList("leanbuild__row");
            header.Add(Label($"{patch.Package}@{patch.Version}", "leanbuild__subtitle"));
            header.Add(Action("↗", $"Open {patch.Package}'s folder in the file browser.",
                () => Reveal(patch.Package, "package.json")));
            header.Add(Action("↺", $"Delete {patch.Package}'s files so Unity lays the package down again; " +
                                   "this patch keeps your change and applies it at build time.",
                () => Restore(patch.Package)));
            header.Add(Remove($"Drop the whole patch for {patch.Package}", () =>
            {
                if (!Confirm($"Delete the patch for {patch.Package}@{patch.Version}?\n\n" +
                             $"It is the only copy of your edits to {entries.Count} " +
                             $"{(entries.Count == 1 ? "file" : "files")}, unless the archive is under " +
                             "version control."))
                    return;
                PatchStore.Remove(path);
                changed();
            }));
            block.Add(header);

            foreach (var entry in entries)
            {
                var relative = entry;
                var file = Label(relative, "leanbuild__file");
                file.tooltip = relative;
                file.pickingMode = PickingMode.Position;

                var row = Row(file,
                    Action("↗", $"Show {relative} in the file browser.",
                        () => Reveal(patch.Package, relative)),
                    Remove($"Stop patching {relative}", () =>
                    {
                        if (!Confirm($"Stop patching {relative}?\n\nThis drops your edited copy of the file" +
                                     (entries.Count == 1
                                         ? $", and with it the whole patch for {patch.Package}."
                                         : ".")))
                            return;
                        PatchStore.Remove(path, relative);
                        changed();
                    }));
                row.AddToClassList("leanbuild__row--file");
                block.Add(row);
            }
            return block;
        }

        /// <summary>
        /// Asks before a deletion, because a patch is where the developer's edit lives: the copy inside
        /// the package is overwritten on the next resolve, so nothing else in the project has it.
        /// </summary>
        private static bool Confirm(string message) =>
            EditorUtility.DisplayDialog("Lean Build", message, "Delete", "Cancel");

        private static VisualElement Row(params VisualElement[] items)
        {
            var row = new VisualElement();
            row.AddToClassList("leanbuild__row");
            foreach (var item in items) row.Add(item);
            return row;
        }

        /// <summary>The house list's removal action: a minus, with the target named in its tooltip.</summary>
        private static Button Remove(string tooltip, Action clicked)
        {
            var button = new Button(clicked) { text = "−", tooltip = tooltip };
            button.AddToClassList("leanbuild__remove");
            return button;
        }

        private static Button Action(string glyph, string tooltip, Action clicked)
        {
            var button = new Button(clicked) { text = glyph, tooltip = tooltip };
            button.AddToClassList("leanbuild__action");
            return button;
        }

        /// <summary>Restores every package a patch covers, so nothing has to be hunted for by hand.</summary>
        private static void RestoreAll()
        {
            var packages = new List<string>();
            foreach (var path in PatchStore.Paths())
            {
                try
                {
                    packages.Add(PackagePatch.Read(path).Package);
                }
                catch (Exception e) when (e is InvalidDataException or IOException)
                {
                    Debug.LogWarning($"[LeanBuild] Skipped '{Path.GetFileName(path)}': {e.Message}");
                }
            }

            if (packages.Count == 0)
            {
                Debug.Log("[LeanBuild] No patched packages to restore.");
                return;
            }
            foreach (var package in packages.Distinct()) Restore(package);
        }

        private static void Restore(string package)
        {
            var info = Installed(package);
            if (info == null) return;

            try
            {
                PatchStore.RestorePackage(info);
                Debug.Log($"[LeanBuild] Deleted {package}'s files; Unity is laying the package down again.");
            }
            catch (Exception e) when (e is InvalidOperationException or IOException)
            {
                Debug.LogWarning($"[LeanBuild] Could not restore {package}: {e.Message}");
            }
        }

        /// <summary>Shows one file of a patched package in the operating system's file browser.</summary>
        /// <remarks>Windows and macOS reveal a folder by selecting it in its parent instead of opening it,
        /// so a file inside is always what gets pointed at — the manifest every registered package carries,
        /// when the package as a whole is meant.</remarks>
        private static void Reveal(string package, string relative)
        {
            var info = Installed(package);
            if (info == null) return;

            var file = Path.Combine(info.resolvedPath, relative);
            if (!File.Exists(file))
            {
                Debug.LogWarning($"[LeanBuild] '{relative}' is not in {package} right now — a patch lays " +
                                 "its files down only for the length of a build.");
                return;
            }
            EditorUtility.RevealInFinder(file);
        }

        private static PackageInfo Installed(string package)
        {
            var info = PackageInfo.GetAllRegisteredPackages()
                .FirstOrDefault(candidate => candidate.name == package);
            if (info == null) Debug.LogWarning($"[LeanBuild] '{package}' is not installed in this project.");
            return info;
        }

        private static Label Label(string text, string className)
        {
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList(className);
            return label;
        }
    }
}
