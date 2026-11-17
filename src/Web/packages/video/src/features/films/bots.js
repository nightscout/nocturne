/** The cards are the bot's own (src/Web/packages/bot/src/cards); the /bg reading comes from src/fixtures/features/clocks.json. */
export default {
  intro: { title: "Chat bot", line: "Your glucose and your alerts, in the chat app your family already uses." },
  scenes: [
    {
      selector: '[data-film="platforms"]',
      fill: 0.7,
      heading: "Four chat apps",
      beats: [
        {
          element: "span",
          at: [0.5, 0.5],
          note: "Nocturne's bot works in Discord, Telegram, Slack and WhatsApp. Each person links their chat account with /connect.",
        },
      ],
    },
    {
      selector: '[data-film="alert"]',
      fill: 0.6,
      heading: "An alert in the family chat",
      beats: [
        {
          element: ".text-lg + .text-lg",
          at: [0.3, 0.5],
          note: "Send an alert rule to a chat, and everyone in it sees the low as it happens.",
        },
        {
          do: [{ click: "button", text: "Acknowledge" }],
          note: "Anyone in the chat can tap Acknowledge.",
        },
      ],
    },
    {
      selector: '[data-film="chat"]',
      fill: 0.8,
      heading: "Everyone knows",
      beats: [
        {
          element: '[data-film="acknowledged"] .text-lg + .text-lg',
          at: [0.4, 0.5],
          note: "The bot tells everyone who dealt with it, so nobody else has to.",
        },
        {
          do: [{ type: "#chat-input", text: "/bg" }, { click: 'button[aria-label="Send"]' }],
          note: "Later, Mum wants to check in. She types /bg.",
        },
      ],
    },
    {
      selector: '[data-film="reading"]',
      fill: 0.6,
      heading: "Ask for the latest",
      beats: [
        {
          element: ".grid",
          at: [0.2, 0.6],
          note: "The bot replies with the latest reading, its trend, and how long ago it came in.",
        },
      ],
    },
  ],
};
