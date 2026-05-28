integer CHANNEL = 99;
string PRESET_KEY = "scene.preset.active";

key gQuery;
string gStep;
string gPreset;

apply_preset(string preset)
{
    gPreset = preset;

    if (preset == "night")
        llSetColor(<0.1, 0.1, 0.3>, ALL_SIDES);
    else if (preset == "storm")
        llSetColor(<0.2, 0.2, 0.2>, ALL_SIDES);
    else if (preset == "party")
        llSetColor(<1.0, 0.2, 0.8>, ALL_SIDES);
    else
        llSetColor(<1.0, 1.0, 1.0>, ALL_SIDES);

    llSetText("Scene preset: " + preset + "\n/99 night | storm | party | day", <0.8, 0.9, 1.0>, 1.0);
}

save_preset(string preset, string oldValue)
{
    apply_preset(preset);
    if (oldValue == "")
    {
        gStep = "create";
        gQuery = llCreateKeyValue(PRESET_KEY, preset);
    }
    else
    {
        gStep = "update";
        gQuery = llUpdateKeyValue(PRESET_KEY, preset, TRUE, oldValue);
    }
}

default
{
    state_entry()
    {
        llListen(CHANNEL, "", llGetOwner(), "");
        gStep = "read";
        gQuery = llReadKeyValue(PRESET_KEY);
    }

    listen(integer channel, string name, key id, string msg)
    {
        string preset = llToLower(msg);
        if (preset != "night" && preset != "storm" && preset != "party" && preset != "day")
        {
            llRegionSayTo(id, 0, "Use /99 night, storm, party or day.");
            return;
        }

        gPreset = preset;
        gStep = "read_before_save";
        gQuery = llReadKeyValue(PRESET_KEY);
    }

    dataserver(key query, string data)
    {
        if (query != gQuery)
            return;

        integer comma = llSubStringIndex(data, ",");
        integer ok = (integer)llGetSubString(data, 0, comma - 1);
        string payload = llGetSubString(data, comma + 1, -1);

        if (gStep == "read")
        {
            if (ok)
                apply_preset(payload);
            else
                apply_preset("day");
            return;
        }

        if (gStep == "read_before_save")
        {
            if (ok)
                save_preset(gPreset, payload);
            else
                save_preset(gPreset, "");
            return;
        }

        if (ok)
            llOwnerSay("Scene preset saved: " + gPreset);
        else
            llOwnerSay("Scene save failed: " + llGetExperienceErrorMessage((integer)payload));
    }
}
