import { StrictMode } from "react"
import { createRoot } from "react-dom/client"
import "@fortawesome/fontawesome-free/css/all.min.css"
import "@fontsource-variable/inter"
import "@fontsource-variable/jetbrains-mono"
import "@fontsource-variable/lora"
import "./index.css"
import { App } from "./App"

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
