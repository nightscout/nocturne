The state of pumps, loop systems and uploaders, and the age of consumables.

```resources Devices
```

## Good to know

- Site, sensor, reservoir and battery changes are recorded as [Device Events](#nocturne/tag/device-event). Device age is computed from them.
- A legacy Nightscout devicestatus document is stored as up to three snapshots (APS, pump, uploader) that share a `correlationId`.
- The pumps, sensors and meters that a person uses are registered in the [Patient Record](#nocturne/tag/patient-record).
