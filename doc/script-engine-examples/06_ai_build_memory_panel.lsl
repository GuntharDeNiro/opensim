key gUser;
key gQuery;
string gStep;
string gProjectKey;
string gPendingText;

integer CHANNEL = 88;

tell(string msg)
{
    llRegionSayTo(gUser, 0, msg);
}

default
{
    state_entry()
    {
        llListen(CHANNEL, "", NULL_KEY, "");
        llSetText("AI Build Memory Panel\n/88 project text\n/88 show\n/88 clear", <0.6, 0.8, 1.0>, 1.0);
    }

    listen(integer channel, string name, key id, string msg)
    {
        if (channel != CHANNEL)
            return;

        gUser = id;
        gProjectKey = "ai.build.project." + (string)id;
        llRequestExperiencePermissions(id, "AI Build Memory");
        gPendingText = msg;
    }

    experience_permissions(key avatar)
    {
        gUser = avatar;
        string lower = llToLower(gPendingText);

        if (lower == "show")
        {
            gStep = "show";
            gQuery = llReadKeyValue(gProjectKey);
            return;
        }

        if (lower == "clear")
        {
            gStep = "clear";
            gQuery = llDeleteKeyValue(gProjectKey);
            return;
        }

        gStep = "read_before_save";
        gQuery = llReadKeyValue(gProjectKey);
    }

    experience_permissions_denied(key avatar, integer reason)
    {
        gUser = avatar;
        tell("AI build memory unavailable: " + llGetExperienceErrorMessage(reason));
    }

    dataserver(key query, string data)
    {
        if (query != gQuery)
            return;

        integer comma = llSubStringIndex(data, ",");
        integer ok = (integer)llGetSubString(data, 0, comma - 1);
        string payload = llGetSubString(data, comma + 1, -1);

        if (gStep == "show")
        {
            if (ok)
                tell("Current project memory: " + payload);
            else
                tell("No project memory stored yet.");
            return;
        }

        if (gStep == "clear")
        {
            if (ok)
                tell("Project memory cleared.");
            else
                tell("Nothing to clear or delete failed: " + llGetExperienceErrorMessage((integer)payload));
            return;
        }

        if (gStep == "read_before_save")
        {
            string next = gPendingText;
            if (ok)
                next = payload + " || " + gPendingText;

            if (llStringLength(next) > 3500)
                next = llGetSubString(next, -3500, -1);

            gPendingText = next;
            if (ok)
            {
                gStep = "update";
                gQuery = llUpdateKeyValue(gProjectKey, next, TRUE, payload);
            }
            else
            {
                gStep = "create";
                gQuery = llCreateKeyValue(gProjectKey, next);
            }
            return;
        }

        if (gStep == "create" || gStep == "update")
        {
            if (ok)
            {
                tell("Project memory saved.");
                tell("Stats: " + llDumpList2String(llGetExperienceKeyValueStoreStats(), " | "));
            }
            else
            {
                tell("Save failed: " + llGetExperienceErrorMessage((integer)payload));
            }
        }
    }
}
