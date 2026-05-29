// Rezzer provenance quarantine
//
// This example focuses on object provenance and cleanup. It uses APIs that
// were missing or not exposed before the compatibility work:
//
// - llDetectedRezzer
// - llReturnObjectsByID
// - llMatchGroup
// - llFindNotecardTextSync
// - llGetAttachedListFiltered
//
// Optional notecard:
// Create "TrustedRezzers" and put trusted rezzer UUIDs or owner UUIDs in it.
// If the notecard is missing, the scanner falls back to trusting only the
// object owner and configured active groups.

integer MENU_CHANNEL = -90150015;
integer REQUIRED_PERMS = PERMISSION_RETURN_OBJECTS;

string TRUST_NOTECARD = "TrustedRezzers";
list TRUSTED_GROUPS = [
    "00000000-0000-0000-0000-000000000000"
];

key gOperator = NULL_KEY;
integer gHavePerms;
list gFlaggedObjects;
list gFlaggedNames;
list gTrustedTokens;

say_to(key agent, string message)
{
    llRegionSayTo(agent, 0, "[provenance] " + message);
}

integer is_authorized(key agent)
{
    if (agent == llGetOwner())
        return TRUE;

    if (llMatchGroup(agent, TRUSTED_GROUPS))
        return TRUE;

    return FALSE;
}

integer token_is_trusted(key token)
{
    if (token == NULL_KEY)
        return FALSE;

    if (token == llGetOwner())
        return TRUE;

    if (llListFindList(gTrustedTokens, [(string)token]) >= 0)
        return TRUE;

    return FALSE;
}

integer has_notecard(string name)
{
    return llGetInventoryType(name) == INVENTORY_NOTECARD;
}

load_policy_summary(key agent)
{
    gTrustedTokens = [(string)llGetOwner()];

    if (!has_notecard(TRUST_NOTECARD))
    {
        say_to(agent, "Trust notecard is not present. Add '" + TRUST_NOTECARD + "' to test llFindNotecardTextSync; using object owner only.");
        return;
    }

    list hits = llFindNotecardTextSync(
        TRUST_NOTECARD,
        "([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
        0,
        24,
        []
    );

    integer len = llGetListLength(hits);
    integer rows = len / 3;
    integer i;

    for (i = 0; i < rows; i = i + 1)
    {
        integer line = llList2Integer(hits, i * 3);
        string text = llGetNotecardLineSync(TRUST_NOTECARD, line);
        list words = llParseString2List(text, [" ", ",", ";"], []);
        integer w;
        for (w = 0; w < llGetListLength(words); w = w + 1)
        {
            string maybe_key = llList2String(words, w);
            if ((key)maybe_key != NULL_KEY)
            {
                if (llListFindList(gTrustedTokens, [maybe_key]) < 0)
                    gTrustedTokens = gTrustedTokens + [maybe_key];
            }
        }
    }

    say_to(agent, "Loaded " + (string)llGetListLength(gTrustedTokens) + " trusted owner/rezzer token(s).");
}

show_menu(key agent)
{
    gOperator = agent;
    llDialog(agent, "Rezzer provenance quarantine", [
        "LOAD",
        "SCAN",
        "HUDS",
        "REPORT",
        "RETURN"
    ], MENU_CHANNEL);
}

run_scan(key agent)
{
    load_policy_summary(agent);
    gFlaggedObjects = [];
    gFlaggedNames = [];
    say_to(agent, "Scanning 96m for untrusted provenance...");
    llSensor("", NULL_KEY, ACTIVE | PASSIVE | SCRIPTED, 96.0, PI);
}

run_huds(key agent)
{
    list huds = llGetAttachedListFiltered(agent, [
        FILTER_INCLUDE, ATTACH_ANY_HUD,
        FILTER_FLAGS, FILTER_FLAG_HUDS
    ]);

    say_to(agent, "HUD attachments visible to this script: " + llDumpList2String(huds, ", "));
}

run_report(key agent)
{
    integer count = llGetListLength(gFlaggedObjects);
    if (count == 0)
    {
        say_to(agent, "No flagged objects from the last scan.");
        return;
    }

    integer i;
    string report = "Flagged objects:";
    for (i = 0; i < count; i = i + 1)
    {
        report = report +
            "\n" + llList2String(gFlaggedNames, i) +
            " id=" + llList2String(gFlaggedObjects, i);
    }

    say_to(agent, report);
}

run_return(key agent)
{
    if (!gHavePerms)
    {
        say_to(agent, "Missing PERMISSION_RETURN_OBJECTS.");
        llRequestPermissions(llGetOwner(), REQUIRED_PERMS);
        return;
    }

    integer count = llGetListLength(gFlaggedObjects);
    if (count == 0)
    {
        say_to(agent, "Nothing to return. Run SCAN first.");
        return;
    }

    integer result = llReturnObjectsByID(gFlaggedObjects);
    say_to(agent, "llReturnObjectsByID returned " + (string)result + ".");
}

default
{
    state_entry()
    {
        llListen(MENU_CHANNEL, "", NULL_KEY, "");
        llRequestPermissions(llGetOwner(), REQUIRED_PERMS);
        llSetText("Rezzer Provenance Quarantine\nTouch for menu", <1.0, 0.7, 0.2>, 1.0);
    }

    run_time_permissions(integer permissions)
    {
        gHavePerms = ((permissions & REQUIRED_PERMS) == REQUIRED_PERMS);
    }

    touch_start(integer total)
    {
        key agent = llDetectedKey(0);
        if (is_authorized(agent))
            show_menu(agent);
        else
            say_to(agent, "Access denied. Active group is not trusted.");
    }

    listen(integer channel, string name, key id, string message)
    {
        if (channel != MENU_CHANNEL)
            return;

        if (!is_authorized(id))
        {
            say_to(id, "Access denied.");
            return;
        }

        if (message == "LOAD") load_policy_summary(id);
        else if (message == "SCAN") run_scan(id);
        else if (message == "HUDS") run_huds(id);
        else if (message == "REPORT") run_report(id);
        else if (message == "RETURN") run_return(id);
    }

    sensor(integer count)
    {
        integer i;
        for (i = 0; i < count; i = i + 1)
        {
            key object_id = llDetectedKey(i);
            key owner = llDetectedOwner(i);
            key rezzer = llDetectedRezzer(i);

            integer trusted = FALSE;
            if (token_is_trusted(owner))
                trusted = TRUE;
            if (token_is_trusted(rezzer))
                trusted = TRUE;

            if (!trusted)
            {
                if (llListFindList(gFlaggedObjects, [object_id]) < 0)
                {
                    gFlaggedObjects = gFlaggedObjects + [object_id];
                    gFlaggedNames = gFlaggedNames + [llDetectedName(i)];
                }
            }
        }

        say_to(gOperator, "Scan complete. Flagged " + (string)llGetListLength(gFlaggedObjects) + " object(s).");
    }

    no_sensor()
    {
        say_to(gOperator, "Nothing found in scan range.");
    }

    changed(integer change)
    {
        if (change & CHANGED_OWNER)
            llResetScript();
    }
}
