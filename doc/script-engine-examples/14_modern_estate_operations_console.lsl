// Modern estate operations console
//
// This is intentionally a "busy" example. It exercises LSL calls that stock
// OpenSim either did not expose or could not compile before this compatibility
// pass:
//
// - llMatchGroup
// - llGetAttachedListFiltered
// - llFindNotecardTextSync
// - llSetParcelForSale
// - llReturnObjectsByOwner
// - llSetGroundTexture
// - llSetLinkRenderMaterial
// - llSetLinkGLTFOverrides
//
// Setup:
// 1. Put this script in an estate/parcel control prim.
// 2. Optional: add a notecard named "EstatePolicy" with lines containing
//    words such as ALLOW, DENY, RETURN, SALE or TERRAIN.
// 3. Optional: set MATERIAL_NAME to a material asset in this object's inventory.
// 4. Terrain writes require the script owner, not just the toucher, to be the
//    estate owner/manager.
//    Leave ENABLE_TERRAIN_CHANGE as FALSE until you intentionally test that path.
// 5. Configure TRUSTED_GROUPS, CLEANUP_OWNER and SALE_PRICE for your estate.

integer MENU_CHANNEL = -90140014;
integer REQUIRED_PERMS = PERMISSION_RETURN_OBJECTS | PERMISSION_PRIVILEGED_LAND_ACCESS;

string POLICY_NOTECARD = "EstatePolicy";
string MATERIAL_NAME = "";
string TERRAIN_TEXTURE_1 = "";
integer ENABLE_TERRAIN_CHANGE = FALSE;

integer SALE_PRICE = 2500;
key SALE_BUYER = NULL_KEY;
key CLEANUP_OWNER = NULL_KEY;

list TRUSTED_GROUPS = [
    "00000000-0000-0000-0000-000000000000"
];

key gOperator = NULL_KEY;
integer gListen;
integer gHavePerms;
list gLastOwners;

say_to(key agent, string message)
{
    llRegionSayTo(agent, 0, "[estate-console] " + message);
}

integer configured_key(key value)
{
    return value != NULL_KEY;
}

integer has_notecard(string name)
{
    return llGetInventoryType(name) == INVENTORY_NOTECARD;
}

integer has_material(string name)
{
    if (name == "")
        return FALSE;

    return llGetInventoryType(name) == INVENTORY_MATERIAL;
}

integer is_authorized(key agent)
{
    if (agent == llGetOwner())
        return TRUE;

    if (llMatchGroup(agent, TRUSTED_GROUPS))
        return TRUE;

    return FALSE;
}

string sale_error(integer code)
{
    if (code == PARCEL_SALE_OK) return "parcel sale updated";
    if (code == PARCEL_SALE_ERROR_NO_PARCEL) return "no parcel under the console";
    if (code == PARCEL_SALE_ERROR_NO_PERMISSIONS) return "missing parcel sale permission or parcel ownership";
    if (code == PARCEL_SALE_ERROR_IN_ESCROW) return "parcel is in escrow";
    if (code == PARCEL_SALE_ERROR_INVALID_PRICE) return "invalid sale price";
    if (code == PARCEL_SALE_ERROR_BAD_PARAMS) return "bad sale option list";
    return "parcel sale returned " + (string)code;
}

string return_error(integer code)
{
    if (code >= 0) return "returned " + (string)code + " object(s)";
    if (code == ERR_RUNTIME_PERMISSIONS) return "missing PERMISSION_RETURN_OBJECTS";
    if (code == ERR_PARCEL_PERMISSIONS) return "parcel permissions refused the return";
    if (code == ERR_MALFORMED_PARAMS) return "bad object owner/scope parameters";
    return "return failed: " + (string)code;
}

refresh_display(string extra)
{
    vector color = <0.2, 0.8, 1.0>;
    if (!gHavePerms)
        color = <1.0, 0.4, 0.2>;

    llSetText(
        "Modern Estate Console\n" +
        "perms=" + (string)gHavePerms + "\n" +
        extra,
        color,
        1.0
    );

    if (has_material(MATERIAL_NAME))
        llSetLinkRenderMaterial(LINK_SET, MATERIAL_NAME, ALL_SIDES);

    llSetLinkGLTFOverrides(LINK_SET, ALL_SIDES, [
        OVERRIDE_GLTF_BASE_COLOR_FACTOR, color,
        OVERRIDE_GLTF_BASE_ALPHA, 0.92,
        OVERRIDE_GLTF_BASE_ALPHA_MODE, PRIM_GLTF_ALPHA_MODE_BLEND,
        OVERRIDE_GLTF_ROUGHNESS_FACTOR, 0.35,
        OVERRIDE_GLTF_METALLIC_FACTOR, 0.05
    ]);
}

request_console_permissions()
{
    llRequestPermissions(llGetOwner(), REQUIRED_PERMS);
}

show_menu(key agent)
{
    gOperator = agent;
    llDialog(agent, "Estate operations", [
        "SCAN",
        "POLICY",
        "HUDS",
        "SALE ON",
        "SALE OFF",
        "RETURN",
        "TERRAIN",
        "PBR"
    ], MENU_CHANNEL);
}

run_policy(key agent)
{
    if (!has_notecard(POLICY_NOTECARD))
    {
        say_to(agent, "Policy notecard is not present. Add '" + POLICY_NOTECARD + "' to test llFindNotecardTextSync.");
        return;
    }

    list hits = llFindNotecardTextSync(
        POLICY_NOTECARD,
        "(ALLOW|DENY|RETURN|SALE|TERRAIN)",
        0,
        12,
        []
    );

    integer len = llGetListLength(hits);
    if (len < 3)
    {
        say_to(agent, "No policy hits. Add a notecard named " + POLICY_NOTECARD + ".");
        return;
    }

    integer rows = len / 3;
    integer i;
    string report = "Policy hits:";
    for (i = 0; i < rows; i = i + 1)
    {
        integer stride = i * 3;
        report = report +
            "\nline " + llList2String(hits, stride) +
            " col " + llList2String(hits, stride + 1) +
            " len " + llList2String(hits, stride + 2);
    }

    say_to(agent, report);
}

run_hud_audit(key agent)
{
    list huds = llGetAttachedListFiltered(agent, [
        FILTER_INCLUDE, ATTACH_ANY_HUD,
        FILTER_FLAGS, FILTER_FLAG_HUDS
    ]);

    integer count = llGetListLength(huds);
    if (count == 0)
    {
        say_to(agent, "No visible HUD attachments reported.");
        return;
    }

    say_to(agent, "HUD attachment keys: " + llDumpList2String(huds, ", "));
}

run_sale(key agent, integer enabled)
{
    if (!gHavePerms)
    {
        say_to(agent, "Runtime permissions are not ready yet.");
        request_console_permissions();
        return;
    }

    integer result;
    if (enabled)
    {
        result = llSetParcelForSale(TRUE, [
            PARCEL_SALE_PRICE, SALE_PRICE,
            PARCEL_SALE_AGENT, SALE_BUYER,
            PARCEL_SALE_OBJECTS, FALSE
        ]);
    }
    else
    {
        result = llSetParcelForSale(FALSE, []);
    }

    say_to(agent, sale_error(result));
}

run_return(key agent)
{
    if (!configured_key(CLEANUP_OWNER))
    {
        say_to(agent, "Set CLEANUP_OWNER before using RETURN.");
        return;
    }

    integer result = llReturnObjectsByOwner(CLEANUP_OWNER, OBJECT_RETURN_PARCEL);
    say_to(agent, return_error(result));
}

run_terrain(key agent)
{
    if (!ENABLE_TERRAIN_CHANGE)
    {
        say_to(agent, "Terrain demo is disabled. Set ENABLE_TERRAIN_CHANGE to TRUE only when the script owner is estate owner/manager.");
        return;
    }

    if (TERRAIN_TEXTURE_1 == "")
    {
        say_to(agent, "Set TERRAIN_TEXTURE_1 to a texture UUID before changing terrain.");
        return;
    }

    llSetGroundTexture([
        TERRAIN_DETAIL_1, TERRAIN_TEXTURE_1,
        TERRAIN_HEIGHT_RANGE_SW, 18.0, 58.0,
        TERRAIN_HEIGHT_RANGE_SE, 18.0, 58.0,
        TERRAIN_HEIGHT_RANGE_NW, 22.0, 72.0,
        TERRAIN_HEIGHT_RANGE_NE, 22.0, 72.0
    ]);
    say_to(agent, "Terrain detail layer 1 and blend heights requested.");
}

run_pbr(key agent)
{
    refresh_display("PBR refreshed by " + llKey2Name(agent));
    if (has_material(MATERIAL_NAME))
        say_to(agent, "Applied render material and GLTF overrides to LINK_SET.");
    else
        say_to(agent, "Applied GLTF overrides. Set MATERIAL_NAME to an inventory material to also test llSetLinkRenderMaterial.");
}

run_scan(key agent)
{
    gLastOwners = [];
    say_to(agent, "Scanning 96m for objects and avatars...");
    llSensor("", NULL_KEY, ACTIVE | PASSIVE | SCRIPTED, 96.0, PI);
}

handle_command(key agent, string command)
{
    if (!is_authorized(agent))
    {
        say_to(agent, "Access denied. Active group is not trusted.");
        return;
    }

    if (command == "SCAN") run_scan(agent);
    else if (command == "POLICY") run_policy(agent);
    else if (command == "HUDS") run_hud_audit(agent);
    else if (command == "SALE ON") run_sale(agent, TRUE);
    else if (command == "SALE OFF") run_sale(agent, FALSE);
    else if (command == "RETURN") run_return(agent);
    else if (command == "TERRAIN") run_terrain(agent);
    else if (command == "PBR") run_pbr(agent);
}

default
{
    state_entry()
    {
        gListen = llListen(MENU_CHANNEL, "", NULL_KEY, "");
        request_console_permissions();
        refresh_display("Touch for estate menu");
    }

    run_time_permissions(integer permissions)
    {
        gHavePerms = ((permissions & REQUIRED_PERMS) == REQUIRED_PERMS);
        refresh_display("Touch for estate menu");
    }

    touch_start(integer total)
    {
        key agent = llDetectedKey(0);
        if (is_authorized(agent))
            show_menu(agent);
        else
            say_to(agent, "Touch denied. Wear one of the configured active groups.");
    }

    listen(integer channel, string name, key id, string message)
    {
        if (channel == MENU_CHANNEL)
            handle_command(id, message);
    }

    sensor(integer count)
    {
        integer i;
        string report = "Scan report:";
        integer max_rows = count;
        if (max_rows > 10)
            max_rows = 10;

        for (i = 0; i < max_rows; i = i + 1)
        {
            key owner = llDetectedOwner(i);
            key rezzer = llDetectedRezzer(i);
            if (llListFindList(gLastOwners, [owner]) < 0)
                gLastOwners = gLastOwners + [owner];

            report = report +
                "\n" + llDetectedName(i) +
                " owner=" + (string)owner +
                " rezzer=" + (string)rezzer;
        }

        say_to(gOperator, report);
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
