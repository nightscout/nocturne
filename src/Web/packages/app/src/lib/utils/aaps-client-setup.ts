/**
 * The text of the QR code AAPSClient 4 scans in its setup wizard ("Set up from the master") and
 * in Pair with master: AAPS's `ClientSetupCode`. AAPS refuses a code whose `type` or `v` differs,
 * and one with a blank `url` or `token`. There is no `pairing`, so the phone reads the site and
 * is not paired with a master.
 */
export function buildAapsClientSetupCode(instanceUrl: string, token: string): string {
  return JSON.stringify({
    type: "aaps-client-setup",
    v: 1,
    url: instanceUrl.replace(/\/+$/, ""),
    token,
  });
}
