using System;

namespace PowerMonitorService.Models
{
    public class CurrentMonitorRecord
    {
        public int Id { get; set; }
        public DateTime Timestamp { get; set; }  
        public double ElectricalCurrent { get; set; }
        public double ShuntVoltage { get; set; }
        public double BusVoltage { get; set; }
        public double ElectricalPower { get; set; }
        public double Load { get; set; }
    }
}