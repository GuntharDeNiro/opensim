// Agent Language Privacy Panel
//
// Demonstrates SL-style llGetAgentLanguage behavior:
// - only in-region root avatars return a language
// - avatars with private language preferences return an empty string
// - unavailable agent-preference service returns an empty string

integer MENU_CHANNEL = -88230124;
integer gListen;
integer gVerbose;
list gAgents;
list gNames;
list gUsernames;
list gLanguages;
list gPrivateOrUnknown;

string emptyFallback(string value, string fallback)
{
    if (value == "")
    {
        return fallback;
    }

    return value;
}

integer indexOfAgent(key agent)
{
    return llListFindList(gAgents, [agent]);
}

addOrUpdate(key agent, string displayName, string username, string language)
{
    integer index = indexOfAgent(agent);

    if (index < 0)
    {
        gAgents += [agent];
        gNames += [displayName];
        gUsernames += [username];
        gLanguages += [language];
        gPrivateOrUnknown += [(language == "")];
        return;
    }

    gNames = llListReplaceList(gNames, [displayName], index, index);
    gUsernames = llListReplaceList(gUsernames, [username], index, index);
    gLanguages = llListReplaceList(gLanguages, [language], index, index);
    gPrivateOrUnknown = llListReplaceList(gPrivateOrUnknown, [(language == "")], index, index);
}

scanRegion()
{
    list agents = llGetAgentList(AGENT_LIST_REGION, []);
    integer count = llGetListLength(agents);
    integer i;

    gAgents = [];
    gNames = [];
    gUsernames = [];
    gLanguages = [];
    gPrivateOrUnknown = [];

    for (i = 0; i < count; i += 1)
    {
        key agent = llList2Key(agents, i);
        string displayName = emptyFallback(llGetDisplayName(agent), llKey2Name(agent));
        string username = emptyFallback(llGetUsername(agent), "(unknown username)");
        string language = llStringTrim(llGetAgentLanguage(agent), STRING_TRIM);

        addOrUpdate(agent, displayName, username, language);
    }
}

integer countVisibleLanguages()
{
    integer i;
    integer total = 0;
    integer count = llGetListLength(gLanguages);

    for (i = 0; i < count; i += 1)
    {
        if (llList2String(gLanguages, i) != "")
        {
            total += 1;
        }
    }

    return total;
}

string buildReport(integer full)
{
    integer count = llGetListLength(gAgents);
    integer visible = countVisibleLanguages();
    integer i;
    integer limit = count;
    string report = "Agent language scan\n";

    report += "avatars: " + (string)count + "\n";
    report += "public languages: " + (string)visible + "\n";
    report += "private/unknown: " + (string)(count - visible) + "\n";

    if (!full && limit > 7)
    {
        limit = 7;
    }

    for (i = 0; i < limit; i += 1)
    {
        string language = llList2String(gLanguages, i);
        string displayName = llList2String(gNames, i);
        string username = llList2String(gUsernames, i);

        if (language == "")
        {
            language = "(private)";
        }

        report += "\n" + displayName + "\n";
        report += "  " + username + " -> " + language;
    }

    if (!full && count > limit)
    {
        report += "\n\n+" + (string)(count - limit) + " more avatars";
    }

    return report;
}

showHover()
{
    vector color = <0.20, 0.75, 1.00>;
    llSetText(buildReport(FALSE), color, 1.0);
}

sayFullReport()
{
    string report = buildReport(TRUE);

    if (llStringLength(report) > 1000)
    {
        report = llGetSubString(report, 0, 999) + "\n...";
    }

    llOwnerSay(report);
}

setVerbose(integer enabled)
{
    gVerbose = enabled;

    if (gVerbose)
    {
        llSetTimerEvent(30.0);
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
        "Agent Language Privacy Panel\n"
        + "Public languages: " + (string)countVisibleLanguages()
        + " / " + (string)llGetListLength(gAgents),
        [
            "SCAN",
            "REPORT",
            "VERBOSE ON",
            "VERBOSE OFF",
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
        setVerbose(FALSE);
        llOwnerSay("Ready. Touch for scan/report menu.");
    }

    touch_start(integer total)
    {
        key toucher = llDetectedKey(0);

        if (toucher != llGetOwner())
        {
            llRegionSayTo(toucher, 0, "This panel only reports aggregate language visibility to the owner.");
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
            sayFullReport();
        }
        else if (message == "VERBOSE ON")
        {
            setVerbose(TRUE);
            llOwnerSay("Verbose timer scan enabled.");
        }
        else if (message == "VERBOSE OFF")
        {
            setVerbose(FALSE);
            llOwnerSay("Verbose timer scan disabled.");
        }
        else if (message == "CLEAR")
        {
            gAgents = [];
            gNames = [];
            gUsernames = [];
            gLanguages = [];
            gPrivateOrUnknown = [];
            llSetText("", ZERO_VECTOR, 0.0);
            llOwnerSay("Panel cleared.");
        }

        showMenu(id);
    }

    timer()
    {
        scanRegion();
        showHover();

        if (gVerbose)
        {
            llOwnerSay("Timer scan: " + (string)countVisibleLanguages() + " public language values.");
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
