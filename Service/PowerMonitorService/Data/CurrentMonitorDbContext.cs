using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using PowerMonitorService.Models;

namespace PowerMonitorService.Data
{
    public class CurrentMonitorDbContext : DbContext
    {
        public CurrentMonitorDbContext(DbContextOptions<CurrentMonitorDbContext> options) : base(options)
        {
            
        }

        public DbSet<CurrentMonitorRecord> CurrentMonitorRecords { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Configurar tabla
            modelBuilder.Entity<CurrentMonitorRecord>(entity =>
            {
                entity.ToTable("TelemetryRecords");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Timestamp).IsRequired();
                entity.Property(e => e.ElectricalCurrent).IsRequired(false);
                entity.Property(e => e.ShuntVoltage).IsRequired(false);
                entity.Property(e => e.BusVoltage).IsRequired(false);
                entity.Property(e => e.ElectricalPower).IsRequired(false);
                entity.Property(e => e.Load).IsRequired(false);
            });
        }
    }
}