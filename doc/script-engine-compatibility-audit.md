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
  - Adds region/parcel/agent environment replacement and clearing through the existing EEP environment module.
  - Per-parameter `llSetEnvironment`/`llSetAgentEnvironment` overrides return `ENV_INVALID_RULE` until OpenSim has matching persistent override storage.

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
  - Exposes `llSetSculptAnim` for script compatibility; OpenSim still lacks a sculpt-map animation backend.
  - Keeps `llGodLikeRezObject` restricted to actual god-mode script owners instead of logging unsupported while still rezzing.

- Damage and combat helpers
  - Adds `llDamage` using OpenSim's existing avatar health and death/teleport-home path.
  - Adds `PRIM_DAMAGE` and `PRIM_HEALTH` support in primitive params.
  - Adds `OBJECT_HEALTH`, `OBJECT_DAMAGE` and `OBJECT_DAMAGE_TYPE` details.
  - Exposes `llDetectedDamage` as an empty result outside missing Combat2 event metadata.
  - Exposes `llAdjustDamage` with an explicit unsupported status because OpenSim does not yet carry `on_damage` adjustment state.

- Pathfinding compatibility surface
  - Exposes the Second Life pathfinding/character function names and constants so scripts compile.
  - `llGetStaticPath` returns `PU_FAILURE_NO_NAVMESH`.
  - Character movement commands post `path_update(PU_FAILURE_NO_NAVMESH, [])` instead of faking movement.

- GLTF override helpers
  - Adds `llSetLinkGLTFOverrides` for material factor overrides backed by OpenSim render material override storage.
  - Supports base color/alpha, alpha mode, alpha mask, double-sided, metallic, roughness and emissive factors.
  - Adds `PRIM_RENDER_MATERIAL` support through `llSetPrimitiveParams`, `llSetLinkPrimitiveParams`, `llGetPrimitiveParams` and `llGetLinkPrimitiveParams`.
  - Adds `PRIM_GLTF_NORMAL`, `PRIM_GLTF_EMISSIVE`, `PRIM_GLTF_METALLIC_ROUGHNESS` and `PRIM_GLTF_BASE_COLOR` set/readback for stored override values through primitive params.
  - Reads compact texture, transform and factor overrides without claiming access to unset values inside the underlying material asset.

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
  - Adds an in-world identity lookup example covering synchronous and dataserver-based name/key lookups.

- Parcel prim count compatibility
  - Completes `llGetParcelPrimCount` for same-owner simulator-wide `PARCEL_COUNT_OWNER`, `PARCEL_COUNT_GROUP`, `PARCEL_COUNT_OTHER` and `PARCEL_COUNT_SELECTED`.
  - Adds `PARCEL_COUNT_TEMP` support by counting temporary-on-rez non-mesh linksets on the target parcel or same-owner parcels.
  - Adds an in-world parcel prim count auditor example for estate/rental/rules consoles.

## Missing Or Backend-Limited After This Pass

- True Second Life navmesh/pathfinding character simulation.
- Combat2 `on_damage` event state and mutable per-hit damage adjustment.
- Full per-parameter EEP override persistence for `llSetEnvironment` and `llSetAgentEnvironment`.
- Underlying material asset readback for unset `PRIM_GLTF_*` properties.
- True client-visible sculpt-map animation for `llSetSculptAnim`.

## Next High-Value Buckets

- Pathfinding backend work if OpenSim gains a region navmesh provider.
- Environment functions: full per-parameter EEP override storage for `llSetEnvironment` and `llSetAgentEnvironment`.
- Render material functions: optional material asset inspection if OpenSim exposes it safely.
- Damage/combat functions: `on_damage` event metadata and adjustment.
- Sculpt animation: simulator/viewer protocol support if OpenSim gains a real backend for it.
