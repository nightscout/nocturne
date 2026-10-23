import { Server as SocketIOServerClass, Socket, Namespace, type DefaultEventsMap } from 'socket.io';
import { Server as HttpServer } from 'http';
import logger from './logger.js';
import type { ClientInfo, AlarmData, AlarmEvent, ServerStats } from '../types.js';
import {
  verifyHandshakeTicket,
  normalizeHandshakeHost,
  REALTIME_ADMISSION_PATH,
  HANDSHAKE_TICKET_LIFETIME_MS,
  readRealtimeGrant,
  type RealtimeGrant,
} from './handshake-ticket.js';
import { isRecord, stringField, type Payload } from './payload.js';

/** What a socket carries from its handshake to its handlers. */
interface BridgeSocketData {
  /** The tenant whose rooms the socket is authorized for. */
  tenantSlug?: string;
  /** The tenant the socket's host resolved to, before it has authorized. */
  pendingTenantSlug?: string;
  /**
   * Whether the socket's credential may join the tenant-wide room. The room
   * carries live payloads of more than one category, so a guest link or
   * anonymous share that holds single categories stays out of it.
   */
  tenantRelay?: boolean;
  /**
   * The subject whose per-subject room the socket joined, carried from the API
   * admission via the handshake ticket. Absent for a credential that owns no
   * subject, which then receives no per-subject notifications.
   */
  subjectId?: string;
  /** The API broadcast categories whose storage events the socket receives. */
  categories?: string[];
  /** Whether an `/alarm` socket's credential may silence the tenant's alarms. */
  mayAck?: boolean;
  /** How the socket proves its admission again; absent until it is admitted. */
  readmission?: Readmission;
  /** The pending re-check of the socket's admission. */
  readmissionTimer?: ReturnType<typeof setTimeout>;
  /** Until when a browser that could not produce a fresh ticket keeps its admission. */
  ticketGraceUntil?: number;
}

/**
 * How an admitted socket proves its admission again, so a credential that is
 * revoked, expires or narrows stops reaching the socket's rooms. A browser holds
 * no credential the bridge can replay, so it is asked for a fresh ticket; every
 * other client's own credential is replayed against the API.
 */
type Readmission =
  | { kind: 'ticket' }
  | { kind: 'credential'; apiSecret?: string; token?: string }
  | { kind: 'storage'; accessToken: string; collections: string[]; unanswered: string[] }
  | { kind: 'alarm'; accessToken: string };

/**
 * How often a socket admitted on its own credential is re-checked, matching the
 * API's re-check of its hub connections. A ticket socket is re-checked when its
 * ticket expires instead.
 */
export const ADMISSION_RECHECK_INTERVAL_MS = 60_000;

/**
 * How soon a /storage socket's collections whose probe went unanswered are probed again. A
 * collection the API has not yet answered for is neither joined nor refused.
 */
export const UNANSWERED_COLLECTION_RETRY_MS = 10_000;

/** How long a browser has to answer a request for a fresh ticket. */
export const TICKET_REFRESH_TIMEOUT_MS = 10_000;

/** How soon a browser that could not produce a fresh ticket is asked again. */
export const TICKET_RETRY_MS = 30_000;

/** A probe the API did not answer: unreachable, timed out, or failing itself. */
const UNANSWERED = 'unanswered';
type Unanswered = typeof UNANSWERED;

/**
 * Whether a probe's status is the API's answer about the credential. A 5xx,
 * 408 or 429 says nothing about it, and re-checking keeps the socket's current
 * admission until the API answers: while it cannot, it broadcasts nothing.
 */
function isAnswer(status: number): boolean {
  return status < 500 && status !== 408 && status !== 429;
}

/** Room a socket joins for one subject's per-subject payloads within a tenant. */
function subjectRoom(tenantSlug: string, subjectId: string): string {
  return `tenant:${tenantSlug}:subject:${subjectId}`;
}

/** Room a socket joins for one API broadcast category's storage events within a tenant. */
function categoryRoom(tenantSlug: string, category: string): string {
  return `tenant:${tenantSlug}:category:${category}`;
}

/**
 * The categories a grant's socket receives storage events for. A storage event
 * carries whatever record was just written, backdated or not, so a
 * history-clamped credential receives none and catches up through its own
 * clamped reads.
 */
function storageCategories(grant: RealtimeGrant): string[] {
  return grant.historyClamped ? [] : grant.categories;
}

/** The default-namespace rooms a socket's admission entitles it to. */
function grantedRooms(data: BridgeSocketData): string[] {
  const { tenantSlug, tenantRelay, subjectId, categories = [] } = data;
  if (!tenantSlug || tenantRelay !== true) return [];
  return [
    `tenant:${tenantSlug}`,
    ...categories.map((category) => categoryRoom(tenantSlug, category)),
    ...(subjectId ? [subjectRoom(tenantSlug, subjectId)] : []),
  ];
}

type BridgeServer = SocketIOServerClass<DefaultEventsMap, DefaultEventsMap, DefaultEventsMap, BridgeSocketData>;
type BridgeNamespace = Namespace<DefaultEventsMap, DefaultEventsMap, DefaultEventsMap, BridgeSocketData>;
type BridgeSocket = Socket<DefaultEventsMap, DefaultEventsMap, DefaultEventsMap, BridgeSocketData>;

/**
 * The collection names the Nightscout v3 `/storage` namespace lets a client
 * subscribe to. Upstream spells the food collection `food`; AndroidAPS sends
 * `foods`, so both are accepted and each socket is answered in its own spelling.
 * Not all of them are broadcast by the API today (`settings` has no realtime
 * path) but the client is allowed to ask for any of them.
 */
const KNOWN_STORAGE_COLLECTIONS = [
  'devicestatus',
  'entries',
  'profile',
  'treatments',
  'food',
  'foods',
  'settings',
] as const;

type StorageCollection = (typeof KNOWN_STORAGE_COLLECTIONS)[number];

const isStorageCollection = (name: string): name is StorageCollection =>
  KNOWN_STORAGE_COLLECTIONS.some((known) => known === name);

/** Collections a subscribe without a `collections` list receives, as upstream names them. */
const DEFAULT_STORAGE_COLLECTIONS: StorageCollection[] = KNOWN_STORAGE_COLLECTIONS.filter(
  (name) => name !== 'foods',
);

/**
 * The API broadcasts storage events under its own collection names (singular
 * `food`, plural `profiles`). Each name maps to every spelling a v3 client may
 * have joined a room under; the first spelling is the family's identity, so two
 * spellings of one collection can be recognised as the same subscription.
 */
const COLLECTION_SPELLINGS: Record<string, string[]> = {
  food: ['food', 'foods'],
  foods: ['food', 'foods'],
  profiles: ['profile'],
  profile: ['profile'],
};

/** The client spellings a broadcast collection is delivered under. */
function clientCollectionNames(broadcastCollection: string): string[] {
  return COLLECTION_SPELLINGS[broadcastCollection] ?? [broadcastCollection];
}

/** Identity shared by every spelling of one collection. */
function collectionIdentity(collection: string): string {
  return clientCollectionNames(collection)[0];
}

/** Room a `/storage` socket joins for a tenant + collection (client spelling). */
function storageRoom(tenantSlug: string, clientCollection: string): string {
  return `storage:${tenantSlug}:${clientCollection}`;
}

/** Room a `/alarm` socket joins for a tenant. */
function alarmRoom(tenantSlug: string): string {
  return `alarm:${tenantSlug}`;
}

/**
 * Map each v3-client collection name to the API v3 endpoint path it reads from.
 * The API's controller names don't always match the client collection names
 * (`food` and `foods` → `/api/v3/food`), so this is used to probe per-collection
 * read authorization: a token that can read `/api/v3/entries` is authorized for
 * the `entries` room, but not necessarily for `treatments` or `settings`.
 */
const COLLECTION_READ_ENDPOINT: Record<string, string> = {
  entries: '/api/v3/entries',
  treatments: '/api/v3/treatments',
  devicestatus: '/api/v3/devicestatus',
  profile: '/api/v3/profile',
  food: '/api/v3/food',
  foods: '/api/v3/food',
  settings: '/api/v3/settings',
};

interface SocketIOConfig {
  cors?: {
    origin: string | string[];
    methods?: string[];
    credentials?: boolean;
  };
  transports?: ('websocket' | 'polling')[];
  pingTimeout?: number;
  pingInterval?: number;
}

/** Pick the host the connection arrived on: the proxy-forwarded host if present
 *  (X-Forwarded-Host), otherwise the Host header. Returns the first value when a
 *  header is repeated.
 *
 *  Trust model: X-Forwarded-Host is NOT sanitized at the edge — the YARP gateway
 *  forwards it as-is and nothing overwrites it, so a client can set it to any
 *  value. Safety does not depend on trusting this header. The chosen host is
 *  replayed to the API authorization probe together with the client's OWN cookie,
 *  and the API applies its per-tenant read policy: a spoofed host only re-points
 *  the probe at another tenant, and a private tenant still rejects a non-member
 *  cookie, so spoofing can expose only data that is already public. The API
 *  resolves tenants from the same header in TenantResolutionMiddleware, so the
 *  bridge adds no new trust assumption. */
export function pickHandshakeHost(
  headers: Record<string, string | string[] | undefined>,
): string | undefined {
  const value = headers['x-forwarded-host'] ?? headers['host'];
  const single = Array.isArray(value) ? value[0] : value;
  return single || undefined;
}

/** Socket.IO query values are `string | string[]`; take the first. */
function queryValue(value: string | string[] | undefined): string | undefined {
  const single = Array.isArray(value) ? value[0] : value;
  return single || undefined;
}

const APEX = Symbol('apex');

/** The subdomain slug a host names, `APEX` for the apex domain, or null for a foreign host. */
function hostTenant(host: string | undefined, baseDomain: string): string | typeof APEX | null {
  if (!host) return null;

  const hostname = host.split(':')[0];
  const baseDomainHost = baseDomain.split(':')[0];

  if (hostname.endsWith(`.${baseDomainHost}`)) {
    const slug = hostname.slice(0, -(baseDomainHost.length + 1));
    return slug || null;
  }

  return hostname === baseDomainHost ? APEX : null;
}

/** Resolve the tenant slug a host belongs to. A subdomain resolves to its slug;
 *  the apex domain resolves to `apexTenantSlug`, the tenant the API serves there
 *  (see `discoverApexTenant`). Returns null when the host is foreign or the apex
 *  serves no tenant. */
export function resolveTenantSlug(
  host: string | undefined,
  baseDomain: string,
  apexTenantSlug: string | null,
): string | null {
  const tenant = hostTenant(host, baseDomain);
  return tenant === APEX ? apexTenantSlug : tenant;
}

class SocketIOServer {
  private io: BridgeServer | null = null;
  private httpServer: HttpServer;
  private clients: Map<string, ClientInfo> = new Map();
  private config: SocketIOConfig;
  private baseDomain: string;
  private apexTenantSlug: string | null;
  private signingSecret: string;
  private apiBaseUrl: string;
  /** Nightscout v3 namespaces for uploaders (AAPS, xDrip+, ...). null until
   *  start() attaches them to the same httpServer as the default namespace. */
  private storageNsp: BridgeNamespace | null = null;
  private alarmNsp: BridgeNamespace | null = null;

  constructor(
    httpServer: HttpServer,
    config: SocketIOConfig = {},
    baseDomain: string,
    apexTenantSlug: string | null = null,
    signingSecret: string = '',
    apiBaseUrl: string = '',
  ) {
    this.httpServer = httpServer;
    this.baseDomain = baseDomain;
    this.apexTenantSlug = apexTenantSlug;
    this.signingSecret = signingSecret;
    this.apiBaseUrl = apiBaseUrl;
    this.config = {
      cors: config.cors ?? { origin: '*', methods: ['GET', 'POST'], credentials: true },
      transports: config.transports || ['websocket', 'polling'],
      pingTimeout: config.pingTimeout || 60000,
      pingInterval: config.pingInterval || 25000
    };
  }

  start(): Promise<void> {
    return new Promise((resolve, reject) => {
      try {
        // Create Socket.IO server attached to existing HTTP server
        this.io = new SocketIOServerClass<DefaultEventsMap, DefaultEventsMap, DefaultEventsMap, BridgeSocketData>(this.httpServer, {
          cors: this.config.cors,
          transports: this.config.transports,
          pingTimeout: this.config.pingTimeout,
          pingInterval: this.config.pingInterval,
          // Legacy Nightscout clients (LoopFollow's socket.io-client-swift) speak
          // Engine.IO v3 and are otherwise rejected with "Unsupported protocol
          // version" before any handler runs.
          allowEIO3: true
        });

        this.setupHandshakeAuth();
        this.setupEventHandlers();
        this.setupStorageNamespace();
        this.setupAlarmNamespace();

        logger.info('Socket.IO server attached to HTTP server');
        resolve();

      } catch (error) {
        logger.error('Failed to start Socket.IO server:', error);
        reject(error);
      }
    });
  }

  /** Authorize every handshake before it can join a tenant room. The tenant is
   *  resolved from the connection's Host, and the connection must present a valid
   *  handshake ticket (see handshake-ticket.ts) in its Socket.IO `auth` payload.
   *  The web app's `/realtime/ticket` endpoint mints it from the API's realtime
   *  admission for the connection, so verifying it here applies the API's
   *  policy without a per-connection API call. Unauthorized handshakes are
   *  rejected so the socket never receives broadcasts. */
  private setupHandshakeAuth(): void {
    if (!this.io) return;
    this.io.use((socket, next) => this.authorizeHandshake(socket, next));
  }

  /** Resolve the tenant for a handshake and authorize it from its ticket. Sets
   *  `socket.data.tenantSlug` for room assignment on success; calls `next` with
   *  an error to reject. Exposed for unit testing. */
  async authorizeHandshake(socket: BridgeSocket, next: (err?: Error) => void): Promise<void> {
    try {
      const host = pickHandshakeHost(socket.handshake.headers);
      const tenantSlug = await this.resolveTenant(host);
      if (!tenantSlug) {
        logger.warn(`Rejecting connection ${socket.id}: no resolvable tenant`);
        return next(new Error('tenant_unresolved'));
      }

      // Engine.IO v3 clients have no `auth` payload — that arrived with the v4
      // protocol — so also accept the ticket from the handshake query.
      const token =
        stringField(socket.handshake.auth, 'token')
        ?? queryValue(socket.handshake.query?.token);
      if (token) {
        // A ticket was offered, so this is the browser path: reject it outright if
        // it doesn't verify. Admitting it silently would leave a client whose
        // ticket merely expired connected but receiving nothing, instead of
        // getting connect_error and retrying with a fresh one.
        const ticket = verifyHandshakeTicket(this.signingSecret, token);
        if (!ticket) {
          logger.warn(`Rejecting connection ${socket.id}: invalid handshake ticket for tenant ${tenantSlug}`);
          return next(new Error('unauthorized'));
        }

        // Bind the ticket to the host the connection actually arrived on, so a
        // ticket minted for one tenant can't be replayed on another tenant's socket.
        if (ticket.h !== normalizeHandshakeHost(host!)) {
          logger.warn(`Rejecting connection ${socket.id}: handshake ticket host mismatch for tenant ${tenantSlug}`);
          return next(new Error('unauthorized'));
        }

        this.setGrant(socket, tenantSlug, ticket);
        socket.data.readmission = { kind: 'ticket' };
        this.scheduleReadmission(socket, ticket.exp - Date.now());
        return next();
      }

      // No ticket offered at all: admit the socket unauthorized so a legacy
      // Nightscout client can complete the classic `authorize` exchange, which it
      // sends only after connecting. The socket joins no room until that succeeds,
      // so it receives nothing in the meantime.
      logger.debug(`Connection ${socket.id} carries no ticket; awaiting classic authorize for tenant ${tenantSlug}`);
      socket.data.pendingTenantSlug = tenantSlug;
      next();
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      logger.error(`Rejecting connection ${socket.id}: authorization error: ${message}`);
      next(new Error('authorization_error'));
    }
  }

  private setupEventHandlers(): void {
    if (!this.io) return;

    this.io.on('connection', (socket: BridgeSocket) => {
      const clientId = socket.id;
      const clientInfo: ClientInfo = {
        id: clientId,
        connectedAt: new Date(),
        address: socket.handshake.address,
        userAgent: socket.handshake.headers['user-agent']
      };

      this.clients.set(clientId, clientInfo);
      logger.info(`Client connected: ${clientId} from ${clientInfo.address}`);
      logger.debug(`Total connected clients: ${this.clients.size}`);

      this.joinTenantRoom(socket);

      // Classic Nightscout authorization: legacy clients connect first and then
      // send their credentials in an `authorize` message.
      socket.on('authorize', (payload: unknown, callback?: (result: unknown) => void) => {
        void this.handleAuthorize(socket, payload, callback);
      });

      // Handle client disconnection
      socket.on('disconnect', (reason: string) => {
        clearTimeout(socket.data.readmissionTimer);
        this.clients.delete(clientId);
        logger.info(`Client disconnected: ${clientId}, reason: ${reason}`);
        logger.debug(`Total connected clients: ${this.clients.size}`);
      });

      // Send initial connection acknowledgment
      socket.emit('connect_ack', {
        clientId: clientId,
        serverTime: new Date().toISOString(),
        version: '1.0.0'
      });
    });
  }

  /** Join an authorized socket to its tenant room, the category rooms it was granted and, when its
   *  credential owns a subject, that subject's room. A restricted socket stays authorized and joins
   *  nothing, category rooms included, so a guest link or share receives no storage events. A socket
   *  with no subject receives no per-subject notifications. */
  private joinTenantRoom(socket: BridgeSocket): void {
    const { tenantSlug, tenantRelay } = socket.data;
    if (!tenantSlug) return;

    if (tenantRelay !== true) {
      logger.info(`Client ${socket.id} is restricted; not joining tenant room: ${tenantSlug}`);
      return;
    }

    for (const room of grantedRooms(socket.data)) {
      socket.join(room);
    }
    logger.info(`Client ${socket.id} joined tenant room: ${tenantSlug}`);
  }

  /** Records an admission on the socket, for the rooms it joins. */
  private setGrant(socket: BridgeSocket, tenantSlug: string, grant: RealtimeGrant): void {
    this.onTenantAdmitted(tenantSlug);
    socket.data.tenantSlug = tenantSlug;
    socket.data.pendingTenantSlug = undefined;
    socket.data.tenantRelay = grant.tenantRelay;
    socket.data.subjectId = grant.subjectId;
    socket.data.categories = storageCategories(grant);
  }

  /** Moves an admitted socket onto the rooms of a fresh admission, leaving any it no longer holds. */
  private regrant(socket: BridgeSocket, tenantSlug: string, grant: RealtimeGrant): void {
    const before = grantedRooms(socket.data);
    this.setGrant(socket, tenantSlug, grant);
    const after = new Set(grantedRooms(socket.data));
    for (const room of before) {
      if (!after.has(room)) socket.leave(room);
    }
    for (const room of after) {
      socket.join(room);
    }
  }

  private scheduleReadmission(socket: BridgeSocket, delayMs: number): void {
    clearTimeout(socket.data.readmissionTimer);
    const timer = setTimeout(() => void this.readmit(socket), Math.max(delayMs, 0));
    timer.unref();
    socket.data.readmissionTimer = timer;
  }

  /** Proves an admitted socket's admission again, narrows its rooms to what
   *  the fresh admission grants, and closes it when nothing is granted; then
   *  schedules the next re-check. Exposed for unit testing. */
  async readmit(socket: BridgeSocket): Promise<void> {
    const { readmission, tenantSlug } = socket.data;
    if (!socket.connected || !readmission || !tenantSlug) return;

    let nextMs: number | null;
    try {
      nextMs = await this.readmitOnce(socket, tenantSlug, readmission);
    } catch (error) {
      const reason = error instanceof Error ? error.message : String(error);
      logger.warn(`Re-checking ${socket.id} failed: ${reason}`);
      nextMs = ADMISSION_RECHECK_INTERVAL_MS;
    }
    if (nextMs !== null && socket.connected) this.scheduleReadmission(socket, nextMs);
  }

  /** One re-check; resolves to the delay before the next, or null once the socket is closed. */
  private async readmitOnce(
    socket: BridgeSocket,
    tenantSlug: string,
    readmission: Readmission,
  ): Promise<number | null> {
    switch (readmission.kind) {
      case 'ticket': {
        const answer = await this.requestFreshTicket(socket);
        if (answer === 'retry') {
          // A browser that could not reach its ticket endpoint, or a bundle that
          // predates the request and never answers, keeps its admission for one
          // more ticket lifetime: a transient outage does not drop every
          // dashboard, and a socket's admission is still never older than two
          // ticket lifetimes.
          socket.data.ticketGraceUntil ??= Date.now() + HANDSHAKE_TICKET_LIFETIME_MS;
          if (Date.now() < socket.data.ticketGraceUntil) return TICKET_RETRY_MS;
        }
        if (answer === 'retry' || answer === 'refused') {
          // Closing the transport sends the browser back through its handshake,
          // whose ticket fetch asks the API afresh and ends a denied session.
          logger.info(`Client ${socket.id} gave no fresh ticket; closing its transport`);
          socket.conn.close();
          return null;
        }
        socket.data.ticketGraceUntil = undefined;
        this.regrant(socket, tenantSlug, answer);
        return answer.exp - Date.now();
      }
      case 'credential': {
        const admission = await this.probeRealtimeAdmission(tenantSlug, readmission);
        if (admission === UNANSWERED) return ADMISSION_RECHECK_INTERVAL_MS;
        if (admission === null) {
          logger.warn(`Client ${socket.id} lost its admission for tenant ${tenantSlug}; disconnecting`);
          socket.disconnect(true);
          return null;
        }
        this.regrant(socket, tenantSlug, admission);
        return ADMISSION_RECHECK_INTERVAL_MS;
      }
      case 'storage': {
        const [joined, waiting] = await Promise.all([
          this.probeCollections(readmission.accessToken, tenantSlug, readmission.collections),
          this.probeCollections(readmission.accessToken, tenantSlug, readmission.unanswered),
        ]);
        for (const collection of joined.refused) socket.leave(storageRoom(tenantSlug, collection));
        for (const collection of waiting.granted) socket.join(storageRoom(tenantSlug, collection));
        if (waiting.granted.length > 0) this.onTenantAdmitted(tenantSlug);

        const collections = readmission.collections.filter((c) => !joined.refused.includes(c))
          .concat(waiting.granted);
        const unanswered = waiting.unanswered;
        if (collections.length === 0 && unanswered.length === 0) {
          logger.warn(`/storage client ${socket.id} lost every collection for tenant ${tenantSlug}; disconnecting`);
          socket.disconnect(true);
          return null;
        }
        socket.data.readmission = { ...readmission, collections, unanswered };
        return unanswered.length > 0 ? UNANSWERED_COLLECTION_RETRY_MS : ADMISSION_RECHECK_INTERVAL_MS;
      }
      case 'alarm': {
        const admission = await this.probeRealtimeAdmission(tenantSlug, readmission);
        if (admission === UNANSWERED) return ADMISSION_RECHECK_INTERVAL_MS;
        if (admission?.tenantRelay !== true) {
          logger.warn(`/alarm client ${socket.id} lost its admission for tenant ${tenantSlug}; disconnecting`);
          socket.disconnect(true);
          return null;
        }
        socket.data.mayAck = admission.mayAck;
        return ADMISSION_RECHECK_INTERVAL_MS;
      }
    }
  }

  /** Asks a browser socket for a fresh handshake ticket. Resolves to the
   *  ticket once it verifies for the host the socket arrived on; `refused` for
   *  a denial or a ticket that does not verify; `retry` when the browser gave no
   *  ticket and no denial, or no answer at all. */
  private async requestFreshTicket(socket: BridgeSocket) {
    let reply: unknown;
    try {
      reply = await socket.timeout(TICKET_REFRESH_TIMEOUT_MS).emitWithAck('realtime_ticket');
    } catch {
      return 'retry';
    }
    const token = stringField(reply, 'token');
    if (!token) return isRecord(reply) && reply.denied === true ? 'refused' : 'retry';

    const host = pickHandshakeHost(socket.handshake.headers);
    const ticket = verifyHandshakeTicket(this.signingSecret, token);
    return ticket && host && ticket.h === normalizeHandshakeHost(host) ? ticket : 'refused';
  }

  /** Handle the classic Nightscout `authorize` message.
   *
   *  Legacy clients (LoopFollow, Nightscout watchfaces) don't have — and can't
   *  obtain — a handshake ticket, because /realtime/ticket mints one only for a
   *  browser session the API admits to realtime. They
   *  authenticate the way they do against classic Nightscout instead: an API
   *  secret (already SHA-1 hashed by the client) and/or a subject token.
   *
   *  Rather than interpret those credentials here, replay them against the same
   *  admission the ticket endpoint asks for. The API applies its own per-tenant
   *  policy and decides whether the credential may join the tenant room.
   *
   *  The probe carries the client's credential and nothing else — never the
   *  bridge's instance key, which would authenticate any anonymous caller as a
   *  service and hand them another tenant's data. */
  async handleAuthorize(
    socket: BridgeSocket,
    payload: unknown,
    callback?: (result: unknown) => void,
  ): Promise<void> {
    const deny = (reason: string) => {
      logger.warn(`Authorize failed for ${socket.id}: ${reason}`);
      callback?.({ read: false, write: false, write_treatment: false });
      socket.disconnect(true);
    };

    // Already authorized by a valid handshake ticket — nothing more to do.
    if (socket.data.tenantSlug) {
      callback?.({ read: true, write: false, write_treatment: false });
      return;
    }

    const tenantSlug = socket.data.pendingTenantSlug;
    if (!tenantSlug) return deny('no resolvable tenant');

    if (!this.apiBaseUrl) return deny('bridge has no API base URL configured');

    const secret = stringField(payload, 'secret');
    const token = stringField(payload, 'token');
    if (!secret && !token) return deny('no credentials supplied');

    const admission = await this.probeRealtimeAdmission(tenantSlug, { apiSecret: secret, token });
    if (admission === null || admission === UNANSWERED) return deny('API denied the credential');

    this.setGrant(socket, tenantSlug, admission);
    this.joinTenantRoom(socket);
    socket.data.readmission = { kind: 'credential', apiSecret: secret, token };
    this.scheduleReadmission(socket, ADMISSION_RECHECK_INTERVAL_MS);
    logger.info(`Client ${socket.id} authorized via legacy credentials for tenant: ${tenantSlug}`);
    callback?.({ read: true, write: false, write_treatment: false });
  }

  /** Replays a client credential against the API's realtime admission endpoint,
   *  scoped to the connection's own tenant, and returns its grant, null when
   *  the API refused the credential, or `unanswered` when it gave no answer.
   *  The probe carries the client's credential and nothing else: the bridge's
   *  instance key would authenticate any anonymous caller as a service. */
  private async probeRealtimeAdmission(
    tenantSlug: string,
    credential: { apiSecret?: string; token?: string; accessToken?: string },
  ): Promise<RealtimeGrant | null | Unanswered> {
    if (!this.apiBaseUrl) {
      logger.warn('Realtime admission probe has no API base URL configured');
      return null;
    }

    try {
      const url = new URL(`${this.apiBaseUrl}${REALTIME_ADMISSION_PATH}`);
      if (credential.token) url.searchParams.set('token', credential.token);

      const headers: Record<string, string> = {
        'X-Forwarded-Host': `${tenantSlug}.${this.baseDomain}`,
        // The API's response cache runs before auth and keys on the internal
        // Host, so a cached authenticated 200 could otherwise authorize an
        // unauthenticated probe.
        'Cache-Control': 'no-cache, no-store',
      };
      if (credential.apiSecret) headers['api-secret'] = credential.apiSecret;
      if (credential.accessToken) headers['Authorization'] = `Bearer ${credential.accessToken}`;

      const probe = await fetch(url, {
        method: 'GET',
        headers,
        signal: AbortSignal.timeout(5000),
      });

      if (!probe.ok) return isAnswer(probe.status) ? null : UNANSWERED;
      return readRealtimeGrant(await probe.json());
    } catch (error) {
      const reason = error instanceof Error ? error.message : String(error);
      logger.warn(`Realtime admission probe failed: ${reason}`);
      return UNANSWERED;
    }
  }

  /** Return the Socket.IO emit target for a tenant room.
   *
   *  Always scoped: an unscoped broadcast would also reach sockets that are
   *  connected but not yet authorized (legacy clients mid-`authorize`), which
   *  would leak one tenant's data to them. */
  private emitTarget(tenantSlug?: string) {
    if (!this.io) return null;
    if (!tenantSlug) {
      logger.warn('Refusing to broadcast without a tenant slug');
      return null;
    }
    return this.io.to(`tenant:${tenantSlug}`);
  }

  // Methods to broadcast messages to clients
  broadcastDataUpdate(data: unknown, tenantSlug?: string): void {
    const target = this.emitTarget(tenantSlug);
    if (!target) return;

    logger.debug(`Broadcasting dataUpdate${tenantSlug ? ` to tenant ${tenantSlug}` : ''}`);
    target.emit('dataUpdate', data);
  }

  broadcastTrackerUpdate(data: unknown, tenantSlug?: string): void {
    const target = this.emitTarget(tenantSlug);
    if (!target) return;

    logger.debug(`Broadcasting trackerUpdate${tenantSlug ? ` to tenant ${tenantSlug}` : ''}`);
    target.emit('trackerUpdate', data);
  }

  broadcastAnnouncement(message: unknown, tenantSlug?: string): void {
    this.broadcastToBothNamespaces('announcement', message, tenantSlug);
  }

  broadcastAlarm(event: AlarmEvent, alarm: AlarmData, tenantSlug?: string): void {
    this.broadcastToBothNamespaces(event, alarm, tenantSlug);
  }

  broadcastClearAlarm(clear: unknown, tenantSlug?: string): void {
    this.broadcastToBothNamespaces('clear_alarm', clear, tenantSlug);
  }

  broadcastNotification(notification: unknown, tenantSlug?: string): void {
    this.broadcastToBothNamespaces('notification', notification, tenantSlug);
  }

  /** Emit an event to a tenant's `/alarm` subscribers alone, as the API's
   *  engine alarms go out. */
  broadcastToAlarmNamespace(event: AlarmEvent | 'clear_alarm', payload: unknown, tenantSlug?: string): void {
    if (!this.alarmNsp) return;
    if (!tenantSlug) {
      logger.warn('Refusing to broadcast without a tenant slug');
      return;
    }

    logger.debug(`Broadcasting ${event} to /alarm for tenant ${tenantSlug}`);
    this.alarmNsp.to(alarmRoom(tenantSlug)).emit(event, payload);
  }

  private broadcastToBothNamespaces(event: string, payload: unknown, tenantSlug?: string): void {
    const target = this.emitTarget(tenantSlug);
    if (!target || !tenantSlug) return;

    logger.debug(`Broadcasting ${event} to tenant ${tenantSlug}`);
    target.emit(event, payload);
    this.alarmNsp?.to(alarmRoom(tenantSlug)).emit(event, payload);
  }

  broadcastStatusUpdate(status: unknown, tenantSlug?: string): void {
    const target = this.emitTarget(tenantSlug);
    if (!target) return;

    logger.debug(`Broadcasting status update${tenantSlug ? ` to tenant ${tenantSlug}` : ''}`);
    target.emit('status', status);
  }

  /** Emit a storage event to the sockets granted its category, read from `colName`. An event naming
   *  no category reaches no default-namespace socket: the tenant room holds members whose scopes
   *  do not cover every category. */
  broadcastStorageEvent(eventType: 'create' | 'update' | 'delete', data: Payload, tenantSlug?: string): void {
    if (!this.io) return;
    if (!tenantSlug) {
      logger.warn('Refusing to broadcast without a tenant slug');
      return;
    }

    const clientCount = this.clients.size;
    logger.debug(`Broadcasting storage ${eventType} event to ${clientCount} connected clients (tenant: ${tenantSlug})`);

    if (clientCount === 0) {
      // This is normal while the service starts or a connector catches up
      // before anyone opens the UI. Keep it at debug level so a backfill does
      // not turn hundreds of harmless undeliverable events into a log storm.
      logger.debug('No Socket.IO clients connected - event will not be delivered to frontend');
    }

    const broadcastCollection = stringField(data, 'colName') ?? stringField(data, 'collection');
    if (!broadcastCollection) return;

    this.io.to(categoryRoom(tenantSlug, broadcastCollection)).emit(eventType, data);

    // Fan out to v3 `/storage` namespace clients (AAPS, xDrip+, ...). Each
    // spelling of the collection has its own room and its own `colName`, since
    // AAPS routes the event by reading `colName` from the payload. A socket
    // joins one spelling only, so it never receives the event twice.
    if (this.storageNsp) {
      for (const clientCollection of clientCollectionNames(broadcastCollection)) {
        this.storageNsp
          .to(storageRoom(tenantSlug, clientCollection))
          .emit(eventType, { ...data, colName: clientCollection });
      }
    }
  }

  /** Emit an in-app notification only to the room of the subject it belongs to. A relayed event
   *  that carries no recipient is dropped, never falling back to the tenant room: the tenant room
   *  holds every member, so a fallback would deliver one member's notification to all of them. */
  broadcastInAppNotification(
    eventType: 'notificationCreated' | 'notificationArchived' | 'notificationUpdated',
    data: unknown,
    tenantSlug?: string,
    subjectId?: string,
  ): void {
    if (!this.io) return;
    if (!tenantSlug) {
      logger.warn('Refusing to broadcast without a tenant slug');
      return;
    }
    if (!subjectId) {
      logger.debug(`Not broadcasting ${eventType}: no subject id for tenant ${tenantSlug}`);
      return;
    }

    const room = subjectRoom(tenantSlug, subjectId);
    logger.debug(`Broadcasting ${eventType} to ${room}`);
    this.io.to(room).emit(eventType, data);
  }

  broadcastSyncProgress(data: unknown, tenantSlug?: string): void {
    const target = this.emitTarget(tenantSlug);
    if (!target) return;
    logger.debug(`Broadcasting syncProgress${tenantSlug ? ` to tenant ${tenantSlug}` : ''}`);
    target.emit('syncProgress', data);
  }

  broadcastConfigChanged(data: unknown, tenantSlug?: string): void {
    const target = this.emitTarget(tenantSlug);
    if (!target) return;
    logger.debug(`Broadcasting configChanged${tenantSlug ? ` to tenant ${tenantSlug}` : ''}`);
    target.emit('configChanged', data);
  }

  // Send message to specific room
  sendToRoom(room: string, event: string, data: unknown): void {
    if (!this.io) return;

    logger.debug(`Sending ${event} to room: ${room}`);
    this.io.to(room).emit(event, data);
  }

  // Get server statistics
  getStats(): ServerStats {
    return {
      connectedClients: this.clients.size,
      clients: Array.from(this.clients.values()),
      uptime: process.uptime()
    };
  }

  setApexTenantSlug(slug: string | null): void {
    this.apexTenantSlug = slug;
  }

  /** Called with the tenant of every socket the API admits, so the bridge can open a relay for a
   *  tenant created since it last read the tenant list. */
  onTenantAdmitted: (tenantSlug: string) => void = () => {};

  /** Awaited when a connection arrives on the apex while the apex serves no tenant, before it is
   *  refused: an install's first tenant is created after the bridge starts. */
  onApexUnresolved: () => Promise<void> = async () => {};

  private async resolveTenant(host: string | undefined): Promise<string | null> {
    const tenant = hostTenant(host, this.baseDomain);
    if (tenant !== APEX) return tenant;
    if (this.apexTenantSlug === null) await this.onApexUnresolved();
    return this.apexTenantSlug;
  }

  /** Takes every socket bound to a tenant out of its rooms at once, then closes its transport, in
   *  every namespace. A transport may finish closing only once its buffer drains. A client reconnects
   *  from a closed transport and goes through its handshake again, where a browser fetches a fresh
   *  ticket; a server-side disconnect would leave it closed for good. */
  disconnectTenant(tenantSlug: string): void {
    for (const nsp of [this.io?.of('/'), this.storageNsp, this.alarmNsp]) {
      for (const socket of nsp?.sockets.values() ?? []) {
        if (socket.data.tenantSlug !== tenantSlug && socket.data.pendingTenantSlug !== tenantSlug) continue;
        for (const room of [...socket.rooms]) {
          if (room !== socket.id) socket.leave(room);
        }
        socket.conn.close();
      }
    }
  }

  getIO(): BridgeServer | null {
    return this.io;
  }

  /**
   * Resolve the tenant for a v3-namespace connection (AAPS, xDrip+, ...) from
   * the handshake Host. Unlike browser connections on the default namespace,
   * these clients carry no signed handshake ticket — they authenticate in-band
   * via the `subscribe` event — so the handshake only resolves the tenant and
   * admits the socket unauthorized, exactly like the legacy `authorize` path.
   * Returns the slug, or null when the host resolves to no tenant. Exposed for
   * unit testing.
   */
  resolveNamespaceTenant(socket: BridgeSocket): Promise<string | null> {
    return this.resolveTenant(pickHandshakeHost(socket.handshake.headers));
  }

  /**
   * Replay an AAPS access token against the API to authorize a v3-namespace
   * `subscribe`. This mirrors the legacy `handleAuthorize` probe (#547): the
   * token is sent to the API scoped to the connection's own tenant via
   * X-Forwarded-Host, and the API applies its per-tenant, per-collection read
   * policy (each v3 endpoint carries its own `RequireScope`). The probe carries
   * the client's credential and nothing else — never the bridge's instance key,
   * which would authenticate any anonymous caller as a service.
   *
   * Returns true when the API accepts the token for that tenant + endpoint,
   * false when it refuses it, and `unanswered` when it gave no answer.
   */
  async probeAccessToken(
    accessToken: string,
    tenantSlug: string,
    endpointPath: string,
  ): Promise<boolean | Unanswered> {
    if (!this.apiBaseUrl) {
      logger.warn('v3 namespace auth probe has no API base URL configured');
      return false;
    }
    try {
      const url = new URL(`${this.apiBaseUrl}${endpointPath}`);
      // v3 reads any parameter but its own (`limit`, `sort`, ...) as a field filter, which the
      // API matches over up to the tenant's whole history.
      url.searchParams.set('limit', '1');
      const probe = await fetch(url, {
        method: 'GET',
        headers: {
          Authorization: `Bearer ${accessToken}`,
          'X-Forwarded-Host': `${tenantSlug}.${this.baseDomain}`,
          // The API's response cache runs before auth and keys on the internal
          // Host, so a cached authenticated 200 could authorize an
          // unauthenticated probe.
          'Cache-Control': 'no-cache, no-store',
        },
        signal: AbortSignal.timeout(5000),
      });
      return probe.ok || (isAnswer(probe.status) ? false : UNANSWERED);
    } catch (error) {
      const reason = error instanceof Error ? error.message : String(error);
      logger.warn(`v3 namespace auth probe failed: ${reason}`);
      return UNANSWERED;
    }
  }

  /** Probes a token's read access to each collection at once, each against its own v3 read
   *  endpoint, so access is per collection as the API's per-endpoint scopes are. */
  private async probeCollections(
    accessToken: string,
    tenantSlug: string,
    collections: readonly string[],
  ): Promise<{ granted: string[]; refused: string[]; unanswered: string[] }> {
    const answers = await Promise.all(collections.map((collection) =>
      this.probeAccessToken(accessToken, tenantSlug, COLLECTION_READ_ENDPOINT[collection])));
    return {
      granted: collections.filter((_, i) => answers[i] === true),
      refused: collections.filter((_, i) => answers[i] === false),
      unanswered: collections.filter((_, i) => answers[i] === UNANSWERED),
    };
  }

  /**
   * Nightscout v3 `/storage` namespace. AAPS connects to `<url>/storage` and,
   * on connect, emits `subscribe` with its access token and the collections it
   * wants. On a successful auth the socket joins a per-collection room and
   * subsequently receives `create` / `update` / `delete` events fanned out by
   * {@link broadcastStorageEvent}.
   *
   * Auth is in-band (the `subscribe` event), not at the handshake, because
   * AAPS sends the token as a JSON field — there is no signed ticket path for
   * native uploaders.
   */
  private setupStorageNamespace(): void {
    if (!this.io) return;
    this.storageNsp = this.io.of('/storage');

    // Admit the socket unauthorized; the tenant is resolved for room routing
    // but no room is joined until `subscribe` succeeds.
    this.storageNsp.use(async (socket, next) => {
      const tenantSlug = await this.resolveNamespaceTenant(socket);
      if (!tenantSlug) {
        logger.warn(`Rejecting /storage connection ${socket.id}: no resolvable tenant`);
        return next(new Error('tenant_unresolved'));
      }
      socket.data.pendingTenantSlug = tenantSlug;
      next();
    });

    this.storageNsp.on('connection', (socket: BridgeSocket) => {
      logger.info(`v3 /storage client connected: ${socket.id}`);

      socket.on('subscribe', async (payload: unknown, ack?: (result: unknown) => void) => {
        await this.handleStorageSubscribe(socket, payload, ack);
      });

      socket.on('disconnect', (reason: string) => {
        clearTimeout(socket.data.readmissionTimer);
        logger.info(`v3 /storage client disconnected: ${socket.id}, reason: ${reason}`);
      });
    });
  }

  /**
   * Handle the v3 `/storage` `subscribe` event. Exposed for unit testing.
   *
   * Each requested collection is probed independently against its own v3 read
   * endpoint, so a token with read access to `entries` but not `treatments`
   * only joins the `entries` room. A token denied for every collection is
   * rejected outright — and the socket is disconnected so the client has to
   * re-establish the connection (going back through TLS/transport) rather than
   * immediately retrying `subscribe` as a credential-guessing oracle.
   *
   * A collection the API gave no answer for (timeout, 5xx, unreachable) is
   * neither joined nor refused: re-admission probes it again and joins it once
   * the API grants it, so an outage never reads as a refusal.
   */
  async handleStorageSubscribe(
    socket: BridgeSocket,
    payload: unknown,
    ack?: (result: unknown) => void,
  ): Promise<void> {
    const tenantSlug = socket.data.pendingTenantSlug;
    if (!tenantSlug) {
      ack?.({ success: false, message: 'no resolvable tenant' });
      socket.disconnect(true);
      return;
    }

    const accessToken = stringField(payload, 'accessToken');
    if (!accessToken) {
      ack?.({ success: false, message: 'Missing or bad accessToken' });
      socket.disconnect(true);
      return;
    }

    // Normalize the requested collections to the known v3 set, preserving order,
    // dropping anything unrecognized and keeping the first spelling of a
    // collection requested under two.
    const collections = isRecord(payload) ? payload.collections : undefined;
    const requested = Array.isArray(collections)
      ? collections.filter((c): c is string => typeof c === 'string')
      : [];
    const candidateCollections: StorageCollection[] = requested.length > 0
      ? requested
          .filter(isStorageCollection)
          .filter((name, i, all) => all.findIndex((other) => collectionIdentity(other) === collectionIdentity(name)) === i)
      : DEFAULT_STORAGE_COLLECTIONS;

    const { granted: authorized, unanswered } = await this.probeCollections(
      accessToken, tenantSlug, candidateCollections);

    if (authorized.length === 0 && unanswered.length === 0) {
      logger.warn(
        `/storage subscribe denied for ${socket.id} (tenant ${tenantSlug}): token not authorized for any collection`,
      );
      ack?.({ success: false, message: 'Unauthorized to receive any collection' });
      socket.disconnect(true);
      return;
    }

    for (const collection of authorized) {
      socket.join(storageRoom(tenantSlug, collection));
    }
    socket.data.tenantSlug = tenantSlug;
    socket.data.pendingTenantSlug = undefined;
    if (authorized.length > 0) this.onTenantAdmitted(tenantSlug);
    socket.data.readmission = { kind: 'storage', accessToken, collections: authorized, unanswered };
    this.scheduleReadmission(
      socket, unanswered.length > 0 ? UNANSWERED_COLLECTION_RETRY_MS : ADMISSION_RECHECK_INTERVAL_MS);
    logger.info(
      `/storage client ${socket.id} subscribed for tenant ${tenantSlug}: ${authorized.join(', ')}`
        + (unanswered.length > 0 ? ` (awaiting the API for ${unanswered.join(', ')})` : ''),
    );
    ack?.({ success: true, collections: authorized });
  }

  /**
   * Nightscout v3 `/alarm` namespace. AAPS connects to `<url>/alarm` and emits
   * `subscribe` with its access token; thereafter it receives `alarm`,
   * `urgent_alarm`, `announcement`, and `clear_alarm` events. It may also emit a
   * positional `ack` (level, group, silenceTime) to silence an alarm.
   *
   * `onAlarmAck` is invoked when a client acks an alarm; it defaults to a no-op
   * log because forwarding to the API's AlarmHub requires the per-tenant
   * SignalR client, which the server doesn't hold a reference to. Callers (the
   * bridge setup) can override it to wire up the forward.
   */
  onAlarmAck: (tenantSlug: string, level: number, group: string, silenceTime: number) => void =
    (tenantSlug, level, group, silenceTime) => {
      logger.info(
        `/alarm ack received (tenant ${tenantSlug}): level=${level} group=${group} silence=${silenceTime}ms (not forwarded)`,
      );
    };

  /**
   * The tenant's alarm still sounding, as the event and payload an `/alarm`
   * subscriber hears, or null. The bridge setup wires it to the tenant's
   * SignalR client.
   */
  currentAlarm: (tenantSlug: string) => Promise<{ event: AlarmEvent; alarm: unknown } | null> =
    async () => null;

  private setupAlarmNamespace(): void {
    if (!this.io) return;
    this.alarmNsp = this.io.of('/alarm');

    this.alarmNsp.use(async (socket, next) => {
      const tenantSlug = await this.resolveNamespaceTenant(socket);
      if (!tenantSlug) {
        logger.warn(`Rejecting /alarm connection ${socket.id}: no resolvable tenant`);
        return next(new Error('tenant_unresolved'));
      }
      socket.data.pendingTenantSlug = tenantSlug;
      next();
    });

    this.alarmNsp.on('connection', (socket: BridgeSocket) => {
      logger.info(`v3 /alarm client connected: ${socket.id}`);

      socket.on('subscribe', async (payload: unknown, ack?: (result: unknown) => void) => {
        await this.handleAlarmSubscribe(socket, payload, ack);
      });

      // Positional ack: AAPS emits ("ack", level, group, silenceTime) — three
      // separate arguments, not a JSON object (matches cgm-remote-monitor).
      socket.on('ack', (level: unknown, group: unknown, silenceTime: unknown) => {
        this.handleAlarmAck(socket, level, group, silenceTime);
      });

      socket.on('disconnect', (reason: string) => {
        clearTimeout(socket.data.readmissionTimer);
        logger.info(`v3 /alarm client disconnected: ${socket.id}, reason: ${reason}`);
      });
    });
  }

  /**
   * Handle a v3 `/alarm` `ack`. The bridge forwards it on its own AlarmHub
   * connection, which the API always lets ack, so the subscriber's own right to
   * silence the tenant's alarms is checked here. Exposed for unit testing.
   */
  handleAlarmAck(socket: BridgeSocket, level: unknown, group: unknown, silenceTime: unknown): void {
    const tenantSlug = socket.data.tenantSlug;
    if (!tenantSlug) return;
    if (socket.data.mayAck !== true) {
      logger.warn(`/alarm ack from ${socket.id} (tenant ${tenantSlug}) dropped: its credential may not silence alarms`);
      return;
    }
    this.onAlarmAck(
      tenantSlug,
      typeof level === 'number' ? level : Number(level) || 0,
      typeof group === 'string' ? group : String(group ?? ''),
      typeof silenceTime === 'number' ? silenceTime : Number(silenceTime) || 0,
    );
  }

  /**
   * Handle the v3 `/alarm` `subscribe` event. Exposed for unit testing.
   *
   * On failure the socket is disconnected so the client has to re-establish the
   * connection rather than immediately retrying `subscribe` as a
   * credential-guessing oracle.
   */
  async handleAlarmSubscribe(
    socket: BridgeSocket,
    payload: unknown,
    ack?: (result: unknown) => void,
  ): Promise<void> {
    const tenantSlug = socket.data.pendingTenantSlug;
    if (!tenantSlug) {
      ack?.({ success: false, message: 'no resolvable tenant' });
      socket.disconnect(true);
      return;
    }

    const accessToken = stringField(payload, 'accessToken');
    if (!accessToken) {
      ack?.({ success: false, message: 'Missing or bad accessToken' });
      socket.disconnect(true);
      return;
    }

    // The alarm room carries every category and every member's alert state, so
    // the subscriber must be a credential the API admits to the tenant relay,
    // not merely one that can read entries.
    const admitted = await this.probeRealtimeAdmission(tenantSlug, { accessToken });
    if (admitted === UNANSWERED || admitted?.tenantRelay !== true) {
      logger.warn(`/alarm subscribe denied for ${socket.id} (tenant ${tenantSlug})`);
      ack?.({ success: false, message: 'Missing or bad accessToken' });
      socket.disconnect(true);
      return;
    }

    socket.join(alarmRoom(tenantSlug));
    socket.data.tenantSlug = tenantSlug;
    socket.data.pendingTenantSlug = undefined;
    socket.data.mayAck = admitted.mayAck;
    this.onTenantAdmitted(tenantSlug);
    socket.data.readmission = { kind: 'alarm', accessToken };
    this.scheduleReadmission(socket, ADMISSION_RECHECK_INTERVAL_MS);
    logger.info(`/alarm client ${socket.id} subscribed for tenant ${tenantSlug}`);
    ack?.({ success: true, message: 'Subscribed for alarms' });
    await this.replayCurrentAlarm(socket, tenantSlug);
  }

  /** The API raises an alarm once, so a socket that subscribes after it was
   *  raised hears it only from this replay. */
  private async replayCurrentAlarm(socket: BridgeSocket, tenantSlug: string): Promise<void> {
    try {
      const current = await this.currentAlarm(tenantSlug);
      if (current) socket.emit(current.event, current.alarm);
    } catch (error) {
      logger.error(`Failed to replay the sounding alarm to /alarm client ${socket.id}:`, error);
    }
  }

  async stop(): Promise<void> {
    if (this.io) {
      await this.io.close();
      logger.info('Socket.IO server stopped');
    }
  }
}

export default SocketIOServer;
