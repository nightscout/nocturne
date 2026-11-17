/** Figures quoted here come from src/fixtures/data-quality.json; re-capturing means re-reading them. */
const STRIP = '[data-slot="card"]:has(> dl)';

export default {
  intro: { title: "Data Quality", line: "Find readings that may come from the sensor, not from your glucose." },
  scenes: [
    {
      selector: STRIP,
      heading: "Compression lows to review",
      beats: [
        {
          element: "dl > div:first-child",
          at: [0.3, 0.75],
          note: "A compression low is a false low reading when you sleep on the sensor. Possible ones wait here for your review. Now there are none.",
        },
      ],
    },
    {
      card: "Run Detection",
      heading: "Scan your nights",
      beats: [
        {
          do: [{ type: "#start-date", text: "2026-10-22" }],
          note: "To scan a night for compression lows, pick its date. Add an end date to scan more nights.",
        },
        { do: [{ click: "button", text: "Run Detection" }], note: "Then select Run Detection. A notice shows how many wait for review." },
      ],
    },
    {
      selector: STRIP,
      heading: "Review what it found",
      beats: [{ element: "dl > div:first-child", at: [0.3, 0.75], note: "It found 1 possible compression low. It now waits here for your review." }],
    },
    {
      selector: '[data-slot="card"]:has(a[href$="compression-lows"])',
      heading: "Accept or dismiss",
      beats: [
        {
          element: "p.font-medium",
          at: [0.5, 2.6],
          note: "For each one, accept it as a false low, or dismiss it as a real low. Your reports still include these readings.",
        },
      ],
    },
  ],
};
