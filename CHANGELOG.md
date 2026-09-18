# Changelog

## [0.2.0] - 2026-09-18

### Added
- Every build with the setting on ends with a report of whether UI Toolkit's engine module left the
  player, and names the dependencies Unity records when it stays — which is what an editor that cannot
  exclude the module falls back to.
- **Patches**, under the same settings page. Drop a package file you have fixed and Lean Build writes
  an archive pinned to that package's installed version; every build lays its patches over their
  packages before the player is compiled and takes them off afterwards. Patches live in
  `ProjectSettings/LeanBuildPatches` and belong in version control, so a machine that downloads its
  packages fresh on every run is patched on every run — no vendoring, no lost package updates.

### Changed
- **Breaking:** **The setting now fails a build it cannot make good on.** UI Toolkit's engine module is
  held out of the player's compilation, so every player file that uses it becomes a compiler error
  naming its line, and what has to be fixed is named rather than hunted for. Such a build used to
  succeed and carry the module anyway, saving nothing. The Editor is unaffected either way.
- The settings page moved to **Project Settings → LightSide → Lean Build** and is drawn in the
  LightSide style. Its toggle no longer truncates its label.

## [0.1.0] - 2026-09-17

### Added
- **Strip UI Toolkit from players**, a project setting under Project Settings → Lean Build. When on,
  the UI Toolkit entries Unity's editor seeds into the player's managed link roots are removed before
  UnityLinker runs, which takes `UnityEngine.UIElementsModule`, `UnityEngine.PropertiesModule` and
  `UnityEngine.InputForUIModule` out of the player along with the native engine module. Off by
  default; the Editor is unaffected.
