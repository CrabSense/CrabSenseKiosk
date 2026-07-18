using CrabSenseKiosk.Data;
using CrabSenseKiosk.DTOs;
using CrabSenseKiosk.Models;
using Microsoft.EntityFrameworkCore;

namespace CrabSenseKiosk.Services
{
    public class SensorService : ISensorService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<SensorService> _logger;

        public SensorService(ApplicationDbContext context, ILogger<SensorService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<SensorDto> CreateSensorAsync(CreateSensorDto dto)
        {
            try
            {
                var sensor = new Sensor
                {
                    Name = dto.Name,
                    SensorType = dto.SensorType,
                    Location = dto.Location,
                    IsActive = dto.IsActive,
                    InstallationDate = DateTime.UtcNow,
                    Description = dto.Description
                };

                _context.Sensors.Add(sensor);
                await _context.SaveChangesAsync();

                return MapToDto(sensor);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating sensor");
                throw;
            }
        }

        public async Task<SensorDto?> GetSensorByIdAsync(int id)
        {
            var sensor = await _context.Sensors.FindAsync(id);
            return sensor != null ? MapToDto(sensor) : null;
        }

        public async Task<List<SensorDto>> GetAllSensorsAsync()
        {
            var sensors = await _context.Sensors.ToListAsync();
            return sensors.Select(MapToDto).ToList();
        }

        public async Task<List<SensorDto>> GetActiveSensorsAsync()
        {
            var sensors = await _context.Sensors
                .Where(s => s.IsActive)
                .ToListAsync();
            return sensors.Select(MapToDto).ToList();
        }

        public async Task<SensorDto?> UpdateSensorAsync(int id, UpdateSensorDto dto)
        {
            var sensor = await _context.Sensors.FindAsync(id);
            if (sensor == null)
                return null;

            sensor.Name = dto.Name;
            sensor.SensorType = dto.SensorType;
            sensor.Location = dto.Location;
            sensor.IsActive = dto.IsActive;
            sensor.LastMaintenanceDate = dto.LastMaintenanceDate;
            sensor.NextMaintenanceDate = dto.NextMaintenanceDate;
            sensor.Description = dto.Description;

            await _context.SaveChangesAsync();
            return MapToDto(sensor);
        }

        public async Task<bool> DeleteSensorAsync(int id)
        {
            var sensor = await _context.Sensors.FindAsync(id);
            if (sensor == null)
                return false;

            _context.Sensors.Remove(sensor);
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<bool> ToggleSensorStatusAsync(int id)
        {
            var sensor = await _context.Sensors.FindAsync(id);
            if (sensor == null)
                return false;

            sensor.IsActive = !sensor.IsActive;
            await _context.SaveChangesAsync();
            return true;
        }

        private SensorDto MapToDto(Sensor sensor)
        {
            return new SensorDto
            {
                Id = sensor.Id,
                Name = sensor.Name,
                SensorType = sensor.SensorType,
                Location = sensor.Location,
                IsActive = sensor.IsActive,
                InstallationDate = sensor.InstallationDate,
                LastMaintenanceDate = sensor.LastMaintenanceDate,
                NextMaintenanceDate = sensor.NextMaintenanceDate,
                Description = sensor.Description
            };
        }
    }
}
