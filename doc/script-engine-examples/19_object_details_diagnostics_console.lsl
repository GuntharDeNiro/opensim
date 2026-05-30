// Object details diagnostics console
//
// Demonstrates compatibility implemented in this build:
//
// - llGetObjectDetails cost readback for OBJECT_PRIM_EQUIVALENCE
// - llGetObjectDetails cost readback for OBJECT_SERVER_COST
// - llGetObjectDetails cost readback for OBJECT_STREAMING_COST
// - llGetObjectDetails cost readback for OBJECT_PHYSICS_COST
// - llGetObjectDetails render estimate for OBJECT_RENDER_WEIGHT
// - llGetObjectDetails avatar attachment estimates for the same cost fields
// - llGetObjectDetails avatar hover-height readback with OBJECT_HOVER_HEIGHT
// - llGetObjectDetails object selection state readback with OBJECT_SELECT_COUNT
// - Existing object provenance/readback details such as OBJECT_REZZER_KEY,
//   OBJECT_REZ_TIME, OBJECT_CREATION_TIME, OBJECT_TEXT and OBJECT_TEXT_COLOR
//
// Setup:
// Drop this script into a linked object. Touch it and choose SELF, OWNER or
// SCAN. SELF reports this linkset. OWNER reports the owner's avatar and their
// attachments. SCAN reports the first nearby object/avatar detected.

integer MENU_CHANNEL = -90150019;
integer LISTEN_HANDLE = 0;

key gOperator = NULL_KEY;
key gTarget = NULL_KEY;

say_to(key agent, string message)
{
    llRegionSayTo(agent, 0, "[object-details] " + message);
}

string short_key(key id)
{
    string value = (string)id;
    if (value == (string)NULL_KEY)
        return "NULL_KEY";

    return llGetSubString(value, 0, 7);
}

string compact_float(float value)
{
    return (string)((float)llRound(value * 100.0) / 100.0);
}

string compact_vector(vector value)
{
    return "<" +
        compact_float(value.x) + ", " +
        compact_float(value.y) + ", " +
        compact_float(value.z) + ">";
}

show_menu(key agent)
{
    gOperator = agent;

    if (LISTEN_HANDLE != 0)
        llListenRemove(LISTEN_HANDLE);

    LISTEN_HANDLE = llListen(MENU_CHANNEL, "", agent, "");

    llDialog(agent,
        "Object details diagnostics\n" +
        "Target: " + short_key(gTarget),
        [
            "SELF",
            "OWNER",
            "SCAN",
            "REPORT",
            "SET TEXT",
            "CLEAR TEXT",
            "HELP"
        ],
        MENU_CHANNEL);
}

list detail_query()
{
    return [
        OBJECT_NAME,
        OBJECT_DESC,
        OBJECT_OWNER,
        OBJECT_GROUP,
        OBJECT_CREATOR,
        OBJECT_ROOT,
        OBJECT_PRIM_COUNT,
        OBJECT_PRIM_EQUIVALENCE,
        OBJECT_SERVER_COST,
        OBJECT_STREAMING_COST,
        OBJECT_PHYSICS_COST,
        OBJECT_RENDER_WEIGHT,
        OBJECT_HOVER_HEIGHT,
        OBJECT_SELECT_COUNT,
        OBJECT_SIT_COUNT,
        OBJECT_SCRIPT_TIME,
        OBJECT_RUNNING_SCRIPT_COUNT,
        OBJECT_TOTAL_SCRIPT_COUNT,
        OBJECT_TOTAL_INVENTORY_COUNT,
        OBJECT_REZZER_KEY,
        OBJECT_REZ_TIME,
        OBJECT_CREATION_TIME,
        OBJECT_LINK_NUMBER,
        OBJECT_SCALE,
        OBJECT_TEXT,
        OBJECT_TEXT_COLOR,
        OBJECT_TEXT_ALPHA,
        OBJECT_HEALTH,
        OBJECT_DAMAGE,
        OBJECT_DAMAGE_TYPE
    ];
}

report_target(key agent, key target)
{
    if (target == NULL_KEY)
    {
        say_to(agent, "No target selected. Choose SELF, OWNER or SCAN first.");
        return;
    }

    list details = llGetObjectDetails(target, detail_query());
    integer expected = llGetListLength(detail_query());

    if (llGetListLength(details) < expected)
    {
        say_to(agent, "llGetObjectDetails returned no data for " + short_key(target) + ".");
        return;
    }

    integer i = 0;
    string name = llList2String(details, i++);
    string desc = llList2String(details, i++);
    key owner = llList2Key(details, i++);
    key group = llList2Key(details, i++);
    key creator = llList2Key(details, i++);
    key root = llList2Key(details, i++);
    integer prims = llList2Integer(details, i++);
    integer landImpact = llList2Integer(details, i++);
    float server = llList2Float(details, i++);
    float streaming = llList2Float(details, i++);
    float physics = llList2Float(details, i++);
    integer renderWeight = llList2Integer(details, i++);
    float hoverHeight = llList2Float(details, i++);
    integer selectCount = llList2Integer(details, i++);
    integer sitCount = llList2Integer(details, i++);
    float scriptTime = llList2Float(details, i++);
    integer runningScripts = llList2Integer(details, i++);
    integer totalScripts = llList2Integer(details, i++);
    integer inventoryCount = llList2Integer(details, i++);
    key rezzer = llList2Key(details, i++);
    string rezTime = llList2String(details, i++);
    string creationTime = llList2String(details, i++);
    integer linkNumber = llList2Integer(details, i++);
    vector scale = llList2Vector(details, i++);
    string hoverText = llList2String(details, i++);
    vector textColor = llList2Vector(details, i++);
    float textAlpha = llList2Float(details, i++);
    float health = llList2Float(details, i++);
    float damage = llList2Float(details, i++);
    integer damageType = llList2Integer(details, i++);

    string report =
        "Target " + short_key(target) + "\n" +
        "Name: " + name + "\n" +
        "Description: " + desc + "\n" +
        "Owner/group/creator: " + short_key(owner) + " / " + short_key(group) + " / " + short_key(creator) + "\n" +
        "Root/link/scale: " + short_key(root) + " / " + (string)linkNumber + " / " + compact_vector(scale) + "\n" +
        "Prims/LI/render: " + (string)prims + " / " + (string)landImpact + " / " + (string)renderWeight + "\n" +
        "Costs server/stream/physics: " + compact_float(server) + " / " + compact_float(streaming) + " / " + compact_float(physics) + "\n" +
        "Hover/select/sit: " + compact_float(hoverHeight) + " / " + (string)selectCount + " / " + (string)sitCount + "\n" +
        "Scripts running/total/time: " + (string)runningScripts + " / " + (string)totalScripts + " / " + compact_float(scriptTime) + "\n" +
        "Inventory count: " + (string)inventoryCount + "\n" +
        "Rezzer: " + short_key(rezzer) + "\n" +
        "Rez/creation: " + rezTime + " / " + creationTime + "\n" +
        "Text: '" + hoverText + "' color " + compact_vector(textColor) + " alpha " + compact_float(textAlpha) + "\n" +
        "Health/damage/type: " + compact_float(health) + " / " + compact_float(damage) + " / " + (string)damageType;

    say_to(agent, report);
}

set_test_text(key agent)
{
    llSetText("Diagnostics online\n" + llGetTimestamp(), <0.18, 0.72, 1.0>, 0.88);
    say_to(agent, "Set hover text. Choose SELF then REPORT to verify OBJECT_TEXT, OBJECT_TEXT_COLOR and OBJECT_TEXT_ALPHA.");
}

help(key agent)
{
    say_to(agent,
        "SELF queries this linkset and demonstrates the new nonzero OBJECT_SERVER_COST, OBJECT_STREAMING_COST, OBJECT_PHYSICS_COST and OBJECT_RENDER_WEIGHT readback. " +
        "OWNER queries avatar attachment totals. SCAN queries the first nearby object/avatar.");
}

default
{
    state_entry()
    {
        gTarget = llGetKey();
        llSetText("", ZERO_VECTOR, 0.0);
    }

    touch_start(integer count)
    {
        show_menu(llDetectedKey(0));
    }

    listen(integer channel, string name, key id, string message)
    {
        if (channel != MENU_CHANNEL || id != gOperator)
            return;

        if (message == "SELF")
        {
            gTarget = llGetKey();
            report_target(id, gTarget);
            show_menu(id);
        }
        else if (message == "OWNER")
        {
            gTarget = llGetOwner();
            report_target(id, gTarget);
            show_menu(id);
        }
        else if (message == "SCAN")
        {
            say_to(id, "Scanning 96m for avatars and scripted objects...");
            llSensor("", NULL_KEY, AGENT | ACTIVE | PASSIVE, 96.0, PI);
        }
        else if (message == "REPORT")
        {
            report_target(id, gTarget);
            show_menu(id);
        }
        else if (message == "SET TEXT")
        {
            set_test_text(id);
            gTarget = llGetKey();
            report_target(id, gTarget);
            show_menu(id);
        }
        else if (message == "CLEAR TEXT")
        {
            llSetText("", ZERO_VECTOR, 0.0);
            say_to(id, "Cleared hover text.");
            gTarget = llGetKey();
            report_target(id, gTarget);
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
            say_to(gOperator, "No nearby avatars or objects found.");
            show_menu(gOperator);
            return;
        }

        gTarget = llDetectedKey(0);
        say_to(gOperator, "Selected nearest target: " + llDetectedName(0) + " (" + short_key(gTarget) + ").");
        report_target(gOperator, gTarget);
        show_menu(gOperator);
    }

    no_sensor()
    {
        say_to(gOperator, "No nearby avatars or objects found.");
        show_menu(gOperator);
    }
}
