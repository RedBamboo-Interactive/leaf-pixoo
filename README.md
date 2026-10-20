# Leaf Pixoo

Leaf Pixoo is a RedLeaf extension that gives one selected Agent a quiet 64×64 peripheral face. v1 is emulator-first: its Leaf page renders the current server-generated loop as a convincing LED matrix, lets the owner bind devices to Agent entities, and provides explicit state/gesture simulation. The same surface is contributed to Nova as its **Display** section, scoped to Nova's selected Agent.

It is intentionally a face, not a dashboard. It never renders conversation text, Agent names, notifications, clocks, or status icons on the display.

## What v1 includes

- Multiple durable `pixoo-device` records, each bound to exactly one real `agent` entity.
- `emulator` and `pixoo64` device kinds, Agent-colour weather field, brightness, and a configurable minimum push gap (default 1000 ms).
- Face states: `dark`, `resting`, `listening`, `thinking`, `speaking`, `working`, `dreaming`, and `asleep`.
- One-shot gestures: `arrive` and `react`.
- Swappable `IFaceRenderer` whose primary renderer is a cached portrait pack from the bound Agent's current Outfit image (or avatar), with a faithful deterministic reduction and finally the procedural face as fallbacks.
- `IDisplaySink` implementations for the in-memory emulator and an explicitly disabled-by-default Pixoo-64 HTTP driver.
- One serialized latest-wins queue per device. A push is a complete multi-frame loop; unchanged state does not push, and changes arriving during the minimum gap coalesce to the newest state.
- Emulator state and a bounded push history exposed to the frontend. Logs contain timestamps and event types/causes, never content.
- RedLeaf design-system chrome from `@redbamboo/ui` with Tailwind v4 tokens in both light and dark themes. Only the physical LED matrix, glow, gaps, and bezel are bespoke.
- A standalone Leaf surface at `/apps/pixoo/emulator`, including canonical Agent `EntityCard` references and a design-system Agent picker.
- A Nova `app-section` at `/apps/nova/display`; it reads Nova's reactive `nova_settings.agentFilter`, lists only devices bound to that exact Agent, and offers no independent Agent dropdown.

Nova's extension component context currently contains contributor/extension/target/route metadata, not Agent identity. Nova itself stores the selected Agent as `agentFilter` in the shared `nova_settings` local store. Pixoo observes that canonical local store through `@redbamboo/utility`; when Nova is in its all-Agents state, the section asks the owner to choose an Agent instead of guessing one.

## Portrait packs

Pixoo resolves the bound Agent's current `outfit` entity and its `asset`; if either is absent it uses the Agent avatar. The source bytes are SHA-256 keyed, so a source change invalidates the cache while an unchanged image reuses its accepted pack. Outfit selection's `agent.avatar-changed` event schedules a rebuild. The bridge also subscribes to `entity.updated`, but the current kernel only broadcasts that mutation to browser WebSockets, not backend `IPluginEvents`; normal device reads therefore perform the same source-hash check as a reliable fallback.

The renderer first finds a bust crop and deterministic facial regions locally. It submits one `wait:false` RedCompute job at a time through `qwen-image-21-reference-e1003bc3`, waits only on the returned durable job ID, and permits at most three deterministic seeds. The redraw is reduced with grid-pitch detection (when a regular grid is measurable), box sampling, and a non-dithered 48-colour maximum palette. If no semantic candidate validates, the B-style source reduction becomes the active pack. An already active pack stays on the display until its replacement passes validation; if no image can be read, the procedural face remains available.

Animation is deterministic compositing over the accepted 64×64 base. Eye, mouth, torso, subject, and background regions drive blinks, mouth shapes, gaze, breathing, dimming, and background-only weather tint. It never asks the image model for per-frame variants, avoiding identity drift and shimmer. Each stored pack includes the crop, primary base, faithful fallback, provenance manifest (source asset/hash, workflow, seed, timestamp, regions and metrics), native state/gesture strips, 8× nearest-neighbour review strips, and a source-versus-result comparison as Leaf Assets.

As discovered on 20 October 2026, RedCompute `/discover` exposes the running image generator (`generate`, exact-job progress, and output), but declares no face/landmark detector, mask endpoint, inpainting, or image-edit capability. Region finding is consequently deterministic and local; it is deliberately replaceable when a declared landmark capability becomes available.

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

The local visual harness is `pnpm run dev` from `web/`; in development only, it reads `artifacts/portrait/portrait-preview.json` so Playwright can exercise the accepted Nova pack without installing the extension or contacting Leaf. `node tests/visual.cjs` captures desktop and phone evidence. Production builds do not copy the harness preview.

## Loading after review

Do not add this checkout to `leaf.workspace.json`. After review, build the `.leafpkg`, then have a RedLeaf administrator install that exact local package through `POST /api/plugins/install` with JSON `{ "path": "<absolute package path>", "sha256": "<value from .sha256>" }` and the signed execution bearer. Verify `GET /api/plugins/pixoo/status` reports the package on disk and restart pending. Coordinate one planned `POST /api/setup/restart`; after Leaf returns, verify the same status reports the backend loaded, then open `/apps/pixoo/emulator` or Nova's `/apps/nova/display` section.

No Skill seeds ship in this package.
