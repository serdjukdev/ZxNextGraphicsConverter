using System.Collections.Generic;
using System.Windows.Input;

namespace ZxNext.App;

/// <summary>
/// Named Key/ModifierKeys constants for every real KeyBinding in the app — XAML references these via
/// {x:Static} instead of literal Key="X" Modifiers="Y" strings, so the Settings screen's Shortcuts tab
/// (<see cref="ShortcutReference"/> below) can never drift from what's actually bound: same value, same
/// place. F1 (Help) is deliberately NOT here — it's intercepted at the raw Win32 message level in
/// MainWindow's constructor (see its WndProc), not through any WPF routed-event/command mechanism.
/// </summary>
public static class Shortcuts
{
    public static readonly Key SaveKey = Key.S;
    public static readonly ModifierKeys SaveModifiers = ModifierKeys.Control;

    public static readonly Key UndoKey = Key.Z;
    public static readonly ModifierKeys UndoModifiers = ModifierKeys.Control;

    /// <summary>Map Editor only — the only window in the app with a Redo command (the main window's own undo stack, and the Metatile Editor, currently have no Redo).</summary>
    public static readonly Key RedoKey = Key.Y;
    public static readonly ModifierKeys RedoModifiers = ModifierKeys.Control;

    /// <summary>Map Editor only — copies the current grid/sprite selection to the shared clipboard. A real declarative KeyBinding (see MapEditorWindow.xaml), unlike Paste below.</summary>
    public static readonly Key CopyKey = Key.C;
    public static readonly ModifierKeys CopyModifiers = ModifierKeys.Control;

    /// <summary>Map Editor only — pastes the last Ctrl+C'd selection under the cursor. NOT a declarative KeyBinding: Paste needs the View's own last-known cursor position, which isn't naturally bindable from XAML, so it's handled via PreviewKeyDown in MapEditorWindow.xaml.cs — that handler checks against these same constants so it can't drift from what Settings/Help documents.</summary>
    public static readonly Key PasteKey = Key.V;
    public static readonly ModifierKeys PasteModifiers = ModifierKeys.Control;

    public static readonly Key DeleteKey = Key.Delete;

    public static readonly Key ReQuantizeKey = Key.R;
    public static readonly ModifierKeys ReQuantizeModifiers = ModifierKeys.Control;
}

/// <summary>One row of the Settings screen's Shortcuts tab.</summary>
public record ShortcutEntry(string Area, string Gesture, string ShortDescription, string FullDescription);

/// <summary>
/// Every shortcut/gesture in the app, grouped by Area, for the Settings screen's Shortcuts tab. The
/// Gesture text for entries backed by a real KeyBinding (see <see cref="Shortcuts"/>) is guaranteed
/// accurate — it's the same value the binding itself uses. Mouse+modifier gestures (Ctrl+click, Alt+drag,
/// etc.) are NOT real KeyBindings — they're Keyboard.Modifiers checks scattered across many MouseDown
/// handlers — so their entries here are hand-maintained text mirroring HelpWindow.xaml, same as Help
/// itself always has been; there's no single mechanism tying either of them to the actual mouse-handling
/// code, only to each other.
/// </summary>
public static class ShortcutReference
{
    public static IReadOnlyList<ShortcutEntry> All { get; } =
    [
        new("Main Window", "Ctrl+S", "Save project", "Saves in place. If the project has no location yet, use Save Project As... first."),
        new("Main Window", "Ctrl+Z", "Undo", "One shared undo stack for pixel painting and palette colour edits (a whole paint stroke is one undo step). Cleared after any non-undoable operation (re-quantize category/folder, Optimize bank, a compacting delete)."),
        new("Main Window", "F1", "Help", "Opens the Shortcuts & Controls window (or brings it forward if already open)."),

        new("Project Tree", "Delete", "Delete selected", "Deletes the selected tile/sprite, the whole checked multi-selection, or a user-created 8bpp sub-folder and everything in it. Always confirms first."),
        new("Project Tree", "Ctrl+R", "Re-quantize", "Opens the Re-quantize dialog (choose a dithering mode) and re-converts the selected tile/sprite from its original source region."),
        new("Project Tree", "Ctrl+click / Shift+click", "Multi-select", "Ctrl+click adds/removes one item from the multi-selection; Shift+click selects the whole range since your last click."),
        new("Project Tree", "Drag a row onto another row", "Reorder", "Drop on the top/bottom half of the target to land before/after it: reassigns real export indices. Tile8Bpp/Sprite8Bpp only reorder within the same folder (moving to a different one would need re-quantizing, not just renumbering). The reserved Blank tile can never be dragged, or have anything dropped before it."),

        new("Source Images Panel", "Delete", "Remove source image", "Removes the selected source image and, if it was ever used, every tile/sprite placed from it too, after confirmation."),

        new("Pixel & Palette Editor", "Ctrl+click/drag (editable) or plain click/drag (preview)", "Eyedropper", "Picks the colour under the cursor and selects that palette swatch. Ctrl is only needed in the editable pixel editor, where a plain click paints instead."),
        new("Pixel & Palette Editor", "Double-click a palette swatch", "Edit colour", "Opens the colour picker for that slot. Changes it everywhere it's used, live, and pushes one undo step. Blocked on the transparent-index slot."),

        new("Metatile Editor", "Drag a metatile onto another", "Reorder", "Drop on the left/right half of the target to land before/after it. Reassigns real export indices AND rewrites every map cell placing an affected metatile under its old index. The reserved Blank metatile can never be dragged, or have anything dropped before it."),
        new("Metatile Editor", "Ctrl+click a cell (draft grid)", "Clear cell", "Clears that cell back to unfilled, to correct a mistake."),

        new("Map Editor", "Ctrl+Z", "Undo", "A local undo stack for this window: paint/erase strokes, Fill/Delete Selection, Move/Copy, Link, type assignment, and Resize/Trim each undo as one step."),
        new("Map Editor", "Ctrl+Y", "Redo", "Reapplies the last undone step. A new edit after an Undo clears whatever Redo history existed — same as every other editor."),
        new("Map Editor", "Ctrl+C", "Copy selection", "Copies the current grid-cell or sprite selection to the clipboard."),
        new("Map Editor", "Ctrl+V", "Paste", "Pastes the last Ctrl+C'd selection under the cursor, snapped to the tile grid, and selects it immediately so it can be Alt-dragged into place. Works across maps: switch to a different map, then Ctrl+V, to copy a piece from one map to another — no remapping needed, since metatiles/tiles/sprites are project-wide. A pasted sprite's link (if any) is not carried over, same as a same-map copy."),
        new("Map Editor", "Delete", "Delete selection", "Clears the current selection's contents: grid cells become empty, selected sprites are removed."),
        new("Map Editor", "Ctrl+click/drag", "Force-erase", "Erases on the active layer no matter what else you're doing, including mid-drag."),
        new("Map Editor", "Shift (placing a sprite)", "Snap to grid", "Snaps the new sprite's position to the faint 8x8 tile grid."),
        new("Map Editor", "Alt+drag", "Select / move", "Drag on empty space to draw a new selection; drag from inside the current selection to move it."),
        new("Map Editor", "Alt+Shift+drag", "Copy selection", "Same as Alt+drag from inside a selection, but copies instead of moving."),
        new("Map Editor", "Mouse wheel / middle-drag", "Zoom / pan", "Zooms 25%-800% anchored under the cursor, or pans the view.")
    ];
}
