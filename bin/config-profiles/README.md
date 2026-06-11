OpenSim Config Profiles
=======================

This folder is a non-destructive switch kit for moving the same OpenSim build
between your existing OSGrid setup and a local standalone Hypergrid setup.

It does not modify anything by itself. The PowerShell helpers below only run
when you call them from `bin`.

Quick Workflow
--------------

Easy server wrappers from `bin`:

```bat
switch-to-standalone-hg.bat
switch-to-osgrid.bat
switch-to-multigrid.bat
```

The standalone and multigrid wrappers default to `vanilla-sim.com`. To use a
different public Hypergrid DNS name, pass it as the first argument:

```bat
switch-to-standalone-hg.bat example.com
switch-to-multigrid.bat example.com
```

1. While your current OSGrid configuration is working, capture it once:

   ```powershell
   cd C:\Users\Administrator\Desktop\opensim\opensim\bin
   powershell -ExecutionPolicy Bypass -File .\config-profiles\capture-osgrid-profile.ps1
   ```

2. Switch to standalone Hypergrid:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\config-profiles\switch-to-standalone-hg.ps1 -HostName vanilla-sim.com
   ```

   Replace `vanilla-sim.com` with the public DNS name that Hypergrid visitors
   can reach. Some grids reject raw-IP Hypergrid addresses, so prefer a domain
   over `173.212.208.126`. Do not use `127.0.0.1` for a public Hypergrid.

   To publish the standalone regions to OSGrid and Neverworld Grid at the same
   time:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\config-profiles\switch-to-standalone-hg.ps1 -HostName vanilla-sim.com -AttachPublicGrids
   ```

3. Switch back to OSGrid:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\config-profiles\switch-to-osgrid.ps1
   ```

Existing Regions
----------------

For your current server, do not pass `-InstallFreshRegions`. The standalone
switcher will only replace `OpenSim.ini`, and it will leave your existing
`Regions\Regions.ini` untouched.

Databases
---------

The standalone Hypergrid profile includes and switches
`config-include\storage\SQLiteStandalone.ini` to dedicated SQLite files under
`bin\StandaloneHG\`. It also writes the local currency balance, transaction,
wallet-request and PayPal-order files under `bin\StandaloneHG\Currency\`, plus
the built-in V2 groups database under `bin\StandaloneHG\groups.db` and offline
IM storage under `bin\StandaloneHG\offlineim.db`. That keeps standalone users,
inventory, assets, friends, groups, offline messages and local currency state
separate from the OSGrid profile.

The capture command stores your current `OpenSim.ini` and current
`config-include\storage\SQLiteStandalone.ini` under `config-profiles\osgrid\`.
The OSGrid switch restores both files when that captured storage profile exists.

For a new clean lab only, you can install the sample region too:

```powershell
powershell -ExecutionPolicy Bypass -File .\config-profiles\switch-to-standalone-hg.ps1 -HostName vanilla-sim.com -InstallFreshRegions
```

What The Standalone Profile Enables
-----------------------------------

- `config-include/StandaloneHypergrid.ini`
- `GatekeeperURI` and `HomeURI` for Hypergrid travel
- YEngine
- ubODE physics with Vanilla Sim realism tuning for solver precision, avatar movement, terrain contact, material friction/bounce and procedural boat water dynamics
- Warp3D map rendering with depth-shaded water
- Weather module with automatic forecast cycling and visitor IMs
- Built-in Groups Module V2 with local SQLite storage
- Offline IM V2 with local SQLite storage for group invites and notices
- RegionWeb
- Viewer-visible local currency ledger
- RegionWeb wallet in request mode by default
- PayPal settings present but disabled until real credentials are configured
- TextBuild enabled on channel `/88` for estate managers

Physics Realism Tuning
----------------------

Vanilla Sim uses ubODE by default and reads the realism profile from
`[ODEPhysicsSettings]` in `OpenSimDefaults.ini`. The important knobs are:

```ini
world_stepsize = 0.01333
world_solver_iterations = 24
body_frames_auto_disable = 180
world_erp = 0.52
world_cfm = 0.00075
world_linear_damping = 0.0005
world_angular_damping = 0.001
world_contact_surface_layer = 0.006
world_contact_max_correcting_velocity = 25.0
world_contact_bounce_velocity = 0.15
world_contact_slip = 0.01
avatar_terminal_velocity = 54
ubode_terrain_friction = 0.64
ubode_terrain_bounce = 0.72
physical_prim_material_density_enabled = true
material_stone_density = 18.0
material_metal_density = 24.0
material_glass_density = 9.0
material_wood_density = 6.0
material_flesh_density = 7.0
material_plastic_density = 4.5
material_rubber_density = 3.5
material_light_density = 1.0
material_rubber_bounce = 0.98
boat_water_dynamics_enabled = true
boat_wave_height_1 = 0.09
boat_wave_drift_scale = 0.25
physical_prim_water_dynamics_enabled = true
physical_prim_water_smoothing_timescale = 0.65
physical_prim_water_surface_damping = 3.5
physical_prim_water_max_rise_acceleration = 2.5
physical_prim_water_drag = 1.35
physical_prim_water_drift_scale = 12.0
physical_prim_water_righting_strength = 3.0
physical_prim_water_angular_damping = 5.0
physical_prim_air_dynamics_enabled = true
physical_prim_air_linear_drag = 0.018
physical_prim_air_angular_drag = 0.004
physical_prim_resting_damping_enabled = true
physical_prim_resting_linear_damping = 3.0
physical_prim_resting_angular_damping = 2.0
physical_prim_resting_speed = 0.08
physical_prim_resting_angular_speed = 0.12
physical_prim_shape_inertia_enabled = true
physical_prim_base_inertia_scale = 1.08
physical_prim_thin_shape_inertia_boost = 0.45
avatar_friction = 0.35
avatar_physics_tuning_enabled = true
avatar_ground_rest_damping = 0.55
avatar_landing_damping = 0.35
avatar_air_control_scale = 0.65
avatar_contact_normal_smoothing = 0.35
avatar_slope_damping = 0.18
avatar_slope_speed_damping = 0.35
avatar_ground_traction = 0.14
avatar_movement_smoothing_timescale = 0.12
avatar_step_assist_strength = 0.18
avatar_step_assist_max_velocity = 2.4
avatar_fall_damping_enabled = true
avatar_fall_damping = 0.18
avatar_water_dynamics_enabled = true
avatar_water_buoyancy = 0.92
avatar_water_drag = 0.85
avatar_water_walk_speed_scale = 0.55
avatar_water_surface_damping = 1.8
avatar_water_smoothing_timescale = 0.45
water_buoyancy_wood = 1.55
water_buoyancy_metal = 0.10
boat_turn_banking_enabled = true
```

At startup ubODE writes a `Vanilla physics tuning` line to `OpenSim.log` so you
can confirm which solver, contact and boat-water settings are active on the
server. Materials now drive density as well as contact response unless a viewer
or script has explicitly set a custom density: metal and stone feel heavier,
wood/plastic/rubber feel lighter, and the same-sized objects no longer all move
with the same weight. Rubber and plastic use a more visible bounce profile for
inworld demos; terrain contact uses a square-root bounce blend so low terrain
restitution does not cancel a bouncy material. Physical prims now also get
material-based water buoyancy: wood, plastic and rubber float, while metal and
stone mostly sink.
Floating prims receive wave drift and a small water-normal tilt so simple
inworld boat hulls can move without being scripted as vehicles first. The solver
runs at a slightly smaller step with softer contact correction so high-bounce
objects move smoothly instead of jittering between frames. Buoyant prims also
low-pass their water height, immersion, wave normal and drift so floating motion
feels heavier and more continuous instead of twitching with every tiny wave
sample. The water response also limits maximum upward rise and applies extra
surface damping when an object exits the water, so a wooden cube dropped from
above settles into the surface instead of pogoing. Submerged objects now receive
projected-area water drag, so broad faces push more water than narrow ones.
Physical prims also get light air drag and a near-rest damping pass that removes
small residual terrain or water jitter once an object is almost still. Shape
inertia tuning adds a little rotational weight, especially for thin or stretched
objects, so boards and poles do not spin like massless props. Buoyant prims use
their current orientation and dimensions to find a stable floating face:
cube-like shapes damp their spin, while stretched boxes right themselves so
their broad face settles toward the water surface.
Avatar movement also receives a conservative realism pass: light avatar-to-prim
friction, softer landings, reduced air steering while falling, near-rest ground
damping and water movement that slows and supports the avatar body instead of
making water feel like empty air. Contact normals are smoothed between physics
ticks, slopes add a little rest damping, and low prim edges get a small step
assist so walking over uneven builds feels less twitchy. ubODE also applies the
configured avatar terminal velocity with a gentle fall damping curve, and avatar
water immersion is smoothed over several physics ticks so walking into water or
breaking the surface feels less abrupt. Movement commands are also eased over a
short timescale, while slopes reduce push speed slightly and add lateral
traction so turning or stopping on uneven ground feels less skittery.

RegionWeb Portal And Inventory Carousels
----------------------------------------

RegionWeb is enabled by default at:

```text
http://vanilla-sim.com:9000/regionweb/
```

The same URL is also advertised through the grid info service as the viewer
login splash, economy, about, help and registration page. The generated portal
uses Vanilla Sim branding, sticky top navigation, page-local back links, a
back-to-top button, a GitHub link, a wallet entry and live estate/region stats.
Money Admin is not exposed as a main navigation item; it appears from the wallet
only after an estate owner opens the admin token flow.

RegionWeb can use owner inventory images for the front page and individual
region hero carousels. At startup it auto-creates these folders when they are
missing:

```text
RegionWeb Carousel
RegionWeb <Region Name> Carousel
```

Put snapshots or textures in `RegionWeb Carousel` to feed the estate landing
page carousel. Put snapshots or textures in `RegionWeb Vanilla Code Carousel`,
`RegionWeb Vanilla Test Carousel`, or the matching folder for another region to
feed that single region page. The module serves only assets found in authorized
carousel folders through `/regionweb/inventory-carousel/<asset-id>.jpg`.
Browser-ready JPEG/PNG/GIF/WebP assets are passed through, and JPEG2000 texture
assets are decoded to JPEG and cached.

If no inventory images are present, the landing page falls back to a slow
carousel of generated region map tiles. A single region page falls back to one
large full-bleed map hero instead of repeating the tile. Tune the behavior in
`[RegionWeb]`:

```ini
InventoryCarouselEnabled = true
InventoryCarouselFolder = "RegionWeb Carousel"
RegionInventoryCarouselFolderTemplate = "RegionWeb {RegionName} Carousel"
InventoryCarouselLimit = 12
InventoryCarouselCacheSeconds = 300
```

The scripted and generated RegionWeb feature pages also merge newer built-in
usage notes at render time. Existing `bin\RegionWeb\features\*.ini` files keep
their custom text, but they still pick up new default checklist items for recent
portal, wallet, multi-grid and script-engine features.

Multi-Grid Attachments
----------------------

The standalone profile includes a disabled `[MultiGridAttachments]` section.
When enabled, the primary grid registration still happens first as usual, then
the simulator fan-outs the same region metadata to any named secondary grid
registries:

```ini
[MultiGridAttachments]
    Enabled = true
    Grids = "osgrid,neverworld,zetasim,craft"
    ContinueOnFailure = true
    AutoCreateInboundPresence = true

[MultiGridAttachment.osgrid]
    Enabled = true
    GridServerURI = "http://grid.osgrid.org"
    GridPostURI = "http://grid.osgrid.org/grid"
    ExternalHostName = "vanilla-sim.com"
    ServerURI = "http://vanilla-sim.com:9000"
    Regions = ""
    Location = ""
    TimeoutSeconds = 5
    Strict = false

[MultiGridAttachment.neverworld]
    Enabled = true
    GridServerURI = "http://hg.neverworldgrid.com:8003"
    GridPostURI = "http://hg.neverworldgrid.com:8003/grid"
    ExternalHostName = "vanilla-sim.com"
    ServerURI = "http://vanilla-sim.com:9000"
    Regions = ""
    Location = ""
    TimeoutSeconds = 5
    Strict = false

[MultiGridAttachment.zetasim]
    Enabled = true
    GridServerURI = "http://robust.zetaworlds.com:8003"
    GridPostURI = "http://robust.zetaworlds.com:8003/grid"
    ExternalHostName = "vanilla-sim.com"
    ServerURI = "http://vanilla-sim.com:9000"
    Regions = ""
    Location = ""
    TimeoutSeconds = 5
    Strict = false

[MultiGridAttachment.craft]
    Enabled = true
    GridServerURI = "http://craft-world.org:8003"
    GridPostURI = "http://craft-world.org:8003/grid"
    ExternalHostName = "vanilla-sim.com"
    ServerURI = "http://vanilla-sim.com:9000"
    AuthType = "BasicHttpAuthentication"
    HttpAuthUsername = "plipbadalippHH"
    HttpAuthPassword = "cTm78eVf5Lk2gBje8hyfdr1116gbh6"
    Regions = ""
    Location = ""
    TimeoutSeconds = 5
    Strict = false
```

In this standalone profile, Vanilla is already the primary grid. If you run a
different primary profile and want to publish back to Vanilla too, add
`vanilla` to `Grids` and enable the optional Vanilla attachment:

```ini
[MultiGridAttachments]
    Grids = "osgrid,neverworld,vanilla"

[MultiGridAttachment.vanilla]
    Enabled = true
    GridServerURI = "http://vanilla-sim.com:9000"
    ExternalHostName = "vanilla-sim.com"
    ServerURI = "http://vanilla-sim.com:9000"
    Regions = ""
    Location = ""
    TimeoutSeconds = 5
    Strict = false
```

Use this as a publication/attachment layer, not as three mixed identity
backends. The region keeps one primary grid for inventory, assets, user
accounts and presence. A secondary grid must allow your simulator to register
with its grid service; public grids may refuse this unless they explicitly
support or authorize it.

OSGrid exposes Hypergrid login and home identity on `http://hg.osgrid.org:80`,
but region registration uses its GridService endpoint on
`http://grid.osgrid.org/grid`. If you post registration to
`http://hg.osgrid.org:80/grid`, nginx may answer with HTML/502 instead of a
GridService XML response.

Neverworld Grid exposes login, gatekeeper and grid info on
`http://hg.neverworldgrid.com:8002`, but self-hosted region registration uses
the GridService endpoint on `http://hg.neverworldgrid.com:8003/grid`. If a
target returns HTML or an empty body from `/grid`, the configured URL is probably
a login/gatekeeper endpoint rather than a region registration endpoint.

ZetaWorlds exposes Hypergrid login and gatekeeper on
`http://hg.zetaworlds.com:80`, but self-hosted region registration uses its
Robust GridService endpoint on `http://robust.zetaworlds.com:8003/grid`.
The default ZetaWorlds Hypergrid destination is
`hop://hg.zetaworlds.com:80/Welcome/128/128/25`.

Craft World exposes Hypergrid login and gatekeeper on
`http://craft-world.org:8002`, but self-hosted region registration uses its
authenticated GridService endpoint on `http://craft-world.org:8003/grid`.
The default Craft Hypergrid destination is
`hop://craft-world.org:8002/Pulse%20Pavilion/128/128/25`.

Keep `AutoCreateInboundPresence = true` for multigrid publication. Some attached
grids teleport from their world map directly to this simulator's `/agent`
endpoint instead of entering through this grid's gatekeeper first; the simulator
then creates the local presence row needed by `Scene.VerifyUserPresence` before
continuing normal access checks.

Ports
-----

Open TCP `9000` for the simulator HTTP endpoint and Hypergrid services. Open
the UDP region ports used in `Regions\Regions.ini` for viewer traffic. If you
run several regions, every region usually needs its own UDP port.

Safety
------

Every switch backs up the current `OpenSim.ini` into
`bin\config-profiles\backups\`. The OSGrid profile is captured into
`bin\config-profiles\osgrid\OpenSim.ini` and can be overwritten by running
`capture-osgrid-profile.ps1 -Overwrite`.
