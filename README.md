# Next.js + separate API

The public Next.js component is hybrid and owns `/`. A second Node.js compute
component is mounted at `/api`. The Next.js server calls the public `/api`
route in deployed environments. `API_BASE_URL` overrides that address, and
local development defaults to `http://localhost:8080`.

```powershell
npm install
npm run build
```

For local development, run `npm run dev:api` and `npm run dev:web` in separate
terminals.

Internal-only component addressing remains a future capability; this current
manifest uses the supported public `/api` mount.
