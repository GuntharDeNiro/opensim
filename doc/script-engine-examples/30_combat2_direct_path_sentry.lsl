// Combat2 Direct Path Sentry
//
// Demonstrates the new Combat2-style pre-health damage transaction together with
// persistent character options and terrain-aware obstacle-avoiding pathfinding.
//
// Drop this script into a non-physical prim. Touch it to cycle a sentry route.
// Say "damage" on channel 5 to apply self-damage and exercise on_damage,
// final_damage, on_death, llDetectedDamage and llAdjustDamage.

integer CHAT_CHANNEL = 5;
integer gListen;
integer gStep;
float gHealth = 100.0;

say(string msg)
{
    llOwnerSay("[Combat2 Path Sentry] " + msg);
}

vector nav(vector p)
{
    return llGetClosestNavPoint(p, [GCNP_RADIUS, 0.6]);
}

list staticPath(vector start, vector end)
{
    return llGetStaticPath(start, end, 0.6, []);
}

reportPath(string label, vector start, vector end)
{
    list path = staticPath(start, end);
    integer status = llList2Integer(path, 0);
    say(label + " status=" + (string)status + " points=" + (string)llGetListLength(path));

    integer i;
    for (i = 1; i < llGetListLength(path); ++i)
    {
        say("  point " + (string)i + " = " + (string)llList2Vector(path, i));
    }
}

setup()
{
    llSetPrimitiveParams([
        PRIM_HEALTH, gHealth,
        PRIM_DAMAGE, 12.0, DAMAGE_TYPE_FORCE
    ]);

    llCreateCharacter([
        CHARACTER_DESIRED_SPEED, 2.0,
        CHARACTER_MAX_SPEED, 4.0,
        CHARACTER_RADIUS, 0.6,
        CHARACTER_LENGTH, 1.6,
        CHARACTER_AVOIDANCE_MODE, AVOID_DYNAMIC_OBSTACLES | AVOID_CHARACTERS,
        CHARACTER_STAY_WITHIN_PARCEL, TRUE
    ]);

    // Stores sculpt animation state and mirrors it through viewer-visible texture animation.
    llSetSculptAnim(ANIM_ON | LOOP, 4, 4, 0, 15, 8.0, TRUE);

    if (gListen)
        llListenRemove(gListen);
    gListen = llListen(CHAT_CHANNEL, "", llGetOwner(), "");

    say("ready. Touch for movement. Say /5 damage to test Combat2 metadata.");
}

doStep()
{
    vector here = llGetPos();
    vector goal;

    if (gStep == 0)
        goal = nav(here + <12.0, 0.0, 0.0>);
    else if (gStep == 1)
        goal = nav(here + <0.0, 12.0, 0.0>);
    else if (gStep == 2)
        goal = nav(here + <-12.0, 0.0, 0.0>);
    else
        goal = nav(here + <0.0, -12.0, 0.0>);

    reportPath("obstacle-aware route", here, goal);
    llNavigateTo(goal, [CHARACTER_DESIRED_SPEED, 2.0, REQUIRE_LINE_OF_SIGHT, FALSE]);

    gStep = (gStep + 1) % 4;
}

damageSelf()
{
    say("Applying llDamage to this object with DAMAGE_TYPE_FIRE.");
    llDamage(llGetKey(), 18.0, DAMAGE_TYPE_FIRE);
    say("Health is applied after the on_damage quiet transaction; final_damage will report the new value.");
}

default
{
    state_entry()
    {
        setup();
    }

    touch_start(integer n)
    {
        doStep();
    }

    listen(integer channel, string name, key id, string message)
    {
        if (llToLower(message) == "damage")
            damageSelf();
        else if (llToLower(message) == "stop")
            llExecCharacterCmd(CHARACTER_CMD_STOP, []);
        else if (llToLower(message) == "jump")
            llExecCharacterCmd(CHARACTER_CMD_JUMP, []);
        else if (llToLower(message) == "wander")
            llWanderWithin(llGetPos(), <20.0, 20.0, 0.0>, [CHARACTER_DESIRED_SPEED, 2.0, REQUIRE_LINE_OF_SIGHT, FALSE]);
    }

    path_update(integer type, list reserved)
    {
        say("path_update type=" + (string)type + " data=" + llList2CSV(reserved)
            + " (PU_GOAL_REACHED now arrives after motion completion)");
    }

    on_damage(integer n)
    {
        integer i;
        for (i = 0; i < n; ++i)
        {
            list d = llDetectedDamage(i);
            float incoming = llList2Float(d, 0);
            integer dtype = llList2Integer(d, 1);
            key source = llList2Key(d, 3);
            vector sourcePos = llList2Vector(d, 4);

            say("on_damage incoming=" + (string)incoming
                + " type=" + (string)dtype
                + " source=" + (string)source
                + " sourcePos=" + (string)sourcePos);

            if (dtype == DAMAGE_TYPE_FIRE)
            {
                float resisted = incoming * 0.50;
                llAdjustDamage(i, resisted);
                say("fire resistance metadata adjusted damage to " + (string)resisted);
            }
        }
    }

    final_damage(integer n)
    {
        integer i;
        for (i = 0; i < n; ++i)
        {
            list d = llDetectedDamage(i);
            say("final_damage final=" + llList2String(d, 0)
                + " original=" + llList2String(d, 2)
                + " owner=" + llList2String(d, 5)
                + " health=" + (string)llGetHealth((string)llGetKey()));
        }
    }

    on_death()
    {
        say("on_death fired. Resetting PRIM_HEALTH for another test round.");
        gHealth = 100.0;
        llSetPrimitiveParams([PRIM_HEALTH, gHealth]);
    }
}
