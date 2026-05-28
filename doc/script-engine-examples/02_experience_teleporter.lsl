integer PERMS = PERMISSION_TELEPORT;

vector DESTINATION = <128.0, 128.0, 25.0>;
vector LOOK_AT = <1.0, 0.0, 0.0>;

key gAgent;
key gQuery;
string gKey;
string gStep;
integer gTeleports;

tell(string msg)
{
    llRegionSayTo(gAgent, 0, msg);
}

default
{
    state_entry()
    {
        llSetText("Experience Teleporter\nTouch to teleport", <0.4, 1.0, 0.4>, 1.0);
    }

    touch_start(integer total)
    {
        gAgent = llDetectedKey(0);
        gKey = "teleporter.count." + (string)gAgent;
        llRequestExperiencePermissions(gAgent, "Estate Teleporter");
    }

    experience_permissions(key avatar)
    {
        gAgent = avatar;
        llRequestPermissions(avatar, PERMS);
    }

    experience_permissions_denied(key avatar, integer reason)
    {
        gAgent = avatar;
        tell("Teleport experience denied: " + llGetExperienceErrorMessage(reason));
    }

    run_time_permissions(integer permissions)
    {
        if ((permissions & PERMISSION_TELEPORT) == 0)
        {
            tell("Teleport permission was not granted.");
            return;
        }

        gStep = "read";
        gQuery = llReadKeyValue(gKey);
        llTeleportAgent(gAgent, "", DESTINATION, LOOK_AT);
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
            {
                gTeleports = (integer)payload + 1;
                gStep = "update";
                gQuery = llUpdateKeyValue(gKey, (string)gTeleports, TRUE, payload);
            }
            else
            {
                gTeleports = 1;
                gStep = "create";
                gQuery = llCreateKeyValue(gKey, "1");
            }
            return;
        }

        if (ok)
            tell("Teleport logged. Your teleport count here: " + (string)gTeleports);
        else
            tell("Teleport log failed: " + llGetExperienceErrorMessage((integer)payload));
    }
}
