// Hypergrid HUD Navigator
// Wear this script in a HUD prim. Touch the HUD to browse active
// Hypergrid-enabled grids and teleport/open the map.
//
// Source list: Hypergrid Business "Active OpenSim Grids", last update
// 2026-05-28. The site notes that Hypergrid-enabled grids use the same
// address for LoginURI and HG address.
//
// Notes:
// - Direct teleport uses llTeleportAgent and PERMISSION_TELEPORT.
// - The map fallback uses llMapDestination with the same HG address.
// - A hop:// link is also printed privately to the owner for viewers that
//   recognize clickable Hypergrid links in chat.
// - Custom HG accepts examples like:
//   hg.osgrid.org:80
//   grid.kitely.com:8002
//   hg.osgrid.org:80:LBSA Plaza
//   hop://hg.osgrid.org:80/LBSA%20Plaza/128/128/25

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
    "OSgrid", "hg.osgrid.org:80",
    "Wolf Territories", "grid.wolfterritories.org:8002",
    "Kitely", "grid.kitely.com:8002",
    "Neverworld", "hg.neverworldgrid.com:8002",
    "ZetaWorlds", "login.zetaworlds.com",
    "Alternate MV", "login.alternatemetaverse.com:8002",
    "DigiWorldz", "login.digiworldz.com:8002",
    "Craft World", "craft-world.org:8002",
    "AviWorlds", "login.aviworlds.com:8002",
    "3DLES", "edugrid.3dles.com:8002",
    "3rd Life", "72.230.211.14:8002",
    "Atek Grid", "login.atekgrid.com:8002",
    "Atlantide", "real-3d.zapto.org:8002",
    "Atlas Grid", "atlasgrid.fr:8002",
    "Ausgrid", "login.ausgridds.biz:8002",
    "Austria Grid", "Austriagrid.eu:10100",
    "AusVirtual", "ausvirtualgrid.xyz:12000",
    "Avacon", "grid.avacon.org:8002",
    "Ay Island", "ay-island.de:8002",
    "Baller Nation", "login.ballernation.us:8002",
    "Barefoot", "login.barefoot-dreamers.com:8002",
    "Beta Tech", "opensim.betatechnologies.info:8002",
    "Beyond Infinity", "infinity.outworldz.net:8002",
    "Bezerkly", "ga.bezerkly.net:8002",
    "Big City", "bigcity.ddns.net:8042",
    "BloodMoon", "bloodmoonpack.com:8002",
    "Bridgemere", "bridgemere.outworldz.net:8002",
    "Caelum Alpha", "caelumalpha.outworldz.net:8002",
    "CandM World", "grid.candmworld.com:8002",
    "CandorsRP", "candorsrpworld.ch:8002",
    "Carima Welt", "carima-welt.de:8002",
    "Carnivale", "hg.carnivalegrid.com:8002",
    "Casperia", "casperia.ddns.net:8002",
    "Cat Woman Rose", "catwomanrose.de:8002",
    "Cave Grid", "cavegrid.org:8002",
    "Ciudad 404", "hg.ciudad404.com:8002",
    "Conectados", "conectados.opensim.fun:8802",
    "Cooperation", "hg.cc-group.cc:8002",
    "Counter Earth", "jand.dyndns.biz:7002",
    "Cozy Comforts", "grid.cozycomforts.net:8002",
    "CreaNovale", "login.creanovale.ca:8052",
    "Curiosity Zone", "login.curiosity-zone.net:8002",
    "CyberDataStorm", "cyberdatastorm.com:8002",
    "Dajas Grid", "Grid.Daja.at:8002",
    "Dark Angel", "hg.darkangelgrid.com:26002",
    "Darkheart", "playground.darkheartsos.com:8002",
    "Decadence", "decadence.ddns.net:8002",
    "Deep Playa", "rajal.org:9000",
    "Devhalla", "devhalla-grid.duckdns.org:9000",
    "Digital Kittens", "hg.digitalkittens.net:8002",
    "Discovery", "discoverygrid.net:8002",
    "Dorena", "dorenas-world.de:8002",
    "DowCraft", "dowcraft.servegame.com:8002",
    "Dragonz Kin", "grid.dragonzkinterritory.org:18002",
    "Dream Life", "dreamlife.opensim.fun:58002",
    "DreamGrid Nyd", "nydalimas.outworldz.net:8002",
    "DWGrid", "dwgrid.nl:8002",
    "Dynamic Worldz", "grid.dynamicworldz.com:8822",
    "EdMondo", "slw.indire.it:8002",
    "Eenhgrid", "eenhgrid.outworldz.net:8002",
    "Elysium", "world.elysiumisles.com:8002",
    "Endivatomic", "endivatomic.eu:8002",
    "Endless Grid", "hg.endless-grid.org:8002",
    "Equinox", "equinoxgrid.com:8002",
    "Escape 2 Reality", "grid.escape2reality.org:8002",
    "EscapeLands", "world.escapelands.com:8002",
    "Esfera Split", "split.esferavirtual.com.br:8002",
    "Esfera Virtual", "esferavirtual.com.br:8002",
    "Eureka World", "grid1.eurekaworld.co.il:8002",
    "Exotic Realities", "login.exoticrealities.com:8002",
    "Fae Farms", "alanna.outworldz.net:8002",
    "Farm Grid", "farm-grid.ddns.net:8002",
    "Flotsam", "hg.flotsamgrid.com:34002",
    "France RP", "francerpgrid.fr:8002",
    "Free Life", "freelife.outworldz.net:8002",
    "Freedom Grid", "freedomgrid.world:8002",
    "Fresh MetaVerse", "os-metavers.fresh-projects.top:8002",
    "Fresh Retro", "retro-metavers.fresh-projects.top:9002",
    "Friends Grid", "login.friends-grid.com:8002",
    "Funny Rides", "login.funny-rides.net:8002",
    "FuoriGrid", "fuorigrid.opensim.online:8002",
    "Furry World", "Furry-World.de:8002",
    "GBG World", "hg.gbg-world.com",
    "Genesis RP", "grid.genesis-roleplay.org:8002",
    "Gentle Fire", "gentlefire.opensim.fun:8002",
    "GerGrid", "gergrid.de:8002",
    "Grid Nirvana", "grid.gridnirvana.net:4002",
    "Grid Racers", "GridRacers.com:8002",
    "GridExperience", "hg.gridexperience.com:8002",
    "GridMania", "gridmania.net:8002",
    "Groovy Verse", "groovyverse.com:8002",
    "Hartland", "hartland.ddns.net:8002",
    "Hawaiian Dreams", "hg.hawaiiandreamsgrid.net:26002",
    "Herederos", "herederos.inworldz.net:8002",
    "HG Safari", "grid.hgsafari.org:58002",
    "Holo Neon", "hg.holoneon.com:8002",
    "Homelandz", "homelandz.opensim.fun:8802",
    "Hypergrid City", "login.hypergridcity.com:8002",
    "I Love You", "iloveyougrid.net:8002",
    "Icelady", "iceladygrid.de:8002",
    "Infinite Grid", "grid.infinitegrid.org:8002",
    "JamGrid", "jamgrid.de:8002",
    "Japan Open", "jogrid.net:8002",
    "Jungle Friends", "junglefriends.opensim.fun:8002",
    "Kamilian Radio", "login.kamilianradio.com:8002",
    "Kara Islands", "kara-grid.world:9000",
    "Kinky Haven", "kinkyhaven.com:8002",
    "Kirmes", "kirmescreations.ddnssec.de:8002",
    "KittyBlue", "grid.xylinvale.space:9000",
    "Kokomo", "grid.kokomoworld.de:8002",
    "Logicamp", "logicamp.org:8002",
    "LuxeLife", "luxelifevirtual.com:8002",
    "MAGA Grid", "magagrid.us:8002",
    "Magic Grid", "magic-grid.de:8002",
    "Majickal Life", "majickallife.com:8002",
    "Margoon", "grid.margoon.ovh:9000",
    "Maze", "maze.bz:7002",
    "Medieval Fantasy", "medieval-fantasy.de:8002",
    "Metaverse Dim", "login.metaversedimensions.com:8002",
    "Midway", "midway.outworldz.net:8002",
    "Migrating Coco", "migratingcoconuts.net:8002",
    "Miracles", "miracles-welt.de:8002",
    "MisFitz", "login.misfitzgrid.com:8002",
    "Mobius", "main.mobiusgrid.us",
    "Moonrose", "moonrose-grid.de:8002",
    "Morada", "moradagrid.com:8002",
    "Moss Grid", "moss.mossgrid.uk:8002",
    "MS Axiom", "grid.msaxiom.com:9000",
    "My Virtual Beach", "grid.myvirtualbeach.com:8002",
    "Naras Nook", "world.narasnook.com:8900",
    "Nemeton Grove", "nemetongrove.online:8002",
    "Neocorex", "neocorex.ru:7002",
    "New Horizon", "newhorizonworld.ddns.net:8002",
    "New Life Italy", "newlifeitaly.com:8002",
    "NewWorld", "newworld.no-ip.org:8002",
    "Next Reality", "login.nextreality.uk:8002",
    "Nexus-Haven", "grid.nexus-haven.ovh:8002",
    "Nomads", "nomads-metaverse.com:8002",
    "OS Experience", "opensimexperience.com:8002",
    "OutofThisWorld", "outofthisworld.opensim.fun:36002",
    "Outworldz", "www.outworldz.com:9000",
    "Pangea", "pangeagrid.de:8002",
    "Paralax", "paralax.life:8002",
    "Party Dest", "partydestinationgrid.com:8002",
    "Phaandoria", "phaandoria.de:8002",
    "SV3D", "sv3d.fr:8002",
    "Swiss Grid", "swissgrid.opensim.ch:8002",
    "T-Grid", "hg.viewtwo.net:8600",
    "Tanduria", "hg.tanduria.de:8002",
    "Terra Nova", "terranova.outworldz.net:8002",
    "The E Grid", "theegrid.outworldz.net:8002",
    "The Haven", "thehavengrid.outworldz.net:9002",
    "The Islands", "theislands.online:9000",
    "The Verse", "theverse.ddns.net:8002",
    "ThinkSim", "thinksim.space:9000",
    "Time Grid", "timegrid.de:8002",
    "TinyOne", "tinyone.uk:9002",
    "Tomi World", "tomis-world.de:8002",
    "Tranquility", "tranquilgrid.uk:8002",
    "Trianon", "hg.trianon-world.com:18002",
    "Twilight", "twlght.com:8002",
    "Twiztid Timez", "login.twiztidtimez.com:8002",
    "Utopia Skye", "utopiaskyegrid.com:8002",
    "Vallands", "hg.vallands.ca:8002",
    "Vendetta Life", "hg.vendettagrid.life:16002",
    "Vibel", "grid.vibel.eu:8002",
    "Virtual Business", "login.virtualbusinessgrid.com:8002",
    "Virtualife", "grid.virtualife-grid.it:8002",
    "Vivo Sim", "hg.vivosim.net:8002",
    "WestWorld", "westworldgrid.net:8002",
    "Whispering", "hg.whisperingwillows.net:8002",
    "Wild Grid", "wildgrid.ddns.net:8002",
    "Wolf Grid alt", "grid.wolf-grid.com:8002",
    "WonderVerse", "grid.wonderverse.pro:8002",
    "Wyldwood Bayou", "wyldwoodbayou.com:8002",
    "Xaara CA", "grid.xaara.ca:8002",
    "Xenolandia", "xenolandia.de:8002",
    "Xmir", "grid.xmir.org:8002",
    "Zodiack", "zodiack.net:8002",
    "Zone Nations", "login.zonenations.com:8002"
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

    string msg = "Hypergrid Navigator\n";
    msg += "Page " + (string)(gPage + 1) + "/" + (string)(maxPage() + 1) + "\n";
    msg += "Choose a grid. The HUD will try direct teleport, open map fallback, and print a hop:// link.";
    llDialog(gOwner, msg, buttons, gMenuChannel);
}

openCustomBox()
{
    ensureListen();
    llTextBox(
        gOwner,
        "Paste an HG address, region address, or hop URL.\n\nExamples:\nhg.osgrid.org:80\ngrid.kitely.com:8002\nhg.osgrid.org:80:LBSA Plaza\nhop://hg.osgrid.org:80/LBSA%20Plaza/128/128/25",
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
    say("Wear this object as HUD, touch it, pick a grid. For exact arrivals use Custom HG with host:port:Region Name. Examples: hg.osgrid.org:80:LBSA Plaza or hop://hg.osgrid.org:80/LBSA%20Plaza/128/128/25");
    say("Some grids are part-time or may reject HG visitors. That is normal for the public active-grid list.");
}

default
{
    state_entry()
    {
        gOwner = llGetOwner();
        gMenuChannel = -880032 - (integer)llFrand(100000.0);
        gTextChannel = gMenuChannel - 1;
        gPage = 0;
        llSetText("HG Navigator\nTouch for grids", <0.2, 0.9, 1.0>, 1.0);
    }

    attach(key id)
    {
        if (id != NULL_KEY)
        {
            gOwner = id;
            ensureListen();
            say("Ready. Touch the HUD to browse Hypergrid destinations.");
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

        string destination = gPendingDestination;
        string destinationName = gPendingName;
        gPendingDestination = "";
        gPendingName = "";

        say("Trying direct teleport to " + destinationName + "...");
        llTeleportAgent((string)gOwner, destination, LANDING, LOOK_AT);
    }
}
