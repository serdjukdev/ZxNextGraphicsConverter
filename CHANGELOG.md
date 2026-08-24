# Changelog

## Unreleased

- Fixed GridSplitter panes (main window, Map Editor, Metatile Editor) being draggable past the window's actual bounds — could silently store an oversized column width that only became visible on the next full layout pass (e.g. maximizing). All splitters now clamp live against the real current window size on every drag tick.
- Widened all GridSplitters to 5px (was 1px in Map Editor/Metatile Editor) so they're easier to grab.
- Added: Metatile Editor can now edit an existing metatile in place (click it in the library to load it into the draft, "Save Changes" writes back into the same metatile — every map already placing it picks it up immediately). Ctrl+click a draft cell to clear it back to unfilled while sketching (every cell still needs a real tile before it can be saved). "Cancel" button discards an in-progress edit.
- Fixed: after Save Changes or Delete in the Metatile Editor, the draft could silently stay loaded with stale data, so the next "Create Metatile" click would create an unwanted duplicate.
- Added: Atlas Slicer offers a "place the transparent tile first" checkbox when a slice contains a fully transparent cell and the target category doesn't already have one — moves it to export index 0 so it's easy to find (e.g. as a walk-through tile) instead of paging through the rest.
- Fixed: "place the transparent tile first" changed the underlying export order but the project tree didn't actually show it first — the tree's display order was plain insertion order, not SortIndex.
- Fixed: deleting a source image (which cascades to delete every tile/sprite it produced) never freed the now-unused 4bpp palette bank slot — it lingered forever (visible, "Optimize"-able, survives save/reload) even once its folder had zero real assets left. The equivalent cleanup already existed for flat/8bpp palettes but was missing for 4bpp banks in this one delete path.
- Added: a "Blank" reserved tile and metatile are now auto-generated (not detected/imported from any source image) the first time a Tile4Bpp/Tile8Bpp category, a given metatile Kind+GridSize, or a map of that GridSize is first used — always undeletable and always sorted first, so it's guaranteed to be export index 0. Replaces the map grid's old 0xFF "empty cell" sentinel entirely: an unpainted/erased cell now just references this real metatile, giving a branch-free blit a genuine blank pattern to read instead of an out-of-bounds index, and giving hardware tilemap code a real tile to draw when explicitly clearing a cell. Existing projects get this backfilled automatically on next load (any pre-existing 0xFF cell is remapped to the new blank).
- Fixed: since Tile4Bpp/Tile8Bpp always have that auto-generated reserved blank now, a brand-new import (single drop or Atlas Slicer) whose pixels are entirely transparent is no longer imported as a duplicate — it's silently skipped (Atlas Slicer counts it the same as a same-batch duplicate cell) instead of creating a second, redundant all-transparent tile. Atlas Slicer's "place the transparent tile first" checkbox no longer offers itself for these two categories either, since it would never have anything left to do; it still works normally for Sprite4Bpp/Sprite8Bpp, which have no reserved-blank concept.
- Fixed: a reserved blank tile silently auto-created mid-session (during an import, an Atlas Slicer batch, or indirectly by creating the first metatile/map of some Kind+GridSize) never got a node in the main tree — it existed in the project (correct for save/export) but stayed invisible until the next full project reload.
- Fixed: opening the Metatile Editor on an empty Kind+GridSize didn't show its reserved blank metatile until the user created their first real one there — now it's ensured (and shown) as soon as that Kind+GridSize is selected, matching the same "always there" guarantee everywhere else.

## v0.1.0 (2026-08-23)

- Renamed the app to **ZxNext Studio**.
- Added the Map Objects Attribute/Linking system: user-defined object types, directed links between map objects (drawn as arrows, with a dedicated Link tool), exported as extra bytes per object plus a project-wide `object_types.asm`.
- Fixed: mouse wheel not scrolling several palette/list panels (a redundant nested ScrollViewer was swallowing the wheel event).
- Fixed: "System.Windows.Documents.Run is not a Visual or Visual3D" crash when clicking directly on list-row text in three different panels.
- Fixed: Pixel Editor's Undo silently dropping the first pixel of a paint stroke.
- Fixed: Map Editor canvas hint text incorrectly saying "click or drag" on the Sprites layer (placement is click-only there).
- Changed: eyedropper in the read-only tile/sprite preview no longer requires Ctrl (click or drag directly); the editable canvas below it still does, to disambiguate from painting.
- Packaging: added framework-dependent and self-contained single-file Windows builds, published automatically via GitHub Actions on a pushed version tag.
