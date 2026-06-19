---
displayName: Profile
standalone: true
---
How legacy **profiles** map to Nocturne's v4 therapy model.

A Nightscout profile is a monolithic document holding therapy settings plus four time-of-day schedules. Nocturne decomposes it into granular v4 models — **Therapy Settings** and separate **Basal**, **Carb Ratio**, **Sensitivity**, and **Target Range** schedules — so each can be versioned and validated independently. Reads project them back into the single legacy profile shape.
