/** Figures quoted here come from src/fixtures/readings.json; re-capturing means re-reading them. */
export default {
  intro: { title: "Readings", line: "Every glucose reading, with your insulin and carbs, over the days you choose." },
  scenes: [
    {
      card: "Blood Glucose",
      focus: [0],
      fill: 0.98,
      heading: "A week of readings",
      beats: [
        { at: [0.5552, 0.17], note: "Each dot is one reading, coloured by range. The highest is 388 mg/dL, on Wednesday at 10:20pm." },
      ],
    },
    {
      card: "Blood Glucose",
      heading: "Choose the days",
      beats: [
        {
          do: [{ click: 'button[aria-label="Change date range"]' }, { click: "button", text: "3 Days" }],
          note: "To look closer, open the date range and choose 3 days.",
        },
        { do: [{ click: "button", text: "Apply Filters" }], note: "Apply it, and the chart shows only those days." },
      ],
    },
    {
      card: "Blood Glucose",
      focus: [0],
      fill: 0.98,
      heading: "Three days, in detail",
      beats: [
        { at: [0.589, 0.82], note: "On Friday at 7:15pm you ate 78g of carbs for dinner." },
        { at: [0.591, 0.76], note: "Then you gave a bolus of 7.8U." },
        { at: [0.609, 0.54], note: "Your glucose went no higher than 139 mg/dL, at 8:50pm." },
      ],
    },
  ],
};
