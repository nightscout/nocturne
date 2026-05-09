# Nocturne Helm Chart

Helm chart for deploying [Nocturne](https://github.com/nightscout/nocturne) on Kubernetes.

> **Status:** alpha (v0.1.x). External Postgres only; opinionated single-replica defaults; many production-grade toggles are not yet implemented. See [Roadmap](#roadmap) below.

## Prerequisites

- Kubernetes 1.27+
- Helm 3.10+
- A reachable PostgreSQL 17 server (managed or self-hosted) that you can either:
  - **Bootstrap automatically:** provide superuser credentials and let the chart's pre-install Job run [`bootstrap-roles.sql`](https://github.com/nightscout/nocturne/blob/main/docs/postgres/bootstrap-roles.sql) for you, or
  - **Bootstrap manually:** run the SQL yourself ahead of time and disable the Job (`bootstrap.enabled: false`) — necessary on managed services where superuser is unavailable
- Three Kubernetes Secrets containing the per-role passwords you want to use (chart does not generate them)
- One Kubernetes Secret containing the `INSTANCE_KEY` (shared HMAC between API and Web for JWT signing)

## Why three Postgres roles?

Nocturne enforces multi-tenant isolation via PostgreSQL Row-Level Security. The schema is owned by `nocturne_migrator` (which runs DDL/migrations), the API runs as `nocturne_app` (`NOBYPASSRLS`, owns nothing — so a compromised API cannot disable RLS), and the SvelteKit web container's bot-framework state is stored under `nocturne_web`. Collapsing to a single role removes the isolation guarantee. **The chart requires all three.**

See [`docs/postgres/bootstrap-roles.sql`](https://github.com/nightscout/nocturne/blob/main/docs/postgres/bootstrap-roles.sql) for the full rationale.

## Quickstart (external Postgres, bootstrap Job enabled)

```bash
# 1. Create the four secrets the chart needs.
kubectl create secret generic nocturne-instance-key \
  --from-literal=instance-key="$(openssl rand -hex 32)"

kubectl create secret generic nocturne-db-admin \
  --from-literal=username=postgres \
  --from-literal=password="$ADMIN_PASSWORD"

kubectl create secret generic nocturne-db-app \
  --from-literal=password="$(openssl rand -hex 24)"
kubectl create secret generic nocturne-db-migrator \
  --from-literal=password="$(openssl rand -hex 24)"
kubectl create secret generic nocturne-db-web \
  --from-literal=password="$(openssl rand -hex 24)"

# 2. Write a values file.
cat > my-values.yaml <<EOF
baseUrl: https://nocturne.example.com

instanceKey:
  existingSecret: nocturne-instance-key

externalDatabase:
  host: postgres.example.com
  port: 5432
  database: nocturne
  appSecret:       { existingSecret: nocturne-db-app }
  migratorSecret:  { existingSecret: nocturne-db-migrator }
  webSecret:       { existingSecret: nocturne-db-web }

bootstrap:
  enabled: true
  adminSecret:
    existingSecret: nocturne-db-admin

ingress:
  enabled: true
  className: traefik
  host: nocturne.example.com
EOF

# 3. Install.
helm install nocturne ./deploy/helm/nocturne -f my-values.yaml
```

## Managed Postgres (RDS / Cloud SQL / Neon)

Disable the bootstrap Job and run [`bootstrap-roles.sql`](https://github.com/nightscout/nocturne/blob/main/docs/postgres/bootstrap-roles.sql) yourself with whatever admin tooling your provider gives you. Then `bootstrap.enabled: false` in your values.

## Configuration

The full set of configurable values is in [`values.yaml`](./values.yaml). Highlights:

| Key | Description |
|---|---|
| `baseUrl` | Public URL the deployment is reachable at. Used by the API for OIDC redirects, invite links, etc. |
| `instanceKey.existingSecret` | Secret containing the shared HMAC key. **Required.** |
| `externalDatabase.host` / `.port` / `.database` / `.sslMode` | Postgres connection details. |
| `externalDatabase.{app,migrator,web}Secret.existingSecret` | Secret with each role's password under key `password` (override with `existingSecretKey`). |
| `bootstrap.enabled` | If true, runs `bootstrap-roles.sql` as a Helm pre-install hook against your Postgres using `bootstrap.adminSecret`. |
| `ingress.enabled` / `.host` / `.className` / `.tls` | Single-host ingress fronting the web service. Optional `ingress.api.externalPath` exposes the API on the same host. |
| `api.replicaCount` / `web.replicaCount` | Replica counts (default 1 each). HPA support not yet wired. |

## Roadmap

The chart is intentionally minimal in v0. Planned for v1:

- [ ] README + NOTES.txt (this commit)
- [ ] HPA, PodDisruptionBudget, NetworkPolicy toggles
- [ ] Prometheus ServiceMonitor toggle
- [ ] Bundled-Postgres quickstart via Bitnami `postgresql` subchart with auto-bootstrap
- [ ] `values.schema.json` for editor autocomplete
- [ ] CI: `helm lint` + `kubeconform` + drift check between `files/bootstrap-roles.sql` and `docs/postgres/bootstrap-roles.sql`
- [ ] Distribution: OCI publish to `oci://ghcr.io/nightscout/charts/nocturne`

## Known limitations / things to verify

- **Web image's `PUBLIC_API_URL` is baked at build time** to `http://localhost:1612`. The chart sets `NOCTURNE_API_URL` (read at runtime by `server.js`), but client-side fetches may use the baked URL. Needs in-cluster verification before the chart can be called production-ready.
- **Web container has no documented HTTP health endpoint** — TCP probe used for now.
- `containerSecurityContext.readOnlyRootFilesystem: false` — both containers may tolerate `true` but this hasn't been verified.
- `bootstrap-roles.sql` lives in two places (`docs/postgres/` and `deploy/helm/nocturne/files/`). Drift is not yet enforced by CI.
- The docstring in `docs/postgres/bootstrap-roles.sql` references env var names (`ConnectionStrings__NocturneDb`, `ConnectionStrings__NocturneDbMigrator`) that do not match the actual code (`ConnectionStrings__nocturne-postgres[-migrator]`). Documentation fix tracked separately.
