/** Figures quoted here come from src/fixtures/steps.json; re-capturing means re-reading them. */
export default {
  intro: { title: "Step Count", line: "Your steps through each day, with your glucose on the same line." },
  scenes: [
    {
      selector: 'div[data-slot="card"]:has(dl)',
      heading: "Your steps in numbers",
      beats: [{ element: "dl > div:nth-child(2)", note: "You took 119,660 steps in these 14 days: 8,547 on an average day." }],
    },
    {
      card: "Step Count Actogram",
      focus: [5, 6, 7],
      heading: "Your most active day",
      beats: [
        {
          chart: 6,
          at: [0.3, 0.3],
          note: "Each row is one day. On Oct 18 you took 14,583 steps. The line is your glucose: it peaks at 301 mg/dL at 2:24 PM.",
        },
        { chart: 6, at: [0.345, 0.85], note: "By 4:33 PM it falls to 47 mg/dL, below range." },
        {
          chart: 6,
          at: [0.36, 0.85],
          note: "Each grey bar is the steps in a short time. At 5:16 PM a walk adds 4,466 steps, and your glucose is back in range.",
        },
      ],
    },
    {
      card: "Step Count Actogram",
      heading: "Go back a week",
      beats: [
        {
          do: [{ click: 'button[aria-label="Next week"]' }],
          note: "The arrows move the rows a week at a time. The down arrow shows the week before.",
        },
      ],
    },
    {
      card: "Step Count Actogram",
      focus: [5, 6, 7],
      heading: "The Sunday before",
      beats: [
        {
          chart: 6,
          at: [0.35, 0.85],
          note: "One week before, Oct 11 has 14,501 steps. Again a long walk comes late in the afternoon: 4,427 steps at 4:47 PM.",
        },
      ],
    },
  ],
};
