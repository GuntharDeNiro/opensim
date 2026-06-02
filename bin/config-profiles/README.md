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
   powershell -ExecutionPolicy Bypass -File .\config-profiles\switch-to-standalone-hg.ps1 -HostName 173.212.208.126
   ```

   Replace `173.212.208.126` with the public IP or DNS name that Hypergrid
   visitors can reach. Do not use `127.0.0.1` for a public Hypergrid.

3. Switch back to OSGrid:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\config-profiles\switch-to-osgrid.ps1
   ```

Existing Regions
----------------

For your current server, do not pass `-InstallFreshRegions`. The standalone
switcher will only replace `OpenSim.ini`, and it will leave your existing
`Regions\Regions.ini` untouched.

For a new clean lab only, you can install the sample region too:

```powershell
powershell -ExecutionPolicy Bypass -File .\config-profiles\switch-to-standalone-hg.ps1 -HostName 173.212.208.126 -InstallFreshRegions
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
