/** Figures quoted here come from src/fixtures/comparison.json; re-capturing means re-reading them. */
const PICKER = 'div[data-slot="card"]:has(#cmp-preset)';
const TABLE = 'div[data-slot="card"]:has(.divide-y.divide-border)';
const ROW = (n) => `.divide-y > div:nth-child(${n})`;

export default {
  intro: { title: "Comparison", line: "Put two periods side by side, and see what changed." },
  scenes: [
    {
      selector: TABLE,
      heading: "Two weeks against two weeks",
      beats: [
        {
          element: ROW(2),
          at: [0.674, 0.5],
          note: "The report opens on the last 14 days against the 14 days before. Time in range went from 75.9% to 81.1%.",
        },
      ],
    },
    {
      selector: PICKER,
      heading: "Choose the periods",
      beats: [
        { do: [{ click: "#cmp-preset" }], note: "To compare other periods, open the presets." },
        {
          do: [{ click: '[role="option"]', text: "Last 30 vs Prior 30 days" }],
          note: "Choose the last 30 days against the 30 days before.",
        },
      ],
    },
    {
      selector: TABLE,
      heading: "What changed",
      beats: [
        { element: ROW(2), at: [0.674, 0.5], note: "The table updates at once. Over 30 days, time in range went from 72.5% to 78.2%." },
        { element: ROW(5), at: [0.6, 0.5], note: "Your Glycemic Risk Index fell from 37 to 29. A bar to the left means lower, and here lower is better." },
      ],
    },
    {
      card: "Time in Range — stacked comparison",
      heading: "Time in range, side by side",
      beats: [
        { chart: 0, at: [0.5, 0.75], note: "In the earlier 30 days, 72% of your readings were in range." },
        { chart: 1, at: [0.5, 0.75], note: "In the last 30 days, 78% are in range, and the red very high band is smaller." },
      ],
    },
  ],
};
