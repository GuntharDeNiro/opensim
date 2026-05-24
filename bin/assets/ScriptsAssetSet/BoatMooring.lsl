integer MOORING_CHANNEL = 77;
float TIMER_RATE = 0.1;
float WAVE_HEIGHT_1 = 0.09;
float WAVE_HEIGHT_2 = 0.04;
float WAVE_LENGTH_1 = 18.0;
float WAVE_LENGTH_2 = 12.0;
float WAVE_SPEED_1 = 0.85;
float WAVE_SPEED_2 = 0.55;
float MOOR_ROLL_SCALE = 0.65;
float MOOR_PITCH_SCALE = 0.65;

integer gMoored = FALSE;
vector gAnchorPos;
rotation gAnchorRot;
float gMoorStartTime;
integer gListenHandle;

default
{
    state_entry()
    {
        llSetStatus(STATUS_PHYSICS, TRUE);
        llSetVehicleType(VEHICLE_TYPE_BOAT);
        llSetVehicleFloatParam(VEHICLE_HOVER_HEIGHT, 0.4);
        llSetVehicleFloatParam(VEHICLE_HOVER_EFFICIENCY, 0.45);
        llSetVehicleFloatParam(VEHICLE_HOVER_TIMESCALE, 2.0);
        llSetVehicleFloatParam(VEHICLE_BUOYANCY, 1.0);
        llSetVehicleFloatParam(VEHICLE_VERTICAL_ATTRACTION_EFFICIENCY, 0.35);
        llSetVehicleFloatParam(VEHICLE_VERTICAL_ATTRACTION_TIMESCALE, 4.0);

        gListenHandle = llListen(MOORING_CHANNEL, "", llGetOwner(), "");
        llOwnerSay("Boat mooring ready. Say /77 moor or touch to moor here.");
    }

    on_rez(integer start_param)
    {
        llResetScript();
    }

    changed(integer change)
    {
        if (change & CHANGED_OWNER)
            llResetScript();
    }

    touch_start(integer total_number)
    {
        if (llDetectedKey(0) == llGetOwner())
        {
            if (gMoored)
            {
                gMoored = FALSE;
                llSetTimerEvent(0.0);
                llSetStatus(STATUS_PHYSICS, TRUE);
                llOwnerSay("Mooring released.");
            }
            else
            {
                gAnchorPos = llGetPos();
                gAnchorRot = llGetRot();
                gMoorStartTime = llGetTime();
                gMoored = TRUE;
                llStopMoveToTarget();
                llStopLookAt();
                llSetVelocity(<0.0, 0.0, 0.0>, FALSE);
                llSetStatus(STATUS_PHYSICS, FALSE);
                llSetTimerEvent(TIMER_RATE);
                llOwnerSay("Moored. Say /77 release to cast off, or touch to toggle.");
            }
        }
    }

    listen(integer channel, string name, key id, string message)
    {
        message = llToLower(llStringTrim(message, STRING_TRIM));

        if (message == "moor" || message == "toggle")
        {
            if (gMoored)
            {
                gMoored = FALSE;
                llSetTimerEvent(0.0);
                llSetStatus(STATUS_PHYSICS, TRUE);
                llOwnerSay("Mooring released.");
            }
            else
            {
                gAnchorPos = llGetPos();
                gAnchorRot = llGetRot();
                gMoorStartTime = llGetTime();
                gMoored = TRUE;
                llStopMoveToTarget();
                llStopLookAt();
                llSetVelocity(<0.0, 0.0, 0.0>, FALSE);
                llSetStatus(STATUS_PHYSICS, FALSE);
                llSetTimerEvent(TIMER_RATE);
                llOwnerSay("Moored. Say /77 release to cast off.");
            }
        }
        else if (message == "release")
        {
            gMoored = FALSE;
            llSetTimerEvent(0.0);
            llSetStatus(STATUS_PHYSICS, TRUE);
            llOwnerSay("Mooring released.");
        }
        else if (message == "status")
        {
            if (gMoored)
                llOwnerSay("Moored at " + (string)gAnchorPos + ". Physics drift disabled; scripted roll/pitch active.");
            else
                llOwnerSay("Not moored. Say /77 moor to set the current point.");
        }
        else if (message == "help")
        {
            llOwnerSay("Commands: /77 moor, /77 release, /77 toggle, /77 status.");
        }
    }

    timer()
    {
        if (gMoored)
        {
            float t = llGetTime() - gMoorStartTime;
            float waveNumber1 = 6.283185 / WAVE_LENGTH_1;
            float waveNumber2 = 6.283185 / WAVE_LENGTH_2;
            float dir1 = (gAnchorPos.x * 0.928477) + (gAnchorPos.y * -0.371391);
            float dir2 = (gAnchorPos.x * 0.691905) + (gAnchorPos.y * 0.721989);
            float phase1 = (dir1 * waveNumber1) + (t * WAVE_SPEED_1);
            float phase2 = (dir2 * waveNumber2) + (t * WAVE_SPEED_2);
            float bob = WAVE_HEIGHT_1 * llSin(phase1) + WAVE_HEIGHT_2 * llSin(phase2);
            float slope1 = WAVE_HEIGHT_1 * waveNumber1 * llCos(phase1);
            float slope2 = WAVE_HEIGHT_2 * waveNumber2 * llCos(phase2);
            float slopeX = (slope1 * 0.928477) + (slope2 * 0.691905);
            float slopeY = (slope1 * -0.371391) + (slope2 * 0.721989);
            vector lockedPos = gAnchorPos;
            rotation waveRot;

            lockedPos.z = gAnchorPos.z + bob;
            waveRot = llEuler2Rot(<slopeY * MOOR_ROLL_SCALE, -slopeX * MOOR_PITCH_SCALE, 0.0>);

            llSetRegionPos(lockedPos);
            llSetRot(waveRot * gAnchorRot);
        }
    }
}
