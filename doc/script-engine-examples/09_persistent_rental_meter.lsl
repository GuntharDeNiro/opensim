integer RENT_SECONDS = 604800;
integer ADMIN_CHANNEL = 66;

key gTenant = NULL_KEY;
key gQuery;
string gStep;
string gTenantKey = "rental.tenant";
string gExpiryKey = "rental.expiry";
integer gExpiry;

owner_say(string msg)
{
    llRegionSayTo(llGetOwner(), 0, msg);
}

update_text()
{
    if (gTenant == NULL_KEY)
    {
        llSetText("Rental Meter\nAvailable\nOwner: /66 rent avatar-uuid", <0.3, 1.0, 0.3>, 1.0);
        return;
    }

    integer remaining = gExpiry - llGetUnixTime();
    if (remaining < 0)
        remaining = 0;
    llSetText("Rental Meter\nTenant: " + (string)gTenant + "\nDays left: " + (string)(remaining / 86400), <1.0, 0.8, 0.3>, 1.0);
}

load_tenant()
{
    gStep = "load_tenant";
    gQuery = llReadKeyValue(gTenantKey);
}

default
{
    state_entry()
    {
        llListen(ADMIN_CHANNEL, "", llGetOwner(), "");
        llSetTimerEvent(60.0);
        load_tenant();
    }

    listen(integer channel, string name, key id, string msg)
    {
        list parts = llParseString2List(msg, [" "], []);
        string command = llToLower(llList2String(parts, 0));

        if (command == "rent")
        {
            key tenant = (key)llList2String(parts, 1);
            if (tenant == NULL_KEY)
            {
                owner_say("Usage: /66 rent avatar-uuid");
                return;
            }

            gTenant = tenant;
            gExpiry = llGetUnixTime() + RENT_SECONDS;
            gStep = "read_before_save_tenant";
            gQuery = llReadKeyValue(gTenantKey);
            return;
        }

        if (command == "clear")
        {
            gStep = "clear_tenant";
            gQuery = llDeleteKeyValue(gTenantKey);
            return;
        }

        if (command == "stats")
            owner_say(llDumpList2String(llGetExperienceKeyValueStoreStats(), " | "));
    }

    dataserver(key query, string data)
    {
        if (query != gQuery)
            return;

        integer comma = llSubStringIndex(data, ",");
        integer ok = (integer)llGetSubString(data, 0, comma - 1);
        string payload = llGetSubString(data, comma + 1, -1);

        if (gStep == "load_tenant")
        {
            if (ok)
            {
                gTenant = (key)payload;
                gStep = "load_expiry";
                gQuery = llReadKeyValue(gExpiryKey);
            }
            else
            {
                update_text();
            }
            return;
        }

        if (gStep == "load_expiry")
        {
            if (ok)
                gExpiry = (integer)payload;
            update_text();
            return;
        }

        if (gStep == "read_before_save_tenant")
        {
            if (ok)
            {
                gStep = "save_tenant_update";
                gQuery = llUpdateKeyValue(gTenantKey, (string)gTenant, TRUE, payload);
            }
            else
            {
                gStep = "save_tenant_create";
                gQuery = llCreateKeyValue(gTenantKey, (string)gTenant);
            }
            return;
        }

        if (gStep == "save_tenant_create" || gStep == "save_tenant_update")
        {
            if (!ok)
            {
                owner_say("Tenant save failed: " + llGetExperienceErrorMessage((integer)payload));
                return;
            }

            gStep = "read_before_save_expiry";
            gQuery = llReadKeyValue(gExpiryKey);
            return;
        }

        if (gStep == "read_before_save_expiry")
        {
            if (ok)
            {
                gStep = "save_expiry_update";
                gQuery = llUpdateKeyValue(gExpiryKey, (string)gExpiry, TRUE, payload);
            }
            else
            {
                gStep = "save_expiry_create";
                gQuery = llCreateKeyValue(gExpiryKey, (string)gExpiry);
            }
            return;
        }

        if (gStep == "save_expiry_create" || gStep == "save_expiry_update")
        {
            if (ok)
            {
                owner_say("Rental saved.");
                update_text();
            }
            else
            {
                owner_say("Expiry save failed: " + llGetExperienceErrorMessage((integer)payload));
            }
            return;
        }

        if (gStep == "clear_tenant")
        {
            gTenant = NULL_KEY;
            gExpiry = 0;
            gStep = "clear_expiry";
            gQuery = llDeleteKeyValue(gExpiryKey);
            update_text();
        }
    }

    timer()
    {
        if (gTenant != NULL_KEY && llGetUnixTime() > gExpiry)
        {
            owner_say("Rental expired for " + (string)gTenant);
            gTenant = NULL_KEY;
            gExpiry = 0;
        }
        update_text();
    }
}
