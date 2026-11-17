#!/usr/bin/env python3
# The v8 client installs paused and only its websocket API can change that.
import base64, json, os, socket, sys, time

for _ in range(30):
    try:
        s = socket.create_connection(("127.0.0.1", 7396), timeout=5)
        break
    except OSError:
        time.sleep(2)
else:
    sys.exit("fah-client did not open its control port")

key = base64.b64encode(os.urandom(16)).decode()
s.sendall(("GET /api/websocket HTTP/1.1\r\nHost: 127.0.0.1:7396\r\nUpgrade: websocket\r\n"
           "Connection: Upgrade\r\nSec-WebSocket-Key: %s\r\nSec-WebSocket-Version: 13\r\n\r\n" % key).encode())
resp = b""
while b"\r\n\r\n" not in resp:
    chunk = s.recv(4096)
    if not chunk:
        sys.exit("websocket handshake failed")
    resp += chunk
if not resp.startswith(b"HTTP/1.1 101"):
    sys.exit("websocket handshake refused: " + resp.split(b"\r\n")[0].decode())

payload = json.dumps({"cmd": "state", "state": "fold"}).encode()
mask = os.urandom(4)
s.sendall(bytes([0x81, 0x80 | len(payload)]) + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(payload)))
time.sleep(2)
s.close()
