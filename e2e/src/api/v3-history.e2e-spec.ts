import { beforeAll, describe, expect, it } from "vitest";
import { postEntries, sgvSeries } from "../helpers/data.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";

interface V3Envelope<T> {
  status: number;
  result: T[];
}

interface V3Entry {
  identifier?: string;
  date: number;
  sgv: number;
  srvModified: number;
}

function cursorOf(etag: string | null): number {
  return Number(etag!.match(/"(\d+)"/)![1]);
}

/** Nightscout v3 history pages on srvModified (write time), not on the reading's clinical date. */
describe("v3 history paging", () => {
  let tenant: Tenant;
  const series = sgvSeries({ count: 25, device: "e2e-v3" });

  beforeAll(async () => {
    tenant = await seedTenant();
    await postEntries(tenant.api, series);
  });

  it("pages by modification time and advances by the ETag cursor until the backlog is drained", async () => {
    let cursor = 0;
    const seen: number[] = [];
    for (let page = 0; page < 10; page++) {
      const res = await tenant.api.get<V3Envelope<V3Entry>>(`/api/v3/entries/history/${cursor}?limit=10`);
      expect(res.status).toBe(200);
      const modified = res.body.result.map((e) => e.srvModified);
      if (modified.length === 0) break;

      expect(modified).toEqual([...modified].sort((a, b) => a - b));
      expect(Math.min(...modified)).toBeGreaterThan(cursor);
      const newest = Math.max(...modified);
      const etag = res.headers.get("etag");
      expect(etag).toBe(`W/"${newest}"`);
      expect(res.headers.get("last-modified")).toBe(new Date(newest).toUTCString());

      seen.push(...res.body.result.map((e) => e.date));
      cursor = cursorOf(etag);
    }

    expect(seen.sort((a, b) => a - b)).toEqual(series.map((e) => e.date).sort((a, b) => a - b));
  });

  it("returns nothing past the newest modification", async () => {
    const all = await tenant.api.get<V3Envelope<V3Entry>>(`/api/v3/entries/history/0?limit=1000`);
    expect(all.status).toBe(200);
    expect(all.body.result).toHaveLength(series.length);

    const res = await tenant.api.get<V3Envelope<V3Entry>>(
      `/api/v3/entries/history/${cursorOf(all.headers.get("etag"))}?limit=10`,
    );
    expect(res.status).toBe(200);
    expect(res.body.result).toEqual([]);
  });

  it("requires authentication", async () => {
    const res = await tenant.anonymous.get(`/api/v3/entries/history/0?limit=1`);
    expect(res.status).toBe(401);
  });
});
