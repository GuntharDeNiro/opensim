key gAgent;
key gQuery;
string gStep;
string gQuestKey;
integer gProgress;

integer MAX_PROGRESS = 5;

tell(string msg)
{
    llRegionSayTo(gAgent, 0, msg);
}

save_progress(integer next, string oldValue)
{
    gProgress = next;
    if (oldValue == "")
    {
        gStep = "create";
        gQuery = llCreateKeyValue(gQuestKey, (string)gProgress);
    }
    else
    {
        gStep = "update";
        gQuery = llUpdateKeyValue(gQuestKey, (string)gProgress, TRUE, oldValue);
    }
}

default
{
    state_entry()
    {
        llSetText("Experience Quest Tracker\nTouch for next quest step", <1.0, 0.8, 0.2>, 1.0);
    }

    touch_start(integer total)
    {
        gAgent = llDetectedKey(0);
        gQuestKey = "quest.harbor_intro." + (string)gAgent;
        llRequestExperiencePermissions(gAgent, "Estate Quest Progress");
    }

    experience_permissions(key avatar)
    {
        gAgent = avatar;
        gStep = "read";
        gQuery = llReadKeyValue(gQuestKey);
    }

    experience_permissions_denied(key avatar, integer reason)
    {
        gAgent = avatar;
        tell("Quest cannot save progress: " + llGetExperienceErrorMessage(reason));
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
                integer next = (integer)payload + 1;
                if (next > MAX_PROGRESS)
                    next = MAX_PROGRESS;
                save_progress(next, payload);
            }
            else
            {
                save_progress(1, "");
            }
            return;
        }

        if (ok)
        {
            tell("Quest progress saved: step " + (string)gProgress + " of " + (string)MAX_PROGRESS + ".");
            if (gProgress == MAX_PROGRESS)
                tell("Quest complete. Your progress will survive simulator restarts.");
        }
        else
        {
            tell("Quest save failed: " + llGetExperienceErrorMessage((integer)payload));
        }
    }
}
