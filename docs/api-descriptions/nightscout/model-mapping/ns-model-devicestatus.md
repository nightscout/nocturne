---
displayName: Device Status
standalone: true
---
How legacy **devicestatus** maps to Nocturne's v4 device model.

A Nightscout devicestatus document bundles several independent status objects (`openaps`/`loop`, `pump`, `uploader`) into one blob. Nocturne decomposes it into separate v4 snapshots — **ApsSnapshot**, **PumpSnapshot**, and **UploaderSnapshot** — sharing a `CorrelationId` so the original document can be reassembled on read. Keeping them separate lets each evolve and be queried on its own.
