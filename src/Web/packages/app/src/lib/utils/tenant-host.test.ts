import { describe, it, expect } from "vitest";
import { tenantUrl } from "./tenant-host";

describe("tenantUrl", () => {
  it("builds a tenant subdomain URL", () => {
    expect(tenantUrl("alice", "example.com", "https:")).toBe(
      "https://alice.example.com/"
    );
  });

  it("keeps a port already embedded in the base domain", () => {
    expect(tenantUrl("alice", "nocturne.localhost:1612", "https:")).toBe(
      "https://alice.nocturne.localhost:1612/"
    );
  });

  it("does not add a port of its own", () => {
    expect(tenantUrl("alice", "example.com", "http:")).toBe(
      "http://alice.example.com/"
    );
  });

  it("supports multi-label base domains", () => {
    expect(tenantUrl("alice", "cgm.example.co.uk", "https:")).toBe(
      "https://alice.cgm.example.co.uk/"
    );
  });
});
