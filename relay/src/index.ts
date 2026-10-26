/**
 * Levity relay: pairs one PC ("host") with phones in a room and forwards opaque, end-to-end
 * encrypted frames between them. It never sees keys or plaintext, so it cannot read chats
 * or send commands to the PC.
 *
 * host  -> relay : {"d": "<ciphertext>", "to"?: "<phoneId>"}   (no "to" = every phone)
 * phone -> relay : {"d": "<ciphertext>"}
 * relay -> host  : {"c": "<phoneId>", "d": "..."} | {"ev":"join"|"leave","c":"<phoneId>"}
 * relay -> phone : {"d": "..."} | {"ev":"host","online":boolean}
 */
import { DurableObject } from 'cloudflare:workers';

export interface Env {
  ROOMS: DurableObjectNamespace<Room>;
}

const MAX_FRAME = 1_000_000;
const MAX_PHONES_PER_ROOM = 8;
const ROOM_RE = /^[0-9a-f]{32}$/;

export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    const url = new URL(req.url);
    if (url.pathname !== '/ws') return new Response('levity relay', { status: 200 });

    const room = url.searchParams.get('room') ?? '';
    const role = url.searchParams.get('role');
    if (!ROOM_RE.test(room) || (role !== 'host' && role !== 'phone')) {
      return new Response('bad request', { status: 400 });
    }
    if (req.headers.get('Upgrade') !== 'websocket') {
      return new Response('expected websocket', { status: 426 });
    }
    return env.ROOMS.get(env.ROOMS.idFromName(room)).fetch(req);
  },
};

export class Room extends DurableObject<Env> {
  async fetch(req: Request): Promise<Response> {
    const role = new URL(req.url).searchParams.get('role');
    const { 0: client, 1: server } = new WebSocketPair();

    if (role === 'host') {
      for (const old of this.ctx.getWebSockets('host')) old.close(1000, 'replaced by a newer host');
      this.ctx.acceptWebSocket(server, ['host']);
      this.toPhones(JSON.stringify({ ev: 'host', online: true }));
      for (const phone of this.ctx.getWebSockets('phone')) {
        server.send(JSON.stringify({ ev: 'join', c: this.phoneId(phone) }));
      }
    } else {
      if (this.ctx.getWebSockets('phone').length >= MAX_PHONES_PER_ROOM) {
        return new Response('room full', { status: 429 });
      }
      const id = crypto.randomUUID().slice(0, 8);
      this.ctx.acceptWebSocket(server, ['phone', `id:${id}`]);
      server.send(JSON.stringify({ ev: 'host', online: this.ctx.getWebSockets('host').length > 0 }));
      this.toHosts(JSON.stringify({ ev: 'join', c: id }));
    }

    return new Response(null, { status: 101, webSocket: client });
  }

  webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): void {
    if (typeof message !== 'string' || message.length > MAX_FRAME) {
      ws.close(1009, 'invalid frame');
      return;
    }

    let frame: { d?: unknown; to?: unknown };
    try { frame = JSON.parse(message); } catch { return; }
    if (typeof frame.d !== 'string') return;

    if (this.ctx.getTags(ws).includes('host')) {
      const out = JSON.stringify({ d: frame.d });
      const to = typeof frame.to === 'string' ? frame.to : null;
      for (const phone of this.ctx.getWebSockets('phone')) {
        if (!to || this.phoneId(phone) === to) phone.send(out);
      }
    } else {
      this.toHosts(JSON.stringify({ c: this.phoneId(ws), d: frame.d }));
    }
  }

  webSocketClose(ws: WebSocket, code: number, reason: string): void {
    this.onGone(ws);
    try { ws.close(code, reason); } catch { /* already closed */ }
  }

  webSocketError(ws: WebSocket): void {
    this.onGone(ws);
  }

  private onGone(ws: WebSocket): void {
    if (this.ctx.getTags(ws).includes('host')) {
      // A replaced host closes after the new one registered; only report offline if none remain.
      const others = this.ctx.getWebSockets('host').filter((h) => h !== ws);
      if (others.length === 0) this.toPhones(JSON.stringify({ ev: 'host', online: false }));
    } else {
      this.toHosts(JSON.stringify({ ev: 'leave', c: this.phoneId(ws) }));
    }
  }

  private phoneId(ws: WebSocket): string {
    return this.ctx.getTags(ws).find((t) => t.startsWith('id:'))?.slice(3) ?? '';
  }

  private toHosts(msg: string): void {
    for (const h of this.ctx.getWebSockets('host')) h.send(msg);
  }

  private toPhones(msg: string): void {
    for (const p of this.ctx.getWebSockets('phone')) p.send(msg);
  }
}
