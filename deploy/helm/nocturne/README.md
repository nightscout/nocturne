# Nocturne Helm Chart

Helm chart for deploying [Nocturne](https://github.com/nightscout/nocturne) on Kubernetes.

> **Status:** alpha (v0.1.x). External Postgres only; opinionated single-replica defaults; many production-grade toggles are not yet implemented. See [Roadmap](#roadmap) below.

## Install

The chart is published to GitHub Container Registry as an OCI artifact. Released builds are tagged via Chart.yaml's `version:`; `latest` follows `main` HEAD.

```bash
helm install nocturne oci://ghcr.io/nightscout/charts/nocturne \
  --version 0.1.0-alpha.3 \
  --namespace nocturne --create-namespace \
  -f my-values.yaml
```

To install via Argo CD, point an Application at the published chart:

```yaml
apiVersion: argoproj.io/v1alpha1
kind: Application
metadata:
  name: nocturne
  namespace: argocd
spec:
  project: default
  destination:
    server: https://kubernetes.default.svc
    namespace: nocturne
  source:
    repoURL: ghcr.io/nightscout/charts
    chart: nocturne
    targetRevision: 0.1.0-alpha.3
    helm:
      releaseName: nocturne
      valueFiles:
        - $values/path/to/your/values.yaml   # optional — via a second `sources:` entry
  syncPolicy:
    automated: { prune: true, selfHeal: true }
    syncOptions: [CreateNamespace=true, ServerSideApply=true]
```

Argo CD needs a Repository CR registered for `ghcr.io/nightscout/charts` with `enableOCI: "true"`. Anonymous read works once the published package is set to Public on GitHub.

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

# 3. Install from the published OCI chart.
helm install nocturne oci://ghcr.io/nightscout/charts/nocturne \
  --version 0.1.0-alpha.3 \
  --namespace nocturne --create-namespace \
  -f my-values.yaml
```

## Developing the chart

If you're iterating on the chart itself rather than consuming it:

```bash
git clone https://github.com/nightscout/nocturne.git
cd nocturne
helm dependency update deploy/helm/nocturne   # resolves Bitnami postgresql subchart
helm install nocturne ./deploy/helm/nocturne -f my-values.yaml
```

The path-source install requires `helm dependency update` because the Bitnami `postgresql` subchart isn't checked into git. The published OCI artifact has it bundled, so consumers don't need to run dep update.

## Managed Postgres (RDS / Cloud SQL / Neon)

Disable the bootstrap Job and run [`bootstrap-roles.sql`](https://github.com/nightscout/nocturne/blob/main/docs/postgres/bootstrap-roles.sql) yourself with whatever admin tooling your provider gives you. Then `bootstrap.enabled: false` in your values.

## Bundled Postgres (quickstart, not for production)

For evaluation or homelab use, the chart can deploy an in-cluster PostgreSQL via the [Bitnami `postgresql` subchart](https://github.com/bitnami/charts/tree/main/bitnami/postgresql):

```yaml
postgresql:
  enabled: true
  primary:
    persistence:
      size: 10Gi
```

What happens:
- Bitnami creates a `<release>-postgresql` StatefulSet, Service, and an auto-generated auth Secret holding `postgres-password`.
- The chart creates `<release>-nocturne-db` containing the three Nocturne role passwords (`app-password`, `migrator-password`, `web-password`), generated via Helm's `lookup` so they remain stable across `helm upgrade`.
- After install, a **post-install hook Job** runs `bootstrap-roles.sql` against the new Postgres using the postgres superuser password from Bitnami's Secret.
- The API Deployment has a `wait-for-postgres` initContainer that loops `pg_isready` and then a strict `psql -U nocturne_app` probe until the bootstrap Job completes.

Caveats:
- **Not recommended for production.** Single replica, no backups, no managed failover. Use a managed Postgres or a Postgres operator (CloudNativePG, Zalando) for serious deployments.
- **`helm template | kubectl apply` is non-idempotent in bundled mode** because the password Secret uses `lookup`, which returns nothing during offline rendering and re-randomizes on every render. **GitOps users (Argo CD / Flux): pre-create your own Secret** with all three role passwords and set `postgresql.auth.existingSecret: <name>` (along with the Bitnami auth keys). See the [Open question](#open-question--gitops-byo-secret) section below.
- **PVC retention.** `postgresql.persistence.retainOnUninstall: true` (default) keeps the PVC behind on `helm uninstall`. Set to `false` to delete with the release.
- **Toggling `postgresql.enabled` in either direction** requires `postgresql.uninstallAcknowledge: true` to avoid silent data loss.

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
| `{api,web,bootstrap}.image.digest` | Pin the image to an immutable content digest (see "Image pinning" below). |

### Image pinning

For deterministic pulls — useful when consuming a mutable tag like `:latest` in production, or when the chart's default `pullPolicy: IfNotPresent` would cause nodes to serve stale cached layers across rebuilds — set the component's `image.digest`:

```yaml
api:
  image:
    digest: "sha256:0fe69ae9befcbb09fddc59cc28cfa8c2453ec0dd4b6426e1ab7918e5f300d479"
```

When `digest` is set, `tag` is ignored and the rendered image reference is `<registry>/<repository>@<digest>`. Get the current `:latest` digest from the Packages UI (`https://github.com/orgs/nightscout/packages/container/nocturne%2Fnocturne-api/<version>?tag=latest`) or `docker buildx imagetools inspect`.

## Observability

Nocturne's API uses Aspire ServiceDefaults' OpenTelemetry plumbing and exports metrics, traces, and logs over **OTLP push** when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. There is no Prometheus `/metrics` endpoint, so direct scrape is not supported.

The web container has no OpenTelemetry SDK and is intentionally not instrumented.

### Three scenarios

**1. You already run an OTLP collector.** Point the chart at it:

```yaml
observability:
  otlp:
    enabled: true
    endpoint: http://otel-collector.observability.svc.cluster.local:4317
    protocol: grpc       # grpc (default, port 4317) or http/protobuf (port 4318)
```

If the collector requires auth (e.g. SaaS like Grafana Cloud, Honeycomb), put headers in a Secret and reference it:

```bash
kubectl create secret generic nocturne-otel-headers \
  --from-literal=headers='x-api-key=...,x-tenant-id=...'
```

```yaml
observability:
  otlp:
    enabled: true
    endpoint: https://otlp.example.com:4317
    headersSecretRef: { name: nocturne-otel-headers, key: headers }
```

**2. You only run Prometheus + Grafana, no OTLP.** Deploy the [OpenTelemetry Collector](https://github.com/open-telemetry/opentelemetry-collector) with an `otlp` receiver and a `prometheus` (scrape target) or `prometheusremotewrite` exporter. Then point the chart at the collector as in scenario 1. The chart does not bundle a collector.

**3. Nothing.** Default. `observability.otlp.enabled: false` registers no exporter; in-process telemetry is collected and dropped with negligible overhead.

### Resource attributes

The chart automatically sets:
- `service.name` → `<release>-nocturne-api` (override via `observability.otlp.serviceName`)
- `service.version` → image tag
- `k8s.namespace.name`, `k8s.pod.name` → via downward API

Add custom attributes via `observability.otlp.resourceAttributes` (a map merged into `OTEL_RESOURCE_ATTRIBUTES`).

### Reserved keys

`observability.prometheus.serviceMonitor.enabled` is reserved and currently emits nothing — it will become functional if upstream Nocturne adds a `/metrics` endpoint. No `PrometheusRule` or Grafana dashboard ConfigMap is shipped because there are no stable scrape-derived metric names yet.

## Roadmap

The chart is intentionally minimal in v0. Planned for v1:

- [x] README + NOTES.txt
- [x] HPA, PodDisruptionBudget toggles
- [x] CI: `helm lint` + `kubeconform` + SHA256 drift check on `bootstrap-roles.sql`
- [x] OTLP observability env wiring
- [x] Bundled-Postgres quickstart via Bitnami `postgresql` subchart with auto-bootstrap
- [x] Distribution: OCI publish to `oci://ghcr.io/nightscout/charts/nocturne`
- [ ] NetworkPolicy toggle
- [ ] `values.schema.json` for editor autocomplete

## Known limitations / things to verify

- **Web image's `PUBLIC_API_URL` is baked at build time** to `http://localhost:1612`. The chart sets `NOCTURNE_API_URL` (read at runtime by `server.js`), but client-side fetches may use the baked URL. Needs in-cluster verification before the chart can be called production-ready.
- **Web container has no documented HTTP health endpoint** — TCP probe used for now.
- `containerSecurityContext.readOnlyRootFilesystem: false` — both containers may tolerate `true` but this hasn't been verified.
- `bootstrap-roles.sql` lives in two places (`docs/postgres/` and `deploy/helm/nocturne/files/`). Drift is not yet enforced by CI.
- The docstring in `docs/postgres/bootstrap-roles.sql` references env var names (`ConnectionStrings__NocturneDb`, `ConnectionStrings__NocturneDbMigrator`) that do not match the actual code (`ConnectionStrings__nocturne-postgres[-migrator]`). Documentation fix tracked separately.

### Open question — GitOps BYO Secret

When `postgresql.auth.existingSecret` is set, Bitnami expects keys `postgres-password`, `password`, `replication-password`. The chart additionally needs `app-password`, `migrator-password`, `web-password` for the three Nocturne roles. The current expectation is that a single Secret holds all six keys; this is not yet validated against a real Argo deployment.
