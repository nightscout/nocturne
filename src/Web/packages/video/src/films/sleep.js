/** Figures quoted here come from src/fixtures/sleep.json; re-capturing means re-reading them. */
export default {
  intro: { title: "Sleep & Overnight", line: "Your sleep, and what your glucose does while you sleep." },
  scenes: [
    {
      selector: 'div[data-slot="card"]:has(dl) + div[data-slot="card"]:has(dl)',
      heading: "Your glucose overnight",
      beats: [
        { element: "dl > div:nth-child(1)", note: "Overnight, your glucose is in range 97% of the time." },
        { element: "dl > div:nth-child(2)", note: "Before you wake, it rises by 22 mg/dL on average." },
      ],
    },
    {
      selector: 'button[aria-label="Change date range"]',
      fill: 0.1,
      heading: "Look further back",
      beats: [
        {
          do: [
            { click: 'button[aria-label="Change date range"]' },
            { click: "button", text: "30 Days" },
            { click: "button", text: "Apply Filters" },
          ],
          note: "To see more nights, open the date range, pick 30 Days, and apply it.",
        },
      ],
    },
    {
      card: "Weekly Breakdown",
      heading: "Week by week",
      beats: [
        {
          element: "div.divide-y > div:nth-child(1) > div:first-child",
          note: "Now the report covers 31 nights, and you tracked 25. Each row is one week of them.",
        },
        {
          element: "div.divide-y > div:nth-child(4) > div:first-child",
          note: "From Sept 28 to Oct 4 you tracked all 7 nights, with 94% time in range and 2 lows.",
        },
        {
          element: 'div.divide-y > div:nth-child(4) [aria-label^="Nights in"]',
          note: "Each letter is one night. Select it to open that night's full report.",
        },
      ],
    },
    {
      card: "Sleep Composition",
      heading: "Sleep stages, night by night",
      beats: [
        { at: [0.916, 0.5], note: "On Thursday, Oct 22, you slept 8h 29m. 2h 33m of it was deep sleep." },
        {
          element: "div.space-y-4 > div:first-child",
          note: "30% of your sleep is deep. That is above the typical range for adults aged 18 to 39.",
        },
      ],
    },
  ],
};
