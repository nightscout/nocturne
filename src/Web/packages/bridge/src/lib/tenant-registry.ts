import logger from './logger.js';

/** An active tenant as the API lists it. The id tells a recreated slug from the tenant it replaced. */
export interface DiscoveredTenant {
  id: string;
  slug: string;
}

/** One answer from the API: its active tenants, and the one it serves on the apex domain. */
export interface TenantDirectory {
  tenants: DiscoveredTenant[];
  apexSlug: string | null;
}

/** A tenant's relay from the API's hubs. */
export interface TenantConnection {
  connect(): Promise<void>;
  disconnect(): Promise<void>;
}

export interface TenantRegistryOptions<C extends TenantConnection> {
  discover: () => Promise<TenantDirectory>;
  /** Opens a relay for the tenant `id` reached at `slug`, pinned to that id (see TenantAwareHub.ExpectedTenantHeader in the API). */
  createConnection: (slug: string, id: string) => C;
  /** The least time between the starts of two discoveries, which bounds what a burst of misses costs the API. */
  refreshIntervalMs: number;
  /** Called once a slug's tenant is gone, deactivated or replaced, before any new connection for it opens. */
  onTenantRemoved?: (slug: string) => void;
  onApexChanged?: (slug: string | null) => void;
}

/**
 * The bridge's hub connections, one per active tenant, kept in step with the API's tenant list.
 * The list is read again whenever the bridge meets a tenant it holds no connection for and when a
 * connection is lost, so a tenant created, deleted or deactivated while the bridge runs is picked up
 * then rather than at the next restart.
 */
export default class TenantRegistry<C extends TenantConnection> {
  private readonly options: TenantRegistryOptions<C>;
  private readonly bySlug = new Map<string, { id: string; connection: C }>();
  private apex: string | null = null;
  private running: Promise<boolean> | null = null;
  private queued: Promise<boolean> | null = null;
  private lastStartedAt = Number.NEGATIVE_INFINITY;
  private stopped = false;
  private wait: { timer: ReturnType<typeof setTimeout>; resolve: () => void } | null = null;

  constructor(options: TenantRegistryOptions<C>) {
    this.options = options;
  }

  get apexSlug(): string | null {
    return this.apex;
  }

  get(slug: string): C | undefined {
    return this.bySlug.get(slug)?.connection;
  }

  connections(): C[] {
    return [...this.bySlug.values()].map((held) => held.connection);
  }

  /** Reads the tenant list again when `slug` has no connection. */
  ensure(slug: string): void {
    if (!this.bySlug.has(slug)) void this.refresh();
  }

  /** Whether `slug` names the tenant `id`, reading the tenant list again first when it names no tenant or another. */
  async holds(slug: string, id: string): Promise<boolean> {
    if (this.holdsNow(slug, id)) return true;
    await this.refresh();
    return this.holdsNow(slug, id);
  }

  /** Whether `slug` names the tenant `id` in the list as last applied, with no read. */
  holdsNow(slug: string, id: string): boolean {
    return this.bySlug.get(slug)?.id === id;
  }

  /**
   * Whether `connection` is still the one held for `slug`, from a tenant list read after this call.
   * A connection that is not, because its tenant is gone or its slug now names another tenant, has
   * been disconnected by the time this resolves.
   */
  async confirm(slug: string, connection: C): Promise<boolean> {
    if (!(await this.refresh())) return false;
    return this.bySlug.get(slug)?.connection === connection;
  }

  /**
   * Reads the tenant list and reconciles the connections with it. The read starts after this call,
   * and no sooner than `refreshIntervalMs` after the previous one started; calls that arrive while one
   * waits share it. Resolves true once the list was read and applied, false when the read failed or
   * the registry stopped.
   */
  refresh(): Promise<boolean> {
    if (this.stopped) return Promise.resolve(false);
    if (this.queued) return this.queued;

    const previous = this.running;
    const queued = (async () => {
      if (previous) await previous;
      await this.delay(this.lastStartedAt + this.options.refreshIntervalMs - Date.now());
      this.queued = null;
      if (this.stopped) return false;
      return this.run();
    })();
    this.queued = queued;
    return queued;
  }

  async stop(): Promise<void> {
    this.stopped = true;
    if (this.wait) {
      clearTimeout(this.wait.timer);
      this.wait.resolve();
    }
    const held = this.connections();
    this.bySlug.clear();
    await Promise.allSettled(held.map((connection) => connection.disconnect()));
  }

  private delay(ms: number): Promise<void> {
    if (ms <= 0) return Promise.resolve();
    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        this.wait = null;
        resolve();
      }, ms);
      timer.unref?.();
      this.wait = { timer, resolve };
    });
  }

  private async run(): Promise<boolean> {
    this.lastStartedAt = Date.now();
    const running = (async () => {
      let directory: TenantDirectory;
      try {
        directory = await this.options.discover();
      } catch (error) {
        const reason = error instanceof Error ? error.message : String(error);
        logger.warn(`Tenant discovery failed: ${reason}`);
        return false;
      }
      if (this.stopped) return false;
      this.apply(directory);
      return true;
    })();
    this.running = running;
    try {
      return await running;
    } finally {
      if (this.running === running) this.running = null;
    }
  }

  private apply(directory: TenantDirectory): void {
    const wanted = new Map(directory.tenants.map((t) => [t.slug, t.id]));

    for (const [slug, held] of this.bySlug) {
      if (wanted.get(slug) === held.id) continue;
      this.bySlug.delete(slug);
      logger.info(`Tenant ${slug} is no longer active; closing its relay`);
      this.options.onTenantRemoved?.(slug);
      held.connection.disconnect().catch((error: unknown) => {
        logger.error(`Closing the relay for tenant ${slug} failed:`, error);
      });
    }

    for (const [slug, id] of wanted) {
      if (this.bySlug.has(slug)) continue;
      const connection = this.options.createConnection(slug, id);
      this.bySlug.set(slug, { id, connection });
      logger.info(`Opening the relay for tenant ${slug}`);
      connection.connect().catch((error: unknown) => {
        logger.error(`Opening the relay for tenant ${slug} failed:`, error);
      });
    }

    if (directory.apexSlug !== this.apex) {
      this.apex = directory.apexSlug;
      logger.info(`Apex domain serves tenant: ${this.apex ?? '(none)'}`);
      this.options.onApexChanged?.(this.apex);
    }
  }
}
