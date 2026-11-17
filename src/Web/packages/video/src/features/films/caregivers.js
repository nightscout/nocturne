/** Names and roles quoted here come from src/fixtures/features/caregivers.json; re-capturing means re-reading them. */
const MEMBERS = '.space-y-4:has(> h2):has([data-slot="card"])';

export default {
  intro: { title: "Caregivers", line: "The people who help you get their own login, not your password." },
  scenes: [
    {
      selector: MEMBERS,
      heading: "Invite someone",
      beats: [
        {
          do: [{ click: "button", text: "Create Invite Link" }],
          note: "Invite the people who look out for you. Each one gets their own login.",
        },
      ],
    },
    {
      selector: '[data-testid="create-invite-card"]',
      fill: 0.8,
      heading: "Choose what they can do",
      beats: [
        {
          do: [{ type: "#invite-label", text: "Grandad" }],
          note: "Name the invite, so you know who it is for.",
        },
        {
          do: [{ click: "label", text: "Viewer" }],
          note: "Choose a role. A Viewer can see glucose and reports, and cannot change anything.",
        },
        {
          do: [{ click: "#limit-to-24-hours" }],
          note: "You can also limit someone to the last 24 hours of data.",
        },
        {
          do: [{ click: "button", text: "Create Link" }],
          note: "Create the link and send it to him. It works for 7 days, and only once.",
        },
      ],
    },
    {
      selector: MEMBERS,
      heading: "Waiting for them",
      beats: [
        {
          do: [{ click: "button", text: "Done" }],
          note: "The invite waits here until he signs in. Mum's invite is there too, as a Caretaker.",
        },
        {
          element: '[data-slot="card"]:first-of-type',
          note: "Change a role or remove someone at any time. Nobody ever needs your password.",
        },
      ],
    },
  ],
};
