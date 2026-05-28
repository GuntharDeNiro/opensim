integer PERMS =
    PERMISSION_TAKE_CONTROLS |
    PERMISSION_CONTROL_CAMERA |
    PERMISSION_TRACK_CAMERA |
    PERMISSION_TRIGGER_ANIMATION;

key gAgent;
key gQuery;
string gStep;
integer gVisits;
string gVisitKey;

say(string msg)
{
    llRegionSayTo(gAgent, 0, msg);
}

set_camera(integer enabled)
{
    if (!enabled)
    {
        llClearCameraParams();
        return;
    }

    vector pos = llGetPos();
    llSetCameraParams([
        CAMERA_ACTIVE, TRUE,
        CAMERA_FOCUS, pos + <0.0, 0.0, 1.2>,
        CAMERA_POSITION, pos + <-6.0, -7.0, 4.5>,
        CAMERA_POSITION_LOCKED, FALSE,
        CAMERA_FOCUS_LOCKED, FALSE,
        CAMERA_POSITION_LAG, 0.25,
        CAMERA_FOCUS_LAG, 0.20,
        CAMERA_DISTANCE, 8.0,
        CAMERA_PITCH, 18.0
    ]);
}

request_experience(key avatar)
{
    gAgent = avatar;
    gVisitKey = "tour.visits." + (string)avatar;
    llRequestExperiencePermissions(avatar, "Estate Tour");
}

read_visits()
{
    gStep = "read";
    gQuery = llReadKeyValue(gVisitKey);
}

create_visits()
{
    gVisits = 1;
    gStep = "create";
    gQuery = llCreateKeyValue(gVisitKey, (string)gVisits);
}

update_visits(string oldValue)
{
    gVisits = (integer)oldValue + 1;
    gStep = "update";
    gQuery = llUpdateKeyValue(gVisitKey, (string)gVisits, TRUE, oldValue);
}

default
{
    state_entry()
    {
        llSetText("Experience Camera Tour\nTouch to start", <0.2, 0.8, 1.0>, 1.0);
    }

    touch_start(integer total)
    {
        key avatar = llDetectedKey(0);
        if (llAgentInExperience(avatar))
            llOwnerSay("Agent is already in this Experience-Lite scope.");

        list details = llGetExperienceDetails(NULL_KEY);
        if (llGetListLength(details) > 0)
            llOwnerSay("Experience details: " + llDumpList2String(details, " | "));

        request_experience(avatar);
    }

    experience_permissions(key avatar)
    {
        gAgent = avatar;
        say("Experience granted. Use movement keys to explore tour prompts.");
        llRequestPermissions(avatar, PERMS);
        read_visits();
    }

    experience_permissions_denied(key avatar, integer reason)
    {
        gAgent = avatar;
        say("Experience denied: " + llGetExperienceErrorMessage(reason));
    }

    run_time_permissions(integer permissions)
    {
        if ((permissions & PERMISSION_CONTROL_CAMERA) != 0)
            set_camera(TRUE);

        if ((permissions & PERMISSION_TAKE_CONTROLS) != 0)
            llTakeControls(CONTROL_FWD | CONTROL_BACK | CONTROL_LEFT | CONTROL_RIGHT | CONTROL_ROT_LEFT | CONTROL_ROT_RIGHT, TRUE, FALSE);
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
                update_visits(payload);
            else
                create_visits();
            return;
        }

        if (gStep == "create" || gStep == "update")
        {
            if (ok)
                say("Visit remembered. Visit count: " + (string)gVisits);
            else
                say("KVP write failed: " + llGetExperienceErrorMessage((integer)payload));

            say("KVP stats: " + llDumpList2String(llGetExperienceKeyValueStoreStats(), " | "));
            gStep = "";
        }
    }

    control(key id, integer level, integer edge)
    {
        if (id != gAgent)
            return;

        if (edge & level & CONTROL_FWD)
            say("Tour point: marina.");
        if (edge & level & CONTROL_BACK)
            say("Tour point: welcome plaza.");
        if (edge & level & (CONTROL_LEFT | CONTROL_ROT_LEFT))
            say("Tour point: west side.");
        if (edge & level & (CONTROL_RIGHT | CONTROL_ROT_RIGHT))
            say("Tour point: east side.");
    }

    on_rez(integer start)
    {
        llResetScript();
    }
}
