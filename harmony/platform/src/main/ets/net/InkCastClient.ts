// InkCast：局域网墨迹同步客户端（NDJSON over TCP）
// 平板把提交后的笔画、清屏与 AI 批注推送到 PC 端的 InkCast 演示服务，
// 由大屏页面实时渲染。演示服务默认监听 9091（TCP）与 9090（大屏 HTTP）。
import { socket } from '@kit.NetworkKit';
import { BusinessError } from '@kit.BasicServicesKit';

export interface InkCastEvent {
  t: string;
}

export class InkCastClient {
  private tcp: socket.TCPSocket | null = null;
  private connected: boolean = false;
  private host: string = '127.0.0.1';
  private port: number = 9091;
  private pending: string[] = [];

  get Connected(): boolean {
    return this.connected;
  }

  connect(host: string, port: number): void {
    this.close();
    this.host = host;
    this.port = port;
    const tcp = socket.constructTCPSocketInstance();
    this.tcp = tcp;
    tcp.on('connect', () => {
      this.connected = true;
      this.flush();
    });
    tcp.on('close', () => {
      this.connected = false;
    });
    tcp.on('error', (err: BusinessError) => {
      void err;
      this.connected = false;
    });
    tcp.on('message', (value: socket.TCPReceiveInfo) => {
      void value;
    });
    tcp.connect({
      address: { address: host, port: port },
      timeout: 5000
    }, (err: BusinessError) => {
      if (err) {
        this.connected = false;
      }
    });
  }

  send(event: Object): void {
    const line = JSON.stringify(event) + '\n';
    if (!this.connected || this.tcp === null) {
      this.pending.push(line);
      if (this.pending.length > 500) {
        this.pending.shift();
      }
      return;
    }
    this.tcp.send({ data: line }, (err: BusinessError) => {
      if (err) {
        this.connected = false;
      }
    });
  }

  sendStroke(id: string, colorArgb: number, width: number, highlighter: boolean,
    alpha: number, taper: number, shape: boolean, points: number[][]): void {
    this.send({
      t: 'stroke',
      id: id,
      color: colorArgb,
      width: width,
      highlighter: highlighter,
      alpha: alpha,
      taper: taper,
      shape: shape,
      points: points
    });
  }

  sendClear(): void {
    this.send({ t: 'clear' });
  }

  sendAi(title: string, text: string, tags: string[]): void {
    this.send({ t: 'ai', title: title, text: text, tags: tags });
  }

  private flush(): void {
    if (this.tcp === null) {
      return;
    }
    const lines = this.pending;
    this.pending = [];
    for (let i = 0; i < lines.length; i++) {
      this.tcp.send({ data: lines[i] }, (err: BusinessError) => {
        void err;
      });
    }
  }

  close(): void {
    if (this.tcp !== null) {
      try {
        this.tcp.close();
      } catch (error) {
        void error;
      }
    }
    this.tcp = null;
    this.connected = false;
    this.pending = [];
  }
}