rotation CLOSED_ROT;
rotation OPEN_ROT;
integer gOpen;

key gUser;
key gQuery;
string gStep;
string gAccessKey;
string gAccessValue;
integer ADMIN_CHANNEL = 55;

string avatar_key(key avatar)
{
    return "door.access." + (string)avatar;
}

say_to(key avatar, string msg)
{
    llRegionSayTo(avatar, 0, msg);
}

open_door()
{
    if (gOpen)
        return;
    gOpen = TRUE;
    llSetRot(OPEN_ROT);
    llSetTimerEvent(8.0);
}

close_door()
{
    gOpen = FALSE;
    llSetRot(CLOSED_ROT);
    llSetTimerEvent(0.0);
}

default
{
    state_entry()
    {
        CLOSED_ROT = llGetRot();
        OPEN_ROT = CLOSED_ROT * llEuler2Rot(<0.0, 0.0, 90.0> * DEG_TO_RAD);
        llListen(ADMIN_CHANNEL, "", llGetOwner(), "");
        llSetText("Persistent Access Door\nOwner: /55 grant avatar-uuid\nOwner: /55 revoke avatar-uuid", <0.7, 0.9, 1.0>, 1.0);
    }

    touch_start(integer total)
    {
        gUser = llDetectedKey(0);
        gAccessKey = avatar_key(gUser);

        if (gUser == llGetOwner())
        {
            say_to(gUser, "Owner always has access. Use /55 grant avatar-uuid or /55 revoke avatar-uuid.");
            open_door();
            return;
        }

        gStep = "read";
        gQuery = llReadKeyValue(gAccessKey);
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
            if (ok && payload == "allow")
            {
                say_to(gUser, "Access granted.");
                open_door();
            }
            else
            {
                say_to(gUser, "Access denied.");
            }
            return;
        }

        if (gStep == "grant")
        {
            if (ok)
                say_to(llGetOwner(), "Access saved.");
            else
                say_to(llGetOwner(), "Grant failed: " + llGetExperienceErrorMessage((integer)payload));
            return;
        }

        if (gStep == "grant_read")
        {
            if (ok)
            {
                gStep = "grant_update";
                gQuery = llUpdateKeyValue(gAccessKey, gAccessValue, TRUE, payload);
            }
            else
            {
                gStep = "grant";
                gQuery = llCreateKeyValue(gAccessKey, gAccessValue);
            }
            return;
        }

        if (gStep == "grant_update")
        {
            if (ok)
                say_to(llGetOwner(), "Access updated.");
            else
                say_to(llGetOwner(), "Access update failed: " + llGetExperienceErrorMessage((integer)payload));
            return;
        }

        if (gStep == "revoke")
        {
            if (ok)
                say_to(llGetOwner(), "Access revoked.");
            else
                say_to(llGetOwner(), "Revoke failed: " + llGetExperienceErrorMessage((integer)payload));
        }
    }

    listen(integer channel, string name, key id, string msg)
    {
        if (channel != ADMIN_CHANNEL || id != llGetOwner())
            return;

        list parts = llParseString2List(msg, [" "], []);
        string command = llToLower(llList2String(parts, 0));
        key avatar = (key)llList2String(parts, 1);

        if (command == "stats")
        {
            say_to(id, "Door KVP stats: " + llDumpList2String(llGetExperienceKeyValueStoreStats(), " | "));
            return;
        }

        if (avatar == NULL_KEY)
        {
            say_to(id, "Usage: /55 grant avatar-uuid | /55 revoke avatar-uuid | /55 stats");
            return;
        }

        gAccessKey = avatar_key(avatar);

        if (command == "grant")
        {
            gAccessValue = "allow";
            gStep = "grant_read";
            gQuery = llReadKeyValue(gAccessKey);
            return;
        }

        if (command == "revoke")
        {
            gStep = "revoke";
            gQuery = llDeleteKeyValue(gAccessKey);
            return;
        }

        say_to(id, "Unknown command. Use grant, revoke or stats.");
    }

    timer()
    {
        close_door();
    }
}
