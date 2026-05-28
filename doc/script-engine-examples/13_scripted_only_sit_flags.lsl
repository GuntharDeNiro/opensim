// Scripted-only sit flags demo
//
// Put this in the root prim of a linked object. Link 2 should be the seat prim.
// Direct viewer sitting on the seat is blocked by SIT_FLAG_SCRIPTED_ONLY; touch
// the controller and the script seats you through llSitOnLink after Experience
// permissions are granted.

integer SEAT_LINK = 2;
vector SIT_OFFSET = <0.0, 0.0, 0.45>;
rotation SIT_ROT = ZERO_ROTATION;
key gAgent;

string sit_error(integer code)
{
    if (code == SIT_NOT_EXPERIENCE) return "script/object is not trusted for Experience-Lite";
    if (code == SIT_NO_EXPERIENCE_PERMISSION) return "avatar has not granted Experience permissions";
    if (code == SIT_NO_SIT_TARGET) return "no free sit target";
    if (code == SIT_INVALID_AGENT) return "invalid or unavailable avatar";
    if (code == SIT_INVALID_LINK) return "seat link is invalid";
    if (code == SIT_NO_ACCESS) return "avatar has no access";
    if (code == SIT_INVALID_OBJECT) return "object cannot be used for scripted sitting";
    return "sit failed: " + (string)code;
}

configure_seat()
{
    llLinkSitTarget(SEAT_LINK, SIT_OFFSET, SIT_ROT);

    llSetLinkSitFlags(
        SEAT_LINK,
        SIT_FLAG_ALLOW_UNSIT |
        SIT_FLAG_SCRIPTED_ONLY |
        SIT_FLAG_NO_COLLIDE |
        SIT_FLAG_NO_DAMAGE
    );

    llSetLinkPrimitiveParamsFast(SEAT_LINK, [
        PRIM_SCRIPTED_SIT_ONLY, TRUE,
        PRIM_ALLOW_UNSIT, TRUE
    ]);

    integer flags = llGetLinkSitFlags(SEAT_LINK);
    llSetText(
        "Scripted-only seat\nTouch to sit\nflags=" + (string)flags,
        <0.2, 1.0, 0.6>,
        1.0
    );
}

default
{
    state_entry()
    {
        configure_seat();
    }

    changed(integer change)
    {
        if (change & CHANGED_LINK)
            configure_seat();
    }

    touch_start(integer total_number)
    {
        gAgent = llDetectedKey(0);
        llRequestExperiencePermissions(gAgent, "Scripted-only seat");
    }

    experience_permissions(key agent)
    {
        integer result = llSitOnLink(agent, SEAT_LINK);
        if (result != 1)
            llInstantMessage(agent, sit_error(result));
    }

    experience_permissions_denied(key agent, integer reason)
    {
        llInstantMessage(agent, "Experience denied: " + llGetExperienceErrorMessage(reason));
    }
}
