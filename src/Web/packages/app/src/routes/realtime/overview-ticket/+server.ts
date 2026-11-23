import type { RequestHandler } from "./$types";
import {
  OVERVIEW_ADMISSION_PATH,
  readOverviewGrant,
  signOverviewTicket,
} from "@nocturne/bridge/ticket";
import { mintRealtimeTicket } from "#lib/server/realtime-ticket.js";

/**
 * Mints the ticket for the bridge's `/overview` namespace, which names the
 * tenants whose live changes the People page hears about (see
 * {@link mintRealtimeTicket}). The API refuses every credential issued for one
 * tenant, so such a session gets a denial and opens no socket.
 */
export const GET: RequestHandler = (event) =>
  mintRealtimeTicket(event, OVERVIEW_ADMISSION_PATH, (secret, host, admission) => {
    const grant = readOverviewGrant(admission);
    return grant ? signOverviewTicket(secret, host, grant) : null;
  });
