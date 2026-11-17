/** Figures quoted here come from src/fixtures/year-overview.json; re-capturing means re-reading them. */
export default {
  intro: { title: "Year Overview", line: "Every day of your year in one calendar, coloured by your glucose." },
  scenes: [
    {
      selector: "div:has(> div.mb-3 > h2)",
      fill: 0.6,
      heading: "Your year, day by day",
      beats: [
        {
          at: [0.782, 0.62],
          note: "Each square is one day, coloured by your average glucose. Green days are near target: on Oct 17 it was 124 mg/dL.",
        },
        {
          at: [0.688, 0.62],
          note: "Yellow and orange days run higher. On Sep 12 your average was 176 mg/dL.",
        },
      ],
    },
    {
      selector: "div:has(> div.mb-3 > h2)",
      fill: 0.6,
      heading: "Change what the colours show",
      beats: [
        {
          do: [
            { click: 'div[class*="w-[460px]"] [data-slot="select-trigger"]' },
            { click: '[role="option"]', text: "Time in Range" },
          ],
          note: "This menu sets what the colours show. Here you pick Time in Range.",
        },
        { element: "div.mb-3", at: [0.842, 4.69], note: "Now a brighter green means more time in range. Oct 21 is the darkest day: only 53% in range." },
        { element: "div.mb-3", at: [0.8225, 7.46], note: "Oct 17 is one of the brightest, at almost 100%." },
      ],
    },
    {
      selector: "div.mt-4.border-t",
      heading: "Glycemic Risk Index, month by month",
      beats: [
        { element: "dl", note: "Your latest Glycemic Risk Index is 29, in zone B. A lower score means less risk." },
        { at: [0.36, 0.68], note: "Each dot is one month, from July to October. All four sit in zone B." },
      ],
    },
  ],
};
