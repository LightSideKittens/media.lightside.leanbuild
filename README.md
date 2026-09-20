# LightSide Lean

Takes out of a Unity project what Unity will not let you take out, and patches whatever breaks.

Two jobs, one page. **Strip UI Toolkit** removes the UIElements runtime from players that never draw
with it. **Patches** keep your fixes to files inside other people's packages — the ones Unity
overwrites whenever it re-resolves — and lay them down again, either for the length of a build or
permanently.

**Project Settings → LightSide → Lean.**

---

## Strip UI Toolkit

Unity compiles the whole UIElements runtime into builds that cannot use it. Removing
`com.unity.modules.uielements` or guarding every reference in your own code does not move it. This
package cuts the roots Unity's editor seeds into the player's managed link, and holds the engine
module out of the player's compilation so nothing can quietly put it back.

Measured on an Android IL2CPP build, ARM64, managed stripping High:

| | before | after |
|---|---|---|
| `libil2cpp.so` | 38 602 560 | 25 008 216 |
| `global-metadata.dat` | 5 561 794 | 3 584 102 |
| resident memory | 237.02 MiB | 227.15 MiB |

Resident figures are the mean of five interleaved runs on one device, sampled nine seconds after
launch.

Off by default. The setting lives in `ProjectSettings/LightSideLean.asset`. **Commit that file.** It
is what a headless build reads, so a machine that checks out the project without it builds at full
size — silently, with no warning. A missing file means the setting is off; nothing fails.

Batch builds are the point, not an afterthought: everything runs from build callbacks and was
measured under `-batchmode -nographics -quit`.

The Editor is unaffected. The package stays installed, the manifest is untouched, and inspectors,
overlays and editor windows written against UIElements keep working — including those in packages you
do not control.

### The build fails first, and that is the design

Cutting the editor's roots only helps a player whose own code never reaches UI Toolkit, and several
common packages reach it from runtime assemblies: **Input System** (its UI input module), **Universal
RP** (its UI Toolkit renderer pass), **Localization**, and uGUI's `PanelEventHandler` and
`PanelRaycaster`. While any of them is compiled in, the linker keeps the module and cutting roots
saves nothing.

So the setting does not quietly settle for that. With it on, the engine module is held out of the
player's compilation, and every player file that touches UI Toolkit becomes a compiler error naming
its line. Turn it on, build, and the failures are your list of work.

The Editor's own assemblies are already compiled by the time this happens, so they are never
affected, and the exclusion is lifted whichever way the build ends.

### What it costs you

Players stop supporting UI Toolkit. Code that uses it no longer compiles, which is the point — but
**a `UIDocument` placed in a scene draws nothing**, and no compiler can warn about that. Turn the
setting on only when you know your players do not use UI Toolkit.

---

## Patches

A fix to someone else's package lives inside a package folder, which Unity overwrites whenever it
re-resolves. Vendoring the package keeps the fix and loses every update after it. A patch keeps both:

1. Fix the file where it lives.
2. Drag it onto a patch section on the settings page. Lean writes an archive pinned to that package's
   installed version.
3. Press `↺` to put the package back as it was — the archive now carries your change.

Archives live in `ProjectSettings`, belong in version control, and are laid down again on a machine
that downloads its packages fresh on every run. The folder is the list: an archive is active because
it is in the folder.

Which folder decides when it is laid down, and that is the only difference between the two sections.
Nothing inside an archive names its section, so moving the file between the two folders is how a
patch changes scope.

### Build Patches — `ProjectSettings/LightSideLeanPatches`

Laid over their packages before a build compiles the player, and taken off afterwards. The window is
exact: the editor's own assemblies are already compiled and the player's are not, so the edit reaches
the player and nothing else.

This is what **Strip UI Toolkit** needs. Most of that work is one deleted line — a well-behaved
package gates its UI Toolkit code behind a `versionDefines` entry in its `.asmdef`, and removing that
entry drops the code from the player without touching a single source file.

### Permanent Patches — `ProjectSettings/LightSideLeanPermanentPatches`

The same archives, except that they stay on. They are laid down again on editor load, whenever the
registered packages change, and before a build — in the Editor, under `-batchmode`, and on a build
agent.

This is for a package whose own source does not compile against your project at all: one reaching for
a type that your fork of another package does not have. The uGUI fork without TextMeshPro is the
case this was written for. A build patch cannot help there, because such a package fails the *editor*
— long before any build callback runs.

Dropping a permanent patch lays the package's own files down again, so what is on disk is what the
project asks for rather than the last thing written to it.

### What is refused, and what is only reported

A patch is refused when its package is not installed, or when the package's source belongs to the
project — `Embedded` or `Local`, where editing in place is the honest fix. Both are shown beside the
patch on the settings page.

A package version that has moved on is reported and applied. Archives hold whole files, not diffs, so
a replacement written against one revision undoes whatever upstream changed in that file later.
Refusing over a version number would throw away the ordinary case — a patch release that never
touched the patched file — to guard against the rare one.

### The first compilation on a cold machine

No public Unity callback runs between a package being laid down and its first compilation. On a
machine whose package cache is cold — a fresh checkout on a build agent — that first compilation
reads the unpatched files, and its errors are real. The patch lands immediately afterwards and the
editor compiles again; the second compilation is the one the project is judged by. The player is
never affected either way, because permanent patches are applied before the player's own code is
compiled.

### Rough edges

A patched build emits two Unity warnings that are expected: one that assets in immutable packages
were altered, and one that uncompiled code changes are present. Both are the patches being laid down
and taken off around the build.

Unity restores an altered immutable package by itself under some conditions. A permanent patch is
written again each time that happens, and after ten rounds in one editor session Lean stops and says
which package would not hold its patch, rather than reloading the domain for as long as the editor is
open.

Where an Editor does not expose engine module inclusion, the module cannot be held out of the
compilation. The roots are still cut, the build still succeeds, and it ends with a report of whether
the module left the player and what kept it.

---

## Why Unity keeps UI Toolkit

`UnityEditorInternal.AssemblyStripper` writes the player's managed link roots from the editor's own
`RuntimeClassRegistry`, and an editor always has UI Toolkit. Two types reach the root list that way —
`UnityEngine.UIElements.PanelSettings` and `UnityEngine.UIElements.DynamicAtlasSettings` — and from
them UnityLinker keeps 1 471 types, three whole assemblies and members of a dozen more.

This package implements `UnityEditor.Build.IUnityLinkerProcessor`, the one public callback between
those roots being written and UnityLinker reading them, and removes the two entries.

An Editor version that writes the roots after the callback loses that half of the saving and builds
normally. The failure mode is a build that is no smaller, never a broken player.

## Upgrading from Lean Build

The package was called `media.lightside.leanbuild` through 0.2.0. Its files in a project are renamed
on the first editor load after the update and nothing has to be moved by hand:

| was | is |
|---|---|
| `ProjectSettings/LeanBuild.asset` | `ProjectSettings/LightSideLean.asset` |
| `ProjectSettings/LeanBuildPatches` | `ProjectSettings/LightSideLeanPatches` |

Both are under version control, so the rename shows up as a move in your next diff. Existing patch
archives are read unchanged.

## Requirements

Unity 2022.3 or newer. Developed and measured on 6000.6.

## Licence

MIT. See [LICENSE.md](LICENSE.md).
