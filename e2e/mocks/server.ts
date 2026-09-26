// Fake third-party vendors for the e2e stack. One HTTP server, one path prefix per vendor
// (`/nightscout/...`), so a connector is pointed at `http://mocks:8080/<vendor>`.
// Adding a vendor means adding a module under ./vendors and listing it below.
//
// Every vendor also answers `GET /<vendor>/__requests` with the requests it has served, and
// `DELETE /<vendor>/__requests` to clear them, so specs can assert what a connector called.
//
// Runs on bare Node (type stripping), no dependencies.

import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { nightscout } from "./vendors/nightscout.ts";
import type { Vendor, VendorRequest } from "./vendors/vendor.ts";

const vendors: Record<string, Vendor> = { nightscout };
const seen: Record<string, VendorRequest[]> = Object.fromEntries(Object.keys(vendors).map((k) => [k, []]));

function send(res: ServerResponse, status: number, body: unknown) {
  const text = typeof body === "string" ? body : JSON.stringify(body);
  res.writeHead(status, { "content-type": typeof body === "string" ? "text/plain" : "application/json" });
  res.end(text);
}

async function readBody(req: IncomingMessage): Promise<string> {
  const chunks: Buffer[] = [];
  for await (const chunk of req) chunks.push(chunk as Buffer);
  return Buffer.concat(chunks).toString("utf8");
}

const server = createServer(async (req, res) => {
  const url = new URL(req.url ?? "/", "http://mocks");
  if (url.pathname === "/health") return send(res, 200, "ok");

  const [, name, ...rest] = url.pathname.split("/");
  const vendor = name ? vendors[name] : undefined;
  if (!vendor) return send(res, 404, { error: `no fake vendor '${name}'` });

  const path = "/" + rest.join("/");
  if (path === "/__requests") {
    if (req.method === "DELETE") {
      seen[name!] = [];
      return send(res, 204, "");
    }
    return send(res, 200, seen[name!]);
  }

  const request: VendorRequest = {
    method: req.method ?? "GET",
    path,
    query: Object.fromEntries(url.searchParams),
    headers: req.headers as Record<string, string>,
    body: req.method === "GET" || req.method === "HEAD" ? "" : await readBody(req),
  };
  seen[name!]!.push({ ...request, headers: { "api-secret": request.headers["api-secret"] ?? "" } });

  try {
    const reply = await vendor.handle(request);
    send(res, reply.status, reply.body);
  } catch (err) {
    send(res, 500, { error: String(err) });
  }
});

const port = Number(process.env.PORT ?? 8080);
server.listen(port, () => console.log(`fake vendors [${Object.keys(vendors).join(", ")}] on :${port}`));
process.on("SIGTERM", () => server.close(() => process.exit(0)));
