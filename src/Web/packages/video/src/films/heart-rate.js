/** Figures quoted here come from src/fixtures/heart-rate.json; re-capturing means re-reading them. */
export default {
  intro: { title: "Heart Rate", line: "Your heart rate through each day, with your glucose on the same line." },
  scenes: [
    {
      selector: 'div[data-slot="card"]:has(dl)',
      heading: "Your heart rate in numbers",
      beats: [
        { element: "dl > div:nth-child(1)", note: "Your average heart rate over these 14 days is 67 bpm." },
        { element: "dl > div:nth-child(2)", note: "Your resting estimate is 53 bpm. Only one reading in ten is lower." },
      ],
    },
    {
      card: "Heart Rate Actogram",
      focus: [5, 6, 7],
      heading: "One row for each day",
      beats: [
        { chart: 6, at: [0.0625, 0.8], note: "The blue dots are your heart rate. At night it stays low: 52 bpm at 3 AM on Oct 18." },
        {
          chart: 6,
          at: [0.366, 0.12],
          note: "At 5:34 PM it climbs to 150 bpm, your highest. Your glucose stays in range, at 92 mg/dL.",
        },
      ],
    },
    {
      card: "Heart Rate Actogram",
      heading: "Go back a week",
      beats: [
        {
          do: [{ click: 'button[aria-label="Next week"]' }],
          note: "The arrows move the rows a week at a time. The down arrow shows the week before.",
        },
      ],
    },
    {
      card: "Heart Rate Actogram",
      focus: [5, 6, 7],
      heading: "The same hour, a week before",
      beats: [
        {
          chart: 6,
          at: [0.357, 0.15],
          note: "Now Oct 11 sits where Oct 18 was. A week before, your heart rate climbs to 148 bpm at almost the same time.",
        },
      ],
    },
  ],
};
