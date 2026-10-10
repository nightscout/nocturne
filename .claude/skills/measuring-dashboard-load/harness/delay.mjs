// Origin-path delay: everything Caddy does not serve as an edge-cached immutable asset waits
// ORIGIN_DELAY_MS before reaching the web server, so origin round-trips cost what they cost
// a distant visitor while static assets keep the browser's base (edge) latency only.
import http from "node:http";
import net from "node:net";
const DELAY = Number(process.env.ORIGIN_DELAY_MS ?? 320);
const [host, port] = ["web", 5173];
const agent = new http.Agent({ keepAlive: true });
const wait = () => new Promise((r) => setTimeout(r, DELAY));
const server = http.createServer({ maxHeaderSize: 1 << 20 }, async (req, res) => {
  await wait();
  const up = http.request({ host, port, method: req.method, path: req.url, headers: req.headers, agent, maxHeaderSize: 1 << 20 }, (u) => {
    res.writeHead(u.statusCode, u.headers);
    u.pipe(res);
  });
  up.on("error", (e) => { console.error(req.method, req.url, e.message); if (!res.headersSent) res.statusCode = 502; res.end(); });
  req.pipe(up);
});
server.on("upgrade", async (req, socket, head) => {
  await wait();
  const up = net.connect(port, host, () => {
    up.write(`${req.method} ${req.url} HTTP/1.1\r\n` + Object.entries(req.headers).map(([k, v]) => `${k}: ${v}`).join("\r\n") + "\r\n\r\n");
    if (head?.length) up.write(head);
    up.pipe(socket); socket.pipe(up);
  });
  up.on("error", () => socket.destroy()); socket.on("error", () => up.destroy());
});
server.listen(5180);
