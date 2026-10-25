/** Figures quoted here come from src/fixtures/day-in-review.json; re-capturing means re-reading them. */
const APS = '[data-slot="card-content"] > .grid';

export default {
  intro: { title: "Day in Review", line: "One day in detail: glucose, insulin, carbs and each decision of your pump." },
  scenes: [
    {
      selector: '[class*="@4xl:grid-cols-3"]',
      heading: "The day in numbers",
      beats: [
        { chart: 0, at: [0.35, 0.5], note: "71% of the day's readings are in range. None are low, but 20% are very high." },
      ],
    },
    {
      card: "Blood Glucose",
      focus: [0],
      fill: 0.98,
      heading: "Saturday's glucose",
      beats: [
        { at: [0.368, 0.18], note: "You ate 60g of carbs at 6:55am. By 8:45am your glucose was 345 mg/dL." },
      ],
    },
    {
      selector: '[class="space-y-6 print:hidden"]',
      heading: "Step through the day",
      beats: [
        { element: APS, at: [0.822, 0.22], note: "The card shows the last decision of the pump, at 10:45am. It forecast 40 mg/dL." },
        { do: [{ click: 'button[title^="Go to start"]' }], note: "Move the scrubber to any moment. Here, back to the start of the day." },
        { element: APS, at: [0.562, 0.85], note: "At midnight your glucose was 171 mg/dL. The pump forecast 144 and set 1.40U/hr." },
      ],
    },
    {
      selector: '[class*="@4xl:grid-cols-3"]',
      heading: "Another day",
      beats: [
        { do: [{ click: "button:has(.lucide-chevron-left)" }], note: "To see another day, step back with the arrow." },
        { chart: 0, at: [0.35, 0.5], note: "On Friday, 88.5% of your readings were in range, and 1.2% were low." },
      ],
    },
  ],
};
