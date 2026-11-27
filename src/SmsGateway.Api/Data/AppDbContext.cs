using Microsoft.EntityFrameworkCore;
using SmsGateway.Api.Domain;

namespace SmsGateway.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ApiClient> ApiClients => Set<ApiClient>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<SmsMessage> SmsMessages => Set<SmsMessage>();
    public DbSet<SmsEvent> SmsEvents => Set<SmsEvent>();

    /// <summary>All DateTime columns are stored and read back as UTC.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        c.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        c.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ApiClient>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.KeyHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.KeyPrefix).HasMaxLength(16).IsRequired();
            e.HasIndex(x => x.KeyHash).IsUnique();
        });

        b.Entity<Device>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.PhoneNumber).HasMaxLength(20);
            e.Property(x => x.KeyHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.KeyPrefix).HasMaxLength(16).IsRequired();
            e.Property(x => x.AppVersion).HasMaxLength(32);
            e.Property(x => x.SimInfo).HasMaxLength(200);
            e.HasIndex(x => x.KeyHash).IsUnique();
        });

        b.Entity<SmsMessage>(e =>
        {
            e.Property(x => x.ToNumber).HasMaxLength(20).IsRequired();
            e.Property(x => x.Body).HasMaxLength(1600).IsRequired();
            e.Property(x => x.ClientReference).HasMaxLength(100);
            e.Property(x => x.CallbackUrl).HasMaxLength(500);
            e.Property(x => x.ErrorMessage).HasMaxLength(500);
            e.HasOne(x => x.ApiClient).WithMany().HasForeignKey(x => x.ApiClientId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.SetNull);

            // Queue scan index: pending messages ordered by priority then age.
            e.HasIndex(x => new { x.Status, x.Priority, x.CreatedAt });
            e.HasIndex(x => new { x.DeviceId, x.AssignedAt });
            e.HasIndex(x => x.CreatedAt);
            // Idempotency: one reference per client.
            e.HasIndex(x => new { x.ApiClientId, x.ClientReference })
                .IsUnique()
                .HasFilter("[ClientReference] IS NOT NULL");
        });

        b.Entity<SmsEvent>(e =>
        {
            e.Property(x => x.Event).HasMaxLength(40).IsRequired();
            e.Property(x => x.Details).HasMaxLength(1000);
            e.HasIndex(x => x.SmsMessageId);
            e.HasOne<SmsMessage>().WithMany().HasForeignKey(x => x.SmsMessageId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
