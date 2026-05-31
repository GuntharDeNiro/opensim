// Animation State Monitor
//
// Demonstrates SL-style llGetAnimation behavior:
// - seated avatars report "Sitting"
// - ground-sitting avatars report "Sitting on Ground"
// - active animation UUIDs are available through llGetAnimationList

integer MENU_CHANNEL = -88230125;
integer gListen;
integer gWatching;
list gAgents;
list gNames;
list gStates;
list gAnimationCounts;
list gAnimationSamples;

string emptyFallback(string value, string fallback)
{
    if (value == "")
    {
        return fallback;
    }

    return value;
}

string shortKey(key value)
{
    return llGetSubString((string)value, 0, 7);
}

string animationSample(list animations)
{
    integer count = llGetListLength(animations);
    integer limit = count;
    integer i;
    string sample = "";

    if (limit > 3)
    {
        limit = 3;
    }

    for (i = 0; i < limit; i += 1)
    {
        if (sample != "")
        {
            sample += ", ";
        }

        sample += shortKey(llList2Key(animations, i));
    }

    if (count > limit)
    {
        sample += ", +" + (string)(count - limit);
    }

    if (sample == "")
    {
        sample = "(none)";
    }

    return sample;
}

integer countState(string wanted)
{
    integer count = llGetListLength(gStates);
    integer i;
    integer total = 0;

    for (i = 0; i < count; i += 1)
    {
        if (llList2String(gStates, i) == wanted)
        {
            total += 1;
        }
    }

    return total;
}

integer countMoving()
{
    integer count = llGetListLength(gStates);
    integer i;
    integer total = 0;

    for (i = 0; i < count; i += 1)
    {
        string state = llList2String(gStates, i);

        if (state != "Standing" && state != "Sitting" && state != "Sitting on Ground" && state != "")
        {
            total += 1;
        }
    }

    return total;
}

scanRegion()
{
    list agents = llGetAgentList(AGENT_LIST_REGION, []);
    integer count = llGetListLength(agents);
    integer i;

    gAgents = [];
    gNames = [];
    gStates = [];
    gAnimationCounts = [];
    gAnimationSamples = [];

    for (i = 0; i < count; i += 1)
    {
        key agent = llList2Key(agents, i);
        string displayName = emptyFallback(llGetDisplayName(agent), llKey2Name(agent));
        string state = llGetAnimation(agent);
        list animations = llGetAnimationList(agent);

        gAgents += [agent];
        gNames += [displayName];
        gStates += [state];
        gAnimationCounts += [llGetListLength(animations)];
        gAnimationSamples += [animationSample(animations)];
    }
}

string buildSummary()
{
    integer total = llGetListLength(gAgents);
    string summary = "Animation monitor\n";

    summary += "avatars: " + (string)total + "\n";
    summary += "standing: " + (string)countState("Standing") + "\n";
    summary += "sitting: " + (string)countState("Sitting") + "\n";
    summary += "ground sit: " + (string)countState("Sitting on Ground") + "\n";
    summary += "moving: " + (string)countMoving();

    return summary;
}

string buildReport(integer sittingOnly)
{
    integer count = llGetListLength(gAgents);
    integer i;
    string report = buildSummary();

    for (i = 0; i < count; i += 1)
    {
        string state = llList2String(gStates, i);

        if (!sittingOnly || state == "Sitting" || state == "Sitting on Ground")
        {
            string name = llList2String(gNames, i);
            integer animCount = llList2Integer(gAnimationCounts, i);
            string sample = llList2String(gAnimationSamples, i);

            if (state == "")
            {
                state = "(unknown)";
            }

            report += "\n\n" + name;
            report += "\nstate: " + state;
            report += "\nanimations: " + (string)animCount + " [" + sample + "]";
        }
    }

    if (sittingOnly && (countState("Sitting") + countState("Sitting on Ground") == 0))
    {
        report += "\n\nNo seated avatars found.";
    }

    return report;
}

showHover()
{
    llSetText(buildSummary(), <0.90, 0.80, 0.25>, 1.0);
}

sayReport(integer sittingOnly)
{
    string report = buildReport(sittingOnly);

    if (llStringLength(report) > 1000)
    {
        report = llGetSubString(report, 0, 999) + "\n...";
    }

    llOwnerSay(report);
}

setWatching(integer enabled)
{
    gWatching = enabled;

    if (gWatching)
    {
        llSetTimerEvent(10.0);
    }
    else
    {
        llSetTimerEvent(0.0);
    }
}

showMenu(key agent)
{
    if (gListen)
    {
        llListenRemove(gListen);
    }

    gListen = llListen(MENU_CHANNEL, "", agent, "");
    llDialog(
        agent,
        "Animation State Monitor\n"
        + "Sitting: " + (string)countState("Sitting")
        + " / Ground: " + (string)countState("Sitting on Ground"),
        [
            "SCAN",
            "REPORT",
            "SITTING",
            "WATCH ON",
            "WATCH OFF",
            "CLEAR"
        ],
        MENU_CHANNEL
    );
}

default
{
    state_entry()
    {
        scanRegion();
        showHover();
        setWatching(FALSE);
        llOwnerSay("Ready. Touch for animation monitor menu.");
    }

    touch_start(integer total)
    {
        key toucher = llDetectedKey(0);

        if (toucher != llGetOwner())
        {
            llRegionSayTo(toucher, 0, "Animation monitor reports detailed avatar state to the owner only.");
            return;
        }

        showMenu(toucher);
    }

    listen(integer channel, string name, key id, string message)
    {
        if (id != llGetOwner())
        {
            return;
        }

        if (message == "SCAN")
        {
            scanRegion();
            showHover();
            llOwnerSay("Scan complete.");
        }
        else if (message == "REPORT")
        {
            scanRegion();
            showHover();
            sayReport(FALSE);
        }
        else if (message == "SITTING")
        {
            scanRegion();
            showHover();
            sayReport(TRUE);
        }
        else if (message == "WATCH ON")
        {
            setWatching(TRUE);
            llOwnerSay("Watch mode enabled.");
        }
        else if (message == "WATCH OFF")
        {
            setWatching(FALSE);
            llOwnerSay("Watch mode disabled.");
        }
        else if (message == "CLEAR")
        {
            gAgents = [];
            gNames = [];
            gStates = [];
            gAnimationCounts = [];
            gAnimationSamples = [];
            llSetText("", ZERO_VECTOR, 0.0);
            llOwnerSay("Monitor cleared.");
        }

        showMenu(id);
    }

    timer()
    {
        scanRegion();
        showHover();

        if (gWatching)
        {
            llOwnerSay("Watch: " + (string)countState("Sitting") + " sitting, "
                + (string)countState("Sitting on Ground") + " ground sitting, "
                + (string)countMoving() + " moving.");
        }
    }

    changed(integer change)
    {
        if (change & (CHANGED_OWNER | CHANGED_REGION | CHANGED_REGION_START))
        {
            llResetScript();
        }
    }
}
