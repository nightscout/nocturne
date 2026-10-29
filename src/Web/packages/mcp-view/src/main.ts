import { mount } from "svelte";
import "./app.css";
import McpReport from "./McpReport.svelte";

mount(McpReport, { target: document.getElementById("app")! });
