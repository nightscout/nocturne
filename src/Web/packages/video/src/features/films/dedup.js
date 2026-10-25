/** What deduplication does, as quoted here, is the app's own explanation in its Deduplicate Records dialog. */
export default {
  intro: { title: "Data quality", line: "Two apps upload the same thing. You see it once." },
  scenes: [
    {
      card: "Data Maintenance",
      heading: "Duplicates become one record",
      beats: [
        {
          element: '[data-testid="deduplicate-records"]',
          at: [0.3, 0.3],
          note: "When your loop and a second app both upload the same bolus, carbs or reading, Nocturne links them as one event.",
        },
        {
          do: [{ click: "button", text: "Run Deduplication" }],
          note: "It does this as data arrives. Run Deduplication checks the records you already have.",
        },
        {
          do: [{ click: '[role="dialog"] li', text: "30 seconds" }],
          note: "Records within 30 seconds of each other, with matching values, count as the same event.",
        },
        {
          do: [{ click: '[role="dialog"] li', text: "preserved" }],
          note: "Nothing is deleted: the original records stay, and only links are made. It is safe to run again.",
        },
        {
          do: [{ click: '[role="dialog"] button', text: "Close" }],
          note: "Start it, and it runs in the background. You get a notification when it is done.",
        },
      ],
    },
    {
      card: "Compression Low Detection",
      heading: "False lows while you sleep",
      beats: [
        {
          element: '[data-slot="card-content"]',
          at: [0.5, 0.7],
          note: "Sleeping on a sensor can give false low readings. Nocturne looks for them overnight and asks you to review them.",
        },
        {
          element: '[data-slot="card-content"]',
          at: [0.97, 0.15],
          note: "Reviewing keeps a record for you. It does not remove the readings from your reports.",
        },
      ],
    },
  ],
};
