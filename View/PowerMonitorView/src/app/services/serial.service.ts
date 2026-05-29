import { Injectable, signal, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import * as signalR from '@microsoft/signalr';

export interface TerminalLine {
  text: string;
  timestamp: string;
  type: 'system' | 'tx' | 'rx' | 'error';
}

export interface TelemetryData {
  current: number;
  voltage: number;
  power: number;
  shuntVoltage?: number;
}

@Injectable({
  providedIn: 'root',
})
export class SerialService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = 'http://localhost:5200/api/serial';
  private hubConnection: signalR.HubConnection | null = null;

  // --- ANGULAR SIGNALS ---
  public readonly isConnected = signal<boolean>(false);
  public readonly activePort = signal<string>('');
  public readonly activeBaudRate = signal<number>(115200);
  
  public readonly savedPort = signal<string>('');
  public readonly savedBaudRate = signal<number>(115200);

  // Telemetría en tiempo real
  public readonly telemetry = signal<TelemetryData>({ current: 0, voltage: 0, power: 0 });

  // Historial de logs
  public readonly terminalLines = signal<TerminalLine[]>([]);

  constructor() {
    this.checkInitialStatus();
    this.startSignalRConnection();
  }

  // 1. PETICIONES HTTP (REST)
  
  public getAvailablePorts(): Observable<string[]> {
    return this.http.get<string[]>(`${this.baseUrl}/ports`);
  }

  public connect(portName: string, baudRate: number): Observable<any> {
    return this.http.post(`${this.baseUrl}/connect`, { portName, baudRate });
  }

  public disconnect(): Observable<any> {
    return this.http.post(`${this.baseUrl}/disconnect`, {});
  }

  public sendCommand(command: string): Observable<any> {
    return this.http.post(`${this.baseUrl}/send`, { command });
  }

  public clearTerminal() {
    this.terminalLines.set([]);
  }

  // 2. CONEXIÓN POR WEBSOCKETS (SignalR)

  private startSignalRConnection() {
    this.hubConnection = new signalR.HubConnectionBuilder()
      .withUrl('http://localhost:5200/hubs/serial')
      .withAutomaticReconnect()
      .build();

    this.hubConnection
      .start()
      .then(() => {
        this.logToTerminal(
          '[Sistema] Conectado al canal WebSockets del servidor backend.',
          'system',
        );
        this.registerSignalREvents();
      })
      .catch((err) => {
        this.logToTerminal(
          `[Sistema] Error al conectar con WebSockets: ${err}`,
          'error',
        );
      });
  }

  private registerSignalREvents() {
    if (!this.hubConnection) return;

    // Escucha logs crudos del puerto COM
    this.hubConnection.on('ReceiveData', (data: string) => {
      this.logToTerminal(data, 'rx');
    });

    // Escucha logs de envío y de sistema
    this.hubConnection.on('ReceiveTxLog', (log: string) => {
      if (log.startsWith('Enviado:')) {
        this.logToTerminal(log, 'tx');
      } else if (log.includes('⚠️') || log.includes('Error') || log.includes('perdida')) {
        this.logToTerminal(log, 'error');
      } else {
        this.logToTerminal(log, 'system');
      }
    });

    // Escucha cambios en el estado de conexión
    this.hubConnection.on(
      'ReceiveStatus',
      (status: {
        isConnected: boolean;
        portName: string | null;
        baudRate: number;
      }) => {
        this.isConnected.set(status.isConnected);
        this.activePort.set(status.portName || '');
        this.activeBaudRate.set(status.baudRate);
      },
    );

    // Escucha telemetría numérica parseada
    this.hubConnection.on('ReceiveTelemetry', (telemetry: TelemetryData) => {
      this.telemetry.set(telemetry);
    });
  }

  private checkInitialStatus() {
    this.http.get<any>(`${this.baseUrl}/status`).subscribe({
      next: (status) => {
        this.isConnected.set(status.isConnected);
        this.activePort.set(status.portName || '');
        this.activeBaudRate.set(status.baudRate);
        this.savedPort.set(status.savedPort || '');
        this.savedBaudRate.set(status.savedBaudRate || 115200);
      },
      error: () => {
        this.logToTerminal(
          '[Sistema] No se pudo obtener el estado inicial del servidor.',
          'error',
        );
      },
    });
  }

  public logToTerminal(text: string, type: 'system' | 'tx' | 'rx' | 'error') {
    const timestamp = new Date().toLocaleTimeString();
    this.terminalLines.update((lines) => {
      // Limitar a los últimos 200 logs para evitar saturación de memoria
      const currentLines = lines.length > 200 ? lines.slice(lines.length - 200) : lines;
      return [...currentLines, { text, timestamp, type }];
    });
  }
}
