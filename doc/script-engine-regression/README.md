# LSL Regression Suite

This directory defines the repeatable compatibility checks for the Second
Life-style script APIs added by this branch.

## How to run

1. Build and start the simulator with the current branch.
2. Rez a single prim in the target region.
3. Drop `doc/script-engine-examples/31_lsl_compatibility_lab_controller.lsl`
   into the prim.
4. Touch the prim, or say `/31 run` as the object owner.
5. Wait for the asynchronous `final_damage` and `path_update` reports.
6. Confirm that every required case in `manifest.json` reports `PASS`.

The lab is intentionally in-world because many SL-compatible behaviors depend
on live simulator state: permissions, object inventory, scene presence,
pathfinding, damage events, linkset storage and viewer-visible PBR state.

## Pass criteria

- The script compiles without missing constants, missing functions or event
  signature errors.
- The synchronous tests report no `FAIL` lines.
- Combat2 reports both `on_damage` and `final_damage`, and `llAdjustDamage`
  reduces health only after the quiet window.
- Pathfinding returns a static route and posts `PU_GOAL_REACHED` after motion.
- RegionWeb `/regionweb/scripts` lists the touched APIs with a compatibility
  status and usage notes.
