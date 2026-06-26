using Microsoft.EntityFrameworkCore;

namespace OrderingApp.Data;

// An EF Core entity, its DbContext, and its configuration (R5: EF Core wiring).
public sealed class OrderEntity
{
    public int Id { get; set; }

    public int Quantity { get; set; }
}

public sealed class OrderingDbContext : DbContext
{
    public DbSet<OrderEntity> Orders { get; set; } = null!;
}

public sealed class OrderEntityConfiguration : IEntityTypeConfiguration<OrderEntity>
{
}
