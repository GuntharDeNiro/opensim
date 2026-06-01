# Second Life LSL Compatibility Audit

Source baseline: official Second Life Wiki `Category:LSL Functions`, captured 2026-05-28 and rechecked 2026-05-29.

This document tracks the script-engine compatibility pass against Second Life LSL.
The goal is to make missing or divergent behavior explicit before implementing it,
so additions are deliberate and testable instead of guessed from individual scripts.

## Current Pass

- Official Second Life function names collected: 514 public function pages after filtering localized subpages.
- OpenSim LSL stub exported functions collected from `LSL_Stub.cs`.
- Current stub name gap against that category: 0 missing names.
- First corrected semantic area: list slicing and strided list search.
- First exposed missing function already implemented in the API: `llGetStartString`.
- First newly implemented environment helper: `llGetRegionTimeOfDay`.

## Implemented Or Corrected In This Pass

- `llList2ListSlice`
  - Handles negative `stride_index`.
  - Handles exclusion ranges where `start > end` by returning the outside ranges.
  - Applies stride indexing over the selected slice/exclusion set.

- `llListFindStrided`
  - Handles empty source and empty test consistently.
  - Prevents matches from crossing the requested search end.
  - Handles negative start/end before scanning.

- `llGetStartString`
  - Was present in `ILSL_Api` and `LSL_Api`, but was not exposed through `LSL_Stub`.

- `llGetRegionTimeOfDay`
  - Returns current region environment time when the environment module is available.
  - Falls back to `llGetTimeOfDay` if the region environment module is absent.

- `llDetectedRezzer`
  - Carries the rezzer UUID through detect params.
  - Persists the value through YEngine capture/restore and serialized detect snapshots.

- `llGetAttachedListFiltered`
  - Supports include filters and the HUD flag.
  - Keeps HUD attachment visibility limited to the script owner.

- `llFindNotecardTextSync`
  - Performs cached synchronous notecard regex search.
  - Returns `[line, index, length]` strides, capped to 64 matches per call.

- `llGiveAgentInventory`
  - Delivers a folder of copyable/transferable task inventory to an in-region agent.
  - Supports `TRANSFER_DEST` root paths, validates `TRANSFER_FLAGS`, and returns SL-style `TRANSFER_*` result codes.

- `llOpenFloater`
  - Exposes the SL signature and returns deterministic attachment/agent/experience status.

- `llSetAgentRot`
  - Applies yaw rotation to the granted in-region avatar when animation permissions are held.

- `llSetLinkRenderMaterial`
  - Applies render material changes across link selectors using the same material storage as `llSetRenderMaterial`.

- `llSignRSA` and `llVerifyRSA`
  - Support PEM RSA signatures with SHA-1, SHA-224, SHA-256, SHA-384 and SHA-512 names.

- Environment helpers
  - Adds `llGetEnvironment` for day info, sky tracks and supported sky fields.
  - Adds `llGetEnvironment` readback for supported water rules: fog, fresnel, normal scale, normal texture, refraction and wave directions.
  - Adds region/parcel/agent environment replacement and clearing through the existing EEP environment module.
  - Adds per-parameter `llSetEnvironment` and `llSetAgentEnvironment` persistence for supported water rules, sky rules, `ENVIRONMENT_DAYINFO` and `SKY_TRACKS`.
  - Unsupported texture-default mutations still return `ENV_INVALID_RULE` until OpenSim has matching persistent override storage.

- Estate and parcel management helpers
  - Adds `llReturnObjectsByID` and `llReturnObjectsByOwner` using the simulator's return permission checks.
  - Adds `llSetGroundTexture` for terrain detail textures and height ranges through the estate module.
  - Corrects `llSetGroundTexture` and `llManageEstateAccess` estate-manager permissions to use owner-or-manager estate command checks where SL-compatible.
  - Persists `llManageEstateAccess` mutations and triggers estate-info change notifications after successful updates.
  - Adds `llSetParcelForSale(forSale, options)` with `PARCEL_SALE_*` result codes and `PERMISSION_PRIVILEGED_LAND_ACCESS` checks.
  - Adds `PARCEL_MEDIA_COMMAND_LOOP_SET` and improves parcel media command/query handling for loop, autoscale, description, MIME type and integer media size values.
  - Adds `llTransferOwnership` for direct in-world transfer and inventory delivery with `TRANSFER_FLAG_COPY` and `TRANSFER_FLAG_TAKE`.
  - Applies SL-style transfer cleanup for embedded no-transfer and no-copy task inventory during ownership transfer.

- Group and sculpt compatibility helpers
  - Adds `llMatchGroup(agent, group_keys)` for same-region active-group checks.
  - Exposes `llSetSculptAnim` for script compatibility, persists the requested sculpt animation state and mirrors it through viewer-supported texture animation for visible playback.
  - Keeps `llGodLikeRezObject` restricted to actual god-mode script owners instead of logging unsupported while still rezzing.

- Damage and combat helpers
  - Adds `llDamage` using OpenSim's existing avatar health and death/teleport-home path.
  - Adds `PRIM_DAMAGE` and `PRIM_HEALTH` support in primitive params.
  - Adds `OBJECT_HEALTH`, `OBJECT_DAMAGE` and `OBJECT_DAMAGE_TYPE` details.
  - Adds Combat2-style `on_damage`, `final_damage` and `on_death` YEngine events for damage-aware object and attachment scripts.
  - `llDetectedDamage` now returns `[damage, damage_type, original_damage, source_key, source_position, source_owner]` during damage events.
  - `llAdjustDamage(integer number, float damage)` updates the current event's damage metadata before health is applied, with a one-argument compatibility overload for row zero.
  - Damage application now uses a pending server-side transaction: `on_damage` opens a quiet adjustment window, each `llAdjustDamage` extends that quiet window, and health is reduced only after the transaction settles or reaches its cap.
  - Extends `llGetHealth` to report PRIM_HEALTH-compatible object health as well as avatar health.

- Pathfinding compatibility surface
  - Exposes the Second Life pathfinding/character function names and constants so scripts compile and can exercise a real simulator-side route backend.
  - `llCreateCharacter` and `llUpdateCharacter` persist local character options such as radius, desired/max speed, avoidance mode and parcel-stay behavior for subsequent movement calls.
  - `llGetClosestNavPoint` returns a terrain-aware in-region point with radius clearance above terrain and static object bounding boxes.
  - `llGetStaticPath` returns `[PU_GOAL_REACHED, waypoint...]` for valid in-region obstacle-aware paths or a `PU_FAILURE_*` code.
  - `llNavigateTo`, `llWanderWithin`, `llPatrolPoints`, `llPursue`, `llEvade`, `llFleeFrom` and stop/jump character commands use keyframed movement over A* routes that avoid scene-object bounds, optional avatar bounds and steep terrain steps.
  - Path completion events are now invalidated by stop/delete/new movement and `PU_GOAL_REACHED` is posted after keyframed movement completes instead of at route start.
  - `FORCE_DIRECT_PATH`, `REQUIRE_LINE_OF_SIGHT` and `CHARACTER_STAY_WITHIN_PARCEL` are honored by the local route backend where applicable.
  - Route generation now bakes a per-region terrain navmesh cache with a terrain signature, slope-derived traversal costs and automatic invalidation when terrain changes.
  - Dynamic scene-object and optional avatar bounds are applied as overlays on top of the baked terrain cache so moving obstacles do not require rebaking the whole region.

- GLTF override helpers
  - Adds `llSetLinkGLTFOverrides` for material factor overrides backed by OpenSim render material override storage.
  - Supports base color/alpha, alpha mode, alpha mask, double-sided, metallic, roughness and emissive factors.
  - Adds `OVERRIDE_GLTF_EXTENSION_JSON` to preserve future GLTF extension JSON in compact override storage for local tooling and forward-compatible readback.
  - Adds `PRIM_RENDER_MATERIAL` support through `llSetPrimitiveParams`, `llSetLinkPrimitiveParams`, `llGetPrimitiveParams` and `llGetLinkPrimitiveParams`.
  - Adds `PRIM_GLTF_NORMAL`, `PRIM_GLTF_EMISSIVE`, `PRIM_GLTF_METALLIC_ROUGHNESS` and `PRIM_GLTF_BASE_COLOR` set/readback for stored override values through primitive params.
  - Reads compact texture, transform and factor overrides and now merges supported assigned GLTF material asset properties when an override is unset.
  - Updates the PBR GLTF physics primitive-param lab with an asset-only path that proves material asset readback before applying script overrides.

- Physics material primitive params
  - Adds `PRIM_PHYSICS_MATERIAL` readback.
  - Aligns `PRIM_PHYSICS_MATERIAL` set argument order with Second Life: bits, gravity, restitution, friction, density.

- Object detail cost/readback compatibility
  - Improves `llGetObjectDetails` for `OBJECT_SERVER_COST`, `OBJECT_STREAMING_COST`, `OBJECT_PHYSICS_COST`, `OBJECT_PRIM_EQUIVALENCE` and `OBJECT_RENDER_WEIGHT`.
  - Returns linkset-level cost estimates for objects instead of placeholder zeroes.
  - Returns attachment-derived cost and render-weight estimates when the target is an avatar.
  - Returns avatar preference hover height for `OBJECT_HOVER_HEIGHT`.
  - Returns object selection state for `OBJECT_SELECT_COUNT` when OpenSim exposes a selected linkset.
  - Updates the RegionWeb script reference entry for `llGetObjectDetails` with the new object-detail fields.

- Identity and name lookup compatibility
  - Improves `llKey2Name`, `llGetUsername` and `llGetDisplayName` to use cached local account data when the avatar is not currently in-region.
  - Improves `llName2Key` to resolve local cached account names synchronously before falling back to `NULL_KEY`.
  - Aligns `llGetAgentLanguage` with Second Life visibility rules by requiring an in-region root avatar and public language sharing instead of returning a global default.
  - Adds an in-world identity lookup example covering synchronous and dataserver-based name/key lookups.

- Animation state compatibility
  - Aligns `llGetAnimation` seated-state reporting with Second Life by returning `Sitting` and `Sitting on Ground` directly from simulator sit state.
  - Adds an in-world animation monitor example covering `llGetAnimation` and `llGetAnimationList`.

- Avatar visual parameter compatibility
  - Hardens `llGetVisualParams` so supported Second Life visual parameter ids and names are matched case-insensitively with common aliases.
  - Returns normalized float values for available avatar appearance parameters and empty entries for unsupported or unavailable values.
  - Bounds-checks legacy appearance arrays so older viewer data cannot throw script-engine exceptions when extended parameters such as hover are absent.
  - Adds an in-world avatar visual parameter inspector example for owner, toucher and nearby-avatar diagnostics.

- Physics energy compatibility
  - Replaces the static `llGetEnergy` placeholder with per-linkset energy readback that drains on supported physical-control calls and recharges over time.
  - Adds an in-world physics energy monitor example for impulse, force, torque, hover, buoyancy and push workflows.

- Environment water compatibility
  - Adds in-world water environment console coverage for parcel/region and agent-local EEP water overrides.
  - Supports `WATER_BLUR_MULTIPLIER`, `WATER_FOG`, `WATER_FRESNEL`, `WATER_NORMAL_SCALE`, `WATER_REFRACTION`, `WATER_WAVE_DIRECTION` and `WATER_NORMAL_TEXTURE`.
  - Applies Second Life validation ranges for supported water rules before persisting overrides.
  - Extends RegionWeb documentation for the now-supported `llGetEnvironment`, `llSetEnvironment` and `llSetAgentEnvironment` water parameter workflows.

- Environment sky compatibility
  - Adds persistent per-parameter `llSetEnvironment` and `llSetAgentEnvironment` support for the simulator-backed `SKY_*` rules OpenSim can store today.
  - Supports `SKY_AMBIENT`, `SKY_BLUE`, `SKY_CLOUDS`, `SKY_DOME`, `SKY_GAMMA`, `SKY_GLOW`, `SKY_HAZE`, `SKY_MOON`, `SKY_PLANET`, `SKY_REFRACTION`, `SKY_REFLECTION_PROBE_AMBIANCE`, `SKY_STAR_BRIGHTNESS`, `SKY_SUN`, `SKY_CLOUD_TEXTURE`, `SKY_MOON_TEXTURE` and `SKY_SUN_TEXTURE`.
  - Adds `llGetEnvironment` readback for sky texture UUIDs and `SKY_TEXTURE_DEFAULTS`.
  - Preserves existing sky tracks when present, creates script-owned static sky frames when missing, and applies whole-region negative-Z updates across all sky tracks.
  - Adds writable `ENVIRONMENT_DAYINFO` and `SKY_TRACKS` coverage for scripted day length/offset and sky altitude bands.
  - Adds an in-world EEP sky environment console example for region, parcel and agent-local sky/water presets.

- Parcel prim count compatibility
  - Completes `llGetParcelPrimCount` for same-owner simulator-wide `PARCEL_COUNT_OWNER`, `PARCEL_COUNT_GROUP`, `PARCEL_COUNT_OTHER` and `PARCEL_COUNT_SELECTED`.
  - Adds `PARCEL_COUNT_TEMP` support by counting temporary-on-rez non-mesh linksets on the target parcel or same-owner parcels.
  - Aligns `llGetParcelDetails` `PARCEL_DETAILS_PRIM_CAPACITY` and `PARCEL_DETAILS_PRIM_USED` with Second Life's same-owner simulator-wide semantics.
  - Adds an in-world parcel prim count auditor example for estate/rental/rules consoles.

- Money transfer guard compatibility
  - Tightens `llGiveMoney` to require a positive amount, owner-granted `PERMISSION_DEBIT`, a non-group-owned object and an avatar target before calling the money backend.
  - Applies the same owner-granted debit and non-object target checks to `llTransferLindenDollars`.
  - Adds an in-world guarded payout lab example for vendor, rental-refund and reward-console testing.

- Script memory/profiler diagnostics
  - Wires YEngine `llGetMemoryLimit`, `llSetMemoryLimit` and `llGetSPMaxMemory` to the actual per-script heap limit and configured heap maximum instead of static placeholder values.
  - Keeps `llGetUsedMemory` and `llGetFreeMemory` on the real YEngine heap counters and enforces lowered memory limits on subsequent allocations.
  - Adds `llScriptProfiler(PROFILE_SCRIPT_MEMORY/PROFILE_NONE)` compatibility state, records profiler flags/counters on prim dynamic attributes and ships an in-world memory/profiler lab example for stress testing heap growth, trimming and limit rejection.

- Sculpt animation compatibility
  - `llSetSculptAnim` now stores the requested sculpt animation mode, frame grid, frame range, rate and texture-sync flag in prim dynamic attributes.
  - The requested state is mirrored through the normal texture-animation update path to provide viewer-visible sculpt texture playback where a viewer honors texture animation.

- Regression and RegionWeb compatibility center
  - Converts RegionWeb `/regionweb/scripts` into an LSL Compatibility Center with documented signatures, return values, permissions, usage notes and implementation status for every locally tracked compatibility function.
  - Auto-discovers any public `ll*` method exposed by `ILSL_Api` that does not yet have a hand-written RegionWeb entry, so newly added functions remain visible instead of silently falling out of the web reference.
  - Imports `//ApiDesc` comments, source signatures and source return types from `ILSL_Api.cs` when the source tree is available, so the web reference keeps precise LSL-facing types instead of relying only on reflection aliases.
  - Adds `doc/script-engine-regression/manifest.json` as the repeatable checklist for post-build in-world compatibility verification.
  - Adds `31_lsl_compatibility_lab_controller.lsl`, an owner-run in-world regression controller covering linkset data, JSON/hash/HMAC, script memory/profiler, object details, PBR/GLTF override storage, Combat2 quiet-window damage and pathfinding callback behavior.
  - Adds `doc/script-engine-regression/report.py` to summarize OpenSim logs from the lab and fail when required manifest passes are missing.
  - Adds `doc/script-engine-regression/sl_coverage.py` to compare the local `ILSL_Api` surface against the official Second Life Wiki LSL function category and emit missing/local-only function reports.

## Missing Or Backend-Limited After This Pass

- Linden Lab's proprietary baked navmesh service is still not present; this branch supplies a per-region baked terrain cache, terrain/object/avatar-clearance A* routing and persistent character state inside the simulator.
- Combat2 damage adjustment is pre-health through a server-side transaction/quiet window; it still does not expose a Linden-owned external Combat2 service contract.
- Full arbitrary EEP day-cycle frame/track asset editing beyond supported day info, sky tracks and persistent sky/water parameter subsets.
- Unsupported or future GLTF extensions outside the supported SL PBR material fields.
- A separate viewer protocol field for sculpt-map animation is still absent, so visible playback uses texture animation as transport.
- Linden viewer profiler UI/capability parity is still absent; profiler state/counters are exposed through simulator-side storage for local tools.
- Full Linden Lab external service parity, where behavior depends on SL-only grid services rather than script-engine functions alone.

## Next High-Value Buckets

- Pathfinding backend work if OpenSim gains a Linden-compatible region navmesh provider beyond the current local baked terrain cache and dynamic object/avatar clearance model.
- Environment functions: advanced day-cycle/track editing if OpenSim exposes more SL-compatible EEP storage primitives.
- Render material functions: additional GLTF extension inspection if OpenSim exposes those asset fields safely.
- Damage/combat functions: protocol-level Combat2 service parity if OpenSim gains Linden-compatible simulator/viewer contracts beyond the current local pre-health transaction model.
- Sculpt animation: simulator/viewer protocol support if OpenSim gains a real backend for it.
