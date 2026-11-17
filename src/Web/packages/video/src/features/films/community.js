/** The counts are written in src/features/stages/Community.svelte, with where each comes from. */
export default {
  intro: {
    title: "Nocturne",
    line: "Free, open source, and made by the people who use it.",
  },
  scenes: [
    {
      selector: '[data-film="top"]',
      heading: "Built by the community",
      beats: [
        {
          element: '[data-film="stats"] > :first-child',
          at: [0.3, 0.55],
          note: "31 people have contributed code to Nocturne so far.",
        },
        {
          element: '[data-film="stats"] > :nth-child(2)',
          at: [0.3, 0.55],
          note: "678 pull requests merged: fixes, reports, connectors and translations.",
        },
        {
          element: '[data-film="stats"] > :nth-child(3)',
          at: [0.3, 0.55],
          note: "It speaks 10 languages besides English, translated by the people who use them.",
        },
      ],
    },
    {
      selector: '[data-film="event"]',
      heading: "HackDiabetes 26",
      beats: [
        {
          element: ".text-6xl",
          at: [0.4, 0.5],
          note: "From 14 to 16 November we build it together at HackDiabetes in Vancouver.",
        },
        {
          element: ".text-xl",
          at: [0.75, 0.5],
          note: "Developers, clinicians and people with diabetes: all skill levels are welcome. Register at hackdiabetes.io.",
        },
      ],
    },
  ],
};
