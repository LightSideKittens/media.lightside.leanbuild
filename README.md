# LightSide Lean

Takes out of a Unity project what Unity will not let you take out, and patches whatever breaks.

Three jobs, one page. **Strip UI Toolkit** removes the UIElements runtime from players that never draw
with it. **Trim LightSide Code** strips LightSide's assemblies in a project that stays on Minimal or
Low. **Patches** keep your fixes to files inside other people's packages — the ones Unity
overwrites whenever it re-resolves — and lay them down again, either for the length of a build or
permanently.

**Project Settings → LightSide → Lean.**

---

## Strip UI Toolkit

Unity keeps the UIElements runtime in builds that never draw with it: uGUI's panel bridge reaches it
from every uGUI project, other packages reach it from their own runtime code, and a UI Toolkit asset
in a build's content adds it to the player's managed link roots. This package compiles out what
packages gate behind their assembly definitions, cuts those roots, and holds the engine module out of
the player's compilation so nothing can quietly put it back.

Saved in an Android IL2CPP APK, ARM64, one-scene uGUI project:

| Managed stripping | Unity 2022.3 | Unity 6.6 |
|---|---:|---:|
| Minimal | 3.05 MiB | 3.95 MiB |
| High | 3.09 MiB | 0.03 MiB |

A project whose content carries a UI Toolkit asset saves more. One such project at High went from
38 602 560 to 25 008 216 bytes of `libil2cpp.so`, from 5 561 794 to 3 584 102 bytes of
`global-metadata.dat`, and from 237.02 to 227.15 MiB of resident memory, the mean of five interleaved
runs on one device, sampled nine seconds after launch.

Off by default. The setting lives in `ProjectSettings/LightSideLean.asset`. **Commit that file.** It
is what a headless build reads, so a machine that checks out the project without it builds at full
size — silently, with no warning. A missing file means the setting is off; nothing fails.

Batch builds are the point, not an afterthought: everything runs from build callbacks and was
measured under `-batchmode -nographics -quit`.

The Editor is unaffected. The package stays installed, the manifest is untouched, and inspectors,
overlays and editor windows written against UIElements keep working — including those in packages you
do not control.

### What compiles out, and what fails the build

Cutting roots only helps a player whose own code never reaches UI Toolkit. While any such code is
compiled in, the linker keeps the module and cutting roots saves nothing.

A package that gates that code behind a `versionDefines` entry on `com.unity.modules.uielements` needs
nothing from you; uGUI's `PanelEventHandler` and `PanelRaycaster` are the case every uGUI project has.
For the length of a build, Lean takes those entries out of the assembly definitions of installed
packages and puts them back afterwards. `Embedded` and `Local` packages are left alone, because their
source belongs to the project.

Everything else fails the build rather than quietly saving nothing. The engine module is held out of
the player's compilation, so every remaining player file that touches UI Toolkit becomes a compiler
error naming its line. Turn the setting on, build, and any failures are your list of work.

The Editor's own assemblies are already compiled by the time this happens, so they are never
affected, and the exclusion is lifted whichever way the build ends.

### LightSide packages

LightSide packages gate their UI Toolkit code behind one scripting define, `LIGHTSIDE_NO_UITOOLKIT`, so none of it
shows up in the list of build errors. Lean owns the define: turning **Strip UI Toolkit** on adds it to every build
target's Scripting Define Symbols, turning it off removes it, and a project opened with the setting on gets it
wherever it is missing. Your other defines are left alone, and with the setting off an opened project is not touched,
so a define you set by hand keeps working.

With it set, Core's UI Toolkit input conversion and debug drawing, MoveIt's `VisualElement` properties, the UniText
inspection overlay and UniText's Scene view overlay compile out of every assembly. The define sits in Player Settings
because the build compiles runtime assemblies three times (editor, code generation, player) and only a Player
Settings define reaches all three; it therefore also applies to the Editor's own compile of those assemblies, so the
features above are unavailable in the Editor while the setting is on (UniText keeps its Respect Scene Visibility
toggle). Changing the setting recompiles the project once.

### What it costs you

Players stop supporting UI Toolkit. Code that uses it no longer compiles, which is the point — but
**a `UIDocument` placed in a scene draws nothing**, and no compiler can warn about that. Turn the
setting on only when you know your players do not use UI Toolkit.

---

## Trim LightSide Code

At Minimal and Low stripping, a player ships every LightSide assembly a scene references whole, including the features
it never uses. A project usually stays on these levels because of its own code: code reached only through reflection
stops working at Medium. This setting separates the two. LightSide's assemblies, and the `Unity.Burst` assembly they
bring, are stripped as Medium does, while every other compiled assembly stays whole.

Saved in an Android IL2CPP APK, ARM64, one-scene UniText project:

| Managed stripping | Unity 2022.3 | Unity 6.6 |
|---|---:|---:|
| Minimal | TBD | TBD |
| Low | TBD | TBD |

Off by default, because its failures show only at run time. It is stored in the same
`ProjectSettings/LightSideLean.asset` and applies to IL2CPP builds at Minimal and Low: Medium and High already strip
this code.

### What it costs you

- A LightSide type a player meets only inside a plain AssetBundle, such as a modifier nothing else uses, is stripped
  unless a `link.xml` keeps it: the linker does not see what a separately built bundle holds. Addressables records
  the types of its own content by itself.
- Engine assemblies follow Medium's rules instead of Minimal's and Low's, so code reaching their members only through
  reflection can need a `link.xml` too. .NET assemblies keep Unity's preservation lists for Minimal and Low.
- At Low, your own assemblies ship as at Minimal: an assembly Low would strip stays whole, which takes back part of
  the saving.

`<assembly fullname="LightSide.UniText" preserve="all"/>` keeps a whole assembly.

### What it relies on

The linker takes the arguments from `UNITYLINKER_ADDITIONAL_ARGS`, an environment variable Unity reads and does not
document. Lean sets it for the length of a build. An editor that stops reading it builds at full size, and the build
ends with a warning saying LightSide's assemblies were not trimmed.

The .NET preservation lists come from the running editor's `il2cpp/LinkerDescriptors/Default`. A build in an editor
without them fails and names the missing file.

Unity reads the variable only while generating the player's build graph, so the first build after the arguments change
deletes that graph and takes longer. The arguments change when the setting moves or when the list of the project's
assemblies does.

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

This is how a failure **Strip UI Toolkit** reports gets fixed inside a package with no gate of its
own: guard the code where it lives, and keep the edit as a build patch.

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

`UnityEditorInternal.AssemblyStripper` writes the player's managed link roots from the editor's
`RuntimeClassRegistry`, which names every type the build's content instances. A UI Toolkit asset the
build carries, such as a `PanelSettings` in a `Resources` folder, puts two types on the root list —
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

## Support

Bugs and feature requests go to [LightSide Ecosystem issues](https://github.com/LightSideKittens/LightSideEcosystem/issues/new/choose). Questions and discussion live on [Discord](https://discord.gg/ynRHp3wRmb).

## License

MIT. See [LICENSE.md](LICENSE.md).
