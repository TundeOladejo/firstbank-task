using Microsoft.EntityFrameworkCore;
using NovaWallet.Api.Domain;

namespace NovaWallet.Api.Persistence;

public class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Wallet>(e =>
        {
            e.ToTable("wallets");
            e.HasKey(w => w.Id);
            e.Property(w => w.CustomerId).HasMaxLength(128).IsRequired();
            e.Property(w => w.Currency).HasMaxLength(3).IsRequired();
            e.Property(w => w.BalanceKobo).IsRequired();
            e.HasIndex(w => w.CustomerId);

            // Map PostgreSQL's xmin system column as an optimistic-concurrency token.
            // Any UPDATE will include "WHERE xmin = @original" so a concurrent writer that
            // moved the row triggers a DbUpdateConcurrencyException instead of a lost update.
            e.Property(w => w.Version)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            // Defense-in-depth: DB-level guard so the balance can never be persisted negative,
            // regardless of application logic.
            e.ToTable(t => t.HasCheckConstraint("ck_wallets_balance_nonnegative", "\"BalanceKobo\" >= 0"));
        });

        b.Entity<Transaction>(e =>
        {
            e.ToTable("transactions");
            e.HasKey(t => t.Id);
            e.Property(t => t.Type).HasConversion<int>();
            e.HasIndex(t => new { t.WalletId, t.CreatedAt });
            e.HasIndex(t => t.TransferId);
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).UseIdentityAlwaysColumn();
            e.Property(a => a.Action).HasMaxLength(32).IsRequired();
            e.Property(a => a.PreviousHash).HasMaxLength(64).IsRequired();
            e.Property(a => a.EntryHash).HasMaxLength(64).IsRequired();
            e.HasIndex(a => new { a.WalletId, a.CreatedAt });
        });

        b.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("idempotency_records");
            e.HasKey(i => i.Id);
            e.Property(i => i.Key).HasMaxLength(200).IsRequired();
            e.Property(i => i.RequestHash).HasMaxLength(64).IsRequired();
            // A unique key is what makes concurrent replays race safely: the second inserter
            // hits a unique-violation and we fall back to returning the stored response.
            e.HasIndex(i => i.Key).IsUnique();
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(o => o.Id);
            e.Property(o => o.Type).HasMaxLength(128).IsRequired();
            e.HasIndex(o => o.ProcessedAt);
        });
    }
}
