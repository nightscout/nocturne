import { describe, it, expect, vi, afterEach } from 'vitest';
import TenantRegistry, { type TenantDirectory } from './tenant-registry.js';

const INTERVAL_MS = 5_000;

class FakeConnection {
  connected = false;
  disconnected = false;
  constructor(readonly slug: string) {}
  connect = vi.fn(async () => {
    this.connected = true;
  });
  disconnect = vi.fn(async () => {
    this.disconnected = true;
  });
  isConnected = () => this.connected && !this.disconnected;
}

function directory(tenants: Record<string, string>, apexSlug: string | null = null): TenantDirectory {
  return { tenants: Object.entries(tenants).map(([slug, id]) => ({ slug, id })), apexSlug };
}

function registry(answers: (TenantDirectory | Error)[]) {
  const discover = vi.fn(async () => {
    const answer = answers.length > 1 ? answers.shift()! : answers[0];
    if (answer instanceof Error) throw answer;
    return answer;
  });
  const created: FakeConnection[] = [];
  const removed: string[] = [];
  const apex: (string | null)[] = [];
  const tenants = new TenantRegistry({
    discover,
    createConnection: (slug) => {
      const connection = new FakeConnection(slug);
      created.push(connection);
      return connection;
    },
    refreshIntervalMs: INTERVAL_MS,
    onTenantRemoved: (slug) => removed.push(slug),
    onApexChanged: (slug) => apex.push(slug),
  });
  return { tenants, discover, created, removed, apex };
}

describe('TenantRegistry', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('connects one client per discovered tenant', async () => {
    const { tenants, created } = registry([directory({ rhys: 't1', sam: 't2' })]);

    expect(await tenants.refresh()).toBe(true);

    expect(created.map((c) => c.slug)).toEqual(['rhys', 'sam']);
    expect(created.every((c) => c.connect.mock.calls.length === 1)).toBe(true);
    expect(tenants.get('rhys')).toBe(created[0]);
  });

  it('connects a tenant created after startup once a socket for it is admitted', async () => {
    vi.useFakeTimers();
    const { tenants, created } = registry([directory({}), directory({ fresh: 't1' })]);
    await tenants.refresh();
    expect(created).toEqual([]);

    tenants.ensure('fresh');
    await vi.advanceTimersByTimeAsync(INTERVAL_MS);

    expect(created.map((c) => c.slug)).toEqual(['fresh']);
    expect(tenants.get('fresh')?.connect).toHaveBeenCalledTimes(1);
  });

  it('names a tenant it holds without reading the tenant list again', async () => {
    const { tenants, discover } = registry([directory({ rhys: 't1' })]);
    await tenants.refresh();

    expect(await tenants.holds('rhys', 't1')).toBe(true);
    expect(discover).toHaveBeenCalledTimes(1);
  });

  it('reads the tenant list before deciding on a slug it holds no tenant for', async () => {
    const { tenants, discover } = registry([directory({ fresh: 't1' })]);

    expect(await tenants.holds('fresh', 't1')).toBe(true);
    expect(discover).toHaveBeenCalledTimes(1);
  });

  it('refuses the tenant a recreated slug named before, once the tenant list is read again', async () => {
    vi.useFakeTimers();
    const { tenants, discover } = registry([directory({ rhys: 'old' }), directory({ rhys: 'new' })]);
    await tenants.refresh();

    const recreated = tenants.holds('rhys', 'new');
    await vi.advanceTimersByTimeAsync(INTERVAL_MS);
    expect(await recreated).toBe(true);

    const replaced = tenants.holds('rhys', 'old');
    await vi.advanceTimersByTimeAsync(INTERVAL_MS);
    expect(await replaced).toBe(false);
    expect(discover).toHaveBeenCalledTimes(3);
  });

  it('answers holdsNow from the list as last applied, without a read', async () => {
    const { tenants, discover } = registry([directory({ rhys: 't1' })]);
    expect(tenants.holdsNow('rhys', 't1')).toBe(false);

    await tenants.refresh();

    expect(tenants.holdsNow('rhys', 't1')).toBe(true);
    expect(tenants.holdsNow('rhys', 't2')).toBe(false);
    expect(tenants.holdsNow('sam', 't1')).toBe(false);
    expect(discover).toHaveBeenCalledTimes(1);
  });

  it('refuses a tenant it cannot confirm because the tenant list cannot be read', async () => {
    const { tenants } = registry([new Error('API down')]);

    expect(await tenants.holds('rhys', 't1')).toBe(false);
  });

  it('does not rediscover for a tenant it already holds', async () => {
    vi.useFakeTimers();
    const { tenants, discover } = registry([directory({ rhys: 't1' })]);
    await tenants.refresh();

    tenants.ensure('rhys');
    await vi.advanceTimersByTimeAsync(INTERVAL_MS * 3);

    expect(discover).toHaveBeenCalledTimes(1);
  });

  it('runs one discovery for every miss within an interval', async () => {
    vi.useFakeTimers();
    const { tenants, discover } = registry([directory({})]);
    await tenants.refresh();

    for (const slug of ['a', 'b', 'c', 'd']) tenants.ensure(slug);
    await vi.advanceTimersByTimeAsync(INTERVAL_MS);

    expect(discover).toHaveBeenCalledTimes(2);
  });

  it('discovers again after a miss that arrives while a discovery is in flight', async () => {
    vi.useFakeTimers();
    let release!: (d: TenantDirectory) => void;
    const answers: Promise<TenantDirectory>[] = [
      new Promise((resolve) => {
        release = resolve;
      }),
      Promise.resolve(directory({ fresh: 't1' })),
    ];
    const created: string[] = [];
    const tenants = new TenantRegistry({
      discover: () => answers.shift()!,
      createConnection: (slug) => {
        created.push(slug);
        return new FakeConnection(slug);
      },
      refreshIntervalMs: INTERVAL_MS,
    });

    const first = tenants.refresh();
    await vi.advanceTimersByTimeAsync(0);
    expect(answers).toHaveLength(1);
    tenants.ensure('fresh');
    release(directory({}));
    expect(await first).toBe(true);
    expect(created).toEqual([]);

    await vi.advanceTimersByTimeAsync(INTERVAL_MS);

    expect(created).toEqual(['fresh']);
  });

  it('disconnects a tenant that is deleted or deactivated, and reports it removed', async () => {
    vi.useFakeTimers();
    const { tenants, created, removed } = registry([
      directory({ rhys: 't1', sam: 't2' }),
      directory({ sam: 't2' }),
    ]);
    await tenants.refresh();

    const refreshed = tenants.refresh();
    await vi.advanceTimersByTimeAsync(INTERVAL_MS);
    await refreshed;

    expect(created[0].disconnect).toHaveBeenCalledTimes(1);
    expect(created[1].disconnect).not.toHaveBeenCalled();
    expect(tenants.get('rhys')).toBeUndefined();
    expect(removed).toEqual(['rhys']);
  });

  it('replaces the client of a slug that now names a different tenant', async () => {
    vi.useFakeTimers();
    const { tenants, created, removed } = registry([
      directory({ rhys: 'old-id' }),
      directory({ rhys: 'new-id' }),
    ]);
    await tenants.refresh();

    const refreshed = tenants.refresh();
    await vi.advanceTimersByTimeAsync(INTERVAL_MS);
    await refreshed;

    expect(created).toHaveLength(2);
    expect(created[0].disconnect).toHaveBeenCalledTimes(1);
    expect(created[1].connect).toHaveBeenCalledTimes(1);
    expect(tenants.get('rhys')).toBe(created[1]);
    expect(removed).toEqual(['rhys']);
  });

  it('keeps every client when a discovery fails', async () => {
    vi.useFakeTimers();
    const { tenants, created, removed } = registry([
      directory({ rhys: 't1' }),
      new Error('API unavailable'),
    ]);
    await tenants.refresh();

    const refreshed = tenants.refresh();
    await vi.advanceTimersByTimeAsync(INTERVAL_MS);

    expect(await refreshed).toBe(false);
    expect(created[0].disconnect).not.toHaveBeenCalled();
    expect(tenants.get('rhys')).toBe(created[0]);
    expect(removed).toEqual([]);
  });

  it('reports the apex tenant as it changes', async () => {
    vi.useFakeTimers();
    const { tenants, apex } = registry([
      directory({}, null),
      directory({ only: 't1' }, 'only'),
      directory({ only: 't1' }, 'only'),
    ]);
    await tenants.refresh();

    const second = tenants.refresh();
    await vi.advanceTimersByTimeAsync(INTERVAL_MS);
    await second;
    const third = tenants.refresh();
    await vi.advanceTimersByTimeAsync(INTERVAL_MS);
    await third;

    expect(apex).toEqual(['only']);
    expect(tenants.apexSlug).toBe('only');
  });

  it('disconnects every client and discovers nothing more once stopped', async () => {
    vi.useFakeTimers();
    const { tenants, created, discover } = registry([directory({ rhys: 't1' })]);
    await tenants.refresh();

    tenants.ensure('other');
    await tenants.stop();
    await vi.advanceTimersByTimeAsync(INTERVAL_MS * 2);

    expect(created[0].disconnect).toHaveBeenCalledTimes(1);
    expect(discover).toHaveBeenCalledTimes(1);
    expect(await tenants.refresh()).toBe(false);
  });
});
