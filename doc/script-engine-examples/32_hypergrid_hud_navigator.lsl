// Vanilla Sim Hypergrid HUD Navigator
// Wear this script in a HUD prim. Touch the HUD to browse the Vanilla
// home regions and the external grid attachments currently configured
// for the multigrid showcase.
//
// Destination list: Vanilla Sim home, plus the attached Vanilla Code and
// Vanilla Test registrations on ZetaWorlds, Craft, Neverworld, and OSGrid.
//
// Notes:
// - Direct teleport uses llTeleportAgent and PERMISSION_TELEPORT.
// - The map fallback uses llMapDestination with the same HG address.
// - A hop:// link is also printed privately to the owner for viewers that
//   recognize clickable Hypergrid links in chat.
// - Custom HG accepts examples like:
//   vanilla-sim.com:9000:Vanilla Code
//   hg.zetaworlds.com:80:Vanilla Test
//   craft-world.org:8002:Vanilla Code
//   hop://craft-world.org:8002/Vanilla%20Code/128/128/25

integer PERMS = PERMISSION_TELEPORT;
integer STRIDE = 2;
integer PAGE_SIZE = 8;

integer gMenuChannel;
integer gTextChannel;
integer gListenMenu;
integer gListenText;
integer gPage;
key gOwner;
string gPendingDestination;
string gPendingName;

vector LANDING = <128.0, 128.0, 25.0>;
vector LOOK_AT = <1.0, 0.0, 0.0>;

list GRIDS = [
    "Home Code", "vanilla-sim.com:9000:Vanilla Code",
    "Home Test", "vanilla-sim.com:9000:Vanilla Test",
    "Zeta Code", "hg.zetaworlds.com:80:Vanilla Code",
    "Zeta Test", "hg.zetaworlds.com:80:Vanilla Test",
    "Craft Code", "craft-world.org:8002:Vanilla Code",
    "Craft Test", "craft-world.org:8002:Vanilla Test",
    "Neverworld Code", "hg.neverworldgrid.com:8002:Vanilla Code",
    "Neverworld Test", "hg.neverworldgrid.com:8002:Vanilla Test",
    "OSGrid Code", "hg.osgrid.org:80:Vanilla Code",
    "OSGrid Test", "hg.osgrid.org:80:Vanilla Test"
];

integer gridCount()
{
    return llGetListLength(GRIDS) / STRIDE;
}

string gridName(integer index)
{
    return llList2String(GRIDS, index * STRIDE);
}

string gridAddress(integer index)
{
    return llList2String(GRIDS, index * STRIDE + 1);
}

integer maxPage()
{
    integer count = gridCount();
    integer pages = (count + PAGE_SIZE - 1) / PAGE_SIZE;
    if (pages <= 0)
        return 0;
    return pages - 1;
}

integer startsWith(string value, string prefix)
{
    integer len = llStringLength(prefix);
    if (llStringLength(value) < len)
        return FALSE;
    return llToLower(llGetSubString(value, 0, len - 1)) == llToLower(prefix);
}

string hopFor(string destination)
{
    destination = llStringTrim(destination, STRING_TRIM);
    if (startsWith(destination, "hop://"))
        return destination;
    if (startsWith(destination, "http://"))
        return "hop://" + llGetSubString(destination, 7, -1);
    if (startsWith(destination, "https://"))
        return "hop://" + llGetSubString(destination, 8, -1);
    return "hop://" + destination + "/";
}

integer buttonToIndex(string button)
{
    integer start = gPage * PAGE_SIZE;
    integer end = start + PAGE_SIZE;
    integer count = gridCount();
    if (end > count)
        end = count;

    integer i;
    for (i = start; i < end; ++i)
    {
        if (gridName(i) == button)
            return i;
    }
    return -1;
}

say(string message)
{
    llRegionSayTo(gOwner, 0, "[HG HUD] " + message);
}

ensureListen()
{
    if (gListenMenu)
        llListenRemove(gListenMenu);
    if (gListenText)
        llListenRemove(gListenText);

    gListenMenu = llListen(gMenuChannel, "", gOwner, "");
    gListenText = llListen(gTextChannel, "", gOwner, "");
}

showMenu()
{
    ensureListen();

    integer start = gPage * PAGE_SIZE;
    integer end = start + PAGE_SIZE;
    integer count = gridCount();
    if (end > count)
        end = count;

    list buttons = [];
    integer i;
    for (i = start; i < end; ++i)
        buttons += [gridName(i)];

    buttons += ["<<", ">>", "Custom HG", "Help"];

    string msg = "Vanilla Hypergrid Navigator\n";
    msg += "Page " + (string)(gPage + 1) + "/" + (string)(maxPage() + 1) + "\n";
    msg += "Choose a Vanilla destination. The HUD will try direct teleport, open map fallback, and print a hop:// link.";
    llDialog(gOwner, msg, buttons, gMenuChannel);
}

openCustomBox()
{
    ensureListen();
    llTextBox(
        gOwner,
        "Paste an HG address, region address, or hop URL.\n\nExamples:\nvanilla-sim.com:9000:Vanilla Code\nhg.zetaworlds.com:80:Vanilla Test\ncraft-world.org:8002:Vanilla Code\nhop://craft-world.org:8002/Vanilla%20Code/128/128/25",
        gTextChannel);
}

goDestination(string name, string destination)
{
    destination = llStringTrim(destination, STRING_TRIM);
    if (destination == "")
        return;

    gPendingName = name;
    gPendingDestination = destination;

    string hop = hopFor(destination);
    say("Destination: " + name);
    say("HG address: " + destination);
    say("Clickable/link fallback: " + hop);
    say("If direct teleport does not resolve, paste the HG address into Search/Map or click the hop link if your viewer supports it.");

    llMapDestination(destination, LANDING, LOOK_AT);
    llRequestPermissions(gOwner, PERMS);
}

showHelp()
{
    say("Wear this object as HUD, touch it, pick a Vanilla destination. Home entries use vanilla-sim.com; attached entries use the target grid gatekeeper.");
    say("For exact arrivals use Custom HG with host:port:Region Name. Example: craft-world.org:8002:Vanilla Code or hop://hg.zetaworlds.com:80/Vanilla%20Test/128/128/25");
    say("If a target grid is offline or still has stale map/cache data, retry after the multigrid registration finishes at startup.");
}

default
{
    state_entry()
    {
        gOwner = llGetOwner();
        gMenuChannel = -880032 - (integer)llFrand(100000.0);
        gTextChannel = gMenuChannel - 1;
        gPage = 0;
        llSetText("Vanilla HG HUD\nTouch for destinations", <0.2, 0.9, 1.0>, 1.0);
    }

    attach(key id)
    {
        if (id != NULL_KEY)
        {
            gOwner = id;
            ensureListen();
            say("Ready. Touch the HUD to browse Vanilla Hypergrid destinations.");
        }
    }

    changed(integer change)
    {
        if (change & CHANGED_OWNER)
            llResetScript();
    }

    touch_start(integer total)
    {
        if (llDetectedKey(0) != gOwner)
            return;
        showMenu();
    }

    listen(integer channel, string name, key id, string message)
    {
        if (id != gOwner)
            return;

        if (channel == gTextChannel)
        {
            goDestination("Custom HG", message);
            return;
        }

        if (message == "<<")
        {
            --gPage;
            if (gPage < 0)
                gPage = maxPage();
            showMenu();
            return;
        }

        if (message == ">>")
        {
            ++gPage;
            if (gPage > maxPage())
                gPage = 0;
            showMenu();
            return;
        }

        if (message == "Custom HG")
        {
            openCustomBox();
            return;
        }

        if (message == "Help")
        {
            showHelp();
            showMenu();
            return;
        }

        integer index = buttonToIndex(message);
        if (index >= 0)
            goDestination(gridName(index), gridAddress(index));
    }

    run_time_permissions(integer permissions)
    {
        if ((permissions & PERMS) == 0)
            return;

        if (gPendingDestination == "")
            return;

        say("Trying direct teleport to " + gPendingName + "...");
        llTeleportAgent((string)gOwner, gPendingDestination, LANDING, LOOK_AT);
        gPendingDestination = "";
    }
}
