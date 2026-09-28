import { env } from "./env.ts";

const RECORD_SEPARATOR = "\x1e";

// SignalR hub protocol message types; pings and stream items are ignored.
const INVOCATION = 1;
const COMPLETION = 3;
const CLOSE = 7;

interface HubMessage {
  type: number;
  target?: string;
  arguments?: unknown[];
  invocationId?: string;
  result?: unknown;
  error?: string;
}

export interface HubEvent {
  target: string;
  args: unknown[];
}

/**
 * A minimal SignalR JSON-protocol client over the runtime's WebSocket, so specs need no client
 * package. Connects without negotiation (WebSockets only), sends the tenant host as
 * X-Forwarded-Host the way the gateway and the bridge do, and records every server invocation so a
 * spec can wait for an event that arrived before it started waiting.
 */
export class HubConnection {
  readonly events: HubEvent[] = [];
  private readonly socket: WebSocket;
  private readonly pending = new Map<string, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
  private readonly listeners = new Set<() => void>();
  private nextId = 1;
  private buffer = "";

  private constructor(socket: WebSocket) {
    this.socket = socket;
  }

  static async connect(opts: { host: string; hub: string; token?: string; timeoutMs?: number }): Promise<HubConnection> {
    const url = `${env.apiUrl.replace(/^http/, "ws")}/hubs/${opts.hub}`;
    const headers: Record<string, string> = { "x-forwarded-host": opts.host };
    if (opts.token) headers.authorization = `Bearer ${opts.token}`;
    // Node's WebSocket (undici) takes an init object with headers; the DOM typing knows only protocols.
    const socket = new WebSocket(url, { headers } as unknown as string[]);
    const connection = new HubConnection(socket);

    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`no SignalR handshake from ${url}`)), opts.timeoutMs ?? 10_000);
      socket.onerror = () => {
        clearTimeout(timer);
        reject(new Error(`WebSocket to ${url} failed`));
      };
      socket.onopen = () => socket.send(JSON.stringify({ protocol: "json", version: 1 }) + RECORD_SEPARATOR);
      socket.onmessage = (event) => {
        const frame = connection.buffer + String(event.data);
        const end = frame.indexOf(RECORD_SEPARATOR);
        if (end < 0) {
          connection.buffer = frame;
          return;
        }
        clearTimeout(timer);
        const handshake = JSON.parse(frame.slice(0, end)) as { error?: string };
        if (handshake.error) return reject(new Error(`SignalR handshake refused: ${handshake.error}`));
        connection.buffer = "";
        socket.onmessage = (e) => connection.receive(String(e.data));
        connection.receive(frame.slice(end + 1));
        resolve();
      };
    });
    return connection;
  }

  private receive(data: string) {
    const frames = (this.buffer + data).split(RECORD_SEPARATOR);
    this.buffer = frames.pop() ?? "";
    for (const frame of frames) {
      if (!frame) continue;
      const message = JSON.parse(frame) as HubMessage;
      if (message.type === INVOCATION && message.target) {
        this.events.push({ target: message.target, args: message.arguments ?? [] });
        for (const listener of this.listeners) listener();
      } else if (message.type === COMPLETION && message.invocationId) {
        const call = this.pending.get(message.invocationId);
        this.pending.delete(message.invocationId);
        if (message.error) call?.reject(new Error(message.error));
        else call?.resolve(message.result);
      } else if (message.type === CLOSE) {
        for (const call of this.pending.values()) call.reject(new Error(`hub closed: ${message.error ?? "no reason"}`));
        this.pending.clear();
      }
    }
  }

  invoke<T = unknown>(target: string, ...args: unknown[]): Promise<T> {
    const invocationId = String(this.nextId++);
    return new Promise<T>((resolve, reject) => {
      this.pending.set(invocationId, { resolve: resolve as (v: unknown) => void, reject });
      this.socket.send(JSON.stringify({ type: INVOCATION, invocationId, target, arguments: args }) + RECORD_SEPARATOR);
    });
  }

  /** Resolves with the first event, received so far or later, that `target` and `match` accept. */
  waitFor(target: string, match: (args: unknown[]) => boolean, opts: { timeoutMs?: number; what?: string } = {}): Promise<unknown[]> {
    const find = () => this.events.find((e) => e.target === target && match(e.args));
    const seen = find();
    if (seen) return Promise.resolve(seen.args);
    return new Promise((resolve, reject) => {
      const listener = () => {
        const found = find();
        if (!found) return;
        clearTimeout(timer);
        this.listeners.delete(listener);
        resolve(found.args);
      };
      const timer = setTimeout(() => {
        this.listeners.delete(listener);
        const received = this.events.map((e) => e.target).join(", ") || "none";
        reject(new Error(`timed out waiting for ${opts.what ?? `'${target}'`}; events received: ${received}`));
      }, opts.timeoutMs ?? 15_000);
      this.listeners.add(listener);
    });
  }

  close() {
    this.socket.close();
  }
}
