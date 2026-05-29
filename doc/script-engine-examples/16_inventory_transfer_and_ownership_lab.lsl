// Inventory transfer and ownership lab
//
// Demonstrates Second Life-style transfer APIs implemented in this build:
//
// - llGiveAgentInventory
// - TRANSFER_DEST
// - TRANSFER_FLAGS
// - TRANSFER_* result codes
// - llTransferOwnership
// - TRANSFER_FLAG_COPY
// - TRANSFER_FLAG_TAKE
//
// Setup:
// Put this script in a rezzed object. Add a few copyable objects, notecards,
// landmarks or textures to the same object's inventory. Touch it as another
// avatar to test transfer-to-agent behavior. Make the object itself
// copy/transfer if you want COPY OBJECT to succeed.

integer MENU_CHANNEL = -90150016;
integer MAX_PACKAGE_ITEMS = 24;

string DEST_ROOT = "Objects|OpenSim LSL Demos";
string DEST_FOLDER = "Inventory Transfer Lab Kit";

key gTarget = NULL_KEY;
list gPackage;
string gReport;

say_to(key agent, string message)
{
    llRegionSayTo(agent, 0, "[transfer-lab] " + message);
}

string transfer_status(integer code)
{
    if (code == TRANSFER_OK) return "TRANSFER_OK";
    if (code == TRANSFER_BAD_OPTS) return "TRANSFER_BAD_OPTS";
    if (code == TRANSFER_NO_TARGET) return "TRANSFER_NO_TARGET";
    if (code == TRANSFER_THROTTLE) return "TRANSFER_THROTTLE";
    if (code == TRANSFER_NO_ITEMS) return "TRANSFER_NO_ITEMS";
    if (code == TRANSFER_BAD_ROOT) return "TRANSFER_BAD_ROOT";
    if (code == TRANSFER_NO_PERMS) return "TRANSFER_NO_PERMS";
    if (code == TRANSFER_NO_ATTACHMENT) return "TRANSFER_NO_ATTACHMENT";
    return "UNKNOWN(" + (string)code + ")";
}

string inventory_type_name(integer inv_type)
{
    if (inv_type == INVENTORY_TEXTURE) return "texture";
    if (inv_type == INVENTORY_SOUND) return "sound";
    if (inv_type == INVENTORY_LANDMARK) return "landmark";
    if (inv_type == INVENTORY_CLOTHING) return "clothing";
    if (inv_type == INVENTORY_OBJECT) return "object";
    if (inv_type == INVENTORY_NOTECARD) return "notecard";
    if (inv_type == INVENTORY_SCRIPT) return "script";
    if (inv_type == INVENTORY_BODYPART) return "bodypart";
    if (inv_type == INVENTORY_ANIMATION) return "animation";
    if (inv_type == INVENTORY_GESTURE) return "gesture";
    return "other";
}

integer item_can_transfer_to(key agent, string name)
{
    integer mask = llGetInventoryPermMask(name, MASK_OWNER);
    if ((mask & PERM_COPY) == 0)
        return FALSE;

    if (agent != llGetOwner())
    {
        if ((mask & PERM_TRANSFER) == 0)
            return FALSE;
    }

    return TRUE;
}

integer collect_package(key agent)
{
    gPackage = [];
    gReport = "";

    integer total = llGetInventoryNumber(INVENTORY_ALL);
    integer i = 0;
    while (i < total && llGetListLength(gPackage) < MAX_PACKAGE_ITEMS)
    {
        string name = llGetInventoryName(INVENTORY_ALL, i);
        integer inv_type = llGetInventoryType(name);

        if (name != llGetScriptName() && inv_type != INVENTORY_NONE)
        {
            if (item_can_transfer_to(agent, name))
            {
                gPackage = gPackage + [name];
                gReport = gReport + "\n+ " + name + " (" + inventory_type_name(inv_type) + ")";
            }
            else
            {
                gReport = gReport + "\n- skipped " + name + " (needs copy, and transfer if target is not owner)";
            }
        }

        i = i + 1;
    }

    return llGetListLength(gPackage);
}

show_menu(key agent)
{
    gTarget = agent;
    llDialog(agent,
        "Inventory transfer and ownership lab\n" +
        "Target: " + llKey2Name(agent) + "\n" +
        "Destination root: " + DEST_ROOT,
        [
            "SCAN",
            "GIVE KIT",
            "BAD ROOT",
            "COPY OBJECT",
            "TAKE OBJECT",
            "DIRECT OWN",
            "REPORT"
        ],
        MENU_CHANNEL
    );
}

scan_inventory(key agent)
{
    integer count = collect_package(agent);
    if (count == 0)
    {
        say_to(agent, "No eligible copyable inventory items found. Add copy/transfer items to this prim, then touch again.");
        return;
    }

    say_to(agent, "Eligible package item count: " + (string)count + "." + gReport);
}

give_kit(key agent)
{
    integer count = collect_package(agent);
    if (count == 0)
    {
        say_to(agent, "Nothing to give. Add copy/transfer inventory to this object first.");
        return;
    }

    integer result = llGiveAgentInventory(agent, DEST_FOLDER, gPackage, [
        TRANSFER_DEST, DEST_ROOT,
        TRANSFER_FLAGS, 0
    ]);

    say_to(agent,
        "llGiveAgentInventory returned " + transfer_status(result) +
        ". Folder should be under " + DEST_ROOT + "|" + DEST_FOLDER + "."
    );
}

bad_root_test(key agent)
{
    integer count = collect_package(agent);
    if (count == 0)
    {
        say_to(agent, "Bad-root test still needs at least one eligible item, otherwise the transfer stops at TRANSFER_NO_ITEMS first.");
        return;
    }

    integer result = llGiveAgentInventory(agent, "Should Not Arrive", gPackage, [
        TRANSFER_DEST, "||||",
        TRANSFER_FLAGS, 0
    ]);

    say_to(agent, "Intentional invalid TRANSFER_DEST returned " + transfer_status(result) + ".");
}

transfer_object(key agent, integer flags, string label)
{
    integer result = llTransferOwnership(agent, flags, []);
    say_to(agent, label + " returned " + transfer_status(result) + ".");
}

show_report(key agent)
{
    string object_perms = "object owner perms: ";
    integer owner_mask = llGetObjectPermMask(MASK_OWNER);
    if (owner_mask & PERM_COPY) object_perms = object_perms + "copy ";
    if (owner_mask & PERM_TRANSFER) object_perms = object_perms + "transfer ";
    if (owner_mask & PERM_MODIFY) object_perms = object_perms + "modify ";

    say_to(agent,
        object_perms +
        "\nUse GIVE KIT for llGiveAgentInventory." +
        "\nUse COPY OBJECT/TAKE OBJECT/DIRECT OWN for llTransferOwnership."
    );
}

default
{
    state_entry()
    {
        llListen(MENU_CHANNEL, "", NULL_KEY, "");
        llSetText("Inventory Transfer + Ownership Lab\nTouch for menu", <0.4, 0.9, 1.0>, 1.0);
    }

    touch_start(integer total_number)
    {
        show_menu(llDetectedKey(0));
    }

    listen(integer channel, string name, key id, string message)
    {
        if (channel != MENU_CHANNEL)
            return;

        if (message == "SCAN") scan_inventory(id);
        else if (message == "GIVE KIT") give_kit(id);
        else if (message == "BAD ROOT") bad_root_test(id);
        else if (message == "COPY OBJECT") transfer_object(id, TRANSFER_FLAG_COPY, "llTransferOwnership COPY");
        else if (message == "TAKE OBJECT") transfer_object(id, TRANSFER_FLAG_TAKE, "llTransferOwnership TAKE");
        else if (message == "DIRECT OWN") transfer_object(id, 0, "llTransferOwnership direct");
        else if (message == "REPORT") show_report(id);
    }

    changed(integer change)
    {
        if (change & (CHANGED_OWNER | CHANGED_INVENTORY))
            llResetScript();
    }
}
