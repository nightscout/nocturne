/** Meals quoted here come from src/fixtures/features/food.json; re-capturing means re-reading them. */
const MEALS = '[data-slot="card"]:has(table)';

export default {
  intro: { title: "Food and meals", line: "Your pump knows the carbs. Nocturne can know the food." },
  scenes: [
    {
      selector: MEALS,
      heading: "Carbs with no food",
      beats: [
        {
          do: [{ click: "button", text: "Unattributed only" }],
          note: "These carb entries came from the pump. None of them says what the food was.",
        },
        {
          do: [{ click: "tbody tr", text: "Unattributed" }],
          note: "Click one to say what it was: here, the 21 g you just ate.",
        },
      ],
    },
    {
      selector: MEALS,
      heading: "Create the food",
      beats: [
        {
          do: [
            { type: '[role="dialog"] input', text: "Oat pancakes" },
            { click: '[role="option"]', text: "Create" },
          ],
          note: "Search your foods, or create a new one: Oat pancakes.",
        },
        {
          do: [
            { click: "button", text: "Nutritional Details" },
            { type: "#food-portion", text: "120" },
            { type: "#food-carbs", text: "42" },
          ],
          note: "Give it a portion and its carbs: 42 g of carbs in 120 g.",
        },
      ],
    },
    {
      selector: MEALS,
      heading: "Pair it with the carbs",
      beats: [
        {
          do: [{ type: "#portions", text: "0.5" }],
          note: "You ate half a portion: 21 g of carbs, which is all of this meal.",
        },
        {
          do: [{ click: "button", text: "Save & Add" }],
          note: "Save & Add puts Oat pancakes in your food catalog and pairs it with the meal.",
        },
      ],
    },
    {
      selector: MEALS,
      heading: "The meal, attributed",
      beats: [
        {
          do: [{ click: "button", text: "All" }],
          note: "The meal now says what it was. Next time, Oat pancakes is in your recent foods.",
        },
      ],
    },
  ],
};
