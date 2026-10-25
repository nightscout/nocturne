/** The reading quoted here comes from src/fixtures/features/clocks.json; re-capturing means re-reading it. */
// Actions run in the live app too, where the film's page wrapper does not exist.
const FACE = ".z-10.flex-col.items-center";
const ADD = '[data-slot="popover-content"] button';

export default {
  intro: { title: "Clocks", line: "Build a glucose clock for the screen by your bed." },
  scenes: [
    {
      selector: FACE,
      fill: 0.5,
      heading: "Start from the reading",
      beats: [
        {
          element: ":scope > :nth-child(2)",
          note: "A new clock shows your reading, its arrow, the change and how long ago it came in.",
        },
        {
          do: [{ click: `${FACE} > [data-slot="popover-trigger"]` }],
          note: "The plus buttons add a row. You can add glucose, loop, tracker and display elements.",
        },
        {
          do: [{ click: ADD, text: "Time" }],
          note: "Add the time. It goes in the row beside the reading, so the clock tells the time too.",
        },
      ],
    },
    {
      selector: '[data-feature-page="0"] .w-80.border-l .space-y-4:has(> h5)',
      fill: 0.8,
      heading: "Style each element",
      beats: [
        {
          do: [{ click: "button", text: "mg/dL" }],
          note: "Click any element to style it: here, the reading. Set its size, font, weight and colour.",
        },
        {
          do: [{ click: '[data-slot="select-trigger"]', text: "Dynamic" }],
          note: "Its colour can follow your glucose, so you can read it from across the room.",
        },
        {
          do: [{ click: '[role="option"]', text: "Dynamic" }],
          note: "Or pick a muted or custom colour for a dark bedroom.",
        },
      ],
    },
    {
      selector: 'header:has(button[title="Copy link"])',
      fill: 0.9,
      heading: "Put it on a screen",
      beats: [
        {
          do: [{ click: "header button:has(svg.lucide-save)" }],
          note: "Save it.",
        },
        {
          element: 'button[title="Copy link"]',
          note: "Then copy its link. It opens full screen with no login, on a phone, a tablet or an old monitor.",
        },
      ],
    },
  ],
};
