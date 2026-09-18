# LightSide Lean Build

Takes UI Toolkit out of players that never draw with it.

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

## Use

**Project Settings → LightSide → Lean Build → Strip UI Toolkit from players.**

Off by default. The setting lives in `ProjectSettings/LeanBuild.asset`. **Commit that file.** It is
what a headless build reads, so a machine that checks out the project without it builds at full size
— silently, with no warning. A missing file means the setting is off; nothing fails.

Batch builds are the point, not an afterthought: everything runs from build callbacks and was
measured under `-batchmode -nographics -quit`.

The Editor is unaffected. The package stays installed, the manifest is untouched, and inspectors,
overlays and editor windows written against UIElements keep working — including those in packages you
do not control.

## The build fails first, and that is the design

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

## Patches

Most of that work is one deleted line. A well-behaved package gates its UI Toolkit code behind a
`versionDefines` entry in its `.asmdef`, and removing that entry drops the code from the player
without touching a single source file. Only a package with no such switch needs its source edited.

Either way the fix lives inside a package folder, which Unity overwrites whenever it re-resolves. So
the settings page keeps it for you:

1. Fix the file where the compiler pointed you.
2. Drag it onto **Patches** on the same page. Lean Build writes an archive pinned to that package's
   installed version.
3. Press `↺` to put the package back as it was — the archive now carries your change.

Every build lays its patches over their packages before the player is compiled and takes them off
afterwards. Patches live in `ProjectSettings/LeanBuildPatches` and belong in version control, so a
machine that downloads its packages fresh on every run is patched on every run. No vendoring, no
embedded copies, no lost package updates.

A patch is refused only when its package is not installed, or when the package's source belongs to
the project — `Embedded` or `Local`, where editing in place is the honest fix. A version that has
moved on is reported and applied.

## What it costs you

Players stop supporting UI Toolkit. Code that uses it no longer compiles, which is the point — but
**a `UIDocument` placed in a scene draws nothing**, and no compiler can warn about that. Turn the
setting on only when you know your players do not use UI Toolkit.

## Rough edges

A patched build emits two Unity warnings that are expected: one that assets in immutable packages
were altered, and one that uncompiled code changes are present. Both are the patches being laid down
and taken off around the build.

Where an Editor does not expose engine module inclusion, the module cannot be held out of the
compilation. The roots are still cut, the build still succeeds, and it ends with a report of whether
the module left the player and what kept it.

## Why Unity does this

`UnityEditorInternal.AssemblyStripper` writes the player's managed link roots from the editor's own
`RuntimeClassRegistry`, and an editor always has UI Toolkit. Two types reach the root list that way —
`UnityEngine.UIElements.PanelSettings` and `UnityEngine.UIElements.DynamicAtlasSettings` — and from
them UnityLinker keeps 1 471 types, three whole assemblies and members of a dozen more.

This package implements `UnityEditor.Build.IUnityLinkerProcessor`, the one public callback between
those roots being written and UnityLinker reading them, and removes the two entries.

An Editor version that writes the roots after the callback loses that half of the saving and builds
normally. The failure mode is a build that is no smaller, never a broken player.

## Requirements

Unity 2022.3 or newer. Developed and measured on 6000.6.

## Licence

MIT. See [LICENSE.md](LICENSE.md).
