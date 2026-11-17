/** Figures quoted here come from src/fixtures/treatments.json; re-capturing means re-reading them. */
const FIGURE = (n) => `:scope > div:nth-child(${n})`;

export default {
  intro: { title: "Treatment Log", line: "Every dose, meal and device event, in one list you can add to." },
  scenes: [
    {
      selector: "dl.bg-border",
      fill: 0.98,
      heading: "The week in numbers",
      beats: [
        { element: `${FIGURE(1)} dd`, at: [0.1, 0.5], note: "The week has 365 records: 280 insulin doses, 45 carb entries and 28 notes." },
        { element: `${FIGURE(3)} dd`, at: [0.15, 0.5], note: "You logged 1444g of carbs in 45 meals." },
      ],
    },
    {
      selector: '[data-slot="card"]:has(#search)',
      heading: "Add a record",
      beats: [
        {
          do: [{ click: "button", text: "Add Treatment" }, { click: '[role="menuitem"]', text: "Carbs" }],
          note: "To log a meal, choose Add Treatment, then Carbs.",
        },
        { do: [{ type: "#carbs", text: "45" }], note: "Type the grams of carbs: here, 45g." },
        { do: [{ click: "button", text: "Create Record" }], note: "Then save it with Create Record." },
      ],
    },
    {
      selector: "tbody tr:nth-child(1)",
      fill: 0.98,
      heading: "In the log",
      beats: [{ element: "td:nth-child(4)", note: "The new entry of 45g is now at the top of the log." }],
    },
    {
      selector: "dl.bg-border",
      fill: 0.98,
      heading: "The totals",
      beats: [{ element: `${FIGURE(3)} dd`, at: [0.15, 0.5], note: "The totals include it: 366 records, and 1489g of carbs in 46 meals." }],
    },
  ],
};
