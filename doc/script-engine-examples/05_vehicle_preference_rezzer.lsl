list MODELS = ["Boat", "Car", "Bike"];
list COLORS = ["red", "blue", "green", "black"];

key gAgent;
key gQuery;
string gStep;
string gPrefKey;
integer gModelIndex;
integer gColorIndex;

tell(string msg)
{
    llRegionSayTo(gAgent, 0, msg);
}

string pref_value()
{
    return llList2String(MODELS, gModelIndex) + "|" + llList2String(COLORS, gColorIndex);
}

show_pref()
{
    tell("Vehicle preference: " + pref_value() + ". Touch cycles model, owner says /77 rez to rez.");
}

save_pref(string oldValue)
{
    if (oldValue == "")
    {
        gStep = "create";
        gQuery = llCreateKeyValue(gPrefKey, pref_value());
    }
    else
    {
        gStep = "update";
        gQuery = llUpdateKeyValue(gPrefKey, pref_value(), TRUE, oldValue);
    }
}

default
{
    state_entry()
    {
        llListen(77, "", NULL_KEY, "");
        llSetText("Experience Vehicle Preference Rezzer\nTouch to cycle preference\n/77 rez", <0.5, 0.9, 1.0>, 1.0);
    }

    touch_start(integer total)
    {
        gAgent = llDetectedKey(0);
        gPrefKey = "vehicle.pref." + (string)gAgent;
        llRequestExperiencePermissions(gAgent, "Estate Vehicle Preferences");
    }

    experience_permissions(key avatar)
    {
        gAgent = avatar;
        gStep = "read";
        gQuery = llReadKeyValue(gPrefKey);
    }

    experience_permissions_denied(key avatar, integer reason)
    {
        gAgent = avatar;
        tell("Preference storage unavailable: " + llGetExperienceErrorMessage(reason));
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
            string oldValue = "";
            if (ok)
            {
                oldValue = payload;
                list parts = llParseString2List(payload, ["|"], []);
                integer model = llListFindList(MODELS, [llList2String(parts, 0)]);
                integer color = llListFindList(COLORS, [llList2String(parts, 1)]);
                if (model >= 0)
                    gModelIndex = model;
                if (color >= 0)
                    gColorIndex = color;
            }

            gModelIndex = (gModelIndex + 1) % llGetListLength(MODELS);
            gColorIndex = (gColorIndex + 1) % llGetListLength(COLORS);
            save_pref(oldValue);
            return;
        }

        if (ok)
            show_pref();
        else
            tell("Preference save failed: " + llGetExperienceErrorMessage((integer)payload));
    }

    listen(integer channel, string name, key id, string msg)
    {
        if (channel != 77 || llToLower(msg) != "rez")
            return;

        gAgent = id;
        string model = llList2String(MODELS, gModelIndex);
        tell("Rezzing " + pref_value() + ". Put inventory objects named Boat, Car and Bike inside this prim.");
        llRezObject(model, llGetPos() + <2.0, 0.0, 1.0>, ZERO_VECTOR, ZERO_ROTATION, gColorIndex);
    }
}
