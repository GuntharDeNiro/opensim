// Script Memory Profiler Lab
//
// Demonstrates YEngine-backed Second Life compatibility for:
// llGetFreeMemory, llGetUsedMemory, llGetMemoryLimit,
// llSetMemoryLimit, llGetSPMaxMemory and llScriptProfiler.

integer MENU_CHANNEL = -88230123;
integer gListen;
integer gProfilerEnabled;
integer gLastLimitRequest;
list gPayload;
string gBlob;

string bytes(integer value)
{
    return (string)value + " bytes";
}

integer freeEnough(integer bytesNeeded)
{
    return llGetFreeMemory() > bytesNeeded;
}

report(string label)
{
    integer used = llGetUsedMemory();
    integer free = llGetFreeMemory();
    integer limit = llGetMemoryLimit();
    integer max = llGetSPMaxMemory();
    string profiler = "PROFILE_NONE";

    if (gProfilerEnabled)
    {
        profiler = "PROFILE_SCRIPT_MEMORY";
    }

    llOwnerSay(
        label
        + "\nused: " + bytes(used)
        + "\nfree: " + bytes(free)
        + "\nlimit: " + bytes(limit)
        + "\nengine max: " + bytes(max)
        + "\nlast requested limit: " + bytes(gLastLimitRequest)
        + "\nprofiler: " + profiler
        + "\npayload entries: " + (string)llGetListLength(gPayload)
        + "\nblob chars: " + (string)llStringLength(gBlob)
    );
}

grow(integer rounds, integer stopAtFreeBytes)
{
    integer i;
    integer before = llGetUsedMemory();

    for (i = 0; i < rounds; ++i)
    {
        if (!freeEnough(stopAtFreeBytes))
        {
            llOwnerSay("Stopped growth before the heap limit. Free memory is " + bytes(llGetFreeMemory()) + ".");
            jump done;
        }

        string stamp = (string)llGetUnixTime() + ":" + (string)i + ":" + (string)llFrand(999999.0);
        gPayload += [stamp, llGetSubString(stamp + stamp + stamp + stamp, 0, 95)];
        gBlob += "0123456789abcdef0123456789abcdef";
    }

@done;
    llOwnerSay("Growth delta: " + bytes(llGetUsedMemory() - before) + ".");
    report("Memory report after grow");
}

trimPayload()
{
    integer before = llGetUsedMemory();
    integer count = llGetListLength(gPayload);
    integer half = count / 2;
    integer blobHalf = llStringLength(gBlob) / 2;

    if (half > 0)
    {
        gPayload = llList2List(gPayload, 0, half - 1);
    }
    else
    {
        gPayload = [];
    }

    if (blobHalf > 0)
    {
        gBlob = llGetSubString(gBlob, 0, blobHalf - 1);
    }
    else
    {
        gBlob = "";
    }

    llOwnerSay("Trim delta: " + bytes(llGetUsedMemory() - before) + ".");
    report("Memory report after trim");
}

clearPayload()
{
    gPayload = [];
    gBlob = "";
    report("Memory report after clear");
}

setLimit(integer requested)
{
    integer ok;
    gLastLimitRequest = requested;
    ok = llSetMemoryLimit(requested);

    if (ok)
    {
        llOwnerSay("Accepted memory limit " + bytes(requested) + ".");
    }
    else
    {
        llOwnerSay("Rejected memory limit " + bytes(requested) + ". It may be below used memory, below 16384 bytes or above llGetSPMaxMemory().");
    }

    report("Memory report after limit request");
}

setProfiler(integer enabled)
{
    gProfilerEnabled = enabled;

    if (enabled)
    {
        llScriptProfiler(PROFILE_SCRIPT_MEMORY);
        llOwnerSay("Profiler flag set to PROFILE_SCRIPT_MEMORY.");
    }
    else
    {
        llScriptProfiler(PROFILE_NONE);
        llOwnerSay("Profiler flag set to PROFILE_NONE.");
    }

    report("Memory report after profiler change");
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
        "Script Memory Profiler Lab\n"
        + "Used " + bytes(llGetUsedMemory())
        + " / limit " + bytes(llGetMemoryLimit())
        + "\nChoose an action.",
        [
            "REPORT",
            "GROW",
            "BURST",
            "TRIM",
            "CLEAR",
            "LIMIT 64K",
            "LIMIT MAX",
            "PROF ON",
            "PROF OFF"
        ],
        MENU_CHANNEL
    );
    llSetTimerEvent(30.0);
}

default
{
    state_entry()
    {
        gLastLimitRequest = llGetMemoryLimit();
        setProfiler(FALSE);
        report("Script Memory Profiler Lab ready");
    }

    touch_start(integer count)
    {
        key toucher = llDetectedKey(0);

        if (toucher != llGetOwner())
        {
            llInstantMessage(toucher, "Owner-only memory diagnostic console.");
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

        if (message == "REPORT")
        {
            report("Manual memory report");
        }
        else if (message == "GROW")
        {
            grow(12, 8192);
        }
        else if (message == "BURST")
        {
            grow(64, 16384);
        }
        else if (message == "TRIM")
        {
            trimPayload();
        }
        else if (message == "CLEAR")
        {
            clearPayload();
        }
        else if (message == "LIMIT 64K")
        {
            setLimit(65536);
        }
        else if (message == "LIMIT MAX")
        {
            setLimit(llGetSPMaxMemory());
        }
        else if (message == "PROF ON")
        {
            setProfiler(TRUE);
        }
        else if (message == "PROF OFF")
        {
            setProfiler(FALSE);
        }

        showMenu(id);
    }

    timer()
    {
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
        {
            llResetScript();
        }
    }
}
