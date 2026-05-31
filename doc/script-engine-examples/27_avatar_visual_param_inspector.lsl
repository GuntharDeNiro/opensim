// Avatar Visual Param Inspector
// Demonstrates llGetVisualParams with Second Life ids, names and aliases.

list PARAM_REQUEST = [
    33, "torso_length", 80, "heel height", "platform-height", "shoe_height",
    "hand_size", 682, "leg_length", "arm", "neck_length", "waist", "hip_length",
    11001, "definitely_not_a_visual_param"
];

list PARAM_LABELS = [
    "height/id33", "torso_length", "male/id80", "heel alias", "platform alias",
    "shoe_height", "hand_size", "head/id682", "leg_length", "arm alias",
    "neck_length", "waist alias", "hip_length", "hover/id11001", "unsupported"
];

integer gListen;
integer gChannel;
key gToucher;
key gWatchTarget;
string gWatchName;
integer gWatch;

string pct(float value)
{
    return llGetSubString((string)(value * 100.0), 0, 4) + "%";
}

string bar(float value)
{
    integer cells = 12;
    integer filled = llRound(value * (float)cells);
    integer i;
    string out = "";

    if (filled < 0)
        filled = 0;
    if (filled > cells)
        filled = cells;

    for (i = 0; i < cells; ++i)
    {
        if (i < filled)
            out += "#";
        else
            out += ".";
    }

    return out;
}

string lineFor(integer index, list values)
{
    string label = llList2String(PARAM_LABELS, index);
    string raw = llList2String(values, index);

    if (raw == "")
        return label + ": unsupported/unavailable";

    float value = llList2Float(values, index);
    return label + ": " + pct(value) + " [" + bar(value) + "]";
}

integer report(key agent, string label, integer rawMode)
{
    if (agent == NULL_KEY)
    {
        llOwnerSay("No avatar selected.");
        return FALSE;
    }

    list values = llGetVisualParams((string)agent, PARAM_REQUEST);
    integer count = llGetListLength(PARAM_REQUEST);
    integer valueCount = llGetListLength(values);
    integer i;
    string text = "Visual params for " + label + " (" + (string)agent + ")\n";

    if (valueCount != count)
    {
        llOwnerSay("llGetVisualParams returned " + (string)valueCount + " entries for " + (string)count + " requests.");
        return FALSE;
    }

    for (i = 0; i < count; ++i)
    {
        if (rawMode)
        {
            text += llList2String(PARAM_LABELS, i) + " = [" + llList2String(values, i) + "]\n";
        }
        else
        {
            text += lineFor(i, values) + "\n";
        }
    }

    llOwnerSay(text);
    llSetText("Visual params: " + label + "\n" + lineFor(0, values) + "\n" + lineFor(13, values), <0.2, 0.8, 1.0>, 1.0);
    return TRUE;
}

openMenu(key user)
{
    if (gListen)
        llListenRemove(gListen);

    gChannel = -900000 - (integer)llFrand(900000.0);
    gListen = llListen(gChannel, "", user, "");
    llDialog(user, "Avatar visual parameter inspector", ["TOUCHER", "OWNER", "SCAN", "WATCH", "RAW", "STOP"], gChannel);
    llSetTimerEvent(30.0);
}

default
{
    state_entry()
    {
        gToucher = llGetOwner();
        llSetText("Touch for llGetVisualParams inspector", <0.4, 0.9, 1.0>, 1.0);
    }

    touch_start(integer total)
    {
        gToucher = llDetectedKey(0);
        openMenu(gToucher);
    }

    listen(integer channel, string name, key id, string message)
    {
        if (message == "TOUCHER")
        {
            report(gToucher, name, FALSE);
        }
        else if (message == "OWNER")
        {
            report(llGetOwner(), "owner", FALSE);
        }
        else if (message == "SCAN")
        {
            llSensor("", NULL_KEY, AGENT, 96.0, PI);
        }
        else if (message == "WATCH")
        {
            gWatchTarget = gToucher;
            gWatchName = name;
            gWatch = TRUE;
            llSetTimerEvent(10.0);
            report(gWatchTarget, gWatchName, FALSE);
        }
        else if (message == "RAW")
        {
            report(gToucher, name, TRUE);
        }
        else if (message == "STOP")
        {
            gWatch = FALSE;
            gWatchTarget = NULL_KEY;
            llSetTimerEvent(0.0);
            llSetText("Visual param inspector stopped", <0.6, 0.6, 0.6>, 1.0);
        }
    }

    sensor(integer total)
    {
        key agent = llDetectedKey(0);
        string name = llDetectedName(0);
        gWatchTarget = agent;
        gWatchName = name;
        report(agent, name, FALSE);
    }

    no_sensor()
    {
        llOwnerSay("No avatars found within scan range.");
    }

    timer()
    {
        if (gWatch && gWatchTarget != NULL_KEY)
        {
            report(gWatchTarget, gWatchName, FALSE);
            llSetTimerEvent(10.0);
            return;
        }

        if (gListen)
        {
            llListenRemove(gListen);
            gListen = 0;
        }

        llSetTimerEvent(0.0);
    }

    changed(integer change)
    {
        if (change & CHANGED_OWNER)
            llResetScript();
    }
}
