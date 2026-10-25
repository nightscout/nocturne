/** Figures quoted here come from src/fixtures/ehba1c.json; re-capturing means re-reading them. */
export default {
  intro: { title: "Estimated HbA1c", line: "What a lab HbA1c test would read, estimated for each day." },
  scenes: [
    {
      card: "Estimated HbA1c",
      heading: "How the estimate works",
      beats: [
        {
          element: '[data-slot="card-description"]',
          at: [0.5, 1.05],
          note: "Each day's estimate uses the average glucose of the 90 days before it. The most recent 30 days count for about half.",
        },
        { element: ".text-3xl", at: [0.5, 1.3], note: "Your latest estimate is 6.5%, from Saturday, October 24." },
      ],
    },
    {
      card: "Estimated HbA1c",
      focus: [0],
      heading: "Your estimate over time",
      beats: [
        { path: [[0.25, 0.4], [0.55, 0.38], [0.85, 0.38]], note: "From mid-August it stays between 6.4% and 6.6%." },
      ],
    },
    {
      card: "Lab results",
      heading: "Add a lab result",
      beats: [
        { do: [{ type: "#lab-date", text: "2026-10-10" }], note: "To check the estimate, add a lab HbA1c result. First, enter the date of the blood draw." },
        {
          do: [{ type: "#lab-value", text: "6.8" }, { type: "#lab-note", text: "GP lab" }],
          note: "Then enter the result in percent, and a note if you want one.",
        },
        { do: [{ click: "button", text: "Add lab result" }], note: "Select Add lab result. The result goes into your list." },
      ],
    },
    {
      card: "Estimated HbA1c",
      focus: [0],
      heading: "Compare it on the chart",
      beats: [
        {
          on: 'polygon[data-testid="lab-marker"]',
          note: "Your lab result shows as a triangle on the chart. On October 10 the lab read 6.8%, and the estimate was 6.6%.",
        },
      ],
    },
  ],
};
