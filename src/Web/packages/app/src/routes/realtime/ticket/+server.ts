import type { RequestHandler } from "./$types";
import {
  REALTIME_ADMISSION_PATH,
  readRealtimeGrant,
  signHandshakeTicket,
} from "@nocturne/bridge/ticket";
import type { RealtimeAdmission } from "#lib/api/index.js";
import { mintRealtimeTicket } from "#lib/server/realtime-ticket.js";

/**
 * Mints the ticket for the bridge's tenant namespace, for the tenant this
 * request's host resolves to (see {@link mintRealtimeTicket}). The admission is
 * gated on the same glucose read as the API's own feed, and says whether the
 * credential may join the tenant-wide room and which category rooms.
 */
export const GET: RequestHandler = (event) =>
  mintRealtimeTicket<RealtimeAdmission>(
    event,
    REALTIME_ADMISSION_PATH,
    (secret, host, admission) =>
      admission.tenantId
        ? signHandshakeTicket(secret, host, admission.tenantId, readRealtimeGrant(admission))
        : null
  );
