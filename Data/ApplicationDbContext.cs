using CrabSenseKiosk.Models;
using Microsoft.EntityFrameworkCore;

namespace CrabSenseKiosk.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<Sensor> Sensors { get; set; }
        public DbSet<WaterQualityReading> WaterQualityReadings { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Configure Sensor
            modelBuilder.Entity<Sensor>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
                entity.Property(e => e.SensorType).IsRequired().HasMaxLength(50);
                entity.Property(e => e.Location).HasMaxLength(200);
                entity.Property(e => e.Description).HasMaxLength(500);
                
                entity.HasMany(e => e.Readings)
                      .WithOne(e => e.Sensor)
                      .HasForeignKey(e => e.SensorId)
                      .OnDelete(DeleteBehavior.SetNull);
            });

            // Configure WaterQualityReading
            modelBuilder.Entity<WaterQualityReading>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Timestamp).IsRequired();
                entity.Property(e => e.Temperature).HasPrecision(5, 2);
                entity.Property(e => e.pH).HasPrecision(4, 2);
                entity.Property(e => e.DissolvedOxygen).HasPrecision(5, 2);
                entity.Property(e => e.Salinity).HasPrecision(5, 2);
                entity.Property(e => e.Turbidity).HasPrecision(6, 2);
                entity.Property(e => e.Ammonia).HasPrecision(5, 2);
                
                entity.HasIndex(e => e.Timestamp);
                entity.HasIndex(e => e.SensorId);
            });

            // Seed data
            modelBuilder.Entity<Sensor>().HasData(
                new Sensor
                {
                    Id = 1,
                    Name = "Sensor 1 - Pool A",
                    SensorType = "Temperature",
                    Location = "Pool A - North",
                    IsActive = true,
                    InstallationDate = DateTime.UtcNow.AddMonths(-6),
                    Description = "Main temperature sensor for Pool A"
                },
                new Sensor
                {
                    Id = 2,
                    Name = "Sensor 2 - Pool A",
                    SensorType = "pH",
                    Location = "Pool A - Center",
                    IsActive = true,
                    InstallationDate = DateTime.UtcNow.AddMonths(-6),
                    Description = "pH monitoring sensor"
                },
                new Sensor
                {
                    Id = 3,
                    Name = "Sensor 3 - Pool B",
                    SensorType = "Dissolved Oxygen",
                    Location = "Pool B - South",
                    IsActive = true,
                    InstallationDate = DateTime.UtcNow.AddMonths(-3),
                    Description = "DO monitoring sensor"
                }
            );
        }
    }
}
