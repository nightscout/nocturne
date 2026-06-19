---
displayName: Treatments
standalone: true
---
How legacy **treatments** map to Nocturne's v4 treatment model.

A Nightscout treatment is a single polymorphic blob whose `eventType` determines which fields are meaningful. On write, Nocturne routes each treatment by `eventType` into focused v4 domain models (boluses, carb intakes, temp basals, BG checks, notes, site/sensor changes, …); sibling rows that originated from one treatment stay linked by a shared `CorrelationId`. Reads recombine those rows back into the legacy treatment shape.
