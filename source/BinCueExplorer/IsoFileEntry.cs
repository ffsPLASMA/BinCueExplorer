namespace BinCueExplorer;

internal sealed record IsoFileEntry(string Name, string Path, uint StartSector, uint Size, DateTimeOffset? RecordedDate);
