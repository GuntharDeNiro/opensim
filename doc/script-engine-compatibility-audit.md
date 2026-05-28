# Second Life LSL Compatibility Audit

Source baseline: official Second Life Wiki `Category:LSL Functions`, captured 2026-05-28.

This document tracks the script-engine compatibility pass against Second Life LSL.
The goal is to make missing or divergent behavior explicit before implementing it,
so additions are deliberate and testable instead of guessed from individual scripts.

## Current Pass

- Official Second Life function names collected: 519.
- OpenSim LSL stub exported functions collected from `LSL_Stub.cs`.
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

## Missing From OpenSim LSL Stub After Initial Scan

These require implementation review and either proper support, deliberate no-op
compatibility behavior, or a documented unsupported status.

- `llAdjustDamage`
- `llCreateCharacter`
- `llDamage`
- `llDeleteCharacter`
- `llDetectedDamage`
- `llDetectedRezzer`
- `llEvade`
- `llExecCharacterCmd`
- `llFindNotecardTextSync`
- `llFleeFrom`
- `llGetAttachedListFiltered`
- `llGetClosestNavPoint`
- `llGetEnvironment`
- `llGetStaticPath`
- `llGiveAgentInventory`
- `llNavigateTo`
- `llOpenFloater`
- `llPatrolPoints`
- `llPursue`
- `llReplaceAgentEnvironment`
- `llReplaceEnvironment`
- `llReturnObjectsByID`
- `llReturnObjectsByOwner`
- `llSetAgentEnvironment`
- `llSetAgentRot`
- `llSetEnvironment`
- `llSetGroundTexture`
- `llSetLinkGLTFOverrides`
- `llSetLinkRenderMaterial`
- `llSignRSA`
- `llTransferOwnership`
- `llUpdateCharacter`
- `llVerifyRSA`
- `llWanderWithin`

## Next High-Value Buckets

- Pathfinding/character functions: `llCreateCharacter`, `llNavigateTo`, `llWanderWithin`, and related commands.
- Environment functions: parcel/agent/region environment replacements and queries.
- Render material functions: GLTF and render-material setters.
- Damage/combat functions: damage metadata and detected damage.
- Administrative return/ownership helpers.
- Crypto helpers: RSA signing and verification.
