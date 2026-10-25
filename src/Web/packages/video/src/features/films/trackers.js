/** Tracker ages quoted here come from src/fixtures/features/trackers.json; re-capturing means re-reading them. */
const TABS = '[role="tablist"]';

export default {
  intro: { title: "Trackers", line: "Set up the reminders for your site, sensor and reservoir." },
  scenes: [
    {
      card: "Active Trackers",
      heading: "What you are wearing now",
      beats: [
        {
          element: '[data-slot="card-content"]',
          at: [0.05, 0.13],
          note: "Each tracker counts down to when something is due. This infusion site has 2 days 11 hours left.",
        },
      ],
    },
    {
      selector: TABS,
      fill: 0.6,
      heading: "How long it lasts",
      beats: [
        {
          do: [{ click: '[role="tab"]', text: "Definitions" }, { click: 'button[aria-label="Edit Infusion Site"]' }],
          note: "To set one up, open its definition.",
        },
        {
          do: [{ type: "#lifespan", text: "72" }],
          note: "An infusion site lasts 72 hours. It restarts on its own when your pump reports a site change.",
        },
      ],
    },
    {
      selector: TABS,
      fill: 0.6,
      heading: "When it reminds you",
      beats: [
        {
          do: [{ click: '[role="dialog"] button', text: "Add" }],
          note: "Add a reminder. Each one has a level and a time.",
        },
        {
          do: [
            { click: '[role="dialog"] [data-slot="select-trigger"]', text: "Info" },
            { click: '[role="option"]', text: "Warning" },
          ],
          note: "Make it a warning.",
        },
        {
          do: [{ type: '[role="dialog"] input[placeholder="e.g., 7x24 or -24"]', text: "-6" }],
          note: "Minus 6 means 6 hours before the site is due.",
        },
        {
          do: [{ type: '[role="dialog"] input[placeholder="Message shown when triggered"]', text: "Change your site tonight" }],
          note: "Write what the reminder says.",
        },
        {
          do: [{ click: '[role="dialog"] button', text: "Save" }],
          note: "Save it. When the time comes, the reminder arrives as a notification.",
        },
      ],
    },
  ],
};
