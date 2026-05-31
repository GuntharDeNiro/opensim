// EEP Water Environment Console
// Demonstrates llGetEnvironment, llSetEnvironment and llSetAgentEnvironment water rules.

integer CHANNEL = 88;
integer gListen;
key gPendingAgent;
list gPendingPreset;

list WATER_DEEP = [
    WATER_FOG, <0.015, 0.135, 0.250>, 9.0, 0.25,
    WATER_FRESNEL, 0.52, 0.42,
    WATER_NORMAL_SCALE, <2.4, 2.0, 2.2>,
    WATER_REFRACTION, 0.04, 0.22,
    WATER_WAVE_DIRECTION, <1.05, -0.42, 0.0>, <1.11, -1.16, 0.0>,
    WATER_BLUR_MULTIPLIER, 0.045
];

list WATER_TROPICAL = [
    WATER_FOG, <0.020, 0.360, 0.430>, 7.5, 0.18,
    WATER_FRESNEL, 0.30, 0.58,
    WATER_NORMAL_SCALE, <1.5, 1.3, 1.1>,
    WATER_REFRACTION, 0.08, 0.16,
    WATER_WAVE_DIRECTION, <0.55, -0.20, 0.0>, <0.80, 0.55, 0.0>,
    WATER_BLUR_MULTIPLIER, 0.030
];

string vectorText(vector v)
{
    return "<" + llGetSubString((string)v.x, 0, 4) + ", "
        + llGetSubString((string)v.y, 0, 4) + ", "
        + llGetSubString((string)v.z, 0, 4) + ">";
}

string resultText(integer result)
{
    if (result == 1)
        return "OK";
    if (result == ENV_NO_PERMISSIONS)
        return "ENV_NO_PERMISSIONS";
    if (result == ENV_NO_ENVIRONMENT)
        return "ENV_NO_ENVIRONMENT";
    if (result == ENV_INVALID_RULE)
        return "ENV_INVALID_RULE";
    if (result == ENV_VALIDATION_FAIL)
        return "ENV_VALIDATION_FAIL";
    if (result == ENV_NOT_EXPERIENCE)
        return "ENV_NOT_EXPERIENCE";
    if (result == ENV_NO_EXPERIENCE_PERMISSION)
        return "ENV_NO_EXPERIENCE_PERMISSION";
    if (result == ENV_INVALID_AGENT)
        return "ENV_INVALID_AGENT";
    return "ENV result " + (string)result;
}

reportWater(vector pos)
{
    list values = llGetEnvironment(pos, [
        WATER_FOG,
        WATER_FRESNEL,
        WATER_NORMAL_SCALE,
        WATER_REFRACTION,
        WATER_WAVE_DIRECTION,
        WATER_BLUR_MULTIPLIER,
        WATER_NORMAL_TEXTURE
    ]);

    integer i = 0;
    vector fog = llList2Vector(values, i++);
    float density = llList2Float(values, i++);
    float underwater = llList2Float(values, i++);
    float fresnelOffset = llList2Float(values, i++);
    float fresnelScale = llList2Float(values, i++);
    vector normalScale = llList2Vector(values, i++);
    float refAbove = llList2Float(values, i++);
    float refBelow = llList2Float(values, i++);
    vector bigWave = llList2Vector(values, i++);
    vector littleWave = llList2Vector(values, i++);
    float blur = llList2Float(values, i++);
    string normalTexture = llList2String(values, i++);

    llOwnerSay(
        "Water at " + vectorText(pos) + "\n"
        + "Fog " + vectorText(fog) + " density " + (string)density + " underwater " + (string)underwater + "\n"
        + "Fresnel offset/scale " + (string)fresnelOffset + " / " + (string)fresnelScale + "\n"
        + "Normal scale " + vectorText(normalScale) + " texture " + normalTexture + "\n"
        + "Refraction above/below " + (string)refAbove + " / " + (string)refBelow + "\n"
        + "Waves big/little " + vectorText(bigWave) + " / " + vectorText(littleWave) + "\n"
        + "Blur " + (string)blur
    );
}

applyRegion(list preset)
{
    integer result = llSetEnvironment(<-1.0, -1.0, -1.0>, preset);
    llOwnerSay("Region water set: " + resultText(result));
}

applyParcel(list preset)
{
    integer result = llSetEnvironment(llGetPos(), preset);
    llOwnerSay("Parcel water set: " + resultText(result));
}

applyAgent(key agent, list preset)
{
    integer result = llSetAgentEnvironment(agent, 4.0, preset);
    llRegionSayTo(agent, 0, "Agent-local water set: " + resultText(result));
}

showHelp()
{
    llOwnerSay(
        "/88 report\n"
        + "/88 deep region | deep parcel | deep me\n"
        + "/88 tropical region | tropical parcel | tropical me\n"
        + "/88 reset region | reset parcel | reset me"
    );
}

default
{
    state_entry()
    {
        gListen = llListen(CHANNEL, "", NULL_KEY, "");
        llSetText("EEP water console\n/88 report | deep | tropical | reset", <0.4, 0.9, 1.0>, 1.0);
        showHelp();
    }

    touch_start(integer count)
    {
        gPendingAgent = llDetectedKey(0);
        gPendingPreset = WATER_TROPICAL;
        llRequestExperiencePermissions(gPendingAgent, "");
    }

    listen(integer channel, string name, key id, string message)
    {
        string msg = llToLower(message);

        if (msg == "help")
        {
            showHelp();
        }
        else if (msg == "report")
        {
            reportWater(llGetPos());
        }
        else if (msg == "deep region")
        {
            applyRegion(WATER_DEEP);
        }
        else if (msg == "deep parcel")
        {
            applyParcel(WATER_DEEP);
        }
        else if (msg == "deep me")
        {
            gPendingAgent = id;
            gPendingPreset = WATER_DEEP;
            llRequestExperiencePermissions(id, "");
        }
        else if (msg == "tropical region")
        {
            applyRegion(WATER_TROPICAL);
        }
        else if (msg == "tropical parcel")
        {
            applyParcel(WATER_TROPICAL);
        }
        else if (msg == "tropical me")
        {
            gPendingAgent = id;
            gPendingPreset = WATER_TROPICAL;
            llRequestExperiencePermissions(id, "");
        }
        else if (msg == "reset region")
        {
            llOwnerSay("Region reset: " + resultText(llSetEnvironment(<-1.0, -1.0, -1.0>, [])));
        }
        else if (msg == "reset parcel")
        {
            llOwnerSay("Parcel reset: " + resultText(llSetEnvironment(llGetPos(), [])));
        }
        else if (msg == "reset me")
        {
            gPendingAgent = id;
            gPendingPreset = [];
            llRequestExperiencePermissions(id, "");
        }
    }

    experience_permissions(key agent)
    {
        string ownerName = llKey2Name(agent);
        if (ownerName == "")
            ownerName = (string)agent;

        if (gPendingAgent == NULL_KEY)
            gPendingAgent = agent;

        if (agent != gPendingAgent)
            return;

        applyAgent(agent, gPendingPreset);
        llOwnerSay("Applied agent-local water to " + ownerName + ".");
    }

    experience_permissions_denied(key agent, integer reason)
    {
        llRegionSayTo(agent, 0, "Experience permission denied: " + llGetExperienceErrorMessage(reason));
    }

    changed(integer change)
    {
        if (change & CHANGED_OWNER)
            llResetScript();
    }
}
