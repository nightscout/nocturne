/** Figures quoted here come from src/fixtures/basal-analysis.json; re-capturing means re-reading them. */
const SHEET = '[role="dialog"] button';
const STRIP = 'div[data-slot="card"]:has(> dl)';
const FIGURE = (n) => `dl > div:nth-child(${n})`;

export default {
  intro: { title: "Basal Rate Analysis", line: "How your background insulin changes through the day." },
  scenes: [
    {
      card: "Basal Rate Percentile Chart",
      heading: "Basal rate by hour",
      beats: [
        { at: [0.44, 0.53], note: "The line is your median basal rate at each hour. Most hours sit between 1.2 and 1.4 U/hr." },
        { at: [0.69, 0.53], note: "At 4pm the median drops to 0.4 U/hr: at this hour your loop often lowers your basal." },
        { path: [[0.45, 0.66], [0.72, 0.66]], note: "Where the shaded bands are wide, the delivered rate changed more at that hour." },
      ],
    },
    {
      selector: STRIP,
      fill: 0.96,
      heading: "How often your loop changes it",
      beats: [
        { element: FIGURE(4), note: "Over 14 days, your loop raised your basal 343 times and lowered it only 93 times." },
      ],
    },
    {
      selector: "div.sticky",
      fill: 0.9,
      heading: "Only the latest week",
      beats: [
        { do: [{ click: 'button[aria-label="Change date range"]' }], note: "Is this still true now? Open the date range at the top of the report." },
        { do: [{ click: SHEET, text: "7 Days" }], note: "Select 7 Days to look at the latest week only." },
        { do: [{ click: SHEET, text: "Apply Filters" }], note: "Then select Apply Filters. The report loads again for those 7 days." },
      ],
    },
    {
      selector: STRIP,
      fill: 0.96,
      heading: "The last 7 days",
      beats: [
        { element: FIGURE(4), note: "In the last 7 days: 161 times higher and 53 times lower. Your loop still raises your basal about three times as often." },
      ],
    },
  ],
};
