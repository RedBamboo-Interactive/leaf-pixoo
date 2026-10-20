# Leaf Pixoo

Leaf Pixoo is a RedLeaf extension that gives one selected Agent a quiet 64×64 peripheral face. v1 is emulator-first: its Leaf page renders the current server-generated loop as a convincing LED matrix, lets the owner bind devices to Agent entities, and provides explicit state/gesture simulation.

It is intentionally a face, not a dashboard. It never renders conversation text, Agent names, notifications, clocks, or status icons on the display.

## What v1 includes

- Multiple durable `pixoo-device` records, each bound to exactly one real `agent` entity.
- `emulator` and `pixoo64` device kinds, Agent-colour weather field, brightness, and a configurable minimum push gap (default 1000 ms).
- Face states: `dark`, `resting`, `listening`, `thinking`, `speaking`, `working`, `dreaming`, and `asleep`.
- One-shot gestures: `arrive` and `react`.
- Swappable `IFaceRenderer` with a procedural server-side placeholder renderer.
- `IDisplaySink` implementations for the in-memory emulator and an explicitly disabled-by-default Pixoo-64 HTTP driver.
- One serialized latest-wins queue per device. A push is a complete multi-frame loop; unchanged state does not push, and changes arriving during the minimum gap coalesce to the newest state.
- Emulator state and a bounded push history exposed to the frontend. Logs contain timestamps and event types/causes, never content.

## Identity and live-event boundary

The state adapter is deliberately fail-closed. It accepts a session only when the event explicitly supplies both an Agent entity identity and a user-facing classification. Once accepted, its session ID may correlate later stream/end events. It never matches titles, project names, paths, or display names. Sources or provenance marked automation, heartbeat, scheduled, or schedule are rejected; tests cover that invariant.

The current RedCompute model defines `UnifiedSessionInfo.OwnerAgentId`, but provider `session.created`/`session.updated` broadcasts do not populate it. Current `session.stream` and `session.ended` broadcasts carry a session ID but no Agent identity. RedLeaf relays these Compute frames verbatim to browser `/ws`; it does not publish them into the backend plugin `IPluginEvents` bus. The event adapter is therefore ready for a future identity-bearing kernel event contract but live Compute activity cannot safely drive v1 today. Manual simulation is the supported input. This is preferable to false attribution.

`job.updated` can carry `creationProvenance.actor.entityId`; the mapper will use it for TTS only when the event is explicitly user-facing and the actor is an Agent. The same backend-subscription gap currently applies.

## Pixoo-64 status and safety

The hardware path is **untested and disabled by default**. It uses only an explicitly configured LAN host and sends local `POST http://<host>/post` requests. It performs no network discovery, no cloud calls, no retries, and never contacts Divoom services.

The protocol is based on unverified community descriptions:

1. `Draw/ResetHttpGifId`
2. one `Draw/SendHttpGif` request per 64×64 RGB frame with `PicNum`, `PicWidth=64`, `PicOffset`, `PicID`, `PicSpeed`, and base64 `PicData`
3. `Channel/SetBrightness`
4. `Channel/OnOffScreen`

Each frame is exactly `64 × 64 × 3 = 12,288` RGB bytes before base64 encoding. Real-device timing and the interpretation/unit of `PicSpeed` must be verified on hardware before enabling it.

## Build and test

The package script is build-only and requires an explicit canonical SDK project. It uses `REDLEAF_SCRATCH_DIR` for intermediate output and never installs or reloads Leaf.

```powershell
dotnet test .\tests\Leaf.Plugins.Pixoo.Tests\Leaf.Plugins.Pixoo.Tests.csproj `
  -p:LeafSdkProject='T:\Projects\redleaf\src\Leaf.Sdk\Leaf.Sdk.csproj'

Push-Location .\web
pnpm install --frozen-lockfile
pnpm run typecheck
pnpm run build:pkg
Pop-Location

.\build-leafpkg.ps1 -LeafSdkProject 'T:\Projects\redleaf\src\Leaf.Sdk\Leaf.Sdk.csproj'
```

The local visual harness is `pnpm run dev` from `web/`; `node tests/visual.cjs` captures desktop and phone evidence without connecting to Leaf or real media.

## Loading after review

Do not add this checkout to `leaf.workspace.json`. After review, build the `.leafpkg`, then have a RedLeaf administrator install that exact local package through `POST /api/plugins/install` with JSON `{ "path": "<absolute package path>", "sha256": "<value from .sha256>" }` and the signed execution bearer. Verify `GET /api/plugins/pixoo/status` reports the package on disk and restart pending. Coordinate one planned `POST /api/setup/restart`; after Leaf returns, verify the same status reports the backend loaded, then open `/apps/pixoo`.

No Skill seeds ship in this package.
