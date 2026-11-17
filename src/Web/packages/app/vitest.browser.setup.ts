import "./src/app.css";

// Every test file runs on one origin, so localStorage and cookies outlive the file that wrote
// them, and a persisted preference (glucose units, say) would change what a later file renders.
localStorage.clear();
for (const name of document.cookie.split(";").map((c) => c.split("=")[0].trim()).filter(Boolean)) {
  document.cookie = `${name}=;path=/;max-age=0`;
}
