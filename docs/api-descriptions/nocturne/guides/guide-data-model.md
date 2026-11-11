---
displayName: Data model
standalone: true
---
The records that Nocturne stores, what each one means, and how they connect.

## The idea

Nightscout stores a few large documents. One treatment document can be a bolus, a meal, a site change or a note, and its `eventType` says which. Nocturne stores one small record type for each kind of fact. A bolus is always a bolus, with the fields of a bolus.

This makes a record easy to validate and to query. A legacy Nightscout document is stored as one or more of these records, and the Nightscout API builds the legacy shape from them when it is read.

<!-- diagram: er-overview.mmd -->

## Every record

Each time-based record starts with the same fields:

| Field | Meaning |
| --- | --- |
| `id` | The identifier that Nocturne assigns. A UUID. |
| `timestamp` | When it happened, in UTC. |
| `mills` | The same time in Unix milliseconds. Read-only. |
| `utcOffset` | The offset of local time, in minutes. |
| `device` | The device that produced the record. |
| `app` | The app that uploaded it. |
| `dataSource` | The system that wrote it, such as a connector or an uploader. |
| `syncIdentifier` | The identifier of the record in that system. With `dataSource`, it makes a retry safe. |
| `correlationId` | Shared by records that describe one event. |
| `legacyId` | The identifier of the Nightscout document that the record came from, if it came from one. |
| `createdAt`, `modifiedAt` | When Nocturne stored and last changed the record. |

## Glucose

| Record | Source | Group |
| --- | --- | --- |
| Sensor glucose | A continuous glucose monitor. | Glucose |
| Meter glucose | A fingerstick meter. | Glucose |
| Calibration | A calibration of a sensor. | Glucose |
| BG check | A value that a person or an app entered. | Glucose |

They are separate because they differ in accuracy, in units and in the device they come from.

<!-- diagram: glucose-source-taxonomy.mmd -->

## Insulin and food

| Record | Meaning | Group |
| --- | --- | --- |
| Bolus | A dose of fast-acting insulin. | Treatments |
| Bolus calculation | What a bolus calculator was given and what it recommended. | Treatments |
| Temp basal | A temporary basal rate from a pump. | Treatments |
| Basal injection | An injection of long-acting insulin. | Treatments |
| Carb intake | Carbohydrates eaten. | Treatments |
| Food | An item in the food list. A carb intake can list the foods in it. | Treatments |
| Note | Free text. | Treatments |

A bolus can refer to the calculation that recommended it, the loop decision that triggered it and the device that delivered it.

<!-- diagram: bolus-anatomy.mmd -->

A meal is not a stored record. It is a view of a carb intake with the boluses and foods that share its `correlationId`.

<!-- diagram: meal-event-view.mmd -->

## Devices

| Record | Meaning | Group |
| --- | --- | --- |
| APS snapshot | What a loop system decided at a moment. | Devices |
| Pump snapshot | The state of a pump at a moment. | Devices |
| Uploader snapshot | The state of the uploading phone or device. | Devices |
| Device event | A site, sensor, reservoir or battery change. | Devices |
| Patient device | A pump, sensor or meter that the person uses. | Health |
| Patient insulin | An insulin that the person uses. | Health |

<!-- diagram: device-insulin-relationships.mmd -->

## States that last

A state span has a start and an end: a pump mode, an override, a temporary target, an active profile, or a period of sleep, exercise, illness or travel. A span with no end is still in effect. See **State Spans** in the Treatments group.

## Therapy settings

A profile has a name, therapy settings and four schedules by time of day: basal rate, carb ratio, sensitivity and target range. Each part is its own record. A profile activation records when a profile was switched on. See **Profiles**.

## Computed data

Statistics, insulin on board, carbs on board, predictions and chart series are not stored records. The API computes them from the records above. See **Analytics**.
