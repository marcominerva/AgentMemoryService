using AgentMemoryService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentMemoryService.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public virtual DbSet<UserMemory> Memories { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserMemory>(entity =>
        {
            entity.HasIndex(e => e.UserName, "IX_Memories_UserName").IsUnique();

            entity.Property(e => e.Id).HasDefaultValueSql("(newsequentialid())", "DF_Memories_Id");
            entity.Property(e => e.UserName).HasMaxLength(64);
        });
    }
}
