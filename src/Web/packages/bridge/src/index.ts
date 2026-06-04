export { setupBridge } from './setup.js';
export {
  signHandshakeTicket,
  verifyHandshakeTicket,
  normalizeHandshakeHost,
  HANDSHAKE_TICKET_LIFETIME_MS,
} from './lib/handshake-ticket.js';
export type { HandshakeTicketPayload } from './lib/handshake-ticket.js';
export type {
  BridgeConfig,
  BridgeInstance,
  BridgeStats,
  ClientInfo,
  AlarmData,
  ServerStats
} from './types.js';
