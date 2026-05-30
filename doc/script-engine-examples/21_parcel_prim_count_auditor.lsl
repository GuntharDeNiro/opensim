// Parcel prim count auditor
//
// Demonstrates compatibility implemented in this build:
//
// - llGetParcelPrimCount now supports sim_wide TRUE for every PARCEL_COUNT_*
//   category instead of only PARCEL_COUNT_TOTAL.
// - PARCEL_COUNT_TEMP now reports temporary-on-rez non-mesh prims on the parcel
//   or on all same-owner parcels when sim_wide is TRUE.
//
// Setup:
// Drop this script into a prim. Touch it and use HERE or SCAN to choose which
// parcel position to inspect. TEMP counts update when temporary objects exist
// on the inspected parcel.

integer MENU_CHANNEL = -90150021;
integer LISTEN_HANDLE;

key gOperator = NULL_KEY;
vector gProbePos;

say_to(key agent, string message)
{
    llRegionSayTo(agent, 0, "[parcel-prim-auditor] " + message);
}

string row(string label, integer category, vector pos)
{
    integer localCount = llGetParcelPrimCount(pos, category, FALSE);
    integer simWideCount = llGetParcelPrimCount(pos, category, TRUE);
    return label + ": local=" + (string)localCount + " sameOwnerSim=" + (string)simWideCount;
}

report(key agent, vector pos)
{
    say_to(agent,
        "Prim counts at " + (string)pos +
        "\n" + row("TOTAL", PARCEL_COUNT_TOTAL, pos) +
        "\n" + row("OWNER", PARCEL_COUNT_OWNER, pos) +
        "\n" + row("GROUP", PARCEL_COUNT_GROUP, pos) +
        "\n" + row("OTHER", PARCEL_COUNT_OTHER, pos) +
        "\n" + row("SELECTED", PARCEL_COUNT_SELECTED, pos) +
        "\n" + row("TEMP", PARCEL_COUNT_TEMP, pos));
}

show_menu(key agent)
{
    gOperator = agent;

    if (LISTEN_HANDLE != 0)
        llListenRemove(LISTEN_HANDLE);

    LISTEN_HANDLE = llListen(MENU_CHANNEL, "", agent, "");

    llDialog(agent,
        "Parcel prim auditor\n" +
        "Probe position: " + (string)gProbePos,
        [
            "HERE",
            "SCAN",
            "REPORT",
            "HELP"
        ],
        MENU_CHANNEL);
}

help(key agent)
{
    say_to(agent,
        "HERE audits the parcel under this object. SCAN audits the nearest detected avatar/object position. REPORT repeats the last audit. sameOwnerSim is llGetParcelPrimCount(pos, category, TRUE), summed across parcels with the same land owner.");
}

default
{
    state_entry()
    {
        gProbePos = llGetPos();
    }

    touch_start(integer count)
    {
        show_menu(llDetectedKey(0));
    }

    listen(integer channel, string name, key id, string message)
    {
        if (channel != MENU_CHANNEL || id != gOperator)
            return;

        if (message == "HERE")
        {
            gProbePos = llGetPos();
            report(id, gProbePos);
            show_menu(id);
        }
        else if (message == "SCAN")
        {
            say_to(id, "Scanning 96m for an avatar/object position to audit...");
            llSensor("", NULL_KEY, AGENT | ACTIVE | PASSIVE, 96.0, PI);
        }
        else if (message == "REPORT")
        {
            report(id, gProbePos);
            show_menu(id);
        }
        else if (message == "HELP")
        {
            help(id);
            show_menu(id);
        }
    }

    sensor(integer count)
    {
        if (count <= 0)
        {
            say_to(gOperator, "No nearby avatar or object found.");
            show_menu(gOperator);
            return;
        }

        gProbePos = llDetectedPos(0);
        say_to(gOperator, "Auditing parcel under " + llDetectedName(0) + ".");
        report(gOperator, gProbePos);
        show_menu(gOperator);
    }

    no_sensor()
    {
        say_to(gOperator, "No nearby avatar or object found.");
        show_menu(gOperator);
    }
}
