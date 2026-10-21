# Leaf Pixoo

Leaf Pixoo is a RedLeaf extension that turns each configured display into one Agent's quiet 64×64 peripheral face. v1 is emulator-first: its Leaf page renders the current server-generated loop as a convincing LED matrix, lets the owner select a display and independently bind it to any available Agent entity, and provides explicit state/gesture simulation. The same device-first surface is contributed to Nova as its **Display** section.

It is intentionally a face, not a dashboard. It never renders conversation text, Agent names, notifications, clocks, or status icons on the display.

## What v1 includes

- Multiple durable `pixoo-device` records, each bound to exactly one real `agent` entity.
- `emulator` and `pixoo64` device kinds, Agent-colour weather field, brightness, and a configurable minimum push gap (default 1000 ms).
- Face states: `dark`, `resting`, `listening`, `thinking`, `speaking`, `working`, `dreaming`, and `asleep`.
- One-shot gestures: `arrive` and `react`.
- Swappable `IFaceRenderer` whose primary renderer is a cached portrait pack from the bound Agent's current Outfit image (or avatar), with faithful BOX-reduced Wan video loops, a still portrait fallback, and finally the procedural face when no portrait exists.
- `IDisplaySink` implementations for the in-memory emulator and an explicitly disabled-by-default Pixoo-64 HTTP driver.
- One serialized latest-wins queue per device. Each state change selects one complete GIF program; changes arriving during the minimum gap coalesce to the newest state.
- Emulator state and a bounded push history exposed to the frontend. Logs contain timestamps and event types/causes, never content.
- RedLeaf design-system chrome from `@redbamboo/ui` with Tailwind v4 tokens in both light and dark themes. Only the physical LED matrix, glow, gaps, and bezel are bespoke.
- No separate app: everything lives in Nova's **Display** section (`/apps/nova/display`). The old `/apps/pixoo` route redirects there.
- A Nova `app-section` at `/apps/nova/display` with the same device-first controls. Device bindings are independent of Nova's selected-Agent filter.

With no device records, both surfaces show a first-run **Add display** action. One device is selected automatically; with several, the owner selects the device before changing its name, Agent binding, brightness, simulated state, or gesture. Each device remains bound to exactly one Agent.

## Portrait packs

Pixoo resolves the bound Agent's current `outfit` entity and its `asset`; if either is absent it uses the Agent avatar. The source bytes are SHA-256 keyed, so a source change invalidates the cache while an unchanged image reuses its accepted pack. Outfit selection's `agent.avatar-changed` event schedules a rebuild. The bridge also subscribes to `entity.updated`, but the current kernel only broadcasts that mutation to browser WebSockets, not backend `IPluginEvents`; normal device reads therefore perform the same source-hash check as a reliable fallback.

The renderer finds the same square bust crop used by the accepted faithful-B look and reduces it directly to 64×64 with an sRGB area/BOX average. There is no semantic redraw, colour guard, palette quantization, face-landmark job, or procedural feature editing. The Pixoo accepts 24-bit RGB, so the faithful reduction is retained without a stylistic palette limit.

For animation, the same square bust crop is uploaded as a private Leaf Asset and used as both the first and last frame. At runtime Pixoo reads the registered `wan-22-i2v-first-last-portrait-film-60fps-4a256f5b` and `wan-video-22-quiet-reportage-film-60fps-88c61e77` workflow entities. It copies the six LightX2V LoRA nodes into a cloned first/last-frame graph, rewires the two samplers, removes FILM interpolation, and submits that frozen raw graph directly to RedCompute with the signed plugin execution identity and an idempotency key. No workflow graph is vendored in this repository.

The fast graph runs at the measured motion floor: 384×384, 41 frames, four steps, sampler split two, CFG one, and 15 fps. The duplicate last frame is dropped, every second frame is retained, and every retained square receives the same BOX reduction to 64×64, producing 20 motion frames. A temporally smoothed Reinhard transfer in CIELAB matches every frame back to the source still, then the first and last four frames cross-fade to the exact still so the loop seam closes without a colour jump. The exact durable job is polled and its declared output URL is downloaded; MP4 decoding uses `ffmpeg` from `PATH`.

If either workflow entity is absent or any required graph node cannot be rewired, Pixoo records the reason in each clip status and falls back to the registered workflow's previous 768×576, 30-step, FILM settings. Other generation failures fail soft to the faithful still and remain bounded by the normal attempt and cooldown policy.

Every state is built as one uniform 100 ms/frame GIF program because the Pixoo ignores per-frame delays. Resting generates four scene-neutral idle variants (look down and back, glance toward the existing scene and back, a small private smile, and slow breathing with a blink). Three deterministic 60–120 second composites interleave those motions with source-hash-seeded 8–25 second runs of duplicate still frames. One composite is selected immediately and the alternatives rotate only every 10–20 minutes. Listening and thinking include short pauses, speaking loops continuously, dreaming is a long eyes-closed breathing program, and asleep reuses it with uniform dimming. The emulator receives the exact same frames and 100 ms timing as the GIF.

Clips are generated strictly one job at a time per Agent: the four resting variants first, followed by speaking, listening, thinking, and dreaming. The first resting variant becomes usable as soon as it is ready. Working reuses thinking, asleep reuses dreaming with uniform dimming, and arrive/react currently reuse resting. Dark turns the screen off. Each clip gets at most two attempts with a 30-second retry cooldown and a 10-minute Agent-level failure cooldown. A per-Agent single-flight gate prevents overlapping GPU jobs. The square crop, source MP4s, reduced loops, prompts, seeds, exact job IDs, and provenance are stored as Leaf Assets and a `pixoo-portrait-pack` entity keyed by source hash and pipeline version. None are repository content.

Independent of conversation signals, a local-time scheduler selects dreaming from 02:00–05:00 and asleep during quiet hours from 23:30–07:30 when no listening, thinking, speaking, or working state is active. Outside those windows it returns to resting. These defaults are configurable with `PIXOO_DREAMING_START`, `PIXOO_DREAMING_END`, `PIXOO_QUIET_START`, and `PIXOO_QUIET_END` using local `HH:mm` values.

## Identity and live-event boundary

The state adapter is deliberately fail-closed. It accepts a session only when the event explicitly supplies both an Agent entity identity and a user-facing classification. Once accepted, its session ID may correlate later stream/end events. It never matches titles, project names, paths, or display names. Sources or provenance marked automation, heartbeat, scheduled, or schedule are rejected; tests cover that invariant.

The current RedCompute model defines `UnifiedSessionInfo.OwnerAgentId`, but provider `session.created`/`session.updated` broadcasts do not populate it. Current `session.stream` and `session.ended` broadcasts carry a session ID but no Agent identity. RedLeaf relays these Compute frames verbatim to browser `/ws`; it does not publish them into the backend plugin `IPluginEvents` bus. The event adapter is therefore ready for a future identity-bearing kernel event contract but live Compute activity cannot safely drive v1 today. Manual simulation is the supported input. This is preferable to false attribution.

`job.updated` can carry `creationProvenance.actor.entityId`; the mapper will use it for TTS only when the event is explicitly user-facing and the actor is an Agent. The same backend-subscription gap currently applies.

## Pixoo-64 status and safety

The hardware path is disabled by default. It uses only an explicitly configured LAN host and sends local `POST http://<host>/post` requests. It performs no network discovery, no cloud calls, no automatic retries, and never contacts Divoom services. The primary path, validated on a real Pixoo-64 on 21 October 2026, is:

1. Determine the specific local IPv4 interface that routes to the configured Pixoo with a UDP connect (no packet is sent).
2. Start a minimal Kestrel HTTP/1 listener on that routed address and port 18950, never on `0.0.0.0`.
3. Serve only `GET /{process-random-128-bit-token}/{short-name}.gif` from the current in-memory GIF set. The token is never logged.
4. Send `Device/PlayTFGif` with `FileType=2` and the plain-HTTP URL, then confirm the device fetched it by recording the request time and remote address.

GIFs are exactly 64×64, loop forever, retain duplicate pause frames, and use a uniform 100 ms delay. `ffmpeg` encodes raw RGB24 with `palettegen`/`paletteuse=dither=none`; the repository adds no image-codec dependency. The GIF host port is configurable with `PIXOO_GIF_PORT`.

If the listener cannot bind or the Pixoo does not fetch the URL within 12 seconds, the sink records “display could not fetch its animation: check the Leaf Pixoo GIF firewall rule” and falls back to `Draw/ResetHttpGifId` plus at most 40 uniformly timed `Draw/SendHttpGif` frames. This fallback retains the serialized latest-wins queue but necessarily shows the device's Loading overlay. Requests use buffered JSON bodies with `Content-Length` because the device rejects chunked bodies while still returning HTTP 200. A non-zero `error_code` is treated as failure and does not kill the queue worker. Dark sends `Channel/OnOffScreen` directly.

## Build and test

The package script is build-only and requires an explicit canonical SDK project. It uses `REDLEAF_SCRATCH_DIR` for intermediate output and never installs or reloads Leaf.

```powershell
dotnet test .\tests\Leaf.Plugins.Pixoo.Tests\Leaf.Plugins.Pixoo.Tests.csproj `
  -p:LeafSdkProject='<path-to-redleaf>\src\Leaf.Sdk\Leaf.Sdk.csproj' `
  --artifacts-path "$env:REDLEAF_SCRATCH_DIR\leaf-pixoo-tests"

Push-Location .\web
pnpm install --frozen-lockfile
pnpm run typecheck
pnpm run build:pkg
Pop-Location

.\build-leafpkg.ps1 -LeafSdkProject '<path-to-redleaf>\src\Leaf.Sdk\Leaf.Sdk.csproj'
```

The local visual harness is `pnpm run dev` from `web/`. It uses a deterministic synthetic face by default, so no Agent imagery is needed in the checkout. To inspect a private local pack, set `PIXOO_PREVIEW_DIR` to a directory inside `REDLEAF_SCRATCH_DIR` (or the ignored local review directory) that contains `portrait-preview.json`; it is served only in dev mode and never copied into production. The Playwright scripts write beneath `REDLEAF_SCRATCH_DIR` when set, otherwise to the ignored local review directory.

## Loading after review

Do not add this checkout to `leaf.workspace.json`. After review, build the `.leafpkg`, then have a RedLeaf administrator install that exact local package through `POST /api/plugins/install` with JSON `{ "path": "<absolute package path>", "sha256": "<value from .sha256>" }` and the signed execution bearer. Verify `GET /api/plugins/pixoo/status` reports the package on disk and restart pending. Coordinate one planned `POST /api/setup/restart`; after Leaf returns, verify the same status reports the backend loaded, then open Nova's `/apps/nova/display` section.

No Skill seeds ship in this package.
