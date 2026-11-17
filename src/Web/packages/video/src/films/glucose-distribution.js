/** Figures quoted here come from src/fixtures/glucose-distribution.json; re-capturing means re-reading them. */
export default {
  intro: { title: "Glucose Distribution", line: "How your time divides across glucose ranges." },
  scenes: [
    {
      card: "Distribution Chart",
      heading: "Time in each range",
      beats: [
        {
          at: [0.71, 0.5],
          note: "This ring shows how your time divides across glucose ranges. The green part is the tight range, 70 to 140 mg/dL: 65% of your time.",
        },
        { at: [0.25, 0.42], note: "The teal part is the rest of the target range, 140 to 180 mg/dL: 16% of your time." },
        { do: [{ click: "button", text: "Hide Tight Range" }], note: "To see the whole target range as one part, select Hide Tight Range." },
        {
          at: [0.71, 0.5],
          note: "Now one part shows all your time from 70 to 180 mg/dL: 81%. The consensus target is more than 70%.",
        },
      ],
    },
    {
      card: "Hourly Distribution",
      heading: "Range by hour of the day",
      beats: [
        {
          path: [[0.06, 0.68], [0.3, 0.68]],
          note: "Each column is one hour of the day, split by range. From 1am to 6am, almost all of your time is in range.",
        },
        { at: [0.39, 0.3], note: "9am is your hardest hour. 33.3% of it is from 180 to 250 mg/dL, and 12.5% is above 250." },
      ],
    },
  ],
};
