/** The rule filmed here is the one scripts/capture-features.js arranges for this film. */
const CARD = '[data-testid="alert-smart-snooze-card"]';

export default {
  intro: { title: "Smart snooze", line: "You treated the low. Your phone can stop telling you." },
  scenes: [
    {
      card: "Smart snooze",
      heading: "Turn it on",
      beats: [
        {
          do: [{ click: "#smart-snooze" }],
          note: "This is a low alert. Turn on smart snooze, and a snooze keeps going while you recover.",
        },
      ],
    },
    {
      card: "Smart snooze",
      heading: "Quiet while you recover",
      beats: [
        {
          do: [{ type: "#smart-snooze-min", text: "10" }],
          note: "When you snooze, it extends the snooze 10 minutes at a time.",
        },
        {
          do: [
            { click: `${CARD} [data-slot="select-trigger"]` },
            { click: '[role="option"]', text: "rising" },
          ],
          note: "It extends only while a condition holds. Set the trend to rising: while you come back up, it stays quiet.",
        },
      ],
    },
    {
      card: "Identity",
      heading: "At night",
      beats: [
        {
          do: [{ click: "#rule-allow-dnd" }],
          note: "Let this alert through Do Not Disturb, so a low at night still wakes you.",
        },
        {
          do: [{ click: "button", text: "Save" }],
          note: "Save the rule. Keep your CGM and pump alarms on too: this is an extra layer.",
        },
      ],
    },
  ],
};
