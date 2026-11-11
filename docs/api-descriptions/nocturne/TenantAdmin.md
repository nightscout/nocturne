---
displayName: Tenant Admin
---
Maintenance of the data of one tenant: migration from Nightscout, deduplication and review of data quality. The OIDC providers and the platform administrator setting in this group apply to the whole instance and need platform administrator rights.

```resources Tenant Admin
```

## Good to know

- Deduplication and migration run as background jobs. The start operation returns at once, and the status operation reports progress.
