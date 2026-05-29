// Parcel media loop console
//
// Demonstrates parcel media compatibility implemented in this build:
//
// - PARCEL_MEDIA_COMMAND_LOOP_SET
// - llParcelMediaCommandList preserving media description/type/size
// - llParcelMediaQuery returning integer media size values
// - llParcelMediaQuery support for AUTO_ALIGN and LOOP_SET
//
// Setup:
// Rez this on land where the script owner can edit parcel media. Touch it and
// use SETUP, LOOP ON/OFF and QUERY. Empty query results usually mean the owner
// lacks parcel media rights for the current parcel.

integer MENU_CHANNEL = -90150017;

string MEDIA_URL = "https://archive.org/download/BigBuckBunny_124/Content/big_buck_bunny_720p_surround.mp4";
string MEDIA_TEXTURE = "5748decc-f629-461c-9a36-a35a221fe21f";
string MEDIA_TYPE = "video/mp4";
string MEDIA_DESC = "OpenSim LSL parcel media loop test";
integer MEDIA_WIDTH = 1280;
integer MEDIA_HEIGHT = 720;

key gOperator = NULL_KEY;

say_to(key agent, string message)
{
    llRegionSayTo(agent, 0, "[parcel-media] " + message);
}

show_menu(key agent)
{
    gOperator = agent;
    llDialog(agent,
        "Parcel media loop console\n" +
        "Tests LOOP_SET, type/desc/size persistence and query readback.",
        [
            "SETUP",
            "LOOP ON",
            "LOOP OFF",
            "PLAY",
            "PAUSE",
            "STOP",
            "UNLOAD",
            "QUERY",
            "HELP"
        ],
        MENU_CHANNEL
    );
}

apply_media(integer loop_enabled, integer play_now)
{
    list commands = [
        PARCEL_MEDIA_COMMAND_URL, MEDIA_URL,
        PARCEL_MEDIA_COMMAND_TEXTURE, MEDIA_TEXTURE,
        PARCEL_MEDIA_COMMAND_TYPE, MEDIA_TYPE,
        PARCEL_MEDIA_COMMAND_DESC, MEDIA_DESC,
        PARCEL_MEDIA_COMMAND_SIZE, MEDIA_WIDTH, MEDIA_HEIGHT,
        PARCEL_MEDIA_COMMAND_AUTO_ALIGN, TRUE,
        PARCEL_MEDIA_COMMAND_LOOP_SET, (float)loop_enabled
    ];

    if (play_now)
        commands = commands + [PARCEL_MEDIA_COMMAND_PLAY];

    llParcelMediaCommandList(commands);
}

set_loop(integer enabled)
{
    llParcelMediaCommandList([
        PARCEL_MEDIA_COMMAND_LOOP_SET, (float)enabled,
        PARCEL_MEDIA_COMMAND_AUTO_ALIGN, TRUE
    ]);

    if (enabled)
        llParcelMediaCommandList([PARCEL_MEDIA_COMMAND_LOOP]);
    else
        llParcelMediaCommandList([PARCEL_MEDIA_COMMAND_PLAY]);
}

query_media(key agent)
{
    list values = llParcelMediaQuery([
        PARCEL_MEDIA_COMMAND_URL,
        PARCEL_MEDIA_COMMAND_DESC,
        PARCEL_MEDIA_COMMAND_TYPE,
        PARCEL_MEDIA_COMMAND_SIZE,
        PARCEL_MEDIA_COMMAND_AUTO_ALIGN,
        PARCEL_MEDIA_COMMAND_LOOP_SET
    ]);

    if (llGetListLength(values) < 7)
    {
        say_to(agent, "Query returned no media data. Check parcel media permissions for the script owner.");
        return;
    }

    string report =
        "URL: " + llList2String(values, 0) +
        "\nDesc: " + llList2String(values, 1) +
        "\nType: " + llList2String(values, 2) +
        "\nSize: " + (string)llList2Integer(values, 3) + "x" + (string)llList2Integer(values, 4) +
        "\nAuto align: " + (string)llList2Integer(values, 5) +
        "\nLoop: " + (string)llList2Float(values, 6);

    say_to(agent, report);
}

help(key agent)
{
    say_to(agent,
        "SETUP writes URL/type/desc/size/autoscale and starts playback." +
        "\nLOOP ON/OFF exercises PARCEL_MEDIA_COMMAND_LOOP_SET." +
        "\nQUERY reads URL, desc, MIME type, integer size, auto-align and loop."
    );
}

default
{
    state_entry()
    {
        llListen(MENU_CHANNEL, "", NULL_KEY, "");
        llSetText("Parcel Media Loop Console\nTouch for menu", <1.0, 0.8, 0.3>, 1.0);
    }

    touch_start(integer count)
    {
        show_menu(llDetectedKey(0));
    }

    listen(integer channel, string name, key id, string message)
    {
        if (channel != MENU_CHANNEL)
            return;

        if (message == "SETUP")
        {
            apply_media(FALSE, TRUE);
            say_to(id, "Wrote media settings and sent PLAY.");
        }
        else if (message == "LOOP ON")
        {
            set_loop(TRUE);
            say_to(id, "Sent LOOP_SET=1.0 and LOOP.");
        }
        else if (message == "LOOP OFF")
        {
            set_loop(FALSE);
            say_to(id, "Sent LOOP_SET=0.0 and PLAY.");
        }
        else if (message == "PLAY") llParcelMediaCommandList([PARCEL_MEDIA_COMMAND_PLAY]);
        else if (message == "PAUSE") llParcelMediaCommandList([PARCEL_MEDIA_COMMAND_PAUSE]);
        else if (message == "STOP") llParcelMediaCommandList([PARCEL_MEDIA_COMMAND_STOP]);
        else if (message == "UNLOAD") llParcelMediaCommandList([PARCEL_MEDIA_COMMAND_UNLOAD]);
        else if (message == "QUERY") query_media(id);
        else if (message == "HELP") help(id);
    }

    changed(integer change)
    {
        if (change & CHANGED_OWNER)
            llResetScript();
    }
}
