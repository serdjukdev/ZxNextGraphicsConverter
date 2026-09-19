namespace ZxNext.Core.Export;

/// <summary><paramref name="PaletteSlotIndex"/> is only meaningful when <paramref name="IsFourBpp"/> is true (which of the category's 16 4bpp palettes this asset uses) — 8bpp assets use a whole-folder flat palette, not a per-asset slot.</summary>
public record ExportableAsset(string Name, byte[] Data, bool IsFourBpp, int PaletteSlotIndex);

/// <summary>Where one asset ended up after chunking: which slot (8KB bank) and its byte offset within that slot, plus which 4bpp palette it needs (if any).</summary>
public record AssetPlacement(string Name, int SlotIndex, int OffsetInSlot, bool IsFourBpp, int PaletteSlotIndex);

public record ChunkFile(string FileName, byte[] Data)
{
    /// <summary>The asset-tree folder path this binary belongs to (e.g. <c>sprite/8bpp/images/walk</c>). Set by <see cref="ExportService"/> and used by <see cref="ExportService.WriteToDisk"/> when <c>exportSubfolders=true</c> to place the file in the correct sub-directory. Null for palette and ASM files, which always go to the result's own folder.</summary>
    public string? FolderPath { get; init; }
}
