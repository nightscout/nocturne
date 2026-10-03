import { createHash, randomBytes } from "node:crypto";
import type { Vendor, VendorReply, VendorRequest } from "./vendor.ts";

interface Account {
  clientId: string;
  callback: string;
  scope: string;
  challenge: string;
  anchor: number;
  refreshToken: string;
  revoked: boolean;
}

const codes = new Map<string, Account>();
const refreshTokens = new Map<string, Account>();
const accessTokens = new Map<string, Account>();
const failure = (error: string): VendorReply => ({ status: 400, body: { error } });
const secret = () => randomBytes(24).toString("base64url");

function tokens(account: Account, initial: boolean): VendorReply {
  const access = secret();
  accessTokens.set(access, account);
  return { status: 200, body: {
    access_token: access, token_type: "Bearer", scope: account.scope,
    // The first token expires within Nocturne's safety buffer, exercising refresh without a sleep.
    expires_in: initial ? 30 : 3600,
    ...(initial ? { refresh_token: account.refreshToken } : {}),
  } };
}

function points(type: string, account: Account): Record<string, unknown>[] {
  return [0, 1].map((index) => {
    const start = account.anchor - (index + 1) * 3_600_000;
    const iso = (mills: number) => new Date(mills).toISOString();
    const name = `users/me/dataTypes/${type}/dataPoints/${account.clientId}-${index}`;
    switch (type) {
      case "steps": return { name, steps: { interval: { startTime: iso(start), endTime: iso(start + 60_000) }, count: 123 + index } };
      case "heart-rate": return { name, heartRate: { sampleTime: { physicalTime: iso(start) }, beatsPerMinute: 72 + index } };
      case "weight": return { name, weight: { sampleTime: { physicalTime: iso(start) }, weightGrams: 75_000 + index * 100 } };
      case "sleep": return { name, sleep: {
        interval: { startTime: iso(start), endTime: iso(start + 30 * 60_000) },
        stages: [{ startTime: iso(start), endTime: iso(start + 30 * 60_000), type: "LIGHT" }],
      } };
      default: return {};
    }
  }).filter((point) => Object.keys(point).length > 0);
}

export const googleHealth: Vendor = {
  handle(request: VendorRequest): VendorReply {
    if (request.method === "GET" && request.path === "/authorize") {
      const q = request.query;
      if (q.response_type !== "code" || q.code_challenge_method !== "S256" || !q.state ||
          !q.code_challenge || !q.client_id || !q.redirect_uri || !q.scope || q.access_type !== "offline") return failure("invalid_request");
      const code = secret();
      const account: Account = {
        clientId: q.client_id, callback: q.redirect_uri!, scope: q.scope!, challenge: q.code_challenge,
        anchor: Date.now(), refreshToken: secret(), revoked: false,
      };
      codes.set(code, account);
      const redirect = new URL(account.callback);
      redirect.searchParams.set("code", code);
      redirect.searchParams.set("state", q.state);
      return { status: 200, body: { redirectUrl: redirect.href } };
    }

    if (request.method === "POST" && request.path === "/oauth2.googleapis.com/token") {
      const form = new URLSearchParams(request.body);
      if (form.get("client_secret") !== "e2e-google-secret") return failure("invalid_client");
      if (form.get("grant_type") === "authorization_code") {
        const code = form.get("code") ?? "";
        const account = codes.get(code);
        codes.delete(code);
        const challenge = createHash("sha256").update(form.get("code_verifier") ?? "").digest("base64url");
        if (!account || account.challenge !== challenge || account.clientId !== form.get("client_id") ||
            account.callback !== form.get("redirect_uri")) return failure("invalid_grant");
        refreshTokens.set(account.refreshToken, account);
        return tokens(account, true);
      }
      if (form.get("grant_type") === "refresh_token") {
        const account = refreshTokens.get(form.get("refresh_token") ?? "");
        if (!account || account.revoked || account.clientId !== form.get("client_id")) return failure("invalid_grant");
        return tokens(account, false);
      }
      return failure("invalid_request");
    }

    if (request.method === "POST" && request.path === "/oauth2.googleapis.com/revoke") {
      const account = refreshTokens.get(new URLSearchParams(request.body).get("token") ?? "");
      if (!account) return failure("invalid_token");
      account.revoked = true;
      return { status: 200, body: {} };
    }

    const account = accessTokens.get((request.headers.authorization ?? "").replace(/^Bearer /, ""));
    if (!account || account.revoked) return { status: 401, body: { error: "invalid_token" } };
    if (request.method === "GET" && request.path === "/openidconnect.googleapis.com/v1/userinfo")
      return { status: 200, body: { sub: account.clientId } };

    const match = /^\/health.googleapis.com\/v4\/users\/me\/dataTypes\/([^/]+)\/dataPoints:reconcile$/.exec(request.path);
    if (request.method === "GET" && match) {
      const data = points(match[1]!, account);
      const times = [...(request.query.filter ?? "").matchAll(/"([^\"]+)"/g)].map((m) => Date.parse(m[1]!));
      if (times.length !== 2 || times.some(Number.isNaN)) return failure("invalid_request");
      const inRange = data.filter((point) => {
        const payload = point[match[1] === "heart-rate" ? "heartRate" : match[1]!] as Record<string, Record<string, string>>;
        const time = Date.parse(match[1] === "sleep" ? payload.interval!.endTime! :
          payload.interval?.startTime ?? payload.sampleTime?.physicalTime ?? "");
        return time >= times[0]! && time < times[1]!;
      });
      if (request.query.pageToken && request.query.pageToken !== "second") return failure("invalid_request");
      const second = request.query.pageToken === "second";
      return { status: 200, body: {
        dataPoints: second ? inRange.slice(1) : inRange.slice(0, 1),
        ...(!second && inRange.length > 1 ? { nextPageToken: "second" } : {}),
      } };
    }
    return { status: 404, body: { error: "unknown_mock_endpoint" } };
  },
};
