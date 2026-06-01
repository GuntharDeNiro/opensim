# Second Life-style Script Engine Examples

These scripts demonstrate features that work in Second Life through Experiences,
but are missing or incomplete in stock OpenSim. They are intended to work with
this build's Experience-Lite script engine.

Required simulator config:

```ini
[ScriptExperiences]
Enabled = true
AllowEstateManagers = true
KeyValueStoreEnabled = true
```

Trust the script owner or the specific object:

```ini
TrustedOwners = 00000000-0000-0000-0000-000000000000
TrustedObjects = 00000000-0000-0000-0000-000000000000
```

The scripts use:

- `llRequestExperiencePermissions`
- `experience_permissions`
- `experience_permissions_denied`
- `llAgentInExperience`
- `llGetExperienceDetails`
- `llSitOnLink`
- `llCreateKeyValue`
- `llReadKeyValue`
- `llUpdateKeyValue`
- `llDeleteKeyValue`
- `llDataSizeKeyValue`
- `llKeyCountKeyValue`
- `llKeysKeyValue`
- `llGetExperienceKeyValueStoreStats`
- `llGetExperienceErrorMessage`
- `llSetLinkSitFlags`
- `llGetLinkSitFlags`
- `PRIM_SCRIPTED_SIT_ONLY`
- `PRIM_ALLOW_UNSIT`
- `llDetectedRezzer`
- `llGetAttachedListFiltered`
- `llFindNotecardTextSync`
- `llMatchGroup`
- `llSetParcelForSale`
- `llReturnObjectsByID`
- `llReturnObjectsByOwner`
- `llSetGroundTexture`
- `llSetLinkRenderMaterial`
- `llSetLinkGLTFOverrides`
- `PRIM_RENDER_MATERIAL`
- `PRIM_GLTF_*` setters through primitive params
- `PRIM_GLTF_*` readback from assigned material assets and stored overrides
- `PRIM_GLTF_NORMAL`
- `PRIM_GLTF_EMISSIVE`
- `PRIM_GLTF_METALLIC_ROUGHNESS`
- `PRIM_GLTF_BASE_COLOR`
- `PRIM_PHYSICS_MATERIAL`
- `llGiveAgentInventory`
- `llTransferOwnership`
- `llGetObjectDetails` cost and render-weight readback
- `OBJECT_SERVER_COST`
- `OBJECT_STREAMING_COST`
- `OBJECT_PHYSICS_COST`
- `OBJECT_PRIM_EQUIVALENCE`
- `OBJECT_RENDER_WEIGHT`
- `OBJECT_HOVER_HEIGHT`
- `OBJECT_SELECT_COUNT`
- `llKey2Name`
- `llGetUsername`
- `llGetDisplayName`
- `llName2Key`
- `llRequestUsername`
- `llRequestDisplayName`
- `llRequestUserKey`
- `llGetAgentLanguage`
- `llGetVisualParams`
- `llGetEnvironment`
- `llSetEnvironment`
- `llSetAgentEnvironment`
- `SKY_AMBIENT`
- `SKY_BLUE`
- `SKY_CLOUDS`
- `SKY_DOME`
- `SKY_GAMMA`
- `SKY_GLOW`
- `SKY_HAZE`
- `SKY_MOON`
- `SKY_PLANET`
- `SKY_REFRACTION`
- `SKY_REFLECTION_PROBE_AMBIANCE`
- `SKY_STAR_BRIGHTNESS`
- `SKY_SUN`
- `SKY_TRACKS`
- `SKY_CLOUD_TEXTURE`
- `SKY_MOON_TEXTURE`
- `SKY_SUN_TEXTURE`
- `llGetAnimation`
- `llGetAnimationList`
- `llGetEnergy`
- `llApplyImpulse`
- `llApplyRotationalImpulse`
- `llSetForce`
- `llSetTorque`
- `llSetHoverHeight`
- `llSetBuoyancy`
- `llPushObject`
- `llGetParcelPrimCount`
- `llGetParcelDetails`
- `llGetParcelPrimOwners`
- `llGiveMoney`
- `llTransferLindenDollars`
- `PERMISSION_DEBIT`
- `llGetFreeMemory`
- `llGetUsedMemory`
- `llGetMemoryLimit`
- `llSetMemoryLimit`
- `llGetSPMaxMemory`
- `llScriptProfiler`
- `PROFILE_NONE`
- `PROFILE_SCRIPT_MEMORY`
- `PARCEL_COUNT_TOTAL`
- `PARCEL_COUNT_OWNER`
- `PARCEL_COUNT_GROUP`
- `PARCEL_COUNT_OTHER`
- `PARCEL_COUNT_SELECTED`
- `PARCEL_COUNT_TEMP`
- `PARCEL_DETAILS_PRIM_CAPACITY`
- `PARCEL_DETAILS_PRIM_USED`
- `PARCEL_MEDIA_COMMAND_LOOP_SET`
- `WATER_BLUR_MULTIPLIER`
- `WATER_FOG`
- `WATER_FRESNEL`
- `WATER_NORMAL_SCALE`
- `WATER_REFRACTION`
- `WATER_WAVE_DIRECTION`
- `WATER_NORMAL_TEXTURE`
- `ENV_NO_PERMISSIONS`
- `ENV_NO_ENVIRONMENT`
- `ENV_INVALID_RULE`
- `ENV_VALIDATION_FAIL`
- `ENV_NOT_EXPERIENCE`
- `ENV_NO_EXPERIENCE_PERMISSION`
- `ENV_INVALID_AGENT`
- `ENVIRONMENT_DAYINFO`
- `TRANSFER_DEST`
- `TRANSFER_FLAGS`
- `TRANSFER_FLAG_COPY`
- `TRANSFER_FLAG_TAKE`
- `on_damage`
- `final_damage`
- `on_death`
- `llDetectedDamage`
- `llAdjustDamage`
- `llDamage`
- `llGetHealth`
- `llCreateCharacter`
- `llNavigateTo`
- `llWanderWithin`
- `llGetStaticPath`
- `llGetClosestNavPoint`
- `llExecCharacterCmd`
- `llSetSculptAnim`
- `ANIM_ON`
- `LOOP`
- `DAMAGE_TYPE_*`
- `CHARACTER_*`
- `PU_*`

## Files

- `01_experience_camera_tour_pad.lsl`: visitor memory, camera, controls and KVP stats.
- `02_experience_teleporter.lsl`: popup-free trusted teleporter with remembered visits.
- `03_persistent_access_door.lsl`: owner-managed access door backed by persistent KVP.
- `04_experience_quest_tracker.lsl`: persistent per-avatar quest progress.
- `05_vehicle_preference_rezzer.lsl`: remembers per-avatar vehicle model/color preferences.
- `06_ai_build_memory_panel.lsl`: stores AI build project notes and command history.
- `07_daily_reward_vendor.lsl`: daily reward cooldown remembered per avatar.
- `08_region_passport_station.lsl`: persistent travel passport stamps.
- `09_persistent_rental_meter.lsl`: owner-controlled rental tenant/expiry memory.
- `10_scene_preset_controller.lsl`: persistent estate scene preset switcher.
- `11_experience_leaderboard.lsl`: persistent player score storage and listing.
- `12_experience_seat_manager.lsl`: Experience scripted sitting on linked seats.
- `13_scripted_only_sit_flags.lsl`: blocks manual sit and seats avatars only through `llSitOnLink`.
- `14_modern_estate_operations_console.lsl`: complex estate console using group matching, attachment filtering, policy notecard search, parcel sale, object return, terrain and PBR helpers.
- `15_rezzer_provenance_quarantine.lsl`: complex provenance scanner using rezzer detection, notecard trust policy, HUD filtering and scripted return-by-ID quarantine.
- `16_inventory_transfer_and_ownership_lab.lsl`: complex inventory transfer lab using `llGiveAgentInventory`, destination roots, transfer result codes and ownership copy/take modes.
- `17_parcel_media_loop_console.lsl`: parcel media console using loop-set command/query, media type, description, integer size and auto-align persistence.
- `18_pbr_gltf_physics_param_lab.lsl`: render material, GLTF asset/override readback, future GLTF extension JSON storage and physics material primitive-param lab.
- `19_object_details_diagnostics_console.lsl`: complex object/avatar diagnostics console using `llGetObjectDetails` cost, render-weight, hover-height, selection, provenance, hover-text and damage readback.
- `20_identity_lookup_console.lsl`: complex identity diagnostics console using synchronous and asynchronous name, username, display-name and key lookups.
- `21_parcel_prim_count_auditor.lsl`: parcel audit console using local and same-owner simulator-wide `llGetParcelPrimCount`, `llGetParcelDetails` capacity/used values and owner/count breakdowns.
- `22_money_transfer_guard_lab.lsl`: debit-permission payout console demonstrating guarded `llGiveMoney` and `llTransferLindenDollars` flows.
- `23_script_memory_profiler_lab.lsl`: memory/profiler console demonstrating real YEngine heap used/free/limit/max reporting and active memory-limit enforcement.
- `24_agent_language_privacy_panel.lsl`: avatar language scanner demonstrating SL-style in-region and public-language checks for `llGetAgentLanguage`.
- `25_animation_state_monitor.lsl`: avatar animation monitor demonstrating seated-state `llGetAnimation` reporting and active animation UUID inspection with `llGetAnimationList`.
- `26_physics_energy_meter.lsl`: physics energy meter demonstrating dynamic `llGetEnergy` drain/recharge around force, torque, impulse, hover, buoyancy and push operations.
- `27_avatar_visual_param_inspector.lsl`: avatar visual parameter inspector demonstrating `llGetVisualParams` ids, names, aliases, unsupported entries, owner/toucher scans and periodic watch mode.
- `28_eep_water_environment_console.lsl`: EEP water console demonstrating `llGetEnvironment`, region/parcel `llSetEnvironment` and agent-local `llSetAgentEnvironment` water overrides.
- `29_eep_sky_environment_console.lsl`: EEP sky console demonstrating persistent `SKY_*` plus `WATER_*` parameter overrides for region, parcel and agent-local environments.
- `30_combat2_direct_path_sentry.lsl`: Combat2/pathfinding sentry demonstrating `on_damage`, `final_damage`, `on_death`, pre-health damage adjustment, damage metadata, obstacle-aware terrain pathfinding and visible sculpt texture animation fallback.

Stock OpenSim may fail to compile or run these scripts because Experience events,
KVP functions, scripted-only sit flags and newer Second Life LSL compatibility
functions are not available there.
