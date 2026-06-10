import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { createServer } from 'http';
import { setupBridge } from './setup.js';

describe('setupBridge fail-closed', () => {
  beforeEach(() => {
    // buildConfig falls back to process.env.BASE_DOMAIN; clear it so the
    // absent-baseDomain case is exercised regardless of the host environment.
    vi.stubEnv('BASE_DOMAIN', '');
  });

  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it('throws when BASE_DOMAIN is absent so no unauthenticated path is started', async () => {
    await expect(
      setupBridge(createServer(), {
        signalr: { hubUrl: 'http://api:8080/hubs/data' },
        instanceKey: 'test-key',
      }),
    ).rejects.toThrow(/BASE_DOMAIN/);
  });
});
