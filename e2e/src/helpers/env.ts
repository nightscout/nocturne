/** Where the running e2e stack is reachable from the host. Mirrors scripts/lib.ts `config`. */
export const env = {
  apiUrl: `http://127.0.0.1:${process.env.E2E_API_PORT ?? 1630}`,
  webPort: Number(process.env.E2E_WEB_PORT ?? 1631),
  mocksUrl: `http://127.0.0.1:${process.env.E2E_MOCKS_PORT ?? 1634}`,
  /** How the API container reaches the fake vendors. */
  mocksUrlFromApi: "http://mocks:8080",
  /** BASE_DOMAIN of the stack; tenants are subdomains of it. */
  get baseDomain() {
    return `nocturne.localhost:${this.webPort}`;
  },
  tenantHost(slug: string) {
    return `${slug}.${this.baseDomain}`;
  },
  /** A tenant's browser origin: *.localhost resolves to loopback in Chromium. */
  tenantWebUrl(slug: string) {
    return `http://${this.tenantHost(slug)}`;
  },
};
