// LSL Compatibility Lab Controller
//
// Drop this script into a single non-physical prim and touch it.
// It runs a repeatable pass/fail suite for the newer SL-style APIs implemented
// by this branch: linkset data, JSON, hash/HMAC, script memory/profiler,
// object details, PBR override storage, Combat2 damage metadata and pathfinding.

integer CHAT_CHANNEL = 31;
integer gListen;
integer gPassed;
integer gFailed;
integer gCombatStarted;
integer gPathStarted;
vector gStartPos;

say(string message)
{
    llOwnerSay("[LSL Compatibility Lab] " + message);
}

pass(string name)
{
    ++gPassed;
    say("PASS " + name);
}

fail(string name, string detail)
{
    ++gFailed;
    say("FAIL " + name + ": " + detail);
}

expect(string name, integer condition, string detail)
{
    if (condition)
        pass(name);
    else
        fail(name, detail);
}

reset_scores()
{
    gPassed = 0;
    gFailed = 0;
}

test_linkset_data()
{
    integer rc = llLinksetDataWrite("lab:plain", "ready");
    integer prc = llLinksetDataWriteProtected("lab:secret", "42", "pass");
    string plain = llLinksetDataRead("lab:plain");
    string secret = llLinksetDataReadProtected("lab:secret", "pass");
    list found = llLinksetDataFindKeys("lab:*", 0, 10);

    expect("linkset data write/read",
        rc == LINKSETDATA_OK && prc == LINKSETDATA_OK && plain == "ready" && secret == "42" && llGetListLength(found) >= 2,
        "plain=" + plain + " secret=" + secret + " found=" + llList2CSV(found));
}

test_json_and_hash()
{
    string json = llList2Json(JSON_OBJECT, [
        "suite", "lsl",
        "count", 3,
        "ok", TRUE
    ]);
    json = llJsonSetValue(json, ["revision"], "31");

    string suite = llJsonGetValue(json, ["suite"]);
    string revision = llJsonGetValue(json, ["revision"]);
    string digest = llComputeHash(json, "sha256");
    string mac = llHMAC("compatibility-lab", json, "sha256");

    expect("json/hash/hmac",
        suite == "lsl" && revision == "31" && llStringLength(digest) > 20 && llStringLength(mac) > 20 && digest != mac,
        "json=" + json + " digest=" + digest + " mac=" + mac);
}

test_memory_and_profiler()
{
    integer used = llGetUsedMemory();
    integer free = llGetFreeMemory();
    integer limit = llGetMemoryLimit();
    integer max = llGetSPMaxMemory();
    integer accepted = llSetMemoryLimit(limit);

    llScriptProfiler(PROFILE_SCRIPT_MEMORY);
    llScriptProfiler(PROFILE_NONE);

    expect("memory/profiler",
        used >= 0 && free >= 0 && limit > 0 && max >= limit && accepted,
        "used=" + (string)used + " free=" + (string)free + " limit=" + (string)limit + " max=" + (string)max + " accepted=" + (string)accepted);
}

test_object_details()
{
    list details = llGetObjectDetails(llGetKey(), [
        OBJECT_SERVER_COST,
        OBJECT_STREAMING_COST,
        OBJECT_PHYSICS_COST,
        OBJECT_PRIM_EQUIVALENCE,
        OBJECT_RENDER_WEIGHT,
        OBJECT_DAMAGE_TYPE
    ]);

    expect("object details costs",
        llGetListLength(details) == 6,
        "details=" + llList2CSV(details));
}

test_pbr_storage()
{
    llSetLinkGLTFOverrides(LINK_THIS, ALL_SIDES, [
        OVERRIDE_GLTF_BASE_COLOR_FACTOR, <0.20, 0.55, 1.00>,
        OVERRIDE_GLTF_BASE_ALPHA, 0.80,
        OVERRIDE_GLTF_BASE_ALPHA_MODE, PRIM_GLTF_ALPHA_MODE_BLEND,
        OVERRIDE_GLTF_BASE_DOUBLE_SIDED, TRUE,
        OVERRIDE_GLTF_METALLIC_FACTOR, 0.05,
        OVERRIDE_GLTF_ROUGHNESS_FACTOR, 0.35,
        OVERRIDE_GLTF_EMISSIVE_FACTOR, <0.02, 0.05, 0.10>,
        OVERRIDE_GLTF_EXTENSION_JSON, "{\"OS_compat_lab\":{\"revision\":31}}"
    ]);

    list base = llGetLinkPrimitiveParams(LINK_THIS, [PRIM_GLTF_BASE_COLOR, 0]);
    list rough = llGetLinkPrimitiveParams(LINK_THIS, [PRIM_GLTF_METALLIC_ROUGHNESS, 0]);

    expect("PBR/GLTF override storage",
        llGetListLength(base) > 0 && llGetListLength(rough) > 0,
        "base=" + llList2CSV(base) + " rough=" + llList2CSV(rough));
}

test_combat2()
{
    gCombatStarted = TRUE;
    llSetPrimitiveParams([
        PRIM_HEALTH, 100.0,
        PRIM_DAMAGE, 9.0, DAMAGE_TYPE_FORCE
    ]);

    llDamage(llGetKey(), 12.0, DAMAGE_TYPE_FIRE);
}

test_pathfinding()
{
    gPathStarted = FALSE;
    gStartPos = llGetPos();

    llCreateCharacter([
        CHARACTER_RADIUS, 0.6,
        CHARACTER_LENGTH, 1.6,
        CHARACTER_DESIRED_SPEED, 1.6,
        CHARACTER_MAX_SPEED, 3.0,
        CHARACTER_AVOIDANCE_MODE, AVOID_DYNAMIC_OBSTACLES | AVOID_CHARACTERS,
        CHARACTER_STAY_WITHIN_PARCEL, TRUE
    ]);

    vector goal = llGetClosestNavPoint(gStartPos + <6.0, 0.0, 0.0>, [GCNP_RADIUS, 0.6]);
    list path = llGetStaticPath(gStartPos, goal, 0.6, [REQUIRE_LINE_OF_SIGHT, FALSE]);
    integer status = llList2Integer(path, 0);

    if (llVecDist(goal, ZERO_VECTOR) > 0.1 && llGetListLength(path) > 0 && status == PU_GOAL_REACHED)
    {
        gPathStarted = TRUE;
        pass("static path query");
        llNavigateTo(goal, [CHARACTER_DESIRED_SPEED, 1.6, REQUIRE_LINE_OF_SIGHT, FALSE]);
    }
    else
    {
        fail("static path query", "goal=" + (string)goal + " path=" + llList2CSV(path));
    }
}

run_all()
{
    reset_scores();
    say("Starting full LSL compatibility lab. Chat /31 status for live totals.");
    test_linkset_data();
    test_json_and_hash();
    test_memory_and_profiler();
    test_object_details();
    test_pbr_storage();
    test_combat2();
    test_pathfinding();
    say("Synchronous tests complete. Waiting for Combat2 final_damage and path_update callbacks.");
}

report()
{
    say("Totals: passed=" + (string)gPassed + " failed=" + (string)gFailed
        + " combatStarted=" + (string)gCombatStarted
        + " pathStarted=" + (string)gPathStarted);
}

default
{
    state_entry()
    {
        if (gListen)
            llListenRemove(gListen);
        gListen = llListen(CHAT_CHANNEL, "", llGetOwner(), "");
        say("ready. Touch to run all tests, or chat /31 run, /31 status, /31 reset.");
    }

    touch_start(integer n)
    {
        run_all();
    }

    listen(integer channel, string name, key id, string message)
    {
        string cmd = llToLower(llStringTrim(message, STRING_TRIM));

        if (cmd == "run")
            run_all();
        else if (cmd == "status")
            report();
        else if (cmd == "reset")
        {
            reset_scores();
            llLinksetDataDelete("lab:plain");
            llLinksetDataDeleteProtected("lab:secret", "pass");
            say("reset done.");
        }
        else
            say("commands: run, status, reset");
    }

    linkset_data(integer action, string name, string value)
    {
        if (llSubStringIndex(name, "lab:") == 0)
            say("linkset_data action=" + (string)action + " name=" + name + " value=" + value);
    }

    on_damage(integer n)
    {
        list data = llDetectedDamage(0);
        float incoming = llList2Float(data, 0);
        integer dtype = llList2Integer(data, 1);

        if (gCombatStarted && incoming > 0.0 && dtype == DAMAGE_TYPE_FIRE)
        {
            llAdjustDamage(0, incoming * 0.50);
            pass("Combat2 on_damage metadata and llAdjustDamage");
        }
        else
        {
            fail("Combat2 on_damage metadata", "data=" + llList2CSV(data));
        }
    }

    final_damage(integer n)
    {
        list data = llDetectedDamage(0);
        float final = llList2Float(data, 0);
        float health = llGetHealth((string)llGetKey());

        expect("Combat2 final_damage after quiet window",
            final > 0.0 && final < 12.0 && health < 100.0,
            "final=" + (string)final + " health=" + (string)health + " data=" + llList2CSV(data));
        report();
    }

    on_death()
    {
        pass("Combat2 on_death event");
        llSetPrimitiveParams([PRIM_HEALTH, 100.0]);
    }

    path_update(integer type, list reserved)
    {
        if (gPathStarted && type == PU_GOAL_REACHED)
        {
            pass("path_update after completed motion");
            gPathStarted = FALSE;
            llNavigateTo(gStartPos, [CHARACTER_DESIRED_SPEED, 1.6, REQUIRE_LINE_OF_SIGHT, FALSE]);
        }
        else if (gPathStarted)
        {
            fail("path_update", "type=" + (string)type + " data=" + llList2CSV(reserved));
        }

        report();
    }
}
