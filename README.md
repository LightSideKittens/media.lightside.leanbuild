# LightSide Lean Build

Takes UI Toolkit out of players that never draw with it.

Unity compiles the whole UIElements runtime into builds that cannot use it. Removing
`com.unity.modules.uielements`, force-excluding the engine module and guarding every reference in
your own code all leave it in place. This package removes it with one project setting.

Measured on an Android IL2CPP build, ARM64, managed stripping High:

| | before | after |
|---|---|---|
| `libil2cpp.so` | 38 602 560 | 25 008 216 |
| `global-metadata.dat` | 5 561 794 | 3 584 102 |
| resident memory | 237.02 MiB | 227.15 MiB |

Resident figures are the mean of five interleaved runs on one device, sampled nine seconds after
launch.

## Use

**Project Settings → Lean Build → Strip UI Toolkit from players.**

Off by default. The setting lives in `ProjectSettings/LeanBuild.asset`. **Commit that file.** It is
what a headless build reads, so a machine that checks out the project without it builds at full size
— silently, with no warning. A missing file means the setting is off; nothing fails.

Batch builds are the point, not an afterthought: the trim runs from a build callback and was
measured under `-batchmode -nographics -quit`.

Nothing else changes. The package stays installed, the manifest is untouched, and the Editor keeps
UI Toolkit — inspectors, overlays and editor windows written against UIElements are unaffected,
including those in packages you do not control.

## What it costs you

Players stop supporting UI Toolkit. Runtime code that calls into `UnityEngine.UIElements` still
compiles and still runs against a 26-type shell, and **a `UIDocument` placed in a scene draws
nothing** — a case no compiler can warn about. Turn the setting on only when you know your players
do not use UI Toolkit.

## Why Unity does this

`UnityEditorInternal.AssemblyStripper` writes the player's managed link roots from the editor's own
`RuntimeClassRegistry`, and an editor always has UI Toolkit. Two types reach the root list that way —
`UnityEngine.UIElements.PanelSettings` and `UnityEngine.UIElements.DynamicAtlasSettings` — and from
them UnityLinker keeps 1 471 types, three whole assemblies and members of a dozen more.

This package implements `UnityEditor.Build.IUnityLinkerProcessor`, the one public callback between
those roots being written and UnityLinker reading them, and removes the two entries. Native engine
module inclusion follows the managed roots, so the native module leaves with them.

An Editor version that writes the roots after the callback loses the saving and builds normally. The
failure mode is a build that is no smaller, never a broken player.

## Requirements

Unity 2022.3 or newer. Verified on 6000.6.

## Licence

MIT. See [LICENSE.md](LICENSE.md).
