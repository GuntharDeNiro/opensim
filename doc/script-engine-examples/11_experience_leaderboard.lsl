integer CHANNEL = 44;
integer PAGE_SIZE = 10;

key gAgent;
key gQuery;
string gStep;
string gScoreKey;
integer gPendingScore;

list gKeys;
integer gIndex;

tell(key avatar, string msg)
{
    llRegionSayTo(avatar, 0, msg);
}

string key_for(key avatar)
{
    return "leaderboard.score." + (string)avatar;
}

default
{
    state_entry()
    {
        llListen(CHANNEL, "", NULL_KEY, "");
        llSetText("Experience Leaderboard\n/44 score number\n/44 board", <1.0, 0.9, 0.3>, 1.0);
    }

    listen(integer channel, string name, key id, string msg)
    {
        gAgent = id;
        list parts = llParseString2List(msg, [" "], []);
        string command = llToLower(llList2String(parts, 0));

        if (command == "score")
        {
            gPendingScore = (integer)llList2String(parts, 1);
            gScoreKey = key_for(id);
            gStep = "read_score";
            gQuery = llReadKeyValue(gScoreKey);
            return;
        }

        if (command == "board")
        {
            gStep = "keys";
            gQuery = llKeysKeyValue(0, PAGE_SIZE);
            return;
        }

        tell(id, "Use /44 score number or /44 board.");
    }

    dataserver(key query, string data)
    {
        if (query != gQuery)
            return;

        integer comma = llSubStringIndex(data, ",");
        integer ok = (integer)llGetSubString(data, 0, comma - 1);
        string payload = llGetSubString(data, comma + 1, -1);

        if (gStep == "read_score")
        {
            if (ok)
            {
                integer old = (integer)payload;
                if (gPendingScore <= old)
                {
                    tell(gAgent, "Score not saved because your previous score is higher: " + (string)old);
                    return;
                }
                gStep = "update_score";
                gQuery = llUpdateKeyValue(gScoreKey, (string)gPendingScore, TRUE, payload);
            }
            else
            {
                gStep = "create_score";
                gQuery = llCreateKeyValue(gScoreKey, (string)gPendingScore);
            }
            return;
        }

        if (gStep == "create_score" || gStep == "update_score")
        {
            if (ok)
                tell(gAgent, "Score saved: " + (string)gPendingScore);
            else
                tell(gAgent, "Score save failed: " + llGetExperienceErrorMessage((integer)payload));
            return;
        }

        if (gStep == "keys")
        {
            if (!ok || payload == "")
            {
                tell(gAgent, "No leaderboard entries yet.");
                return;
            }

            gKeys = llParseString2List(payload, [","], []);
            gIndex = 0;
            gStep = "read_board";
            gScoreKey = llList2String(gKeys, gIndex);
            gQuery = llReadKeyValue(gScoreKey);
            return;
        }

        if (gStep == "read_board")
        {
            if (ok)
                tell(gAgent, gScoreKey + " = " + payload);

            gIndex++;
            if (gIndex < llGetListLength(gKeys))
            {
                gScoreKey = llList2String(gKeys, gIndex);
                gQuery = llReadKeyValue(gScoreKey);
            }
        }
    }
}
