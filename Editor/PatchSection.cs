using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace LightSide.Lean
{
    /// <summary>One store's patch list and the drop target that authors one, as they appear in the
    /// settings page.</summary>
    /// <remarks>
    /// <para>
    /// Authoring is a drop, not a form, because the path a file already sits at names its package, its
    /// version and its place inside the package. The developer fixes the file where the compiler pointed
    /// them, drops it here, and the archive is written from what the path already says.
    /// </para>
    /// <para>
    /// The two stores are drawn by the same code because they differ only in when what they hold is laid
    /// down; every gesture — capture, reveal, restore, drop — means the same thing in both. What they do
    /// not share is the wording, which has to say when a patch takes effect or the page would be telling
    /// a developer the wrong thing about their own project.
    /// </para>
    /// </remarks>
    internal static class PatchSection
    {
        private const string FoldPreference = "LightSide.Lean.Open.";

        /// <summary>What one section says about itself, where saying the same as the other would mislead.</summary>
        private readonly struct Copy
        {
            internal Copy(string title, string lead, string restores, string absent, string removes)
            {
                Title = title;
                Lead = lead;
                Restores = restores;
                Absent = absent;
                Removes = removes;
            }

            /// <summary>The section's heading.</summary>
            internal string Title { get; }

            /// <summary>What the section holds and when it takes effect.</summary>
            internal string Lead { get; }

            /// <summary>What happens to the patch after the package is laid down again.</summary>
            internal string Restores { get; }

            /// <summary>Why a patched file may not be inside its package at this moment.</summary>
            internal string Absent { get; }

            /// <summary>What dropping a patch does beyond deleting it, or null when it does nothing.</summary>
            internal string Removes { get; }
        }

        private static readonly Copy ForBuild = new(
            "Build Patches",
            "Fix a package file where it lives, drop it here, and Lean writes an archive pinned to that " +
            "package's installed version. These are laid over their packages before a build compiles the " +
            "player and taken off afterwards, so a machine that downloads its packages fresh on every run " +
            "is patched on every run.",
            "this patch keeps your change and lays it back over the package on the next build.",
            "a build patch lays its files down only for the length of a build.",
            null);

        private static readonly Copy ForPermanent = new(
            "Permanent Patches",
            "The same archives, except that these stay on. They are laid down again whenever Unity puts a " +
            "package back — on editor load, when the registered packages change, and before a build — so " +
            "a package whose own source does not compile against this project is fixed in the Editor, in " +
            "batch mode and on a build agent, and not only in the player.",
            "this patch keeps your change and Lean lays it back over the package once Unity has finished.",
            "a permanent patch is laid down again on the next editor load.",
            "Unity lays that package's own files down again afterwards, and whatever still patches it is " +
            "put back.");

        /// <summary>Builds <paramref name="store"/>'s section under <paramref name="parent"/>.</summary>
        internal static void Build(VisualElement parent, PatchStore store)
        {
            var copy = store == PatchStore.Permanent ? ForPermanent : ForBuild;

            var card = new VisualElement();
            card.AddToClassList("lean__card");
            parent.Add(card);

            var chevron = Chevron();
            var badge = Label(string.Empty, "lean__count");
            var header = new VisualElement();
            header.AddToClassList("lean__header");
            header.Add(chevron);
            header.Add(Label(copy.Title, "lean__title"));
            header.Add(badge);
            card.Add(header);

            var content = new VisualElement();
            content.AddToClassList("lean__content");
            card.Add(content);

            var key = FoldPreference + store.Directory;
            var open = EditorPrefs.GetBool(key, false);

            void Fold(bool value, bool animate)
            {
                open = value;
                EditorPrefs.SetBool(key, value);
                Unroll.Set(content, value, animate);
                chevron.EnableInClassList("lean__chevron--open", value);
            }

            Fold(open, false);
            header.AddManipulator(new Clickable(() => Fold(!open, true)));

            var list = new VisualElement();
            list.AddToClassList("lean__list");

            void Refresh()
            {
                list.Clear();
                var stored = store.Read();
                badge.text = stored.Count.ToString();
                badge.style.display = stored.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;

                if (stored.Count == 0)
                {
                    list.Add(Label($"No {copy.Title.ToLowerInvariant()} in this project.", "lean__empty"));
                    return;
                }
                foreach (var patch in stored) list.Add(BuildPatch(patch, store, copy, Refresh));
            }

            content.Add(Label(copy.Lead, "lean__lead"));
            content.Add(BuildDropTarget(store, Refresh));
            content.Add(BuildRestoreAll(store));
            content.Add(list);
            Refresh();
        }

        private static VisualElement BuildDropTarget(PatchStore store, Action changed)
        {
            var drop = Label("Drop fixed package files here", "lean__drop");
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
                    Debug.LogWarning("[Lean] Those files are not inside an installed package, " +
                                     "so there is nothing to patch.");
                    return;
                }

                foreach (var group in sources.GroupBy(source => source.Package.name))
                {
                    try
                    {
                        var path = store.Add(group.ToArray());
                        Debug.Log($"[Lean] Wrote {path} for {group.Key}@{group.First().Package.version}: " +
                                  string.Join(", ", group.Select(source => source.Relative)));
                    }
                    catch (Exception e) when (e is ArgumentException or IOException)
                    {
                        Debug.LogWarning($"[Lean] Could not write a patch for {group.Key}: {e.Message}");
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

        private static VisualElement BuildPatch(StoredPatch stored, PatchStore store, Copy copy,
            Action changed)
        {
            var block = new VisualElement();
            block.AddToClassList("lean__patch");
            var name = Path.GetFileName(stored.Path);

            if (stored.Patch == null)
            {
                block.Add(Row(Label($"{name} — unreadable", "lean__subtitle"),
                    Remove($"Delete {name}", () =>
                    {
                        if (!Confirm($"Delete {name}?\n\nIt cannot be read, so what it holds cannot be " +
                                     "shown here — it may still carry edits of yours."))
                            return;
                        PatchStore.Remove(stored.Path);
                        changed();
                    })));
                block.Add(Label(stored.Problem, "lean__problem"));
                return block;
            }

            var patch = stored.Patch;
            var entries = patch.Entries;
            var header = new VisualElement();
            header.AddToClassList("lean__row");
            header.Add(Label($"{patch.Package}@{patch.Version}", "lean__subtitle"));
            if (stored.Usable)
            {
                header.Add(Action("↗", $"Open {patch.Package}'s folder in the file browser.",
                    () => Reveal(patch.Package, "package.json", copy)));
                header.Add(Action("↺", $"Delete {patch.Package}'s files so Unity lays the package down " +
                                       $"again; {copy.Restores}",
                    () => Restore(patch.Package)));
            }
            header.Add(Remove($"Drop the whole patch for {patch.Package}", () =>
            {
                if (!Confirm($"Delete the patch for {patch.Package}@{patch.Version}?\n\n" +
                             $"It is the only copy of your edits to {entries.Count} " +
                             $"{(entries.Count == 1 ? "file" : "files")}, unless the archive is under " +
                             "version control." + Aftermath(copy)))
                    return;
                PatchStore.Remove(stored.Path);
                Dropped(stored, store, changed);
            }));
            block.Add(header);

            if (stored.Problem != null) block.Add(Label(stored.Problem, "lean__problem"));
            else if (stored.Concern != null) block.Add(Label(stored.Concern, "lean__problem"));

            foreach (var entry in entries)
            {
                var relative = entry;
                var file = Label(relative, "lean__file");
                file.tooltip = relative;
                file.pickingMode = PickingMode.Position;

                var row = Row(file,
                    Action("↗", $"Show {relative} in the file browser.",
                        () => Reveal(patch.Package, relative, copy)),
                    Remove($"Stop patching {relative}", () =>
                    {
                        if (!Confirm($"Stop patching {relative}?\n\nThis drops your edited copy of the file" +
                                     (entries.Count == 1
                                         ? $", and with it the whole patch for {patch.Package}."
                                         : ".") + Aftermath(copy)))
                            return;
                        PatchStore.Remove(stored.Path, relative);
                        Dropped(stored, store, changed);
                    }));
                row.AddToClassList("lean__row--file");
                block.Add(row);
            }
            return block;
        }

        /// <summary>
        /// Puts the package back after a patch that was on it is dropped, so what is on disk is what the
        /// project now asks for rather than the last thing written to it.
        /// </summary>
        /// <remarks>Only a permanent patch is on the package outside a build; a build patch was taken off
        /// when its build ended, so there is nothing of it left to undo.</remarks>
        private static void Dropped(StoredPatch stored, PatchStore store, Action changed)
        {
            if (store == PatchStore.Permanent && stored.Target != null) Restore(stored.Patch.Package);
            changed();
        }

        private static string Aftermath(Copy copy) => copy.Removes == null ? string.Empty : "\n\n" + copy.Removes;

        /// <summary>
        /// Asks before a deletion, because a patch is where the developer's edit lives: the copy inside
        /// the package is overwritten on the next resolve, so nothing else in the project has it.
        /// </summary>
        private static bool Confirm(string message) =>
            EditorUtility.DisplayDialog("Lean", message, "Delete", "Cancel");

        private static VisualElement Row(params VisualElement[] items)
        {
            var row = new VisualElement();
            row.AddToClassList("lean__row");
            foreach (var item in items) row.Add(item);
            return row;
        }

        /// <summary>The house list's removal action: a minus, with the target named in its tooltip.</summary>
        private static Button Remove(string tooltip, Action clicked)
        {
            var button = new Button(clicked) { text = "−", tooltip = tooltip };
            button.AddToClassList("lean__remove");
            return button;
        }

        private static Button Action(string glyph, string tooltip, Action clicked)
        {
            var button = new Button(clicked) { text = glyph, tooltip = tooltip };
            button.AddToClassList("lean__action");
            return button;
        }

        /// <summary>Restores every package a patch covers, so nothing has to be hunted for by hand.</summary>
        private static Button BuildRestoreAll(PatchStore store)
        {
            var button = new Button(() =>
            {
                var packages = store.Read()
                    .Where(stored => stored.Usable)
                    .Select(stored => stored.Patch.Package)
                    .Distinct()
                    .ToArray();
                if (packages.Length == 0)
                {
                    Debug.Log("[Lean] No patched packages to restore.");
                    return;
                }
                foreach (var package in packages) Restore(package);
            })
            {
                text = "Restore every patched package",
                tooltip = "Deletes the files of every package a patch covers so Unity lays them down " +
                          "again. Use it after editing a package in place: the patch already carries " +
                          "your change, and the copy inside the package is what the editor compiles.",
            };
            button.AddToClassList("lean__wide");
            return button;
        }

        private static void Restore(string package)
        {
            var info = Installed(package);
            if (info == null) return;

            try
            {
                PermanentPatches.ExpectRestore(package);
                PatchStore.RestorePackage(info);
                Debug.Log($"[Lean] Deleted {package}'s files; Unity is laying the package down again.");
            }
            catch (Exception e) when (e is InvalidOperationException or IOException)
            {
                Debug.LogWarning($"[Lean] Could not restore {package}: {e.Message}");
            }
        }

        /// <summary>Shows one file of a patched package in the operating system's file browser.</summary>
        /// <remarks>Windows and macOS reveal a folder by selecting it in its parent instead of opening it,
        /// so a file inside is always what gets pointed at — the manifest every registered package carries,
        /// when the package as a whole is meant.</remarks>
        private static void Reveal(string package, string relative, Copy copy)
        {
            var info = Installed(package);
            if (info == null) return;

            var file = Path.Combine(info.resolvedPath, relative);
            if (!File.Exists(file))
            {
                Debug.LogWarning($"[Lean] '{relative}' is not in {package} right now — {copy.Absent}");
                return;
            }
            EditorUtility.RevealInFinder(file);
        }

        private static PackageInfo Installed(string package)
        {
            var info = PackageInfo.GetAllRegisteredPackages()
                .FirstOrDefault(candidate => candidate.name == package);
            if (info == null) Debug.LogWarning($"[Lean] '{package}' is not installed in this project.");
            return info;
        }

        private static Label Label(string text, string className)
        {
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList(className);
            return label;
        }

        /// <summary>A fold marker drawn the LightSide way: two accent strokes, turned by the stylesheet.</summary>
        /// <remarks>Two strokes rather than one of the triangles a font carries, because those are sized
        /// and placed by the editor font's own metrics — different on every platform, and centred on a
        /// text baseline rather than on the row.</remarks>
        private static VisualElement Chevron()
        {
            var chevron = Mark("lean__chevron");
            chevron.Add(Mark("lean__chevron-top"));
            chevron.Add(Mark("lean__chevron-bottom"));
            return chevron;
        }

        private static VisualElement Mark(string className)
        {
            var mark = new VisualElement { pickingMode = PickingMode.Ignore };
            mark.AddToClassList(className);
            return mark;
        }
    }
}
