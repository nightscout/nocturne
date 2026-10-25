/** What the link shares, as quoted here, comes from src/fixtures/features/public-link.json; re-capturing means re-reading it. */
const CARD = '[data-testid="public-access-card"]';

export default {
  intro: { title: "Public link", line: "One link that shows only what you choose." },
  scenes: [
    {
      selector: CARD,
      heading: "Turn it on",
      beats: [
        {
          do: [{ click: '[data-testid="public-access-toggle"]' }],
          note: "The public link is off until you turn it on. Then anyone with the link can view, with no login.",
        },
        {
          element: '[data-testid="public-access-url-redacted"]',
          at: [0.3, 0.5],
          note: "The address is a long random code. It stays hidden on this page until you show or copy it.",
        },
      ],
    },
    {
      selector: `${CARD} .grid`,
      fill: 0.8,
      heading: "Choose what it shows",
      beats: [
        {
          do: [{ click: 'button[aria-label="Treatments"]' }],
          note: "It starts with blood glucose only. Add what else to share: here, treatments.",
        },
        {
          do: [{ click: 'button[aria-label="Device Status"]' }],
          note: "And device status. Heart rate, steps, food and reports stay private.",
        },
      ],
    },
    {
      selector: CARD,
      heading: "And how far back",
      beats: [
        {
          element: '[data-testid="public-access-window"]',
          note: "It shows only the last 24 hours unless you choose all history.",
        },
        {
          element: "strong",
          note: "The card says in plain words what anyone with the link can see, and what they cannot.",
        },
      ],
    },
  ],
};
