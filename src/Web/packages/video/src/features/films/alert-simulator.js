/** Rules and firings quoted here come from src/fixtures/features/alert-simulator.json; re-capturing means re-reading them. */
const GRID = '[data-slot="card-content"] .grid';
const LIST = '[data-slot="card-content"] .max-h-72';

export default {
  intro: { title: "Alert simulator", line: "Try your alert rules on yesterday's glucose before you rely on them." },
  scenes: [
    {
      selector: GRID,
      fill: 0.8,
      heading: "Replay a day",
      beats: [
        {
          // A seek pauses the replay's own playback, so every frame from here shows the same moment.
          do: [{ click: '[data-testid="event-tick"]:last-of-type', live: '[data-testid="playback-tick-strip"]' }],
          note: "The simulator runs your rules over the last 24 hours of real readings. Nothing is sent to anyone.",
        },
        {
          element: '[data-testid="rule-sidebar"]',
          at: [0.5, 0.12],
          note: "These are your rules. This sample has four: Urgent Low, Low, High and Signal Loss.",
        },
      ],
    },
    {
      selector: LIST,
      fill: 0.7,
      heading: "What would have fired",
      beats: [
        {
          element: ":scope > :first-child",
          at: [0.3, 0.5],
          note: "Each alert the rules would have raised is listed with its time.",
        },
      ],
    },
    {
      selector: GRID,
      fill: 0.8,
      heading: "What each rule checks",
      beats: [
        {
          do: [{ click: '[data-slot="collapsible-trigger"]', text: "Urgent Low" }],
          note: "Open a rule to see what it checks. Change a rule in its editor, then replay the day here before you rely on it.",
        },
      ],
    },
    {
      selector: LIST,
      fill: 0.7,
      heading: "Before you rely on it",
      beats: [
        {
          element: ":scope > :first-child",
          at: [0.3, 0.5],
          note: "Each alert shows its level and its time, so you can see if a rule fires too often, or too late.",
        },
        {
          element: ":scope > :last-child",
          at: [0.3, 0.5],
          note: "It replays readings only. Snoozes and other things a person did at the time cannot be replayed.",
        },
      ],
    },
  ],
};
