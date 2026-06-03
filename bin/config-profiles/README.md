OpenSim Config Profiles
=======================

This folder is a non-destructive switch kit for moving the same OpenSim build
between your existing OSGrid setup and a local standalone Hypergrid setup.

It does not modify anything by itself. The PowerShell helpers below only run
when you call them from `bin`.

Quick Workflow
--------------

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

   To publish the standalone regions to OSGrid, ViBel, FrancoGrid, 3rd Rock Grid
   and Metropolis at the same time:

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
wallet-request and PayPal-order files under `bin\StandaloneHG\Currency\`. That
keeps standalone users, inventory, assets, friends and local currency state
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
- ubODE physics
- Warp3D map rendering with depth-shaded water
- RegionWeb
- Viewer-visible local currency ledger
- RegionWeb wallet in request mode by default
- PayPal settings present but disabled until real credentials are configured
- TextBuild enabled on channel `/88` for estate managers

Multi-Grid Attachments
----------------------

The standalone profile includes a disabled `[MultiGridAttachments]` section.
When enabled, the primary grid registration still happens first as usual, then
the simulator fan-outs the same region metadata to any named secondary grid
registries:

```ini
[MultiGridAttachments]
    Enabled = true
    Grids = "osgrid,vibel,francogrid,thirdrock,metropolis"
    ContinueOnFailure = true

[MultiGridAttachment.osgrid]
    Enabled = true
    GridServerURI = "http://hg.osgrid.org:80"
    ExternalHostName = "vanilla-sim.com"
    ServerURI = "http://vanilla-sim.com:9000"
    Regions = ""
    Location = ""
    Strict = false

[MultiGridAttachment.vibel]
    Enabled = true
    GridServerURI = "http://grid.vibel.eu:8002"
    ExternalHostName = "vanilla-sim.com"
    ServerURI = "http://vanilla-sim.com:9000"
    Regions = ""
    Location = ""
    Strict = false

[MultiGridAttachment.francogrid]
    Enabled = true
    GridServerURI = "http://hg.francogrid.org:80"
    ExternalHostName = "vanilla-sim.com"
    ServerURI = "http://vanilla-sim.com:9000"
    Regions = ""
    Location = ""
    Strict = false

[MultiGridAttachment.thirdrock]
    Enabled = true
    GridServerURI = "http://grid.3rdrockgrid.com:8002"
    ExternalHostName = "vanilla-sim.com"
    ServerURI = "http://vanilla-sim.com:9000"
    Regions = ""
    Location = ""
    Strict = false

[MultiGridAttachment.metropolis]
    Enabled = true
    GridServerURI = "http://hypergrid.org:8002"
    ExternalHostName = "vanilla-sim.com"
    ServerURI = "http://vanilla-sim.com:9000"
    Regions = ""
    Location = ""
    Strict = false
```

For a friend's private grid, add another name to `Grids` and another
attachment section:

```ini
[MultiGridAttachments]
    Grids = "osgrid,vibel,francogrid,thirdrock,metropolis,friend"

[MultiGridAttachment.friend]
    Enabled = true
    GridServerURI = "http://friend-grid.example.com:8002"
    ExternalHostName = "vanilla-sim.com"
    ServerURI = "http://vanilla-sim.com:9000"
    Regions = "Vanilla Code,Vanilla Test"
    Location = ""
    Strict = false
    ; AuthType = "BasicHttpAuthentication"
    ; HttpAuthUsername = ""
    ; HttpAuthPassword = ""
```

Use this as a publication/attachment layer, not as three mixed identity
backends. The region keeps one primary grid for inventory, assets, user
accounts and presence. A secondary grid must allow your simulator to register
with its grid service; public grids may refuse this unless they explicitly
support or authorize it.

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
