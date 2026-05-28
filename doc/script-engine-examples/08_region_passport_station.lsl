string STATION_ID = "harbor";
string STATION_NAME = "Harbor Station";

key gAgent;
key gQuery;
string gStep;
string gPassportKey;

tell(string msg)
{
    llRegionSayTo(gAgent, 0, msg);
}

integer has_stamp(string passport, string stamp)
{
    list stamps = llParseString2List(passport, ["|"], []);
    return llListFindList(stamps, [stamp]) >= 0;
}

default
{
    state_entry()
    {
        llSetText("Region Passport\nTouch to collect stamp: " + STATION_NAME, <0.5, 1.0, 0.7>, 1.0);
    }

    touch_start(integer total)
    {
        gAgent = llDetectedKey(0);
        gPassportKey = "passport." + (string)gAgent;
        llRequestExperiencePermissions(gAgent, "Region Passport");
    }

    experience_permissions(key avatar)
    {
        gAgent = avatar;
        gStep = "read";
        gQuery = llReadKeyValue(gPassportKey);
    }

    experience_permissions_denied(key avatar, integer reason)
    {
        gAgent = avatar;
        tell("Passport unavailable: " + llGetExperienceErrorMessage(reason));
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
                if (has_stamp(payload, STATION_ID))
                {
                    tell("Passport already stamped at " + STATION_NAME + ". Your stamps: " + payload);
                    return;
                }

                string next = payload + "|" + STATION_ID;
                gStep = "update";
                gQuery = llUpdateKeyValue(gPassportKey, next, TRUE, payload);
            }
            else
            {
                gStep = "create";
                gQuery = llCreateKeyValue(gPassportKey, STATION_ID);
            }
            return;
        }

        if (ok)
            tell("Passport stamped: " + STATION_NAME + ".");
        else
            tell("Passport save failed: " + llGetExperienceErrorMessage((integer)payload));
    }
}
