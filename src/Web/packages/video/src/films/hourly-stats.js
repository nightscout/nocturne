/** Figures quoted here come from src/fixtures/hourly-stats.json; re-capturing means re-reading them. */
const GLANCE = 'section[aria-labelledby="hourly-glance"] [data-slot="card"]';
const SHEET = '[role="dialog"] button';

export default {
  intro: { title: "Hourly Patterns", line: "Every day laid over one clock, to find your best and hardest hours." },
  scenes: [
    {
      selector: GLANCE,
      heading: "At a glance",
      beats: [
        {
          element: '[data-testid="worst-hours"] li:nth-child(1)',
          at: [0.5, 0.3],
          note: "Over 14 days, 9am to 10am has the least time in range: 54.2%. Its readings go mostly above range.",
        },
      ],
    },
    {
      card: "Hour by hour",
      heading: "Hour by hour",
      beats: [
        { at: [0.41, 0.62], note: "Each bar is one hour. At 9am, 45.8% of your readings are above range." },
        { at: [0.68, 0.8], note: "Lows show at the bottom, in orange and red. At 4pm, 12.2% of your readings are below range." },
      ],
    },
    {
      selector: "div.sticky",
      fill: 0.9,
      heading: "A longer view",
      beats: [
        { do: [{ click: 'button[aria-label="Change date range"]' }], note: "Two weeks can hide the real pattern. To see more, open the date range." },
        { do: [{ click: SHEET, text: "30 Days" }], note: "Choose 30 days." },
        { do: [{ click: SHEET, text: "Apply Filters" }], note: "Apply, and every hour is worked out again from 30 days of data." },
      ],
    },
    {
      selector: GLANCE,
      heading: "The pattern over 30 days",
      beats: [
        {
          element: '[data-testid="worst-hours"] li:nth-child(1)',
          at: [0.5, 0.3],
          note: "Over 30 days the picture changes. 1pm to 2pm now has the least time in range: 52.6%.",
        },
        {
          element: '[data-testid="most-below-hours"] li:nth-child(1)',
          at: [0.5, 0.3],
          note: "And 3pm to 4pm has the most time below range: 13.8%. The afternoon is the hardest part of your day.",
        },
      ],
    },
  ],
};
