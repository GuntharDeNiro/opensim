// Identity lookup console
//
// Demonstrates compatibility implemented in this build:
//
// - llKey2Name resolves in-region objects/avatars and cached local user accounts
// - llGetUsername resolves cached local user accounts instead of only live avatars
// - llGetDisplayName resolves cached local user accounts instead of only live avatars
// - llName2Key can resolve local cached account names synchronously
// - llRequestUsername, llRequestDisplayName and llRequestUserKey still provide
//   dataserver replies for async SL-style scripts
//
// Setup:
// Drop into any object. Put a legacy name or username in the object description
// if you want DESC KEY to test llName2Key/llRequestUserKey without editing code.

integer MENU_CHANNEL = -90150020;
integer LISTEN_HANDLE;

key gOperator = NULL_KEY;
key gTarget = NULL_KEY;
string gLookupName = "";

list gRequests;

say_to(key agent, string message)
{
    llRegionSayTo(agent, 0, "[identity-lookup] " + message);
}

string short_key(key id)
{
    string value = (string)id;
    if (value == (string)NULL_KEY)
        return "NULL_KEY";
    return llGetSubString(value, 0, 7);
}

set_request(key query, string label)
{
    if (query == NULL_KEY)
        return;

    gRequests += [query, label];
}

string request_label(key query)
{
    integer index = llListFindList(gRequests, [query]);
    if (index < 0 || index + 1 >= llGetListLength(gRequests))
        return "unknown";

    string label = llList2String(gRequests, index + 1);
    gRequests = llDeleteSubList(gRequests, index, index + 1);
    return label;
}

show_menu(key agent)
{
    gOperator = agent;

    if (LISTEN_HANDLE != 0)
        llListenRemove(LISTEN_HANDLE);

    LISTEN_HANDLE = llListen(MENU_CHANNEL, "", agent, "");

    llDialog(agent,
        "Identity lookup console\n" +
        "Target: " + short_key(gTarget) + "\n" +
        "Description lookup: " + gLookupName,
        [
            "OWNER",
            "SCAN",
            "DESC KEY",
            "SYNC",
            "ASYNC",
            "HELP"
        ],
        MENU_CHANNEL);
}

sync_report(key agent, key target)
{
    if (target == NULL_KEY)
    {
        say_to(agent, "No target selected. Choose OWNER or SCAN first.");
        return;
    }

    string keyName = llKey2Name(target);
    string username = llGetUsername(target);
    string displayName = llGetDisplayName(target);
    key syncFromName = NULL_KEY;

    if (keyName != "")
        syncFromName = llName2Key(keyName);

    say_to(agent,
        "Sync identity for " + short_key(target) +
        "\nllKey2Name: " + keyName +
        "\nllGetUsername: " + username +
        "\nllGetDisplayName: " + displayName +
        "\nllName2Key(llKey2Name): " + short_key(syncFromName));
}

async_report(key agent, key target)
{
    if (target == NULL_KEY)
    {
        say_to(agent, "No target selected. Choose OWNER or SCAN first.");
        return;
    }

    set_request(llRequestUsername(target), "llRequestUsername");
    set_request(llRequestDisplayName(target), "llRequestDisplayName");
    say_to(agent, "Requested async username/display-name for " + short_key(target) + ".");
}

desc_lookup(key agent)
{
    gLookupName = llStringTrim(llGetObjectDesc(), STRING_TRIM);
    if (gLookupName == "")
    {
        say_to(agent, "Put a legacy name or username in the object description first.");
        return;
    }

    key syncKey = llName2Key(gLookupName);
    set_request(llRequestUserKey(gLookupName), "llRequestUserKey(" + gLookupName + ")");

    say_to(agent,
        "Description lookup: " + gLookupName +
        "\nllName2Key: " + short_key(syncKey) +
        "\nAsync llRequestUserKey sent.");
}

help(key agent)
{
    say_to(agent,
        "OWNER uses your avatar key. SCAN picks the nearest avatar/object. SYNC shows llKey2Name, llGetUsername, llGetDisplayName and llName2Key. ASYNC sends llRequestUsername and llRequestDisplayName. DESC KEY reads this object's description as a name and tests llName2Key plus llRequestUserKey.");
}

default
{
    state_entry()
    {
        gTarget = llGetOwner();
        gLookupName = llStringTrim(llGetObjectDesc(), STRING_TRIM);
    }

    touch_start(integer count)
    {
        show_menu(llDetectedKey(0));
    }

    listen(integer channel, string name, key id, string message)
    {
        if (channel != MENU_CHANNEL || id != gOperator)
            return;

        if (message == "OWNER")
        {
            gTarget = llGetOwner();
            sync_report(id, gTarget);
            show_menu(id);
        }
        else if (message == "SCAN")
        {
            say_to(id, "Scanning 96m for avatars and objects...");
            llSensor("", NULL_KEY, AGENT | ACTIVE | PASSIVE, 96.0, PI);
        }
        else if (message == "DESC KEY")
        {
            desc_lookup(id);
            show_menu(id);
        }
        else if (message == "SYNC")
        {
            sync_report(id, gTarget);
            show_menu(id);
        }
        else if (message == "ASYNC")
        {
            async_report(id, gTarget);
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

        gTarget = llDetectedKey(0);
        say_to(gOperator, "Selected " + llDetectedName(0) + " (" + short_key(gTarget) + ").");
        sync_report(gOperator, gTarget);
        show_menu(gOperator);
    }

    no_sensor()
    {
        say_to(gOperator, "No nearby avatar or object found.");
        show_menu(gOperator);
    }

    dataserver(key query, string data)
    {
        say_to(gOperator, request_label(query) + " -> " + data);
    }
}
