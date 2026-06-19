System-level status, diagnostics, and service metadata.

- **Status** — V4 JSON status endpoint with detailed system information.
- **System** — Service health and coordination endpoints.
- **System Events** — Point-in-time system events (alarms, warnings, info).
- **Services** — Metadata about available data sources, connectors, and integrations.
- **Compatibility** — Dashboard data for Nightscout compatibility analysis.
- **Debug** — Query inspection and MongoDB query debugging tools.
- **API Secret** — Legacy API secret management.

> **Footgun:** Debug endpoints expose raw query details and are intended for development use. They should be disabled or restricted in production deployments.
