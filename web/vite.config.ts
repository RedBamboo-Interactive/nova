import { fileURLToPath } from "node:url"
import { defineConfig } from "vite"
import react from "@vitejs/plugin-react"
import tailwindcss from "@tailwindcss/vite"

/**
 * Library-mode build for the .leafpkg web bundle (kernel design doc §6.2):
 * a single ESM entry (dist/plugin.js) plus the plugin's own compiled Tailwind
 * (dist/plugin.css). Shell-shared dependencies stay as bare specifiers and are
 * resolved at runtime by the kernel shell's import map — adding an external
 * here requires a matching shared module in the kernel
 * (PluginAssetEndpoints.SharedModules).
 *
 * `pnpm build:pkg` for release packaging, `pnpm dev` (vite build --watch) for
 * the workspace dev loop — the kernel serves dist/ via /plugin-assets/{id}/.
 */
export default defineConfig({
  plugins: [react(), tailwindcss()],
  define: {
    // The bundle loads into an already-built shell — always production semantics.
    "process.env.NODE_ENV": JSON.stringify("production"),
  },
  build: {
    lib: {
      entry: fileURLToPath(new URL("./src/index.ts", import.meta.url)),
      formats: ["es"],
      fileName: () => "plugin.js",
      cssFileName: "plugin",
    },
    rollupOptions: {
      external: [
        "react",
        "react/jsx-runtime",
        "react-dom",
        "react-dom/client",
        "react-router-dom",
        "@redbamboo/ui",
        "@redbamboo/utility",
      ],
      output: {
        chunkFileNames: "chunks/[name]-[hash].js",
        assetFileNames: "assets/[name]-[hash][extname]",
      },
    },
  },
})
