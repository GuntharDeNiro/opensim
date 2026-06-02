OSGrid Profile Slot
===================

Run this while the server is using its working OSGrid configuration:

```powershell
cd C:\Users\Administrator\Desktop\opensim\opensim\bin
powershell -ExecutionPolicy Bypass -File .\config-profiles\capture-osgrid-profile.ps1
```

That stores the current `bin\OpenSim.ini` here as `OpenSim.ini`.

The switcher intentionally does not store or overwrite your region files.
Your existing `config-include\GridCommon.ini`, `Regions\Regions.ini`, estate
data and SQLite/MySQL data remain in their normal locations.
