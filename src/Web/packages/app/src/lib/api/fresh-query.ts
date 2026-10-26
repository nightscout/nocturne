import type { RemoteQuery } from "$app/server";

/**
 * The query's value, fetched from the server now. Awaiting a query on its own answers with the
 * value a live subscriber already holds, which a poll or a read after a write must not use.
 */
export async function fresh<T>(query: RemoteQuery<T>): Promise<T> {
  await query.refresh();
  return await query;
}
