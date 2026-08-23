# Changelog

## Unreleased

- Fixed GridSplitter panes (main window, Map Editor, Metatile Editor) being draggable past the window's actual bounds — could silently store an oversized column width that only became visible on the next full layout pass (e.g. maximizing). All splitters now clamp live against the real current window size on every drag tick.
- Widened all GridSplitters to 5px (was 1px in Map Editor/Metatile Editor) so they're easier to grab.
- Added: Metatile Editor can now edit an existing metatile in place (click it in the library to load it into the draft, "Save Changes" writes back into the same metatile — every map already placing it picks it up immediately). Ctrl+click a draft cell to clear it back to unfilled while sketching (every cell still needs a real tile before it can be saved). "Cancel" button discards an in-progress edit.
- Fixed: after Save Changes or Delete in the Metatile Editor, the draft could silently stay loaded with stale data, so the next "Create Metatile" click would create an unwanted duplicate.
- Added: Atlas Slicer offers a "place the transparent tile first" checkbox when a slice contains a fully transparent cell and the target category doesn't already have one — moves it to export index 0 so it's easy to find (e.g. as a walk-through tile) instead of paging through the rest.
- Fixed: "place the transparent tile first" changed the underlying export order but the project tree didn't actually show it first — the tree's display order was plain insertion order, not SortIndex.
- Fixed: deleting a source image (which cascades to delete every tile/sprite it produced) never freed the now-unused 4bpp palette bank slot — it lingered forever (visible, "Optimize"-able, survives save/reload) even once its folder had zero real assets left. The equivalent cleanup already existed for flat/8bpp palettes but was missing for 4bpp banks in this one delete path.

## v0.1.0 (2026-08-23)

- Renamed the app to **ZxNext Studio**.
- Added the Map Objects Attribute/Linking system: user-defined object types, directed links between map objects (drawn as arrows, with a dedicated Link tool), exported as extra bytes per object plus a project-wide `object_types.asm`.
- Fixed: mouse wheel not scrolling several palette/list panels (a redundant nested ScrollViewer was swallowing the wheel event).
- Fixed: "System.Windows.Documents.Run is not a Visual or Visual3D" crash when clicking directly on list-row text in three different panels.
- Fixed: Pixel Editor's Undo silently dropping the first pixel of a paint stroke.
- Fixed: Map Editor canvas hint text incorrectly saying "click or drag" on the Sprites layer (placement is click-only there).
- Changed: eyedropper in the read-only tile/sprite preview no longer requires Ctrl (click or drag directly); the editable canvas below it still does, to disambiguate from painting.
- Packaging: added framework-dependent and self-contained single-file Windows builds, published automatically via GitHub Actions on a pushed version tag.
