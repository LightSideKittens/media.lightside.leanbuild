# Changelog

## [0.1.0] - 2026-09-17

### Added
- **Strip UI Toolkit from players**, a project setting under Project Settings → Lean Build. When on,
  the UI Toolkit entries Unity's editor seeds into the player's managed link roots are removed before
  UnityLinker runs, which takes `UnityEngine.UIElementsModule`, `UnityEngine.PropertiesModule` and
  `UnityEngine.InputForUIModule` out of the player along with the native engine module. Off by
  default; the Editor is unaffected.
