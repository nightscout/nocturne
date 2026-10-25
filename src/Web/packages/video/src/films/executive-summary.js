/** Figures quoted here come from src/fixtures/executive-summary.json; re-capturing means re-reading them. */
const STRIP = 'div[data-slot="card"]:has(> dl)';
const FIGURE = (n) => `dl > div:nth-child(${n})`;
const SHEET = '[role="dialog"] button';

export default {
  intro: { title: "Executive Summary", line: "Your most important numbers on one page, for the period you choose." },
  scenes: [
    {
      selector: STRIP,
      fill: 0.96,
      heading: "The key numbers",
      beats: [
        { element: FIGURE(1), note: "The report opens on the last 14 days. Your time in range is 81%, and its target is 70% or more." },
      ],
    },
    {
      selector: "div.sticky",
      fill: 0.9,
      heading: "Choose the period",
      beats: [
        { do: [{ click: 'button[aria-label="Change date range"]' }], note: "To see a longer time, open the date range at the top of the report." },
        { do: [{ click: SHEET, text: "30 Days" }], note: "Choose 30 days. The calendar shows the days it covers." },
        { do: [{ click: SHEET, text: "Apply Filters" }], note: "Apply, and the whole report loads again." },
      ],
    },
    {
      selector: STRIP,
      fill: 0.96,
      heading: "The same page, for 30 days",
      beats: [
        { element: FIGURE(1), note: "Every figure now covers 30 days. Your time in range is 78%, against 81% over 14 days." },
      ],
    },
    {
      card: "Time in Range",
      heading: "Time in range",
      beats: [
        {
          element: ".grid-cols-3",
          note: "On an average day, that is 18 hours 46 minutes in range, 57 minutes low and 4 hours 17 minutes high.",
        },
      ],
    },
    {
      card: "What Your Data is Telling Us",
      heading: "What your data tells you",
      beats: [
        {
          element: '[data-slot="card-content"] > div:nth-child(2)',
          at: [0.3, 0.7],
          note: "Over 30 days, the report finds two new patterns: time very low and time very high are above their targets.",
        },
      ],
    },
  ],
};
