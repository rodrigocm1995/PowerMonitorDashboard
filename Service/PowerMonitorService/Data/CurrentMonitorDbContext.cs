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
                entity.ToTable("CurrentMonitorRecords");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Timestamp).IsRequired();
                entity.Property(e => e.ElectricalCurrent).IsRequired();
                entity.Property(e => e.ShuntVoltage).IsRequired();
                entity.Property(e => e.BusVoltage).IsRequired();
                entity.Property(e => e.ElectricalPower).IsRequired();
                entity.Property(e => e.Load).IsRequired();
            });
        }
    }
}