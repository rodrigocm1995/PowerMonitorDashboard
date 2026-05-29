import {
  Component,
  OnInit,
  AfterViewInit,
  ViewChild,
  ElementRef,
  signal,
  effect,
  inject,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { SerialService, TelemetryData } from './services/serial.service';
import { Chart, registerables } from 'chart.js';

Chart.register(...registerables);

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './app.component.html',
  styleUrls: ['./app.component.css'],
})
export class AppComponent implements OnInit, AfterViewInit {
  public readonly serialService = inject(SerialService);

  // --- CANVASES DE LAS GRÁFICAS ---
  @ViewChild('currentChartCanvas') currentChartCanvas!: ElementRef<HTMLCanvasElement>;
  @ViewChild('voltageChartCanvas') voltageChartCanvas!: ElementRef<HTMLCanvasElement>;
  @ViewChild('powerChartCanvas') powerChartCanvas!: ElementRef<HTMLCanvasElement>;

  // --- REFERENCIA DE LA TERMINAL ---
  @ViewChild('serialTerminal') serialTerminal!: ElementRef<HTMLDivElement>;

  // --- VARIABLES DE INTERFAZ ---
  public readonly availablePorts = signal<string[]>([]);
  public readonly selectedPort = signal<string>('');
  public readonly selectedBaudRate = signal<number>(115200);
  public readonly isDarkMode = signal<boolean>(true);

  // Parámetros de calibración del sensor INA236
  public readonly maxCurrentInput = signal<number>(2);
  public readonly shuntResistorInput = signal<number>(10);
  public readonly shuntUnitInput = signal<string>('mOhms');

  public readonly baudRates = [9600, 19200, 38400, 57600, 115200];

  // Instancias de Chart.js
  private currentChart!: Chart;
  private voltageChart!: Chart;
  private powerChart!: Chart;

  constructor() {
    // Efecto reactivo para actualizar gráficas cuando cambia la telemetría
    effect(() => {
      const data = this.serialService.telemetry();
      this.updateCharts(data);
    });

    // Efecto reactivo para auto-scrollear la consola de logs
    effect(() => {
      const lines = this.serialService.terminalLines();
      if (lines.length > 0) {
        setTimeout(() => this.scrollToBottom(), 50);
      }
    });
  }

  ngOnInit() {
    this.loadPorts();

    // Si hay un puerto guardado del servidor backend, configurarlo
    effect(() => {
      const savedPort = this.serialService.savedPort();
      const savedBaud = this.serialService.savedBaudRate();
      if (savedPort) {
        this.selectedPort.set(savedPort);
        this.selectedBaudRate.set(savedBaud);
      }
    });
  }

  ngAfterViewInit() {
    this.initializeCharts();
  }

  // Carga los puertos desde el backend
  public loadPorts() {
    if (this.serialService.isConnected()) return;

    this.serialService.getAvailablePorts().subscribe({
      next: (ports) => {
        this.availablePorts.set(ports);

        // Auto-seleccionar primer puerto disponible si no hay selección
        if (ports.length > 0 && !this.selectedPort()) {
          const defaultPort = ports.includes('SIMULATOR') ? 'SIMULATOR' : ports[0];
          this.selectedPort.set(defaultPort);
        }
      },
      error: () => {
        this.serialService.logToTerminal(
          '[Sistema] No se pudieron cargar los puertos del backend.',
          'error',
        );
      },
    });
  }

  // Maneja la acción de conectar
  public onConnect() {
    const port = this.selectedPort();
    const baud = this.selectedBaudRate();

    if (!port) return;

    this.serialService.connect(port, baud).subscribe({
      next: (res) => {
        // La conexión exitosa actualizará los estados vía SignalR automáticamente
        this.clearChartData();
      },
      error: (err) => {
        const errorMsg = err.error?.message || err.message || 'Error desconocido';
        this.serialService.logToTerminal(`[Sistema] Error de conexión: ${errorMsg}`, 'error');
      },
    });
  }

  // Maneja la acción de desconectar
  public onDisconnect() {
    this.serialService.disconnect().subscribe({
      next: () => {
        // La desconexión actualizará los estados vía SignalR automáticamente
      },
      error: (err) => {
        this.serialService.logToTerminal(`[Sistema] Error al desconectar: ${err.message}`, 'error');
      },
    });
  }

  // Aplica la calibración del sensor INA236
  public onApplyConfig() {
    console.log('Botón presionado');
    if (!this.serialService.isConnected()) {
      this.serialService.logToTerminal(
        '[Sistema] Error: Debe estar conectado a un puerto para aplicar la calibración.',
        'error',
      );
      return;
    }

    let maxI = this.maxCurrentInput();
    let shuntRaw = this.shuntResistorInput();
    let unit = this.shuntUnitInput();

    // Validar Corriente Máxima: entero entre 1 y 100
    if (!Number.isInteger(maxI) || maxI < 1 || maxI > 100) {
      this.serialService.logToTerminal(
        '[Sistema] Error: La corriente máxima debe ser un valor entero entre 1 y 100 A.',
        'error',
      );
      return;
    }

    // Validar Shunt Resistor: positivo
    if (isNaN(shuntRaw) || shuntRaw <= 0) {
      this.serialService.logToTerminal(
        '[Sistema] Error: El resistor Shunt debe ser un valor decimal mayor a 0.',
        'error',
      );
      return;
    }

    // Calcular el valor en Ohms según la unidad
    const shuntOhms = unit === 'mOhms' ? shuntRaw / 1000 : shuntRaw;
    console.log(shuntOhms);
    // Formatear el comando: SET:maxCurrent:shuntOhms
    // Se usa toFixed(6) para evitar notación científica y proveer precisión de micro-Ohms
    const command = `SET:${maxI}:${shuntOhms.toFixed(6)}\n`;
    console.log(command);

    this.serialService.sendCommand(command).subscribe({
      next: () => {
        this.serialService.logToTerminal(
          `[Sistema] Comando de calibración enviado: ${command.trim()}`,
          'system',
        );
      },
      error: (err) => {
        this.serialService.logToTerminal(
          `[Sistema] Error al enviar comando de calibración: ${err.message}`,
          'error',
        );
      },
    });
  }

  // Limpia los logs de consola
  public onClearConsole() {
    this.serialService.clearTerminal();
  }

  // Alterna entre tema claro y oscuro
  public onToggleTheme() {
    this.isDarkMode.update((v) => !v);
    const body = document.body;
    if (this.isDarkMode()) {
      body.classList.remove('light-mode');
    } else {
      body.classList.add('light-mode');
    }

    this.updateChartTheme();
  }

  // Formatea valores menores a 1 en submúltiplos (mA, mV, mW)
  public formatValue(val: number | undefined | null, baseUnit: string): string {
    if (val === undefined || val === null) {
      return `0.00 ${baseUnit}`;
    }
    const absVal = Math.abs(val);
    if (absVal > 0 && absVal < 1) {
      return `${(val * 1000).toFixed(3)} m${baseUnit}`;
    }
    return `${val.toFixed(2)} ${baseUnit}`;
  }

  // Auto-scroll al fondo de la terminal
  private scrollToBottom() {
    if (this.serialTerminal) {
      const el = this.serialTerminal.nativeElement;
      el.scrollTop = el.scrollHeight;
    }
  }

  // Inicializa las instancias de Chart.js
  private initializeCharts() {
    const chartConfig = (
      canvas: HTMLCanvasElement,
      label: string,
      lineColor: string,
      fillColor: string,
      yMaxInit: number,
    ) => {
      const ctx = canvas.getContext('2d');
      let gradient: CanvasGradient | undefined;

      if (ctx) {
        gradient = ctx.createLinearGradient(0, 0, 0, 200);
        gradient.addColorStop(0, fillColor);
        gradient.addColorStop(1, 'rgba(0, 0, 0, 0)');
      }

      return new Chart(canvas, {
        type: 'line',
        data: {
          labels: [],
          datasets: [
            {
              label: label,
              data: [],
              borderColor: lineColor,
              backgroundColor: gradient || lineColor,
              fill: true,
              borderWidth: 2,
              tension: 0.4, // Suaviza la curva
              pointRadius: 0, // Ocultar puntos para una línea fluida
              pointHoverRadius: 5,
            },
          ],
        },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          plugins: {
            legend: { display: false },
            tooltip: {
              mode: 'index',
              intersect: false,
              backgroundColor: 'rgba(15, 23, 42, 0.9)',
              titleColor: '#fff',
              bodyColor: '#fff',
              borderColor: 'rgba(255,255,255,0.1)',
              borderWidth: 1,
            },
          },
          scales: {
            x: {
              grid: { color: 'rgba(255, 255, 255, 0.05)' },
              ticks: {
                color: '#9ca3af',
                maxRotation: 0,
                autoSkip: true,
                maxTicksLimit: 5,
                font: { family: 'Outfit', size: 10 },
              },
            },
            y: {
              min: 0,
              grid: { color: 'rgba(255, 255, 255, 0.05)' },
              ticks: {
                color: '#9ca3af',
                font: { family: 'Outfit', size: 10 },
              },
            },
          },
        },
      });
    };

    // Crear las 3 gráficas
    this.currentChart = chartConfig(
      this.currentChartCanvas.nativeElement,
      'Corriente (A)',
      '#00ff88',
      'rgba(0, 255, 136, 0.2)',
      10,
    );

    this.voltageChart = chartConfig(
      this.voltageChartCanvas.nativeElement,
      'Voltaje (V)',
      '#00f2fe',
      'rgba(0, 242, 254, 0.2)',
      36,
    );

    this.powerChart = chartConfig(
      this.powerChartCanvas.nativeElement,
      'Potencia (W)',
      '#ff9f43',
      'rgba(255, 159, 67, 0.2)',
      100,
    );
  }

  // Agrega datos en tiempo real a las gráficas (mantiene los últimos 40 puntos)
  private updateCharts(data: TelemetryData) {
    if (!this.currentChart || !this.voltageChart || !this.powerChart) return;
    if (!this.serialService.isConnected()) return;

    const timeLabel = new Date().toLocaleTimeString([], {
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    });

    const pushData = (chart: Chart, value: number) => {
      chart.data.labels?.push(timeLabel);
      chart.data.datasets[0].data.push(value);

      if (chart.data.datasets[0].data.length > 45) {
        chart.data.datasets[0].data.shift();
        chart.data.labels?.shift();
      }
      chart.update('none'); // Actualiza de forma asíncrona y eficiente sin animar la transición
    };

    pushData(this.currentChart, data.current);
    pushData(this.voltageChart, data.voltage);
    pushData(this.powerChart, data.power);
  }

  // Limpia los datos de las gráficas
  private clearChartData() {
    const resetChart = (chart: Chart) => {
      if (chart) {
        chart.data.labels = [];
        chart.data.datasets[0].data = [];
        chart.update('none');
      }
    };
    resetChart(this.currentChart);
    resetChart(this.voltageChart);
    resetChart(this.powerChart);
  }

  // Actualiza los colores de las cuadrículas de gráficas al cambiar el tema (Light/Dark)
  private updateChartTheme() {
    const isDark = this.isDarkMode();
    const gridColor = isDark ? 'rgba(255, 255, 255, 0.05)' : 'rgba(0, 0, 0, 0.05)';
    const textColor = isDark ? '#9ca3af' : '#4b5563';

    const applyTheme = (chart: Chart) => {
      if (!chart) return;

      // Actualizar escalas x e y
      if (chart.options.scales?.['x']) {
        chart.options.scales['x'].grid = { color: gridColor };
        if (chart.options.scales['x'].ticks) {
          chart.options.scales['x'].ticks.color = textColor;
        }
      }
      if (chart.options.scales?.['y']) {
        chart.options.scales['y'].grid = { color: gridColor };
        if (chart.options.scales['y'].ticks) {
          chart.options.scales['y'].ticks.color = textColor;
        }
      }
      chart.update('none');
    };

    applyTheme(this.currentChart);
    applyTheme(this.voltageChart);
    applyTheme(this.powerChart);
  }
}
