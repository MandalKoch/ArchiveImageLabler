namespace ArchiveImageLabler.Components.Shared;

public sealed record AssetSelectionChange(long AssetId, bool ShiftKey, bool AdditiveKey, bool Toggle);
