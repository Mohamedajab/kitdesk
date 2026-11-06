using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace KitDesk;

public sealed class Workspace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class Operator : IdentityUser
{
    public Guid WorkspaceId { get; set; }
}

public sealed class Equipment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public string AssetTag { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class Loan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid EquipmentId { get; set; }
    public Equipment Equipment { get; set; } = null!;
    public string Borrower { get; set; } = "";
    public DateTimeOffset IssuedAt { get; set; }
    public DateOnly DueDate { get; set; }
    public DateTimeOffset? ReturnedAt { get; set; }
    public string IssuedBy { get; set; } = "";
    public string? ReturnedBy { get; set; }
}

public sealed class DeskDb(DbContextOptions<DeskDb> options)
    : IdentityDbContext<Operator>(options), IDataProtectionKeyContext
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Equipment> Equipment => Set<Equipment>();
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.Entity<Operator>().HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<Equipment>().HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<Equipment>().HasAlternateKey(x => new { x.WorkspaceId, x.Id });
        model.Entity<Equipment>().HasIndex(x => new { x.WorkspaceId, x.AssetTag }).IsUnique().HasDatabaseName("unique_asset_tag");
        model.Entity<Equipment>().Property(x => x.AssetTag).HasMaxLength(24);
        model.Entity<Equipment>().Property(x => x.Name).HasMaxLength(100);
        model.Entity<Loan>().HasOne(x => x.Equipment).WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.EquipmentId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        model.Entity<Loan>().HasIndex(x => x.EquipmentId).IsUnique()
            .HasFilter("\"ReturnedAt\" IS NULL").HasDatabaseName("one_active_loan");
        model.Entity<Loan>().Property(x => x.Borrower).HasMaxLength(80);
        model.Entity<Loan>().Property(x => x.IssuedBy).HasMaxLength(256);
        model.Entity<Loan>().Property(x => x.ReturnedBy).HasMaxLength(256);
    }
}
