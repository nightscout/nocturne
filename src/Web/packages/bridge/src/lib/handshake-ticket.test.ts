import { createHmac } from 'node:crypto';
import { describe, it, expect } from 'vitest';
import {
  signHandshakeTicket,
  verifyHandshakeTicket,
  normalizeHandshakeHost,
  readRealtimeGrant,
  readOverviewGrant,
  signOverviewTicket,
  verifyOverviewTicket,
  OVERVIEW_TICKET_MAX_TENANTS,
  type OverviewGrant,
  type RealtimeGrant,
} from './handshake-ticket.js';

const SECRET = 'test-instance-key-0123456789';
const TENANT_ID = '0198a3c4-7e21-7b10-9d2f-5c6e7f809a1b';

/** A ticket signed with `SECRET` over any payload, as a minting bug or an older app would produce. */
function signedPayload(payload: Record<string, unknown>): string {
  const encoded = Buffer.from(JSON.stringify(payload), 'utf-8').toString('base64url');
  return `${encoded}.${createHmac('sha256', SECRET).update(encoded).digest('hex')}`;
}

function grant(tenantRelay: boolean, extra: Partial<RealtimeGrant> = {}): RealtimeGrant {
  return { tenantRelay, categories: [], historyClamped: false, mayAck: false, ...extra };
}

describe('normalizeHandshakeHost', () => {
  it('lowercases and strips the port', () => {
    expect(normalizeHandshakeHost('Rhys.Nocturne.Run:443')).toBe('rhys.nocturne.run');
  });
});

describe('handshake ticket sign/verify', () => {
  it('round-trips a valid ticket and returns the normalized host', () => {
    const token = signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(true));
    expect(verifyHandshakeTicket(SECRET, token)).toEqual({
      h: 'rhys.nocturne.run',
      t: TENANT_ID,
      exp: expect.any(Number),
      tenantRelay: true,
      categories: [],
      historyClamped: false,
      mayAck: false,
    });
  });

  it('round-trips the granted categories, the history clamp and the right to silence alarms', () => {
    const token = signHandshakeTicket(
      SECRET,
      'rhys.nocturne.run',
      TENANT_ID,
      grant(true, { categories: ['entries', 'care'], historyClamped: true, mayAck: true }),
    );

    expect(verifyHandshakeTicket(SECRET, token)).toMatchObject({
      categories: ['entries', 'care'],
      historyClamped: true,
      mayAck: true,
    });
  });

  it('round-trips a subject id for a member ticket', () => {
    const subject = '0a5f2c1e-1111-4222-8333-444455556666';
    const token = signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(true, { subjectId: subject }));

    expect(verifyHandshakeTicket(SECRET, token)?.subjectId).toBe(subject);
  });

  it('drops a subject id that is not a canonical lowercase D GUID', () => {
    const token = signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(true, { subjectId: '0A5F2C1E-1111-4222-8333-444455556666' }));

    expect(verifyHandshakeTicket(SECRET, token)?.subjectId).toBeUndefined();
  });

  it('carries a restricted admission through verification', () => {
    const token = signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(false));
    expect(verifyHandshakeTicket(SECRET, token)?.tenantRelay).toBe(false);
  });

  it('rejects a ticket that names no tenant', () => {
    expect(verifyHandshakeTicket(SECRET, signedPayload({ h: 'rhys.nocturne.run', exp: Date.now() + 60_000 }))).toBeNull();
    expect(verifyHandshakeTicket(SECRET, signedPayload({ h: 'rhys.nocturne.run', t: '', exp: Date.now() + 60_000 }))).toBeNull();
  });

  it('treats a ticket signed without an admission as restricted', () => {
    const token = signedPayload({ h: 'rhys.nocturne.run', t: TENANT_ID, exp: Date.now() + 60_000 });
    expect(verifyHandshakeTicket(SECRET, token)).toMatchObject({
      tenantRelay: false,
      categories: [],
      historyClamped: true,
    });
  });

  it('rejects a restricted ticket rewritten to claim the tenant room', () => {
    const [, sig] = signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(false)).split('.');
    const forged = Buffer.from(
      JSON.stringify({ h: 'rhys.nocturne.run', exp: Date.now() + 60_000, tenantRelay: true }),
      'utf-8',
    ).toString('base64url');
    expect(verifyHandshakeTicket(SECRET, `${forged}.${sig}`)).toBeNull();
  });

  it('normalizes the host at signing time', () => {
    const token = signHandshakeTicket(SECRET, 'RHYS.nocturne.run:8443', TENANT_ID, grant(true));
    expect(verifyHandshakeTicket(SECRET, token)?.h).toBe('rhys.nocturne.run');
  });

  it('rejects a ticket signed with a different secret', () => {
    const token = signHandshakeTicket('other-secret-key-9876543210', 'rhys.nocturne.run', TENANT_ID, grant(true));
    expect(verifyHandshakeTicket(SECRET, token)).toBeNull();
  });

  it('rejects a tampered payload', () => {
    const token = signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(true));
    const [, sig] = token.split('.');
    const forged = Buffer.from(
      JSON.stringify({ h: 'evil.nocturne.run', exp: Date.now() + 60_000 }),
      'utf-8',
    ).toString('base64url');
    expect(verifyHandshakeTicket(SECRET, `${forged}.${sig}`)).toBeNull();
  });

  it('rejects an expired ticket', () => {
    const token = signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(true), -1_000);
    expect(verifyHandshakeTicket(SECRET, token)).toBeNull();
  });

  it('fails closed on a missing secret or token', () => {
    const token = signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(true));
    expect(verifyHandshakeTicket('', token)).toBeNull();
    expect(verifyHandshakeTicket(SECRET, undefined)).toBeNull();
    expect(verifyHandshakeTicket(SECRET, 'not-a-ticket')).toBeNull();
  });
});

describe('readRealtimeGrant', () => {
  it.each([
    ['nothing', undefined],
    ['a non-object', 'tenantRelay'],
    ['an admission with no categories or clamp', { tenantRelay: true }],
  ])('grants no category room for %s', (_case, value) => {
    expect(readRealtimeGrant(value)).toMatchObject({ categories: [], historyClamped: true });
  });

  it.each([
    ['true', true, true],
    ['absent', undefined, false],
    ['a truthy string', 'true', false],
  ])('grants the right to silence alarms only when mayAck is %s', (_case, mayAck, expected) => {
    expect(readRealtimeGrant({ tenantRelay: true, mayAck }).mayAck).toBe(expected);
  });

  it('keeps only the string categories of a malformed list', () => {
    expect(
      readRealtimeGrant({ tenantRelay: true, categories: ['care', 7, null], historyClamped: false })
        .categories,
    ).toEqual(['care']);
  });
});

describe('overview ticket sign/verify', () => {
  const SUBJECT = '0a5f2c1e-1111-4222-8333-444455556666';
  const OTHER_TENANT = '0198a3c4-7e21-7b10-9d2f-5c6e7f809a1c';

  function overviewGrant(tenantIds: string[] = [TENANT_ID]): OverviewGrant {
    return { subjectId: SUBJECT, tenantIds };
  }

  /** An overview ticket signed with `SECRET` over any payload, as `signOverviewTicket` signs one. */
  function signedOverviewPayload(payload: Record<string, unknown>): string {
    const encoded = Buffer.from(JSON.stringify(payload), 'utf-8').toString('base64url');
    return `${encoded}.${createHmac('sha256', SECRET).update(`overview.${encoded}`).digest('hex')}`;
  }

  it('round-trips the host, subject and tenants', () => {
    const token = signOverviewTicket(SECRET, 'Nocturne.Run:443', overviewGrant([TENANT_ID, OTHER_TENANT]));

    expect(verifyOverviewTicket(SECRET, token)).toEqual({
      h: 'nocturne.run',
      exp: expect.any(Number),
      subjectId: SUBJECT,
      tenantIds: [TENANT_ID, OTHER_TENANT],
    });
  });

  it('rejects an expired overview ticket', () => {
    const token = signOverviewTicket(SECRET, 'nocturne.run', overviewGrant(), -1_000);
    expect(verifyOverviewTicket(SECRET, token)).toBeNull();
  });

  it('rejects an overview ticket whose tenants were widened after signing', () => {
    const [, sig] = signOverviewTicket(SECRET, 'nocturne.run', overviewGrant()).split('.');
    const forged = Buffer.from(
      JSON.stringify({ h: 'nocturne.run', exp: Date.now() + 60_000, subjectId: SUBJECT, tenantIds: [TENANT_ID, OTHER_TENANT] }),
      'utf-8',
    ).toString('base64url');
    expect(verifyOverviewTicket(SECRET, `${forged}.${sig}`)).toBeNull();
  });

  it('rejects an overview ticket signed with a different secret', () => {
    const token = signOverviewTicket('other-secret-key-9876543210', 'nocturne.run', overviewGrant());
    expect(verifyOverviewTicket(SECRET, token)).toBeNull();
  });

  it('fails closed on a missing secret or token', () => {
    const token = signOverviewTicket(SECRET, 'nocturne.run', overviewGrant());
    expect(verifyOverviewTicket('', token)).toBeNull();
    expect(verifyOverviewTicket(SECRET, undefined)).toBeNull();
  });

  it('is refused as a tenant ticket, even one carrying a tenant id', () => {
    const token = signOverviewTicket(SECRET, 'rhys.nocturne.run', overviewGrant());
    const withTenant = signedOverviewPayload({
      h: 'rhys.nocturne.run', t: TENANT_ID, exp: Date.now() + 60_000, tenantRelay: true,
      subjectId: SUBJECT, tenantIds: [TENANT_ID],
    });

    expect(verifyHandshakeTicket(SECRET, token)).toBeNull();
    expect(verifyHandshakeTicket(SECRET, withTenant)).toBeNull();
  });

  it('refuses a tenant ticket, even one carrying a tenant list', () => {
    const token = signHandshakeTicket(SECRET, 'rhys.nocturne.run', TENANT_ID, grant(true, { subjectId: SUBJECT }));
    const withList = signedPayload({ h: 'rhys.nocturne.run', exp: Date.now() + 60_000, subjectId: SUBJECT, tenantIds: [TENANT_ID] });

    expect(verifyOverviewTicket(SECRET, token)).toBeNull();
    expect(verifyOverviewTicket(SECRET, withList)).toBeNull();
  });

  it.each([
    ['no subject', { tenantIds: [TENANT_ID] }],
    ['a subject not in canonical form', { subjectId: SUBJECT.toUpperCase(), tenantIds: [TENANT_ID] }],
    ['no tenant list', { subjectId: SUBJECT }],
    ['a tenant list that is not a list', { subjectId: SUBJECT, tenantIds: TENANT_ID }],
  ])('rejects a signed overview ticket with %s', (_case, grantFields) => {
    expect(verifyOverviewTicket(SECRET, signedOverviewPayload({ h: 'nocturne.run', exp: Date.now() + 60_000, ...grantFields })))
      .toBeNull();
  });

  it('drops tenant ids that are not canonical lowercase GUIDs', () => {
    const token = signedOverviewPayload({
      h: 'nocturne.run',
      exp: Date.now() + 60_000,
      subjectId: SUBJECT,
      tenantIds: [TENANT_ID, TENANT_ID.toUpperCase(), 'rhys', 7, null, `{${OTHER_TENANT}}`],
    });

    expect(verifyOverviewTicket(SECRET, token)?.tenantIds).toEqual([TENANT_ID]);
  });
});

describe('readOverviewGrant', () => {
  const SUBJECT = '0a5f2c1e-1111-4222-8333-444455556666';

  it('fails closed on anything that is not an admission', () => {
    expect(readOverviewGrant(undefined)).toBeNull();
    expect(readOverviewGrant('admitted')).toBeNull();
  });

  it('keeps each tenant once, in canonical id order', () => {
    const b = 'bbbbbbbb-0000-4000-8000-000000000000';
    const a = 'aaaaaaaa-0000-4000-8000-000000000000';

    expect(readOverviewGrant({ subjectId: SUBJECT, tenantIds: [b, a, b] })?.tenantIds).toEqual([a, b]);
  });

  it('carries the first tenants in id order up to the cap, whatever order the API lists them in', () => {
    const ids = Array.from({ length: OVERVIEW_TICKET_MAX_TENANTS + 5 }, (_, i) =>
      `${i.toString(16).padStart(8, '0')}-0000-4000-8000-000000000000`);

    const carried = readOverviewGrant({ subjectId: SUBJECT, tenantIds: [...ids].reverse() })?.tenantIds;

    expect(carried).toEqual(ids.slice(0, OVERVIEW_TICKET_MAX_TENANTS));
  });
});
