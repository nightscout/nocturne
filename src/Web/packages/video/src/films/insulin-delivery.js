/** Figures quoted here come from src/fixtures/insulin-delivery.json; re-capturing means re-reading them. */
const SHEET = '[role="dialog"] button';
const STRIP = 'div[data-slot="card"]:has(> dl)';
const FIGURE = (n) => `dl > div:nth-child(${n})`;

export default {
  intro: { title: "Insulin Delivery", line: "Your basal and bolus insulin, over 30 days." },
  scenes: [
    {
      card: "Daily Basal/Bolus Breakdown",
      heading: "Insulin, day by day",
      beats: [
        { at: [0.2, 0.6], note: "Each bar is one day. The lower part of the bar is basal insulin. The upper part is bolus insulin." },
        {
          path: [[0.2, 0.6], [0.85, 0.6]],
          note: "On most days you take 30 to 50 units. Over 30 days, the average is 39.9 units a day: 55% basal and 45% bolus.",
        },
      ],
    },
    {
      card: "Hourly Insulin Delivery",
      heading: "Insulin by hour of the day",
      beats: [
        { element: FIGURE(3), at: [0.12, 0.6], note: "This card splits your insulin by time of day. Evening and night get the most: 14.3 units, 36% of the day." },
      ],
    },
    {
      selector: "div.sticky",
      fill: 0.9,
      heading: "Compare the latest week",
      beats: [
        { do: [{ click: 'button[aria-label="Change date range"]' }], note: "Is your last week like the rest of the month? Open the date range." },
        { do: [{ click: SHEET, text: "7 Days" }], note: "Select 7 Days. The calendar marks the days it covers." },
        { do: [{ click: SHEET, text: "Apply Filters" }], note: "Then select Apply Filters. The report loads again for those 7 days." },
      ],
    },
    {
      selector: STRIP,
      fill: 0.96,
      heading: "The last 7 days",
      beats: [
        {
          element: FIGURE(1),
          note: "In the last 7 days your total daily dose is 37.5 units, against 39.9 over 30 days. The split stays near 56% basal and 44% bolus.",
        },
      ],
    },
  ],
};
