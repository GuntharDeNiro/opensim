// Physics Energy Meter
//
// Demonstrates dynamic llGetEnergy behavior:
// - energy drains after supported physical-control calls
// - energy recharges over time
// - scripts can inspect energy before chaining force, torque, impulse or push work

integer MENU_CHANNEL = -88230126;
integer gListen;
integer gForceOn;
integer gTorqueOn;
integer gHoverOn;
integer gBuoyancyOn;
key gPushTarget;
string gPushTargetName = "";
list gHistory;

string percent(float value)
{
    return (string)llRound(value * 100.0) + "%";
}

string trimReport(string report)
{
    if (llStringLength(report) > 1000)
    {
        return llGetSubString(report, 0, 999) + "\n...";
    }

    return report;
}

addHistory(string label, float beforeEnergy, float afterEnergy)
{
    string row = label + ": " + percent(beforeEnergy) + " -> " + percent(afterEnergy);

    gHistory = [row] + gHistory;
    if (llGetListLength(gHistory) > 8)
    {
        gHistory = llList2List(gHistory, 0, 7);
    }
}

string buildStatus()
{
    string status = "Physics energy\n";
    status += "energy: " + percent(llGetEnergy()) + "\n";
    status += "force: " + (string)gForceOn + " torque: " + (string)gTorqueOn + "\n";
    status += "hover: " + (string)gHoverOn + " buoyancy: " + (string)gBuoyancyOn;

    if (gPushTarget != NULL_KEY)
    {
        status += "\npush target: " + gPushTargetName;
    }

    return status;
}

showHover()
{
    vector color = <0.25, 1.00, 0.45>;

    if (llGetEnergy() < 0.35)
    {
        color = <1.00, 0.45, 0.20>;
    }

    llSetText(buildStatus(), color, 1.0);
}

sayHistory()
{
    integer count = llGetListLength(gHistory);
    integer i;
    string report = buildStatus();

    report += "\n\nRecent operations:";
    for (i = 0; i < count; i += 1)
    {
        report += "\n" + llList2String(gHistory, i);
    }

    if (count == 0)
    {
        report += "\n(none yet)";
    }

    llOwnerSay(trimReport(report));
}

ensurePhysical()
{
    llSetStatus(STATUS_PHYSICS, TRUE);
}

doImpulse()
{
    float beforeEnergy = llGetEnergy();

    ensurePhysical();
    llApplyImpulse(<0.0, 0.0, 18.0>, FALSE);
    addHistory("impulse", beforeEnergy, llGetEnergy());
    showHover();
}

doSpin()
{
    float beforeEnergy = llGetEnergy();

    ensurePhysical();
    llApplyRotationalImpulse(<0.0, 0.0, 12.0>, FALSE);
    addHistory("rot impulse", beforeEnergy, llGetEnergy());
    showHover();
}

toggleForce()
{
    float beforeEnergy = llGetEnergy();

    ensurePhysical();
    gForceOn = !gForceOn;

    if (gForceOn)
    {
        llSetForce(<9.0, 0.0, 0.0>, TRUE);
    }
    else
    {
        llSetForce(ZERO_VECTOR, FALSE);
    }

    addHistory("force " + (string)gForceOn, beforeEnergy, llGetEnergy());
    showHover();
}

toggleTorque()
{
    float beforeEnergy = llGetEnergy();

    ensurePhysical();
    gTorqueOn = !gTorqueOn;

    if (gTorqueOn)
    {
        llSetTorque(<0.0, 0.0, 4.0>, TRUE);
    }
    else
    {
        llSetTorque(ZERO_VECTOR, FALSE);
    }

    addHistory("torque " + (string)gTorqueOn, beforeEnergy, llGetEnergy());
    showHover();
}

toggleHover()
{
    float beforeEnergy = llGetEnergy();

    ensurePhysical();
    gHoverOn = !gHoverOn;

    if (gHoverOn)
    {
        llSetHoverHeight(2.5, FALSE, 0.7);
    }
    else
    {
        llStopHover();
    }

    addHistory("hover " + (string)gHoverOn, beforeEnergy, llGetEnergy());
    showHover();
}

toggleBuoyancy()
{
    float beforeEnergy = llGetEnergy();

    ensurePhysical();
    gBuoyancyOn = !gBuoyancyOn;

    if (gBuoyancyOn)
    {
        llSetBuoyancy(0.45);
    }
    else
    {
        llSetBuoyancy(0.0);
    }

    addHistory("buoyancy " + (string)gBuoyancyOn, beforeEnergy, llGetEnergy());
    showHover();
}

pushTarget()
{
    if (gPushTarget == NULL_KEY)
    {
        llOwnerSay("No push target. Ask an avatar to touch the meter first.");
        return;
    }

    float beforeEnergy = llGetEnergy();

    llPushObject(gPushTarget, <0.0, 0.0, 35.0>, ZERO_VECTOR, FALSE);
    addHistory("push " + gPushTargetName, beforeEnergy, llGetEnergy());
    showHover();
}

stopAll()
{
    float beforeEnergy = llGetEnergy();

    gForceOn = FALSE;
    gTorqueOn = FALSE;
    gHoverOn = FALSE;
    gBuoyancyOn = FALSE;

    llSetForce(ZERO_VECTOR, FALSE);
    llSetTorque(ZERO_VECTOR, FALSE);
    llStopHover();
    llSetBuoyancy(0.0);

    addHistory("stop all", beforeEnergy, llGetEnergy());
    showHover();
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
        buildStatus(),
        [
            "IMPULSE",
            "SPIN",
            "FORCE",
            "TORQUE",
            "HOVER",
            "BUOYANCY",
            "PUSH",
            "REPORT",
            "STOP"
        ],
        MENU_CHANNEL
    );
}

default
{
    state_entry()
    {
        llSetTimerEvent(1.0);
        showHover();
        llOwnerSay("Ready. Owner touch opens controls; any other avatar touch sets the push target.");
    }

    touch_start(integer total)
    {
        key toucher = llDetectedKey(0);

        if (toucher != llGetOwner())
        {
            gPushTarget = toucher;
            gPushTargetName = llDetectedName(0);
            llRegionSayTo(toucher, 0, "You are now the owner's push test target for this energy meter.");
            showHover();
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

        if (message == "IMPULSE")
        {
            doImpulse();
        }
        else if (message == "SPIN")
        {
            doSpin();
        }
        else if (message == "FORCE")
        {
            toggleForce();
        }
        else if (message == "TORQUE")
        {
            toggleTorque();
        }
        else if (message == "HOVER")
        {
            toggleHover();
        }
        else if (message == "BUOYANCY")
        {
            toggleBuoyancy();
        }
        else if (message == "PUSH")
        {
            pushTarget();
        }
        else if (message == "REPORT")
        {
            sayHistory();
        }
        else if (message == "STOP")
        {
            stopAll();
        }

        showMenu(id);
    }

    timer()
    {
        showHover();
    }

    changed(integer change)
    {
        if (change & (CHANGED_OWNER | CHANGED_REGION | CHANGED_REGION_START))
        {
            llResetScript();
        }
    }
}
