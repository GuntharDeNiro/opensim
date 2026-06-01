// Complex EEP sky console for the expanded llSetEnvironment/llSetAgentEnvironment support.
// Drop into a prim owned by an estate/parcel environment manager. Agent-local mode
// also needs the object or owner trusted by [ScriptExperiences].

integer MENU_CHANNEL = -882901;
integer gListen;
key gAgent;

list RegionSunset()
{
    rotation sun = llEuler2Rot(<0.0, 1.05, -0.45>);
    rotation moon = llEuler2Rot(<0.0, -1.80, 0.55>);

    return [
        ENVIRONMENT_DAYINFO, 14400, 61200,
        SKY_TRACKS, 800.0, 1600.0, 2600.0,
        SKY_AMBIENT, <0.44, 0.28, 0.20>,
        SKY_BLUE, <0.18, 0.28, 0.58>, <0.68, 0.44, 0.24>,
        SKY_HAZE, 0.62, 0.38, 0.0012, 7.5,
        SKY_SUN, sun, 1.25, <1.00, 0.56, 0.28>,
        SKY_MOON, moon, 0.78, 0.35,
        SKY_GLOW, 7.5, -0.55,
        SKY_CLOUDS, <0.95, 0.62, 0.38>, 0.42, 0.36, 0.18,
            <0.11, -0.04, 0.0>, <0.82, 0.48, 0.72>, <0.45, 0.38, 0.22>, 0,
        SKY_STAR_BRIGHTNESS, 18.0,
        WATER_FOG, <0.02, 0.12, 0.22>, 3.2, 0.45,
        WATER_WAVE_DIRECTION, <1.1, -0.7, 0.0>, <-0.3, 0.9, 0.0>
    ];
}

list ParcelDawn()
{
    rotation sun = llEuler2Rot(<0.0, 0.58, 0.35>);

    return [
        SKY_AMBIENT, <0.50, 0.46, 0.42>,
        SKY_BLUE, <0.30, 0.52, 0.92>, <0.75, 0.82, 0.94>,
        SKY_HAZE, 0.35, 0.22, 0.0008, 5.0,
        SKY_SUN, sun, 1.05, <1.0, 0.82, 0.56>,
        SKY_CLOUDS, <0.78, 0.82, 0.86>, 0.24, 0.48, 0.12,
            <0.03, 0.02, 0.0>, <0.72, 0.42, 0.62>, <0.35, 0.22, 0.14>, 0,
        SKY_GAMMA, 1.02,
        WATER_FRESNEL, 0.32, 0.54,
        WATER_REFRACTION, 0.04, 0.18
    ];
}

list AgentAurora()
{
    rotation sun = llEuler2Rot(<0.0, 1.35, -0.10>);
    rotation moon = llEuler2Rot(<0.0, -1.20, 0.0>);

    return [
        SKY_AMBIENT, <0.18, 0.28, 0.38>,
        SKY_BLUE, <0.05, 0.16, 0.30>, <0.08, 0.22, 0.36>,
        SKY_HAZE, 0.20, 0.10, 0.0005, 4.0,
        SKY_SUN, sun, 0.65, <0.22, 0.38, 0.60>,
        SKY_MOON, moon, 1.15, 0.75,
        SKY_CLOUDS, <0.22, 0.72, 0.62>, 0.18, 0.55, 0.30,
            <-0.06, 0.12, 0.0>, <0.52, 0.35, 0.80>, <0.18, 0.58, 0.42>, 0,
        SKY_STAR_BRIGHTNESS, 220.0,
        SKY_REFLECTION_PROBE_AMBIANCE, 1.0,
        WATER_FOG, <0.01, 0.08, 0.14>, 4.6, 0.60
    ];
}

ShowMenu(key agent)
{
    if (gListen)
        llListenRemove(gListen);

    gAgent = agent;
    gListen = llListen(MENU_CHANNEL, "", agent, "");
    llDialog(agent, "EEP sky environment console", [
        "Region Sunset",
        "Parcel Dawn",
        "Agent Aurora",
        "Report",
        "Clear Parcel",
        "Help"
    ], MENU_CHANNEL);
}

SayResult(string label, integer result)
{
    if (result == 1)
        llOwnerSay(label + ": OK");
    else
        llOwnerSay(label + ": ENV result " + (string)result);
}

default
{
    state_entry()
    {
        llOwnerSay("Touch for EEP sky presets. Region changes need estate rights; agent mode needs Experience trust.");
    }

    touch_start(integer total)
    {
        ShowMenu(llDetectedKey(0));
    }

    listen(integer channel, string name, key id, string message)
    {
        if (message == "Region Sunset")
        {
            SayResult("Region sunset", llSetEnvironment(<-1.0, -1.0, -1.0>, RegionSunset()));
        }
        else if (message == "Parcel Dawn")
        {
            SayResult("Parcel dawn", llSetEnvironment(llGetPos(), ParcelDawn()));
        }
        else if (message == "Agent Aurora")
        {
            gAgent = id;
            llRequestExperiencePermissions(id, "EEP sky console");
        }
        else if (message == "Report")
        {
            list data = llGetEnvironment(llGetPos(), [
                ENVIRONMENT_DAYINFO,
                SKY_TRACKS,
                SKY_AMBIENT,
                SKY_BLUE,
                SKY_HAZE,
                SKY_SUN,
                SKY_CLOUDS,
                WATER_FOG,
                WATER_WAVE_DIRECTION
            ]);
            llOwnerSay("Environment report: " + llDumpList2String(data, " | "));
        }
        else if (message == "Clear Parcel")
        {
            SayResult("Clear parcel environment", llSetEnvironment(llGetPos(), []));
        }
        else if (message == "Help")
        {
            llOwnerSay("Region Sunset writes ENVIRONMENT_DAYINFO, SKY_TRACKS, SKY_* and WATER_* to the whole region. Parcel Dawn writes the current parcel. Agent Aurora uses llSetAgentEnvironment after Experience permissions.");
        }
    }

    experience_permissions(key agent)
    {
        SayResult("Agent aurora", llSetAgentEnvironment(agent, 3.0, AgentAurora()));
    }

    experience_permissions_denied(key agent, integer reason)
    {
        llOwnerSay("Experience denied for " + (string)agent + ": " + llGetExperienceErrorMessage(reason));
    }
}
