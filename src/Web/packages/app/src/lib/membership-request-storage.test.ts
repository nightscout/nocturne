import { describe, expect, it } from "vitest";
import {
  DRAFT_TTL_MS,
  MEMBERSHIP_REQUEST_FLOW_PARAM,
  parkMembershipRequest,
  takeMembershipRequest,
} from "./membership-request-storage";

class MemoryStorage implements Storage {
  private readonly items = new Map<string, string>();
  get length() {
    return this.items.size;
  }
  clear() {
    this.items.clear();
  }
  getItem(key: string) {
    return this.items.get(key) ?? null;
  }
  key(index: number) {
    return [...this.items.keys()][index] ?? null;
  }
  removeItem(key: string) {
    this.items.delete(key);
  }
  setItem(key: string, value: string) {
    this.items.set(key, value);
  }
  keys() {
    return [...this.items.keys()];
  }
}

const HOST = "sam.nocturne.example";
const FLOW = "11111111-1111-4111-8111-111111111111";
const PARKED_AT = 1_800_000_000_000;

function park(storage: Storage, flowId = FLOW, message = "I'm Sam's endocrinologist") {
  return parkMembershipRequest(storage, HOST, message, "/reports?range=7d", flowId, PARKED_AT);
}

function arrival(path: string) {
  return new URL(`https://${HOST}${path}`);
}

describe("parkMembershipRequest", () => {
  it("returns the destination carrying the flow id", () => {
    const returnPath = park(new MemoryStorage());

    expect(returnPath).toBe(`/reports?range=7d&${MEMBERSHIP_REQUEST_FLOW_PARAM}=${FLOW}`);
  });

  it("replaces a flow id the destination already carries, so a re-park submits the new draft", () => {
    const storage = new MemoryStorage();
    const first = park(storage, FLOW, "first draft");
    const second = parkMembershipRequest(
      storage,
      HOST,
      "edited draft",
      first,
      "22222222-2222-4222-8222-222222222222",
      PARKED_AT + 1000,
    );

    expect(second).toBe(
      `/reports?range=7d&${MEMBERSHIP_REQUEST_FLOW_PARAM}=22222222-2222-4222-8222-222222222222`,
    );
    const taken = takeMembershipRequest(
      storage,
      new MemoryStorage(),
      HOST,
      arrival(second),
      PARKED_AT + 2000,
    );
    expect(taken.message).toBe("edited draft");
  });

  it("returns the destination unmarked when the draft cannot be stored", () => {
    const full = new MemoryStorage();
    full.setItem = () => {
      throw new DOMException("full", "QuotaExceededError");
    };

    expect(park(full)).toBe("/reports?range=7d");
  });
});

describe("takeMembershipRequest", () => {
  it("returns the draft for a matching flow id once, and strips the id from the URL", () => {
    const storage = new MemoryStorage();
    const returnPath = park(storage);

    const first = takeMembershipRequest(
      storage,
      new MemoryStorage(),
      HOST,
      arrival(returnPath),
      PARKED_AT + 1000,
    );
    const second = takeMembershipRequest(
      storage,
      new MemoryStorage(),
      HOST,
      arrival(returnPath),
      PARKED_AT + 2000,
    );

    expect(first.message).toBe("I'm Sam's endocrinologist");
    expect(first.rest?.href).toBe(`https://${HOST}/reports?range=7d`);
    expect(second.message).toBeNull();
    expect(storage.keys()).toEqual([]);
  });

  it("returns an empty message as a draft to submit", () => {
    const storage = new MemoryStorage();
    const returnPath = park(storage, FLOW, "");

    const taken = takeMembershipRequest(
      storage,
      new MemoryStorage(),
      HOST,
      arrival(returnPath),
      PARKED_AT,
    );

    expect(taken.message).toBe("");
  });

  it("submits nothing for a sign-in that carries no flow id, and keeps the live draft", () => {
    const storage = new MemoryStorage();
    park(storage);

    const taken = takeMembershipRequest(
      storage,
      new MemoryStorage(),
      HOST,
      arrival("/reports"),
      PARKED_AT + 1000,
    );

    expect(taken).toEqual({ message: null, rest: null });
    expect(storage.keys()).toHaveLength(1);
  });

  it("submits nothing for a different flow id, and strips that id", () => {
    const storage = new MemoryStorage();
    park(storage);

    const taken = takeMembershipRequest(
      storage,
      new MemoryStorage(),
      HOST,
      arrival(`/?${MEMBERSHIP_REQUEST_FLOW_PARAM}=22222222-2222-4222-8222-222222222222`),
      PARKED_AT + 1000,
    );

    expect(taken.message).toBeNull();
    expect(taken.rest?.href).toBe(`https://${HOST}/`);
    expect(storage.keys()).toHaveLength(1);
  });

  it("submits nothing for a draft parked on another host", () => {
    const storage = new MemoryStorage();
    const returnPath = parkMembershipRequest(
      storage,
      "other.nocturne.example",
      "hello",
      "/",
      FLOW,
      PARKED_AT,
    );

    const taken = takeMembershipRequest(
      storage,
      new MemoryStorage(),
      HOST,
      arrival(returnPath),
      PARKED_AT,
    );

    expect(taken.message).toBeNull();
  });

  it("accepts a draft at the expiry limit", () => {
    const storage = new MemoryStorage();
    const returnPath = park(storage);

    const taken = takeMembershipRequest(
      storage,
      new MemoryStorage(),
      HOST,
      arrival(returnPath),
      PARKED_AT + DRAFT_TTL_MS,
    );

    expect(taken.message).toBe("I'm Sam's endocrinologist");
  });

  it("discards an expired draft for the matching flow id unsent", () => {
    const storage = new MemoryStorage();
    const returnPath = park(storage);

    const taken = takeMembershipRequest(
      storage,
      new MemoryStorage(),
      HOST,
      arrival(returnPath),
      PARKED_AT + DRAFT_TTL_MS + 1,
    );

    expect(taken.message).toBeNull();
    expect(storage.keys()).toEqual([]);
  });

  it("discards every expired or unreadable draft on the host, and keeps live ones and other hosts", () => {
    const storage = new MemoryStorage();
    const now = PARKED_AT + DRAFT_TTL_MS + 1;
    park(storage, "expired");
    parkMembershipRequest(storage, HOST, "live", "/", "live", now);
    parkMembershipRequest(storage, "other.example", "x", "/", "elsewhere", PARKED_AT);
    storage.setItem(`nocturne:membership-request:${HOST}:garbled`, "{not json");
    storage.setItem(`nocturne:membership-request:${HOST}:wrong-shape`, '{"message":1}');

    takeMembershipRequest(storage, new MemoryStorage(), HOST, arrival("/"), now);

    expect(storage.keys().sort()).toEqual(
      [
        `nocturne:membership-request:${HOST}:live`,
        "nocturne:membership-request:other.example:elsewhere",
      ].sort(),
    );
  });

  it("removes the legacy draft under the bare host key without returning it", () => {
    const legacy = new MemoryStorage();
    legacy.setItem(`nocturne:membership-request:${HOST}`, "an old introduction");
    legacy.setItem("nocturne:membership-request:other.example", "kept");

    const taken = takeMembershipRequest(
      new MemoryStorage(),
      legacy,
      HOST,
      arrival("/"),
      PARKED_AT,
    );

    expect(taken.message).toBeNull();
    expect(legacy.keys()).toEqual(["nocturne:membership-request:other.example"]);
  });
});
