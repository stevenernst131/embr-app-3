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

## Service Bus validation app

The active `builder.yaml` runs `sb-validation/server.mjs` as one Node 24 web component.
`POST /messages` sends with managed identity; `GET /messages/peek` verifies the queue.
The named connection provides `SERVICE_BUS_NAMESPACE` and `SERVICE_BUS_ENTITY_NAME`.
The assigned UAMI client ID is preferred when exported by Builder; otherwise SAMI is used.
`GET /validation/state` identifies the release, process, and optional `HUNT_MARKER`.
`GET /validation/delay?ms=1000` and `/validation/log?size=30&partial=true` support bounded replica/log testing.
A heartbeat is written every four seconds. For controlled startup tests only, set
`VALIDATION_STARTUP_MODE=crash` or `wrongport`; remove it before the recovery deployment.
The build fails intentionally only when `HUNT_FAIL_BUILD=1`; remove that test variable before recovery.
The current healthy probe release is `hunt-rapid-latest` (iteration 7).
