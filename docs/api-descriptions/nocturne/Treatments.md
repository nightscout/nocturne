Insulin, carbohydrates, food, notes, and the states that last for a period of time.

```resources Treatments
```

## Good to know

- A meal is a view, not a stored record. It is built at read time from the carb intake, boluses and foods that share a `correlationId`.
- A state span has a start and an end. A span with no end is still in effect, and a query for a time range returns each span that overlaps the range.
- A legacy Nightscout treatment is stored as one or more of these record types. The Nightscout API reference shows the mapping.
