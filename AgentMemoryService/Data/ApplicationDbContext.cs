using System.Text.Json;
using AgentMemoryService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentMemoryService.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public virtual DbSet<UserMemory> Memories { get; set; }

    public virtual DbSet<UserConversation> Conversations { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserMemory>(entity =>
        {
            entity.ToTable("Memories");
            entity.HasIndex(e => e.UserName, "IX_Memories_UserName").IsUnique();

            entity.Property(e => e.Id).HasDefaultValueSql("(newsequentialid())", "DF_Memories_Id");
            entity.Property(e => e.UserName).HasMaxLength(64);
            entity.Property(e => e.Facts).HasColumnType("json");
        });

        modelBuilder.Entity<UserConversation>(entity =>
        {
            entity.ToTable("Conversations");
            entity.HasIndex(e => new { e.UserName, e.ConversationId }, "IX_Conversations_UserName_ConversationId").IsUnique();

            entity.Property(e => e.Id).HasDefaultValueSql("(newsequentialid())", "DF_Conversations_Id");
            entity.Property(e => e.UserName).HasMaxLength(64);
            entity.Property(e => e.ConversationId).HasMaxLength(64);

            entity.Property(e => e.Session).HasConversion(
                session => session.GetRawText(),
                sessionString => JsonSerializer.Deserialize<JsonElement>(sessionString, JsonSerializerOptions.Web));
            });
    }
}
