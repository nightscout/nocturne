Data that the API computes for dashboards, charts and reports. The operations in this group read data that is stored elsewhere.

```resources Analytics
```

## Good to know

- To draw the glucose chart, start with [Chart Data](#nocturne/tag/chart-data). It returns the readings and everything drawn with them in one call.
- For a widget or a watch face, use [Summary](#nocturne/tag/summary).
- The API is the source of truth for calculations. A client displays these values and does not recompute them.
- Requests over a long span of days are limited. An operation states its limit in its description.
