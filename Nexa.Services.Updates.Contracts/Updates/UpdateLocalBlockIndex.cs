


namespace Nexa.Services.Updates;

/// <summary>Local raw chunk bytes located inside an installed file.</summary>
public sealed record LocalBlockSource(string Path, long Offset, int Size);
