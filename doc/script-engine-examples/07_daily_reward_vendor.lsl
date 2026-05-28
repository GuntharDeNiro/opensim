integer REWARD_COOLDOWN_SECONDS = 86400;
string REWARD_ITEM = "Daily Gift";

key gAgent;
key gQuery;
string gStep;
string gKey;
integer gNow;

tell(string msg)
{
    llRegionSayTo(gAgent, 0, msg);
}

start_reward(key avatar)
{
    gAgent = avatar;
    gKey = "daily.reward." + (string)avatar;
    gNow = llGetUnixTime();
    llRequestExperiencePermissions(avatar, "Daily Reward");
}

default
{
    state_entry()
    {
        llSetText("Daily Reward Vendor\nTouch once per day", <1.0, 0.8, 0.2>, 1.0);
    }

    touch_start(integer total)
    {
        start_reward(llDetectedKey(0));
    }

    experience_permissions(key avatar)
    {
        gAgent = avatar;
        gStep = "read";
        gQuery = llReadKeyValue(gKey);
    }

    experience_permissions_denied(key avatar, integer reason)
    {
        gAgent = avatar;
        tell("Reward unavailable: " + llGetExperienceErrorMessage(reason));
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
                integer last = (integer)payload;
                integer remaining = REWARD_COOLDOWN_SECONDS - (gNow - last);
                if (remaining > 0)
                {
                    tell("You already claimed this reward. Try again in " + (string)(remaining / 60) + " minutes.");
                    return;
                }

                gStep = "update";
                gQuery = llUpdateKeyValue(gKey, (string)gNow, TRUE, payload);
            }
            else
            {
                gStep = "create";
                gQuery = llCreateKeyValue(gKey, (string)gNow);
            }
            return;
        }

        if (ok)
        {
            tell("Reward claimed. Delivering: " + REWARD_ITEM);
            llGiveInventory(gAgent, REWARD_ITEM);
        }
        else
        {
            tell("Reward save failed: " + llGetExperienceErrorMessage((integer)payload));
        }
    }
}
