import { describe, it, expect, vi } from "vitest";
import type { RequestEvent } from "@sveltejs/kit";
import { resolvePatientTimeZone } from "./patient-timezone";

/**
 * Either the value the endpoint answers with, or a thrower standing in for a
 * refusal.
 */
type Source = Record<string, unknown> | (() => never) | undefined;

interface Stub {
  locals: RequestEvent["locals"];
  getPatientTimezone: ReturnType<typeof vi.fn>;
}

/** One request's locals, answering only the call this resolver makes. */
function request(answer: Source): Stub {
  const getPatientTimezone = vi.fn(async () =>
    typeof answer === "function" ? answer() : answer
  );
  return {
    locals: {
      apiClient: { patientRecord: { getPatientTimezone } },
    } as unknown as RequestEvent["locals"],
    getPatientTimezone,
  };
}

describe("resolvePatientTimeZone", () => {
  it("returns the zone the API resolves", async () => {
    expect(
      await resolvePatientTimeZone(
        request({ timezone: "Europe/Berlin" }).locals
      )
    ).toBe("Europe/Berlin");
  });

  it("rejects a zone this runtime does not know, rather than passing it on", async () => {
    // A bad value reaching Intl throws where it is formatted, a page away from
    // the setting that caused it.
    expect(
      await resolvePatientTimeZone(request({ timezone: "Middle/Earth" }).locals)
    ).toBeNull();
  });

  it("reads as no zone when the caller may not read it", async () => {
    // A public-share viewer is refused it and the report still has to render.
    const denied = () => {
      throw { status: 403 };
    };

    expect(await resolvePatientTimeZone(request(denied).locals)).toBeNull();
  });

  it("fails the read rather than reporting no zone, when the lookup breaks", async () => {
    // Degrading a 500 or a timeout to "no zone" cuts the report's days on UTC and
    // shows a clinician the wrong day with nothing on the page to say so.
    const broken = () => {
      throw { status: 500 };
    };

    await expect(
      resolvePatientTimeZone(request(broken).locals)
    ).rejects.toMatchObject({ status: 500 });
  });

  it("reads as no zone when none is named", async () => {
    expect(await resolvePatientTimeZone(request({}).locals)).toBeNull();
  });

  it("resolves once per request, however many queries ask", async () => {
    // Every report query calls resolveReportRange, and a report page fires several.
    const stub = request({ timezone: "Europe/Berlin" });

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
    expect(stub.getPatientTimezone).toHaveBeenCalledTimes(1);
  });

  it("keeps one request's answer out of the next", async () => {
    const berlin = request({ timezone: "Europe/Berlin" });
    const sydney = request({ timezone: "Australia/Sydney" });

    expect(await resolvePatientTimeZone(berlin.locals)).toBe("Europe/Berlin");
    expect(await resolvePatientTimeZone(sydney.locals)).toBe(
      "Australia/Sydney"
    );
  });
});
