using ArchiveImageLabler.Services;

namespace ArchiveImageLabler.Components.Shared;

public sealed record AssetRatingChange(AssetCard Asset, int? Rating);
