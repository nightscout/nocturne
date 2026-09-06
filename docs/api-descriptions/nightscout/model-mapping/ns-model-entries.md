---
displayName: Entries
standalone: true
---
How legacy **entries** map to Nocturne's v4 glucose model.

A Nightscout entry is a single polymorphic document discriminated by `type` (`sgv`, `mbg`, `cal`). Nocturne does not store entries as one table — on write each entry is routed by type into a dedicated v4 glucose table, and reads project those tables back into the legacy entry shape.

| Legacy entry `type` | Nocturne v4 model |
| --- | --- |
| `sgv` (sensor glucose) | **SensorGlucose** |
| `mbg` (meter blood glucose) | **MeterGlucose** / **BGCheck** |
| `cal` (calibration) | **Calibration** |

Splitting by source preserves each reading's true units, fidelity, provenance, and device linkage rather than flattening everything into one numeric blob.
