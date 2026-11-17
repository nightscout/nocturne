/** The sizes and their names are the widget's own (docs: windows-widget; src/Widgets/Nocturne.Widget.Windows11/Templates); the reading comes from src/fixtures/features/clocks.json. */
const BOARD = '[data-film="board"]';

export default {
  intro: { title: "Windows widget", line: "Press Win + W, and your glucose is there." },
  scenes: [
    {
      selector: BOARD,
      fill: 0.8,
      heading: "Add it to the board",
      beats: [
        {
          do: [{ click: "button", text: "Add widgets" }],
          note: "Nocturne has a widget for Windows 11. Open the Widgets Board with Win + W, then add widgets.",
        },
        {
          do: [{ click: '[data-size="large"] button' }],
          note: "It comes in three sizes. Pin Nocturne Dashboard for the full picture.",
        },
        {
          do: [{ click: '[data-size="small"] button' }, { click: "button", text: "Done" }],
          note: "And Nocturne (Small) for a quick number. You can pin more than one.",
        },
      ],
    },
    {
      selector: '[data-film="pinned"]',
      fill: 0.8,
      heading: "Always one key away",
      beats: [
        {
          element: '[data-film="large"] .text-8xl',
          note: "The reading, its trend and the change, with insulin and carbs on board.",
        },
        {
          element: '[data-film="chart"]',
          at: [0.9, 0.5],
          note: "It refreshes on its own, and it reads from any Nocturne server you can sign in to.",
        },
      ],
    },
  ],
};
