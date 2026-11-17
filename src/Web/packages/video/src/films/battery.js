/** Figures quoted here come from src/fixtures/battery.json; re-capturing means re-reading them. */
const HEADER = "[data-report-root] > div.sticky";

export default {
  intro: { title: "Battery", line: "How the batteries of your devices charge and run down." },
  scenes: [
    {
      card: "Trio",
      heading: "One card for each device",
      beats: [
        { element: '[data-slot="badge"]', at: [0.3, 1.4], note: "Each device that sends status reports gets a card. The Trio battery is at 80% now." },
        {
          element: '[data-slot="card-content"] > :nth-child(5)',
          at: [0.3, 0.55],
          note: "In the last 7 days it charged 6 times. After each charge, the battery lasted 16h 30m.",
        },
      ],
    },
    {
      selector: HEADER,
      heading: "Look further back",
      beats: [
        { do: [{ click: 'button[aria-label="Change date range"]' }], note: "One week shows only a few charges. To see more, change the date range." },
        { do: [{ click: "button", text: "30 Days" }], note: "Select 30 Days." },
        { do: [{ click: "button", text: "Apply Filters" }], note: "Then select Apply Filters." },
      ],
    },
    {
      card: "Trio",
      heading: "Thirty days of charging",
      beats: [
        {
          element: '[data-slot="card-content"] > :nth-child(5)',
          at: [0.3, 0.55],
          note: "Over 30 days the Trio battery charged 29 times, about once a day. Each charge lasted 16h 30m.",
        },
      ],
    },
    {
      card: "Trio/pump",
      heading: "What the longer range shows",
      beats: [
        {
          element: '[data-slot="card-content"] > :nth-child(3)',
          at: [0.9, 0.5],
          note: "The pump battery went as low as 7% in these 30 days.",
        },
        {
          element: '[data-slot="card-content"] > :nth-child(7)',
          at: [0.35, 0.92],
          note: "It was below 30% for 17.3% of the time. In the last 7 days alone, it never went below 53%.",
        },
      ],
    },
  ],
};
