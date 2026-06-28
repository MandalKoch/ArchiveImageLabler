using FileZipPreview.Models;
using Microsoft.EntityFrameworkCore;

namespace FileZipPreview.Data;

public sealed class LibraryDbContext(DbContextOptions<LibraryDbContext> options) : DbContext(options)
{
    public DbSet<LibraryAsset> Assets => Set<LibraryAsset>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LibraryAsset>(entity =>
        {
            entity.HasIndex(asset => asset.StableKey).IsUnique();
            entity.HasIndex(asset => asset.ParentId);
            entity.HasIndex(asset => asset.Kind);
            entity.HasIndex(asset => asset.SourceType);
            entity.HasIndex(asset => asset.IsAvailable);
            entity.HasIndex(asset => asset.IsIgnored);
            entity.HasIndex(asset => asset.SortKey);
            entity.HasOne(asset => asset.Parent)
                .WithMany(asset => asset.Children)
                .HasForeignKey(asset => asset.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
