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

Stock OpenSim may fail to compile or run these scripts because Experience events,
KVP functions and scripted-only sit flags are not available there.
