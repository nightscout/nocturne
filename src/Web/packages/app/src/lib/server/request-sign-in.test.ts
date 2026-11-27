import { describe, it, expect } from "vitest";
import { signInAddressForRequest } from "./request-sign-in";

const ORIGIN = "https://nocturne.test";
const ENDPOINT = `${ORIGIN}/_app/remote/abc123/getThing`;

/** What the browser's query call looks like to the app: the endpoint as `event.url`, no page header. */
function browserQuery(referer?: string, headers: Record<string, string> = {}) {
  return {
    isRemoteRequest: true,
    url: new URL(ENDPOINT),
    request: new Request(ENDPOINT, {
      headers: {
        "x-forwarded-host": "nocturne.test",
        ...headers,
        ...(referer === undefined ? {} : { referer }),
      },
    }),
  };
}

const loginFor = (page: string) => `/auth/login?returnUrl=${encodeURIComponent(page)}`;

describe("signInAddressForRequest", () => {
  describe("for a query the browser calls", () => {
    it("returns to the page named by a same-origin Referer, with its query", () => {
      expect(signInAddressForRequest(browserQuery(`${ORIGIN}/people/wall?group=family`))).toBe(
        loginFor("/people/wall?group=family")
      );
    });

    it("drops the Referer's fragment", () => {
      expect(signInAddressForRequest(browserQuery(`${ORIGIN}/reports#tir`))).toBe(
        loginFor("/reports")
      );
    });

    it("never returns to another origin's Referer", () => {
      expect(signInAddressForRequest(browserQuery("https://evil.test/steal"))).toBe("/auth/login");
    });

    it("never returns to a Referer on another port or scheme of this host", () => {
      expect(signInAddressForRequest(browserQuery("http://nocturne.test/reports"))).toBe(
        "/auth/login"
      );
      expect(signInAddressForRequest(browserQuery("https://nocturne.test:8443/reports"))).toBe(
        "/auth/login"
      );
    });

    it("is the bare address when there is no Referer", () => {
      expect(signInAddressForRequest(browserQuery())).toBe("/auth/login");
    });

    it("is the bare address when the Referer is not a URL", () => {
      expect(signInAddressForRequest(browserQuery("not a url"))).toBe("/auth/login");
    });
  });

  describe("behind the edge", () => {
    const internal = "http://web.internal:3000/_app/remote/abc123/getThing";

    function viaEdge(referer: string) {
      return {
        isRemoteRequest: true,
        url: new URL(internal),
        request: new Request(internal, {
          headers: {
            referer,
            "x-forwarded-proto": "https",
            "x-forwarded-host": "acme.nocturne.test",
          },
        }),
      };
    }

    it("compares the Referer with the origin the edge reports, not the forwarded address", () => {
      expect(signInAddressForRequest(viaEdge("https://acme.nocturne.test/reports?range=7d"))).toBe(
        loginFor("/reports?range=7d")
      );
    });

    it("refuses a Referer on any other origin", () => {
      expect(signInAddressForRequest(viaEdge("http://web.internal:3000/reports"))).toBe(
        "/auth/login"
      );
      expect(signInAddressForRequest(viaEdge("https://other.nocturne.test/reports"))).toBe(
        "/auth/login"
      );
      expect(signInAddressForRequest(viaEdge("http://acme.nocturne.test/reports"))).toBe(
        "/auth/login"
      );
    });
  });

  describe("for every other request", () => {
    it("returns to the page of event.url", () => {
      const url = new URL(`${ORIGIN}/people/wall?group=family`);
      const request = new Request(url);
      expect(signInAddressForRequest({ isRemoteRequest: false, url, request })).toBe(
        loginFor("/people/wall?group=family")
      );
    });

    it("returns to the page SvelteKit left on event.url, not the data request's address", () => {
      const request = new Request(
        `${ORIGIN}/reports/__data.json?range=7d&x-sveltekit-invalidated=01`
      );
      const url = new URL(`${ORIGIN}/reports?range=7d`);
      expect(signInAddressForRequest({ isRemoteRequest: false, url, request })).toBe(
        loginFor("/reports?range=7d")
      );
    });

    it("ignores a Referer from another origin", () => {
      const url = new URL(`${ORIGIN}/reports`);
      const request = new Request(url, { headers: { referer: "https://evil.test/steal" } });
      expect(signInAddressForRequest({ isRemoteRequest: false, url, request })).toBe(
        loginFor("/reports")
      );
    });

    it("returns to the page a command or form names in x-sveltekit-pathname", () => {
      const request = new Request(ENDPOINT, {
        method: "POST",
        headers: { "x-sveltekit-pathname": "/settings", "x-sveltekit-search": "?tab=a" },
      });
      const url = new URL(`${ORIGIN}/settings?tab=a`);
      expect(signInAddressForRequest({ isRemoteRequest: true, url, request })).toBe(
        loginFor("/settings?tab=a")
      );
    });
  });
});
