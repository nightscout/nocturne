/** Figures quoted here come from src/fixtures/idp.json; re-capturing means re-reading them. */
const SHEET = '[role="dialog"] button';

export default {
  intro: { title: "Insulin Dosing Profile", line: "Your insulin and your glucose, side by side." },
  scenes: [
    {
      card: "Glycemic Risk Index",
      heading: "Glycemic Risk Index",
      beats: [
        { on: "circle.fill-foreground", note: "The Glycemic Risk Index puts the risk of lows and highs into one number, from 0 to 100. A lower score is better." },
        { on: "circle.fill-foreground", note: "The dot shows where your lows and highs put you. Over 14 days your score is 25, in Zone B: 21 to 40." },
      ],
    },
    {
      card: "Glucose Pattern",
      focus: [1, 2],
      heading: "Your boluses",
      beats: [
        { chart: 2, at: [0.31, 0.5], note: "Under your glucose pattern and basal rate, this chart shows how many boluses you give in each hour. Most come at breakfast: 3.7 a day at 7am." },
      ],
    },
    {
      selector: "div.sticky",
      fill: 0.9,
      heading: "Ready for a clinic visit",
      beats: [
        { do: [{ click: 'button[aria-label="Change date range"]' }], note: "Before a clinic visit, set the period your care team asks for. Open the date range." },
        { do: [{ click: SHEET, text: "30 Days" }], note: "Select 30 Days. The calendar marks the days it covers." },
        { do: [{ click: SHEET, text: "Apply Filters" }], note: "Then select Apply Filters. The whole report loads again for 30 days." },
      ],
    },
    {
      card: "Glycemic Risk Index",
      heading: "Thirty days",
      beats: [{ on: "circle.fill-foreground", note: "Over 30 days your score is 29. It is still in Zone B, with highs as the larger part of your risk." }],
    },
  ],
};
