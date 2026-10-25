/** Figures quoted here come from src/fixtures/overview.json; re-capturing means re-reading them. */
export default {
  intro: { title: "Reports", line: "Your glucose at a glance, with every report one step away." },
  scenes: [
    {
      selector: 'div[data-slot="card"]:has(dl)',
      heading: "Your last 14 days",
      beats: [
        { at: [0.6, 0.85], note: "81% of your readings are in range, 70 to 180 mg/dL. The target is at least 70%." },
        {
          element: "dl > div:nth-child(1)",
          at: [0.86, 0.75],
          note: "Next to it are your key figures. Your average glucose is 135 mg/dL, and your eHbA1c is 6.3%.",
        },
      ],
    },
    {
      card: "Typical day",
      heading: "Your typical day",
      beats: [
        { at: [0.14, 0.75], note: "The blue line is your median glucose at each hour of the day." },
        { at: [0.375, 0.6], note: "Your glucose is highest at 9am, with a median of 169 mg/dL." },
      ],
    },
    {
      selector: 'section[aria-label="All reports"]',
      heading: "Every report",
      beats: [
        { element: 'a[href="/reports/agp"]', note: "Each report looks at your data in a different way. The AGP shows your typical day in full." },
        { element: 'a[href="/reports/hourly-stats"]', note: "Hourly Patterns finds the hours of the day that go well, and the hours that are harder." },
      ],
    },
  ],
};
