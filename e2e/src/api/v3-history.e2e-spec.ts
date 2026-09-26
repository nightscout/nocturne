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
}

describe("v3 history paging", () => {
  let tenant: Tenant;
  const series = sgvSeries({ count: 25, device: "e2e-v3" });

  beforeAll(async () => {
    tenant = await seedTenant();
    await postEntries(tenant.api, series);
  });

  it("pages oldest first and advances by the ETag cursor until the backlog is drained", async () => {
    let cursor = series.at(-1)!.date - 1;
    const seen: number[] = [];
    for (let page = 0; page < 10; page++) {
      const res = await tenant.api.get<V3Envelope<V3Entry>>(`/api/v3/entries/history/${cursor}?limit=10`);
      expect(res.status).toBe(200);
      const dates = res.body.result.map((e) => e.date);
      if (dates.length === 0) break;

      expect(dates).toEqual([...dates].sort((a, b) => a - b));
      const etag = res.headers.get("etag");
      expect(etag).toBe(`W/"${Math.max(...dates)}"`);
      expect(res.headers.get("last-modified")).toBe(new Date(Math.max(...dates)).toUTCString());

      seen.push(...dates);
      cursor = Number(etag!.match(/"(\d+)"/)![1]);
    }

    expect(seen).toEqual(series.map((e) => e.date).sort((a, b) => a - b));
  });

  it("returns nothing past the newest record", async () => {
    const res = await tenant.api.get<V3Envelope<V3Entry>>(`/api/v3/entries/history/${series[0]!.date}?limit=10`);
    expect(res.status).toBe(200);
    expect(res.body.result).toEqual([]);
  });

  it("requires authentication", async () => {
    const res = await tenant.anonymous.get(`/api/v3/entries/history/0?limit=1`);
    expect(res.status).toBe(401);
  });
});
