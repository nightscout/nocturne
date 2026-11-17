/** Figures quoted here come from src/fixtures/site-change-impact.json; re-capturing means re-reading them. */
const HEADER = "[data-report-root] > div.sticky";

export default {
  intro: { title: "Site Change Impact", line: "How your glucose moves in the hours around a pump site change." },
  scenes: [
    {
      card: "Glucose Pattern Around Site Changes",
      heading: "Glucose around a site change",
      beats: [
        {
          at: [0.34, 0.2],
          note: "The vertical line is the site change. The chart lays your 10 site changes on top of each other.",
        },
        { at: [0.395, 0.3], note: "After the change it rises. Ninety minutes later, the median is 205 mg/dL." },
      ],
    },
    {
      selector: HEADER,
      heading: "Use more site changes",
      beats: [
        { do: [{ click: 'button[aria-label="Change date range"]' }], note: "To include more site changes, change the date range." },
        { do: [{ click: "button", text: "90 Days" }], note: "Select 90 Days." },
        { do: [{ click: "button", text: "Apply Filters" }], note: "Then select Apply Filters." },
      ],
    },
    {
      selector: "div.space-y-4 > div:nth-child(2)",
      heading: "Ninety days of site changes",
      beats: [
        {
          element: "div.items-center.gap-4.text-sm > div:first-child",
          at: [0.55, 1.5],
          do: [{ click: "div.items-center.gap-4.text-sm > div:first-child span.font-medium" }],
          note: "Now the report uses 30 site changes from 90 days, one every 3 days.",
        },
      ],
    },
    {
      card: "Glucose Pattern Around Site Changes",
      heading: "The pattern over 90 days",
      beats: [
        { at: [0.38, 0.3], note: "The rise after a change is now smaller. The median peaks at 185 mg/dL, one hour after." },
        { element: "dl", at: [0.12, 0.85], note: "On average, glucose is 133 mg/dL before a change and 143 after. Time in range falls from 84% to 74%." },
      ],
    },
    {
      card: "Understanding This Report",
      heading: "What to look for",
      beats: [
        {
          element: ".border-t",
          at: [0.55, 0.8],
          note: "A steady rise before your site changes is a pattern to discuss with your care team.",
        },
      ],
    },
  ],
};
