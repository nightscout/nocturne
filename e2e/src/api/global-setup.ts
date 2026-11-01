import { env } from "../helpers/env.ts";

/** Fails fast with a pointer when the stack is not running, rather than 60s timeouts per test. */
export default async function setup() {
  try {
    const res = await fetch(`${env.apiUrl}/alive`);
    if (res.ok) return;
    throw new Error(`HTTP ${res.status}`);
  } catch (err) {
    throw new Error(`The e2e stack is not reachable at ${env.apiUrl} (${err}). Start it with \`pnpm e2e:up\`, or run \`pnpm e2e:api\`.`);
  }
}
