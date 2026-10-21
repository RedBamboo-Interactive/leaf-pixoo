# Leaf Pixoo

Leaf Pixoo is a RedLeaf extension that turns each configured display into one Agent's quiet 64×64 peripheral face. v1 is emulator-first: its Leaf page renders the current server-generated loop as a convincing LED matrix, lets the owner select a display and independently bind it to any available Agent entity, and provides explicit state/gesture simulation. The same device-first surface is contributed to Nova as its **Display** section.

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
- A standalone Leaf surface at `/apps/pixoo/emulator`, including a device list, editable device name, canonical Agent `EntityCard` references, and a design-system Agent picker.
- A Nova `app-section` at `/apps/nova/display` with the same device-first controls. Device bindings are independent of Nova's selected-Agent filter.

With no device records, both surfaces show a first-run **Add display** action. One device is selected automatically; with several, the owner selects the device before changing its name, Agent binding, brightness, simulated state, or gesture. Each device remains bound to exactly one Agent.

## Portrait packs

Pixoo resolves the bound Agent's current `outfit` entity and its `asset`; if either is absent it uses the Agent avatar. The source bytes are SHA-256 keyed, so a source change invalidates the cache while an unchanged image reuses its accepted pack. Outfit selection's `agent.avatar-changed` event schedules a rebuild. The bridge also subscribes to `entity.updated`, but the current kernel only broadcasts that mutation to browser WebSockets, not backend `IPluginEvents`; normal device reads therefore perform the same source-hash check as a reliable fallback.

The renderer first finds a bust crop locally. It submits one `wait:false` RedCompute redraw job at a time through `qwen-image-21-reference-e1003bc3`, waits only on the returned durable job ID, and permits at most three deterministic seeds. Each candidate then gets a separate bounded RedCompute keypoint job over the full 1024×1024 redraw using the already-installed `comfyui_controlnet_aux/MediaPipe-FaceMeshPreprocessor`. The resulting feature-coloured mesh is parsed into independent eye polygons/boxes and upper/lower lip lines before any 64×64 animation is built. Missing landmarks, eyes outside the face box, or a mouth not below both eyes reject the candidate; the old proportional feature guess is never accepted.

The default D-style reduction remains a direct sRGB area/box average of the redraw to 64×64. No palette quantization or smoothing runs by default because the Pixoo accepts 24-bit RGB and both damaged this portrait. Palette limiting (2–256 colours, non-dithered) and a mask-feathered background contrast/saturation calm-down (0–50%) remain explicit opt-ins in `PortraitReductionOptions`; both default off.

The faithful B reduction is also kept as a colour reference and fallback. Hair, amber inner top, and jacket drift are measured per region. The CIELAB guard leaves an in-tolerance redraw byte-identical; only an out-of-tolerance region receives a mean-only L/a/b shift, never variance scaling. Grid pitch is recorded as provenance but does not alter the default direct box reduction. If no semantic candidate validates, the B-style source reduction becomes active. An already active pack stays on the display until its replacement passes validation; if no image can be read, the procedural face remains available.

Animation is deterministic compositing over the accepted 64×64 base. A feathered figure mask limits weather and shimmer to background pixels; state brightness is a uniform whole-frame transform. The independently tilted eye boxes drive blinks and gaze. Mouth openings follow the detected line midway between the upper and lower lips: a byte-identical closed frame followed by 1 px and 2 px openings, capped at 4/6 px across with sampled lip-colour edges and a warm red-brown interior. It never asks the image model for per-frame variants, avoiding identity drift and shimmer. Each stored pack includes the crop, primary base, faithful fallback, figure mask, reduction settings, landmark detector/job provenance and polygons, native state/gesture strips, landmark/eye/mouth review sheets, and a source-versus-result comparison as Leaf Assets.

As discovered on 20 October 2026, RedCompute `/discover` does not declare a standalone face-landmark capability, but its running ComfyUI provider exposes installed preprocessors through the node inventory. `DWPreprocessor` was present but returned no person for this stylized redraw. The installed MediaPipe Face Mesh preprocessor succeeded without adding custom nodes or an ONNX dependency, so Pixoo runs that node through the signed RedCompute image-job envelope and parses its deterministic mesh output. A landmark failure is terminal for that portrait candidate rather than permission to guess.

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

Do not add this checkout to `leaf.workspace.json`. After review, build the `.leafpkg`, then have a RedLeaf administrator install that exact local package through `POST /api/plugins/install` with JSON `{ "path": "<absolute package path>", "sha256": "<value from .sha256>" }` and the signed execution bearer. Verify `GET /api/plugins/pixoo/status` reports the package on disk and restart pending. Coordinate one planned `POST /api/setup/restart`; after Leaf returns, verify the same status reports the backend loaded, then open `/apps/pixoo/emulator` or Nova's `/apps/nova/display` section.

No Skill seeds ship in this package.
