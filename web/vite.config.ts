import path from "path"
import fs from "fs"
import { defineConfig } from "vite"
import react from "@vitejs/plugin-react"
import tailwindcss from "@tailwindcss/vite"

const pkg = JSON.parse(fs.readFileSync(path.resolve(__dirname, "package.json"), "utf-8"))

export default defineConfig({
  plugins: [react(), tailwindcss()],
  define: {
    __APP_VERSION__: JSON.stringify(pkg.version),
  },
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./src"),
      react: path.resolve(__dirname, "node_modules/react"),
      "react-dom": path.resolve(__dirname, "node_modules/react-dom"),
    },
    dedupe: ["react", "react-dom", "react-router-dom"],
  },
  server: {
    port: 18903,
    proxy: {
      "/api": "http://localhost:18803",
      "/auth": "http://localhost:18803",
      "/login": "http://localhost:18803",
      "/ai-session": "http://localhost:18803",
      "/ws": { target: "ws://localhost:18803", ws: true },
      "/ping": "http://localhost:18803",
      "/health": "http://localhost:18803",
      "/discover": "http://localhost:18803",
    },
  },
})
