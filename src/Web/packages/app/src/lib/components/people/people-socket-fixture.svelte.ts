import type { WebSocketConfig } from "#lib/websocket/types.js";

/**
 * A stand-in for `WebSocketClient` for the People page's tests, so rendering the page opens no
 * socket and fetches no ticket. A test file mocks the client module with this one:
 *
 * `vi.mock("#lib/websocket/websocket-client.svelte.js", () => import("./people-socket-fixture.svelte.js"));`
 */
export const overviewFeed = $state({ connected: false });

/** Every client the page built, newest last. */
export const overviewSockets: WebSocketClient[] = [];

export class WebSocketClient {
  readonly handlers: Record<string, () => void> = {};
  admissions = 0;
  destroyed = false;

  constructor(readonly config: WebSocketConfig) {
    overviewSockets.push(this);
  }

  get isConnected(): boolean {
    return overviewFeed.connected && !this.destroyed;
  }

  on(event: string, handler: () => void): void {
    this.handlers[event] = handler;
  }

  async connectIfAdmitted(): Promise<void> {
    this.admissions++;
  }

  destroy(): void {
    this.destroyed = true;
  }
}
