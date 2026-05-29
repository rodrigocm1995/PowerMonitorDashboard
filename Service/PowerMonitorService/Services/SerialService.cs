using PowerMonitorService.Hubs;
using Microsoft.AspNetCore.SignalR;
using System;
using System.IO;
using System.IO.Ports;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PowerMonitorService.Services
{
    public class SerialService
    {
        private readonly IHubContext<SerialHub> _hubContext;
        private readonly ILogger<SerialService> _logger;
        private SerialPort? _serialPort;
        
        private string? _currentPort;
        private int _currentBaudRate = 115200;
        private string? _savedPort;
        private int _savedBaudRate = 115200;
        
        private Thread? _readThread;
        private bool _isRunning;
        private readonly object _lock = new();
        private readonly string _settingsPath;

        // Propiedades para simulación
        private bool _isSimulating;
        private CancellationTokenSource? _simCts;

        // Historial acumulado de variables para el parser serie por líneas
        private double _lastVoltage = 0.0;
        private double _lastCurrent = 0.0;
        private double _lastPower = 0.0;
        private double _lastShuntVoltage = 0.0;

        // Parámetros de calibración en modo simulación
        private double _simMaxCurrent = 2.0;
        private double _simShuntResistor = 0.01;

        public SerialService(IHubContext<SerialHub> hubContext, ILogger<SerialService> logger)
        {
            _hubContext = hubContext;
            _logger = logger;
            _settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "serial-settings.json");
            LoadSettings();
            TryAutoConnect();
        }

        // Obtiene la lista de puertos COM disponibles en Windows y agrega "SIMULATOR"
        public string[] GetAvailablePorts()
        {
            var ports = SerialPort.GetPortNames();
            var list = new List<string>(ports);
            if (!list.Contains("SIMULATOR"))
            {
                list.Add("SIMULATOR");
            }
            return list.ToArray();
        }

        // Devuelve el estado actual de la conexión y las configuraciones guardadas
        public object GetStatus()
        {
            lock (_lock)
            {
                bool isConnected = _isSimulating || (_serialPort?.IsOpen ?? false);
                return new
                {
                    IsConnected = isConnected,
                    PortName = isConnected ? _currentPort : null,
                    BaudRate = isConnected ? _currentBaudRate : 0,
                    SavedPort = _savedPort,
                    SavedBaudRate = _savedBaudRate
                };
            }
        }

        // Abre la conexión con el puerto serie o inicia la simulación
        public bool Connect(string portName, int baudRate, out string errorMessage)
        {
            lock (_lock)
            {
                errorMessage = string.Empty;
                _lastVoltage = 0.0;
                _lastCurrent = 0.0;
                _lastPower = 0.0;
                if (_serialPort != null && _serialPort.IsOpen)
                {
                    Disconnect();
                }
                if (_isSimulating)
                {
                    Disconnect();
                }

                if (portName.Equals("SIMULATOR", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        _isSimulating = true;
                        _currentPort = portName;
                        _currentBaudRate = baudRate;
                        _savedPort = portName;
                        _savedBaudRate = baudRate;
                        _simMaxCurrent = 2.0; // Reset a default de simulación
                        _simShuntResistor = 0.01; // Reset a default de simulación
                        _lastShuntVoltage = 0.0;
                        SaveSettings(portName, baudRate);

                        _simCts = new CancellationTokenSource();
                        Task.Run(() => SimulationLoop(_simCts.Token));

                        _logger.LogInformation("Puerto SIMULATOR iniciado con éxito.");
                        _hubContext.Clients.All.SendAsync("ReceiveStatus", new { IsConnected = true, PortName = portName, BaudRate = baudRate });
                        _hubContext.Clients.All.SendAsync("ReceiveTxLog", "[Sistema] Simulación de puerto COM conectada con éxito.");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        errorMessage = ex.Message;
                        return false;
                    }
                }

                try
                {
                    _serialPort = new SerialPort(portName, baudRate)
                    {
                        ReadTimeout = 1000,
                        WriteTimeout = 1000,
                        NewLine = "\n"
                    };

                    _serialPort.Open();

                    // Limpiar buffers iniciales
                    _serialPort.DiscardInBuffer();
                    _serialPort.DiscardOutBuffer();

                    _currentPort = portName;
                    _currentBaudRate = baudRate;
                    _savedPort = portName;
                    _savedBaudRate = baudRate;
                    SaveSettings(portName, baudRate);

                    _isRunning = true;
                    _readThread = new Thread(ReadLoop)
                    {
                        IsBackground = true,
                        Name = "SerialReadThread"
                    };
                    _readThread.Start();

                    _logger.LogInformation($"Puerto serie {portName} abierto con éxito a {baudRate} bps.");
                    _hubContext.Clients.All.SendAsync("ReceiveStatus", new { IsConnected = true, PortName = portName, BaudRate = baudRate });
                    _hubContext.Clients.All.SendAsync("ReceiveTxLog", $"[Sistema] Puerto COM conectado con éxito a {baudRate} bps.");
                    return true;
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
                    _logger.LogError($"Error al abrir el puerto {portName}: {ex.Message}");
                    _hubContext.Clients.All.SendAsync("ReceiveTxLog", $"[Sistema] Error de conexión en {portName}: {ex.Message}");
                    return false;
                }
            }
        }

        // Cierra la conexión física o detiene la simulación
        public void Disconnect()
        {
            lock (_lock)
            {
                _isRunning = false;
                if (_isSimulating)
                {
                    _isSimulating = false;
                    _simCts?.Cancel();
                    _simCts?.Dispose();
                    _simCts = null;
                }

                if (_serialPort != null)
                {
                    try
                    {
                        if (_serialPort.IsOpen)
                        {
                            _serialPort.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error al cerrar el puerto serie: {ex.Message}");
                    }
                    finally
                    {
                        _serialPort.Dispose();
                        _serialPort = null;
                    }
                }

                _currentPort = null;
                _logger.LogInformation("Puerto serie o simulación desconectada.");
                _hubContext.Clients.All.SendAsync("ReceiveStatus", new { IsConnected = false, PortName = (string?)null, BaudRate = 0 });
                _hubContext.Clients.All.SendAsync("ReceiveTxLog", "[Sistema] Puerto serie desconectado.");
            }
        }

        // Envía un comando de texto a la tarjeta de desarrollo
        public bool SendCommand(string cmd)
        {
            lock (_lock)
            {
                if (!cmd.EndsWith("\n"))
                {
                    cmd += "\n";
                }

                if (_isSimulating)
                {
                    _logger.LogInformation($"Comando simulado enviado: '{cmd}'");
                    _hubContext.Clients.All.SendAsync("ReceiveTxLog", $"Enviado (Simulación): '{cmd.Trim()}'");

                    if (cmd.StartsWith("SET:"))
                    {
                        var parts = cmd.Trim().Split(':');
                        if (parts.Length >= 3 && 
                            double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out double maxI) && 
                            double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out double shuntR))
                        {
                            _simMaxCurrent = maxI; // Actualizar límite de corriente del simulador
                            _simShuntResistor = shuntR; // Actualizar resistencia Shunt del simulador
                            _hubContext.Clients.All.SendAsync("ReceiveTxLog", $"[Simulador] Calibración INA236 Actualizada: Corriente Máx = {maxI} A, Shunt Resistor = {shuntR} Ohms.");
                        }
                    }
                    return true;
                }

                if (_serialPort == null || !_serialPort.IsOpen)
                {
                    _logger.LogWarning("Intento de envío de comando sin puerto serie activo.");
                    return false;
                }

                try
                {
                    _serialPort.Write(cmd);
                    _logger.LogInformation($"Enviado comando serie: '{cmd}'");
                    _hubContext.Clients.All.SendAsync("ReceiveTxLog", $"Enviado: '{cmd}'");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error al transmitir comando serie: {ex.Message}");
                    _hubContext.Clients.All.SendAsync("ReceiveTxLog", $"Error al enviar: {ex.Message}");
                    return false;
                }
            }
        }

        // Bucle de simulación para generar lecturas sin tarjeta física
        private async Task SimulationLoop(CancellationToken token)
        {
            var random = new Random();
            double phase = 0.0;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Voltaje base 12V con fluctuación leve (+/- 0.1V) y caídas bajo carga
                    double baseVoltage = 12.0;
                    
                    // Simular carga de corriente escalada a _simMaxCurrent
                    // Genera una variación sinusoidal con periodos y ruido proporcionales
                    phase += 0.05;
                    double maxI;
                    double shuntR;
                    lock (_lock)
                    {
                        maxI = _simMaxCurrent;
                        shuntR = _simShuntResistor;
                    }
                    double currentLoad = (maxI * 0.4) + (maxI * 0.25) * Math.Sin(phase); // Oscila entre 15% y 65% de I_max

                    // Simular un pulso alto intermitente (un motor que enciende cada 12 segundos)
                    double totalSeconds = DateTime.Now.TimeOfDay.TotalSeconds;
                    if (((int)(totalSeconds / 12.0) % 2) == 0)
                    {
                        currentLoad += (maxI * 0.3); // Agrega 30% del máximo
                    }

                    // Ruido aleatorio en corriente
                    currentLoad += (random.NextDouble() - 0.5) * (maxI * 0.05);
                    currentLoad = Math.Clamp(currentLoad, 0.0, maxI);

                    // El voltaje decae ligeramente con corrientes muy altas (resistencia de fuente)
                    double voltageDrop = currentLoad * 0.08;
                    double voltageNoise = (random.NextDouble() - 0.5) * 0.05;
                    double voltage = Math.Clamp(baseVoltage - voltageDrop + voltageNoise, 0.0, 36.0);

                    // Potencia calculada
                    double power = voltage * currentLoad;

                    // Calcular Voltaje de Shunt simulado (en mV): Vshunt = I * Rshunt * 1000
                    double shuntVoltageVal = currentLoad * shuntR * 1000.0;

                    // Enviar log crudo simulated
                    string rawLine = $"V:{voltage:F2}V, I:{currentLoad:F2}A, P:{power:F2}W, ShuntV:{shuntVoltageVal:F4}mV";
                    await _hubContext.Clients.All.SendAsync("ReceiveData", rawLine, cancellationToken: token);

                    // Enviar datos parseados
                    await _hubContext.Clients.All.SendAsync("ReceiveTelemetry", new
                    {
                        voltage = Math.Round(voltage, 2),
                        current = Math.Round(currentLoad, 2),
                        power = Math.Round(power, 2),
                        shuntVoltage = Math.Round(shuntVoltageVal, 4)
                    }, cancellationToken: token);

                    await Task.Delay(250, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error en bucle de simulación: {ex.Message}");
                    await Task.Delay(1000, token);
                }
            }
        }

        // Bucle de lectura físico en hilo secundario
        private void ReadLoop()
        {
            while (_isRunning)
            {
                try
                {
                    if (_serialPort != null && _serialPort.IsOpen)
                    {
                        string line = _serialPort.ReadLine();
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            string trimmedLine = line.Trim();
                            _logger.LogInformation($"Recibido del microcontrolador: \"{trimmedLine}\"");

                            // Notificar log crudo
                            _hubContext.Clients.All.SendAsync("ReceiveData", trimmedLine);

                            // Parser de telemetría
                            var (v, i, p, sv) = ParseTelemetry(trimmedLine);
                            if (v.HasValue || i.HasValue || p.HasValue || sv.HasValue)
                            {
                                lock (_lock)
                                {
                                    if (v.HasValue) _lastVoltage = v.Value;
                                    if (i.HasValue) _lastCurrent = i.Value;
                                    if (p.HasValue) _lastPower = p.Value;
                                    if (sv.HasValue) _lastShuntVoltage = sv.Value;
                                }

                                _hubContext.Clients.All.SendAsync("ReceiveTelemetry", new
                                {
                                    voltage = _lastVoltage,
                                    current = _lastCurrent,
                                    power = _lastPower,
                                    shuntVoltage = _lastShuntVoltage
                                });
                            }
                        }
                    }
                    else
                    {
                        Thread.Sleep(100);
                    }
                }
                catch (TimeoutException)
                {
                    // Timeout de lectura normal
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error en el bucle de lectura del puerto serie: {ex.Message}");
                    lock (_lock)
                    {
                        if (_serialPort == null || !_serialPort.IsOpen)
                        {
                            HandleUnexpectedDisconnection();
                            break;
                        }
                    }
                    Thread.Sleep(500);
                }
            }
        }

        // Parser robusto para diferentes formatos de trama serie
        private (double? V, double? I, double? P, double? SV) ParseTelemetry(string line)
        {
            double? v = null, i = null, p = null, sv = null;

            // 1. Intentar parser JSON
            try
            {
                if (line.Trim().StartsWith("{") && line.Trim().EndsWith("}"))
                {
                    var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("V", out var vProp) || root.TryGetProperty("voltage", out vProp))
                        v = ConvertToDouble(vProp);
                    if (root.TryGetProperty("I", out var iProp) || root.TryGetProperty("current", out iProp))
                        i = ConvertToDouble(iProp);
                    if (root.TryGetProperty("P", out var pProp) || root.TryGetProperty("power", out pProp))
                        p = ConvertToDouble(pProp);
                    if (root.TryGetProperty("SV", out var svProp) || root.TryGetProperty("shuntVoltage", out svProp))
                        sv = ConvertToDouble(svProp);

                    return (v, i, p, sv);
                }
            }
            catch { }

            // 2. Interceptar tramas de Voltaje Shunt específicas para evitar colisión con el voltaje del bus
            if (line.Contains("Shunt Voltage", StringComparison.OrdinalIgnoreCase) || 
                line.Contains("ShuntV", StringComparison.OrdinalIgnoreCase))
            {
                var matchSV = Regex.Match(line, @"(?:Shunt\s+Voltage|ShuntV)\s*[:=]\s*(-?[0-9.]+)");
                if (matchSV.Success && double.TryParse(matchSV.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double svVal))
                {
                    sv = svVal;
                    // Remover la parte de Shunt Voltage para evitar que el parser de Voltaje normal lo confunda
                    line = line.Replace(matchSV.Value, "");
                }
            }

            // 3. Parser Key-Value usando expresiones regulares
            // Busca voltajes: V:12.50V, V = 12.5, voltage:12, Load Voltage = 12
            var matchV = Regex.Match(line, @"[Vv](?:olt(?:age|aje)?)?\s*[:=]\s*([0-9.]+)");
            // Busca corrientes: I:1.25A, Current = 1.25, corriente:1.2, C:1.2
            var matchI = Regex.Match(line, @"(?:[Ii](?:nst(?:ante)?)?|[Cc](?:urr(?:ent)?|orr(?:iente)?)?)\s*[:=]\s*([0-9.]+)");
            // Busca potencias: P:15.6W, Power = 15.6, potencia:15
            var matchP = Regex.Match(line, @"[Pp](?:ow(?:er)?|ot(?:encia)?)?\s*[:=]\s*([0-9.]+)");

            if (matchV.Success && double.TryParse(matchV.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double vVal))
            {
                v = vVal;
            }
            if (matchI.Success && double.TryParse(matchI.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double iVal))
            {
                // Convertir mA a A
                if (line.Contains("mA", StringComparison.OrdinalIgnoreCase))
                {
                    i = iVal / 1000.0;
                }
                else
                {
                    i = iVal;
                }
            }
            if (matchP.Success && double.TryParse(matchP.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double pVal))
            {
                // Convertir mW a W
                if (line.Contains("mW", StringComparison.OrdinalIgnoreCase))
                {
                    p = pVal / 1000.0;
                }
                else
                {
                    p = pVal;
                }
            }

            if (v.HasValue || i.HasValue || p.HasValue || sv.HasValue)
            {
                return (v, i, p, sv);
            }

            // 4. Fallback: Separación por comas simple (V, I, P, SV)
            var parts = line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                if (double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out double val1) &&
                    double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out double val2) &&
                    double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out double val3))
                {
                    double? parsedSV = null;
                    if (parts.Length >= 4 && double.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out double val4))
                    {
                        parsedSV = val4;
                    }
                    return (val1, val2, val3, parsedSV); // Asume orden Voltaje, Corriente, Potencia, ShuntVoltage
                }
            }

            return (null, null, null, null);
        }

        private double? ConvertToDouble(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number)
            {
                return element.GetDouble();
            }
            if (element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), System.Globalization.CultureInfo.InvariantCulture, out double val))
            {
                return val;
            }
            return null;
        }

        private void HandleUnexpectedDisconnection()
        {
            _logger.LogWarning("Desconexión inesperada de hardware detectada.");
            Disconnect();
            _hubContext.Clients.All.SendAsync("ReceiveTxLog", "[Sistema]⚠️ Advertencia: Conexión perdida con la tarjeta STM32 (Puerto COM cerrado inesperadamente).");
        }

        private void TryAutoConnect()
        {
            if (string.IsNullOrEmpty(_savedPort)) return;

            string[] availablePorts = GetAvailablePorts();
            bool isPortAvailable = Array.Exists(availablePorts, p => p.Equals(_savedPort, StringComparison.OrdinalIgnoreCase));

            if (isPortAvailable)
            {
                _logger.LogInformation($"Restableciendo conexión guardada en puerto: {_savedPort}");
                string error;
                Connect(_savedPort, _savedBaudRate, out error);
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    string json = File.ReadAllText(_settingsPath);
                    var settings = JsonSerializer.Deserialize<SettingsData>(json);
                    if (settings != null)
                    {
                        _savedPort = settings.SavedPort;
                        _savedBaudRate = settings.SavedBaudRate;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"No se pudo cargar la configuración de puerto: {ex.Message}");
            }
        }

        private void SaveSettings(string portName, int baudRate)
        {
            try
            {
                var settings = new SettingsData { SavedPort = portName, SavedBaudRate = baudRate };
                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError($"No se pudo guardar la configuración de puerto: {ex.Message}");
            }
        }

        private class SettingsData
        {
            public string? SavedPort { get; set; }
            public int SavedBaudRate { get; set; }
        }
    }
}
