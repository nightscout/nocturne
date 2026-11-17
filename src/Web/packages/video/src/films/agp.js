/** Figures quoted here come from src/fixtures/agp.json; re-capturing means re-reading them. */
const SHEET = '[role="dialog"] button';

export default {
  intro: { title: "Ambulatory Glucose Profile", line: "Your readings from 14 days, shown as one typical day." },
  scenes: [
    {
      card: "Glucose Pattern",
      heading: "The 24-hour glucose pattern",
      beats: [
        { at: [0.14, 0.65], note: "The blue line is your median: the middle reading at each hour, over 14 days." },
        { at: [0.38, 0.4], note: "Your median is highest at 9am: 169 mg/dL, after breakfast." },
        {
          at: [0.59, 0.3],
          note: "The shaded bands show how much your days differ. At 2pm they are widest: from 80 to 276 mg/dL.",
        },
      ],
    },
    {
      selector: "div.sticky",
      fill: 0.9,
      heading: "Look further back",
      beats: [
        { do: [{ click: 'button[aria-label="Change date range"]' }], note: "Is this a real pattern, or two unusual weeks? Open the date range." },
        { do: [{ click: SHEET, text: "30 Days" }], note: "Select 30 Days. The calendar marks the days it covers." },
        { do: [{ click: SHEET, text: "Apply Filters" }], note: "Then select Apply Filters. The whole report loads again for 30 days." },
      ],
    },
    {
      card: "Glucose Pattern",
      heading: "The pattern over 30 days",
      beats: [
        {
          at: [0.55, 0.4],
          note: "Over 30 days the shape stays the same: your glucose rises after breakfast and stays high into the afternoon. At 1pm the median is 166 mg/dL.",
        },
      ],
    },
    {
      card: "Time in Range Distribution",
      heading: "Time in range over 30 days",
      beats: [
        { at: [0.5, 0.82], note: "78% of your readings are in range, 70 to 180 mg/dL. The consensus target is more than 70%." },
      ],
    },
  ],
};
