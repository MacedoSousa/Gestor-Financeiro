/// <reference types="vitest/config" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    // Em desenvolvimento a API roda com `dotnet run` (Kestrel em http://localhost:5000).
    proxy: { "/api": "http://localhost:5000" },
  },
  build: {
    sourcemap: false,
  },
  test: {
    include: ["src/**/*.test.ts", "src/**/*.test.tsx"],
    environment: "node",
  },
});
