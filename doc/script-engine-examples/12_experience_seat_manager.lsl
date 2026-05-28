// Experience scripted seat manager
//
// Drop this script into the root prim of a linked object that has one or more
// child prims with sit targets. Touch the controller to be seated without the
// usual repeated permission popup once the estate trusts the script owner/object.

integer FIRST_SEAT_LINK = 2;
integer LAST_SEAT_LINK = 6;

integer gNextSeat = 2;

integer nextSeat()
{
    integer seat = gNextSeat;
    gNextSeat = gNextSeat + 1;

    if (gNextSeat > LAST_SEAT_LINK)
    {
        gNextSeat = FIRST_SEAT_LINK;
    }

    return seat;
}

string sitError(integer code)
{
    if (code == SIT_NOT_EXPERIENCE) return "script is not trusted as an Experience";
    if (code == SIT_NO_EXPERIENCE_PERMISSION) return "avatar has not accepted Experience permissions";
    if (code == SIT_NO_SIT_TARGET) return "no available sit target";
    if (code == SIT_INVALID_AGENT) return "avatar is not in this region";
    if (code == SIT_INVALID_LINK) return "seat link is invalid";
    if (code == SIT_NO_ACCESS) return "avatar does not have parcel access";
    if (code == SIT_INVALID_OBJECT) return "target object cannot be sat upon";
    return "unknown sit error " + (string)code;
}

default
{
    state_entry()
    {
        LAST_SEAT_LINK = llGetNumberOfPrims();
        if (LAST_SEAT_LINK < FIRST_SEAT_LINK)
        {
            LAST_SEAT_LINK = FIRST_SEAT_LINK;
        }

        gNextSeat = FIRST_SEAT_LINK;
        llSetText("Touch to take a seat", <0.7, 0.9, 1.0>, 1.0);
    }

    touch_start(integer total)
    {
        key agent = llDetectedKey(0);

        if (!llIsExperienceTrusted())
        {
            llInstantMessage(agent, "This estate has not trusted the Experience-Lite script owner/object yet.");
            return;
        }

        llRequestExperiencePermissions(agent, "");
    }

    experience_permissions(key agent)
    {
        integer attempts = LAST_SEAT_LINK - FIRST_SEAT_LINK + 1;
        integer seat;
        integer result;

        while (attempts > 0)
        {
            attempts = attempts - 1;
            seat = nextSeat();
            result = llSitOnLink(agent, seat);

            if (result == 1)
            {
                llInstantMessage(agent, "Seat assigned on link " + (string)seat + ".");
                return;
            }

            if (result != SIT_NO_SIT_TARGET)
            {
                llInstantMessage(agent, "Cannot seat you: " + sitError(result) + ".");
                return;
            }
        }

        llInstantMessage(agent, "All scripted seats are currently occupied.");
    }

    experience_permissions_denied(key agent, integer reason)
    {
        llInstantMessage(agent, "Experience permissions denied: " + llGetExperienceErrorMessage(reason));
    }

    changed(integer change)
    {
        if (change & CHANGED_LINK)
        {
            llSetText("Touch to take a seat", <0.7, 0.9, 1.0>, 1.0);
        }
    }
}
