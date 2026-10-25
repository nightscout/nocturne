import "./src/app.css";

// Every test file runs on one origin, so localStorage outlives the file that wrote it, and a
// persisted preference (glucose units, say) would change what a later file renders.
localStorage.clear();
