import { SecureChannel } from './crypto';

export type LinkState = 'connecting' | 'online' | 'host-offline' | 'offline';

export interface RelayHandlers {
  onMessage: (msg: Record<string, unknown>) => void;
  onState: (state: LinkState) => void;
  onOpen?: () => void;
}

/** One phone-side connection to a relay room. Reconnects with backoff; every payload is encrypted. */
export class RelayConnection {
  private socket: WebSocket | null = null;
  private attempt = 0;
  private timer: number | undefined;
  private closed = false;
  private hostOnline = false;

  constructor(
    private readonly relay: string,
    private readonly roomId: string,
    private readonly channel: SecureChannel,
    private readonly handlers: RelayHandlers,
  ) {}

  start(): void {
    this.closed = false;
    this.connect();
  }

  close(): void {
    this.closed = true;
    window.clearTimeout(this.timer);
    this.socket?.close(1000);
    this.socket = null;
  }

  async send(message: unknown): Promise<boolean> {
    if (this.socket?.readyState !== WebSocket.OPEN) return false;
    this.socket.send(JSON.stringify({ d: await this.channel.seal(message) }));
    return true;
  }

  private connect(): void {
    if (this.closed) return;
    this.handlers.onState('connecting');

    const url = `${this.relay.replace(/\/$/, '')}/ws?room=${this.roomId}&role=phone`;
    const socket = new WebSocket(url);
    this.socket = socket;

    socket.onopen = () => {
      this.attempt = 0;
      this.handlers.onState(this.hostOnline ? 'online' : 'host-offline');
    };

    socket.onmessage = async (e) => {
      let envelope: { ev?: string; online?: boolean; d?: string };
      try { envelope = JSON.parse(e.data as string); } catch { return; }

      if (envelope.ev === 'host') {
        this.hostOnline = !!envelope.online;
        this.handlers.onState(this.hostOnline ? 'online' : 'host-offline');
        // The relay announces the host right after we join, and again whenever the PC reconnects: sync then.
        if (this.hostOnline) this.handlers.onOpen?.();
        return;
      }
      if (typeof envelope.d !== 'string') return;

      const message = await this.channel.open(envelope.d);
      if (message && typeof message === 'object') this.handlers.onMessage(message as Record<string, unknown>);
    };

    socket.onclose = () => {
      if (this.socket !== socket) return;
      this.hostOnline = false;
      this.handlers.onState('offline');
      if (this.closed) return;
      const delay = Math.min(1000 * 2 ** this.attempt++, 15_000);
      this.timer = window.setTimeout(() => this.connect(), delay);
    };

    socket.onerror = () => socket.close();
  }
}
