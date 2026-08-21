import { createHmac } from "crypto";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import {
  CLIENT_IP_HEADER,
  CLIENT_IP_SIGNATURE_HEADER,
  clientAddressHeaders,
  getClientAddress,
} from "./client-address";

const INSTANCE_KEY = "s3cret-instance-key";
const SOCKET_ADDRESS = "10.0.1.7";

function event(headers: Record<string, string> = {}) {
  return {
    request: new Request("http://web.internal/guest", { headers }),
    getClientAddress: () => SOCKET_ADDRESS,
  };
}

describe("clientAddressHeaders", () => {
  const previous = process.env.INSTANCE_KEY;

  beforeEach(() => {
    process.env.INSTANCE_KEY = INSTANCE_KEY;
  });

  afterEach(() => {
    if (previous === undefined) delete process.env.INSTANCE_KEY;
    else process.env.INSTANCE_KEY = previous;
  });

  it("names the browser the gateway forwarded, signed with the instance key", () => {
    const headers = clientAddressHeaders(
      event({ "X-Forwarded-For": "203.0.113.4" }),
    );

    expect(headers[CLIENT_IP_HEADER]).toBe("203.0.113.4");
    expect(headers[CLIENT_IP_SIGNATURE_HEADER]).toBe(
      createHmac("sha256", INSTANCE_KEY).update("203.0.113.4").digest("hex"),
    );
  });

  it("takes the client from the head of a proxy chain", () => {
    expect(
      getClientAddress(event({ "X-Forwarded-For": "203.0.113.4, 172.16.0.2" })),
    ).toBe("203.0.113.4");
  });

  it("falls back to the connection's peer", () => {
    expect(clientAddressHeaders(event())[CLIENT_IP_HEADER]).toBe(SOCKET_ADDRESS);
  });

  it("sends nothing it cannot sign", () => {
    delete process.env.INSTANCE_KEY;

    expect(clientAddressHeaders(event({ "X-Forwarded-For": "203.0.113.4" }))).toEqual({});
  });

  it("sends nothing when there is no client, as while prerendering", () => {
    const prerender = {
      request: new Request("http://web.internal/guest"),
      getClientAddress: () => {
        throw new Error("Cannot read clientAddress on a prerendered page");
      },
    };

    expect(clientAddressHeaders(prerender)).toEqual({});
  });
});
