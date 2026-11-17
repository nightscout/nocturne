/** Labels quoted here come from src/fixtures/features/guest-links.json; re-capturing means re-reading them. */
const GUEST = '[data-testid="guest-links"]';

export default {
  intro: { title: "Guest links", line: "A one-time view of your data, for an appointment." },
  scenes: [
    {
      selector: GUEST,
      fill: 0.8,
      heading: "Make a guest link",
      beats: [
        {
          do: [{ click: "button", text: "Create Guest Link" }],
          note: "Seeing a specialist? Make them a guest link. They can look at your data with no account.",
        },
        {
          do: [{ type: 'input[placeholder^="e.g., Dr. Smith"]', text: "Dr. Smith" }],
          note: "Say who it is for.",
        },
        {
          do: [{ click: "button", text: "Create Link" }],
          note: "Create it.",
        },
      ],
    },
    {
      // The link beside the code is built from the page's own address, which in a render is the
      // renderer's; the code is the same in the app.
      selector: `${GUEST} div:has(> div > .font-mono.tracking-widest)`,
      fill: 0.5,
      heading: "A code or a link",
      beats: [
        {
          element: ".font-mono",
          note: "Nocturne makes a short code and a link. Send the link, or read the code out over the phone.",
        },
      ],
    },
    {
      selector: GUEST,
      fill: 0.8,
      heading: "Gone in 48 hours",
      beats: [
        {
          do: [{ click: "button", text: "Done" }],
          note: "The code works once. The first device that enters it can look for 48 hours, then it stops on its own.",
        },
        {
          element: '[data-slot="card"]',
          at: [0.9, 0.5],
          note: "Nothing to remember to turn off, and you can revoke it sooner.",
        },
      ],
    },
  ],
};
