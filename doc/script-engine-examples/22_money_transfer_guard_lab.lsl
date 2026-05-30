// Money transfer guard lab
//
// Demonstrates compatibility implemented in this build:
//
// - llGiveMoney rejects invalid amounts, group-owned objects, non-owner
//   PERMISSION_DEBIT grants and object UUID targets before calling the money
//   backend.
// - llTransferLindenDollars uses the same debit-owner and avatar-target guard
//   path, then reports transaction_result for asynchronous diagnostics.
//
// Setup:
// Drop this script into an owner-owned prim. Touch the prim as owner, press
// PERMS, grant debit permission, then SCAN to choose a nearby avatar. Use
// AMOUNT to cycle the test payout size. GIVE calls llGiveMoney; XFER calls
// llTransferLindenDollars and reports the transaction_result event.

integer MENU_CHANNEL = -90150022;
integer LISTEN_HANDLE;

integer gAmount = 1;
key gTarget = NULL_KEY;
string gTargetName = "(none)";
key gLastTransaction = NULL_KEY;

say_owner(string message)
{
    llOwnerSay("[money-transfer-lab] " + message);
}

integer has_debit()
{
    return (llGetPermissionsKey() == llGetOwner()) &&
        ((llGetPermissions() & PERMISSION_DEBIT) != 0);
}

string status_line()
{
    string debit = "no";
    if (has_debit())
        debit = "yes";

    return "Target: " + gTargetName +
        "\nAmount: " + (string)gAmount +
        "\nOwner debit permission: " + debit;
}

show_menu()
{
    key owner = llGetOwner();

    if (LISTEN_HANDLE != 0)
        llListenRemove(LISTEN_HANDLE);

    LISTEN_HANDLE = llListen(MENU_CHANNEL, "", owner, "");

    llDialog(owner,
        "Money transfer guard lab\n" + status_line(),
        [
            "PERMS",
            "SCAN",
            "AMOUNT",
            "GIVE",
            "XFER",
            "HELP"
        ],
        MENU_CHANNEL);
}

integer ready_to_pay()
{
    if (!has_debit())
    {
        say_owner("Debit permission is missing. Press PERMS first.");
        return FALSE;
    }

    if (gTarget == NULL_KEY)
    {
        say_owner("No avatar target selected. Press SCAN near the recipient.");
        return FALSE;
    }

    if (gAmount <= 0)
    {
        say_owner("Amount must be positive.");
        return FALSE;
    }

    return TRUE;
}

cycle_amount()
{
    if (gAmount == 1)
        gAmount = 5;
    else if (gAmount == 5)
        gAmount = 10;
    else
        gAmount = 1;
}

default
{
    state_entry()
    {
        say_owner("Ready. Touch for menu; this script never pays without owner debit permission.");
    }

    touch_start(integer count)
    {
        if (llDetectedKey(0) != llGetOwner())
        {
            llRegionSayTo(llDetectedKey(0), 0, "Only the owner can operate this payout lab.");
            return;
        }

        show_menu();
    }

    listen(integer channel, string name, key id, string message)
    {
        if (channel != MENU_CHANNEL || id != llGetOwner())
            return;

        if (message == "PERMS")
        {
            llRequestPermissions(llGetOwner(), PERMISSION_DEBIT);
        }
        else if (message == "SCAN")
        {
            say_owner("Scanning 32m for the nearest avatar target...");
            llSensor("", NULL_KEY, AGENT, 32.0, PI);
        }
        else if (message == "AMOUNT")
        {
            cycle_amount();
            say_owner("Amount is now " + (string)gAmount + ".");
            show_menu();
        }
        else if (message == "GIVE")
        {
            if (ready_to_pay())
            {
                integer ok = llGiveMoney(gTarget, gAmount);
                say_owner("llGiveMoney returned " + (string)ok + " for " + gTargetName + ".");
            }
            show_menu();
        }
        else if (message == "XFER")
        {
            if (ready_to_pay())
            {
                gLastTransaction = llTransferLindenDollars(gTarget, gAmount);
                say_owner("llTransferLindenDollars transaction id: " + (string)gLastTransaction);
            }
            show_menu();
        }
        else if (message == "HELP")
        {
            say_owner("PERMS requests owner debit permission. SCAN selects the nearest avatar. GIVE is synchronous llGiveMoney. XFER reports transaction_result.");
            show_menu();
        }
    }

    run_time_permissions(integer permissions)
    {
        if ((permissions & PERMISSION_DEBIT) != 0)
            say_owner("Owner debit permission granted.");
        else
            say_owner("Debit permission was not granted.");

        show_menu();
    }

    sensor(integer count)
    {
        integer i;
        for (i = 0; i < count; ++i)
        {
            key candidate = llDetectedKey(i);
            if (candidate != llGetOwner())
            {
                gTarget = candidate;
                gTargetName = llDetectedName(i);
                say_owner("Selected " + gTargetName + " (" + (string)gTarget + ").");
                show_menu();
                return;
            }
        }

        say_owner("No non-owner avatar found in range.");
        show_menu();
    }

    no_sensor()
    {
        say_owner("No avatar found in range.");
        show_menu();
    }

    transaction_result(key id, integer success, string data)
    {
        string marker = "";
        if (id == gLastTransaction)
            marker = " latest";

        say_owner("transaction_result" + marker + ": id=" + (string)id +
            " success=" + (string)success + " data=" + data);
    }
}
