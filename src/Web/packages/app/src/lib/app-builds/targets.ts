import type { BuildTarget } from "#lib/api/index.js";

/**
 * The signing secrets whose format this web app can make: AAPS Builder's packed
 * keystore.
 */
const KEYSTORE_SECRETS: Readonly<Record<string, string>> = {
  aaps: "KEYSTORE_SET",
};

/**
 * The target's keystore secret, when the target declares one this web app knows
 * how to pack.
 */
export function keystoreSecretName(target: BuildTarget): string | undefined {
  const id = target.id ?? "";
  const name = Object.hasOwn(KEYSTORE_SECRETS, id)
    ? KEYSTORE_SECRETS[id]
    : undefined;
  return name && target.secrets?.some((secret) => secret.name === name)
    ? name
    : undefined;
}
