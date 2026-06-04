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
    const token = signHandshakeTicket(SECRET, 'rhys.nocturne.run');
    expect(verifyHandshakeTicket(SECRET, token)).toEqual({
      h: 'rhys.nocturne.run',
      exp: expect.any(Number),
    });
  });

  it('normalizes the host at signing time', () => {
    const token = signHandshakeTicket(SECRET, 'RHYS.nocturne.run:8443');
    expect(verifyHandshakeTicket(SECRET, token)?.h).toBe('rhys.nocturne.run');
  });

  it('rejects a ticket signed with a different secret', () => {
    const token = signHandshakeTicket('other-secret-key-9876543210', 'rhys.nocturne.run');
    expect(verifyHandshakeTicket(SECRET, token)).toBeNull();
  });

  it('rejects a tampered payload', () => {
    const token = signHandshakeTicket(SECRET, 'rhys.nocturne.run');
    const [, sig] = token.split('.');
    const forged = Buffer.from(
      JSON.stringify({ h: 'evil.nocturne.run', exp: Date.now() + 60_000 }),
      'utf-8',
    ).toString('base64url');
    expect(verifyHandshakeTicket(SECRET, `${forged}.${sig}`)).toBeNull();
  });

  it('rejects an expired ticket', () => {
    const token = signHandshakeTicket(SECRET, 'rhys.nocturne.run', -1_000);
    expect(verifyHandshakeTicket(SECRET, token)).toBeNull();
  });

  it('fails closed on a missing secret or token', () => {
    const token = signHandshakeTicket(SECRET, 'rhys.nocturne.run');
    expect(verifyHandshakeTicket('', token)).toBeNull();
    expect(verifyHandshakeTicket(SECRET, undefined)).toBeNull();
    expect(verifyHandshakeTicket(SECRET, 'not-a-ticket')).toBeNull();
  });
});
