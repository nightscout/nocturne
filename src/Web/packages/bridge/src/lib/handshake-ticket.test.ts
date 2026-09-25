import { createHmac } from 'node:crypto';
import { describe, it, expect } from 'vitest';
import {
  signHandshakeTicket,
  verifyHandshakeTicket,
  normalizeHandshakeHost,
} from './handshake-ticket.js';

const SECRET = 'test-instance-key-0123456789';

describe('normalizeHandshakeHost', () => {
  it('lowercases and strips the port', () => {
    expect(normalizeHandshakeHost('Rhys.Nocturne.Run:443')).toBe('rhys.nocturne.run');
  });
});

describe('handshake ticket sign/verify', () => {
  it('round-trips a valid ticket and returns the normalized host', () => {
    const token = signHandshakeTicket(SECRET, 'rhys.nocturne.run', true);
    expect(verifyHandshakeTicket(SECRET, token)).toEqual({
      h: 'rhys.nocturne.run',
      exp: expect.any(Number),
      tenantRelay: true,
    });
  });

  it('carries a restricted admission through verification', () => {
    const token = signHandshakeTicket(SECRET, 'rhys.nocturne.run', false);
    expect(verifyHandshakeTicket(SECRET, token)?.tenantRelay).toBe(false);
  });

  it('treats a ticket signed without an admission as restricted', () => {
    const payload = Buffer.from(
      JSON.stringify({ h: 'rhys.nocturne.run', exp: Date.now() + 60_000 }),
      'utf-8',
    ).toString('base64url');
    const sig = createHmac('sha256', SECRET).update(payload).digest('hex');
    expect(verifyHandshakeTicket(SECRET, `${payload}.${sig}`)?.tenantRelay).toBe(false);
  });

  it('rejects a restricted ticket rewritten to claim the tenant room', () => {
    const [, sig] = signHandshakeTicket(SECRET, 'rhys.nocturne.run', false).split('.');
    const forged = Buffer.from(
      JSON.stringify({ h: 'rhys.nocturne.run', exp: Date.now() + 60_000, tenantRelay: true }),
      'utf-8',
    ).toString('base64url');
    expect(verifyHandshakeTicket(SECRET, `${forged}.${sig}`)).toBeNull();
  });

  it('normalizes the host at signing time', () => {
    const token = signHandshakeTicket(SECRET, 'RHYS.nocturne.run:8443', true);
    expect(verifyHandshakeTicket(SECRET, token)?.h).toBe('rhys.nocturne.run');
  });

  it('rejects a ticket signed with a different secret', () => {
    const token = signHandshakeTicket('other-secret-key-9876543210', 'rhys.nocturne.run', true);
    expect(verifyHandshakeTicket(SECRET, token)).toBeNull();
  });

  it('rejects a tampered payload', () => {
    const token = signHandshakeTicket(SECRET, 'rhys.nocturne.run', true);
    const [, sig] = token.split('.');
    const forged = Buffer.from(
      JSON.stringify({ h: 'evil.nocturne.run', exp: Date.now() + 60_000 }),
      'utf-8',
    ).toString('base64url');
    expect(verifyHandshakeTicket(SECRET, `${forged}.${sig}`)).toBeNull();
  });

  it('rejects an expired ticket', () => {
    const token = signHandshakeTicket(SECRET, 'rhys.nocturne.run', true, -1_000);
    expect(verifyHandshakeTicket(SECRET, token)).toBeNull();
  });

  it('fails closed on a missing secret or token', () => {
    const token = signHandshakeTicket(SECRET, 'rhys.nocturne.run', true);
    expect(verifyHandshakeTicket('', token)).toBeNull();
    expect(verifyHandshakeTicket(SECRET, undefined)).toBeNull();
    expect(verifyHandshakeTicket(SECRET, 'not-a-ticket')).toBeNull();
  });
});
