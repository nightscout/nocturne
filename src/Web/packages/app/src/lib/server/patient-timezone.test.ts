import { describe, it, expect, vi } from "vitest";
import type { RequestEvent } from "@sveltejs/kit";
import { resolvePatientTimeZone } from "./patient-timezone";

/**
 * Either the value a source answers with, or a thrower standing in for a
 * refusal.
 */
type Source = Record<string, unknown> | (() => never) | undefined;

type Sources = { record?: Source; profile?: Source };

interface Stub {
  locals: RequestEvent["locals"];
  getPatientRecord: ReturnType<typeof vi.fn>;
  getProfileSummary: ReturnType<typeof vi.fn>;
}

/** One request's locals, answering only the two calls this resolver makes. */
function request({ record, profile }: Sources): Stub {
  const answer = (value: Source) =>
    vi.fn(async () => (typeof value === "function" ? value() : value));
  const getPatientRecord = answer(record);
  const getProfileSummary = answer(profile);
  return {
    // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- two of ApiClient's methods; the rest is unreachable from this resolver
    locals: {
      apiClient: {
        patientRecord: { getPatientRecord },
        profile: { getProfileSummary },
      },
    } as unknown as RequestEvent["locals"],
    getPatientRecord,
    getProfileSummary,
  };
}

const legacyProfile = (timezone: string) => ({
  therapySettings: [{ timezone }],
});

describe("resolvePatientTimeZone", () => {
  it("prefers the patient record, as the API's own resolver does", async () => {
    // A patient lives in one timezone however many therapy profiles they have.
    const stub = request({
      record: { timezone: "Europe/Berlin" },
      profile: legacyProfile("UTC"),
    });

    expect(await resolvePatientTimeZone(stub.locals)).toBe("Europe/Berlin");
  });

  it("does not read the profile summary when the record answers", async () => {
    // `getProfileSummary` is five sequential repository reads and is served
    // NoStore, so the common path must not touch it.
    const stub = request({
      record: { timezone: "Europe/Berlin" },
      profile: legacyProfile("UTC"),
    });

    await resolvePatientTimeZone(stub.locals);

    expect(stub.getProfileSummary).not.toHaveBeenCalled();
  });

  it("falls back to a connector-imported therapy profile", async () => {
    // Nightscout and Glooko imports wrote the zone here before the patient record
    // became canonical, so it is all an un-migrated instance has.
    const stub = request({
      record: {},
      profile: legacyProfile("Europe/Berlin"),
    });

    expect(await resolvePatientTimeZone(stub.locals)).toBe("Europe/Berlin");
  });

  it("rejects a zone this runtime does not know, rather than passing it on", async () => {
    // A bad value reaching Intl throws where it is formatted, a page away from
    // the setting that caused it.
    const stub = request({
      record: { timezone: "Middle/Earth" },
      profile: legacyProfile("Europe/Berlin"),
    });

    expect(await resolvePatientTimeZone(stub.locals)).toBe("Europe/Berlin");
  });

  it("reads as no zone when the caller may not read either source", async () => {
    // Neither is a shareable data category, so a public-share viewer gets 401 on
    // both and the report still has to render.
    const denied = () => {
      throw { status: 403 };
    };

    expect(
      await resolvePatientTimeZone(
        request({ record: denied, profile: denied }).locals
      )
    ).toBeNull();
  });

  it("fails the read rather than reporting no zone, when the lookup breaks", async () => {
    // Degrading a 500 or a timeout to "no zone" cuts the report's days on UTC and
    // shows a clinician the wrong day with nothing on the page to say so.
    const broken = () => {
      throw { status: 500 };
    };

    await expect(
      resolvePatientTimeZone(request({ record: broken }).locals)
    ).rejects.toMatchObject({ status: 500 });

    await expect(
      resolvePatientTimeZone(request({ record: {}, profile: broken }).locals)
    ).rejects.toMatchObject({ status: 500 });
  });

  it("reads as no zone when neither source names one", async () => {
    expect(
      await resolvePatientTimeZone(request({ record: {}, profile: {} }).locals)
    ).toBeNull();
  });

  it("resolves once per request, however many queries ask", async () => {
    // Every report query calls resolveReportRange, and a report page fires several.
    const stub = request({ record: { timezone: "Europe/Berlin" } });

    const answers = await Promise.all([
      resolvePatientTimeZone(stub.locals),
      resolvePatientTimeZone(stub.locals),
      resolvePatientTimeZone(stub.locals),
    ]);

    expect(answers).toEqual([
      "Europe/Berlin",
      "Europe/Berlin",
      "Europe/Berlin",
    ]);
    expect(stub.getPatientRecord).toHaveBeenCalledTimes(1);
  });

  it("keeps one request's answer out of the next", async () => {
    const berlin = request({ record: { timezone: "Europe/Berlin" } });
    const sydney = request({ record: { timezone: "Australia/Sydney" } });

    expect(await resolvePatientTimeZone(berlin.locals)).toBe("Europe/Berlin");
    expect(await resolvePatientTimeZone(sydney.locals)).toBe(
      "Australia/Sydney"
    );
  });
});
