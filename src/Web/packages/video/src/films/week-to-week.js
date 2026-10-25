/** Figures quoted here come from src/fixtures/week-to-week.json; re-capturing means re-reading them. */
const CHART = ".rounded-sm.border.p-4";

export default {
  intro: { title: "Week to Week", line: "Each day of the week as its own line, on one 24-hour axis." },
  scenes: [
    {
      selector: CHART,
      heading: "This week",
      beats: [
        { at: [0.2, 0.7], note: "Each line is one day of the week. At 4am the seven lines are close together, from 72 to 110 mg/dL." },
        { at: [0.929, 0.05], note: "The highest point of the week is on Wednesday: 388 mg/dL at 10:20pm." },
      ],
    },
    {
      selector: '[class="@container space-y-6"]',
      heading: "Compare with last week",
      beats: [
        { do: [{ click: "button:has(.lucide-chevron-left)" }], note: "To compare, step back one week with the arrow." },
      ],
    },
    {
      selector: CHART,
      heading: "Last week",
      beats: [
        { at: [0.588, 0.144], note: "Last week, the highest point was 303 mg/dL, on Tuesday at 1:45pm." },
        { at: [0.451, 0.5], note: "Last Saturday never went above 174 mg/dL. This Saturday went up to 345." },
      ],
    },
  ],
};
