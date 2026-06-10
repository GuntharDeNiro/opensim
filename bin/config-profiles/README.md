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
- ubODE physics with Vanilla Sim realism tuning for solver precision, terrain contact and material friction/bounce
- Warp3D map rendering with depth-shaded water
- Weather module with automatic forecast cycling and visitor IMs
- Built-in Groups Module V2 with local SQLite storage
- Offline IM V2 with local SQLite storage for group invites and notices
- RegionWeb
- Viewer-visible local currency ledger
- RegionWeb wallet in request mode by default
- PayPal settings present but disabled until real credentials are configured
- TextBuild enabled on channel `/88` for estate managers

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
