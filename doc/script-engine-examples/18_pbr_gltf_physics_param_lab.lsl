// PBR GLTF and physics primitive-param lab
//
// Demonstrates compatibility implemented in this build:
//
// - PRIM_RENDER_MATERIAL in llSetPrimitiveParams/llSetLinkPrimitiveParams
// - PRIM_RENDER_MATERIAL readback through llGetPrimitiveParams/llGetLinkPrimitiveParams
// - PRIM_GLTF_* setters through llSetPrimitiveParams/llSetLinkPrimitiveParams
// - PRIM_GLTF_BASE_COLOR readback after llSetLinkGLTFOverrides
// - PRIM_GLTF_METALLIC_ROUGHNESS readback after llSetLinkGLTFOverrides
// - PRIM_GLTF_EMISSIVE readback after llSetLinkGLTFOverrides
// - PRIM_GLTF_NORMAL texture/transform readback shape for stored override data
// - PRIM_GLTF_* readback from the assigned material asset when no override exists
// - PRIM_PHYSICS_MATERIAL set and readback using Second Life argument order
//
// Setup:
// Put one material asset in this object's inventory. Optionally add one texture
// asset so the direct PRIM_GLTF_* setters can store texture UUID overrides too.
// Touch the object and use ASSET ONLY, PARAM PBR, APPLY PBR or APPLY ALL. Use
// READ PBR to verify what the simulator now returns. Use PHYSICS and READ PHYS
// to test PRIM_PHYSICS_MATERIAL.

integer MENU_CHANNEL = -90150018;
integer TEST_FACE = 0;

key gOperator = NULL_KEY;

say_to(key agent, string message)
{
    llRegionSayTo(agent, 0, "[pbr-physics-lab] " + message);
}

string first_material()
{
    if (llGetInventoryNumber(INVENTORY_MATERIAL) <= 0)
        return "";

    return llGetInventoryName(INVENTORY_MATERIAL, 0);
}

string first_texture()
{
    if (llGetInventoryNumber(INVENTORY_TEXTURE) <= 0)
        return "";

    return llGetInventoryName(INVENTORY_TEXTURE, 0);
}

string alpha_mode_name(integer mode)
{
    if (mode == PRIM_GLTF_ALPHA_MODE_OPAQUE) return "OPAQUE";
    if (mode == PRIM_GLTF_ALPHA_MODE_BLEND) return "BLEND";
    if (mode == PRIM_GLTF_ALPHA_MODE_MASK) return "MASK";
    return "UNKNOWN(" + (string)mode + ")";
}

show_menu(key agent)
{
    gOperator = agent;
    llDialog(agent,
        "PBR GLTF + physics primitive-param lab\n" +
        "Inventory material: " + first_material() +
        "\nInventory texture: " + first_texture(),
        [
            "ASSET ONLY",
            "PARAM PBR",
            "APPLY PBR",
            "APPLY ALL",
            "READ PBR",
            "PHYSICS",
            "READ PHYS",
            "CLEAR PBR",
            "HELP"
        ],
        MENU_CHANNEL
    );
}

list pbr_overrides()
{
    return [
        OVERRIDE_GLTF_BASE_COLOR_FACTOR, <0.20, 0.55, 1.00>,
        OVERRIDE_GLTF_BASE_ALPHA, 0.72,
        OVERRIDE_GLTF_BASE_ALPHA_MODE, PRIM_GLTF_ALPHA_MODE_BLEND,
        OVERRIDE_GLTF_BASE_ALPHA_MASK, 0.35,
        OVERRIDE_GLTF_BASE_DOUBLE_SIDED, TRUE,
        OVERRIDE_GLTF_METALLIC_FACTOR, 0.18,
        OVERRIDE_GLTF_ROUGHNESS_FACTOR, 0.42,
        OVERRIDE_GLTF_EMISSIVE_FACTOR, <0.05, 0.10, 0.22>
    ];
}

apply_asset_only(key agent, integer face)
{
    string material = first_material();
    if (material == "")
    {
        say_to(agent, "Add one material asset to object inventory first. The script will use the first INVENTORY_MATERIAL item.");
        return;
    }

    llSetPrimitiveParams([
        PRIM_RENDER_MATERIAL, face, material
    ]);

    say_to(agent, "Applied only render material '" + material + "' to face " + (string)face + ". READ PBR now checks the assigned GLTF material asset with no scripted override.");
    read_pbr(agent, TEST_FACE);
}

apply_pbr_params(key agent, integer face)
{
    string material = first_material();
    if (material == "")
    {
        say_to(agent, "Add one material asset to object inventory first. The script will use the first INVENTORY_MATERIAL item.");
        return;
    }

    string texture = first_texture();

    llSetPrimitiveParams([
        PRIM_RENDER_MATERIAL, face, material,

        PRIM_GLTF_BASE_COLOR,
            face,
            texture,
            <1.25, 1.25, 0.0>,
            <0.03, -0.02, 0.0>,
            0.18,
            <1.00, 0.62, 0.18>,
            0.86,
            PRIM_GLTF_ALPHA_MODE_MASK,
            0.48,
            TRUE,

        PRIM_GLTF_METALLIC_ROUGHNESS,
            face,
            texture,
            <0.80, 0.80, 0.0>,
            <0.0, 0.0, 0.0>,
            0.0,
            0.08,
            0.30,

        PRIM_GLTF_EMISSIVE,
            face,
            texture,
            <1.0, 1.0, 0.0>,
            <0.0, 0.0, 0.0>,
            0.0,
            <0.18, 0.10, 0.02>,

        PRIM_GLTF_NORMAL,
            face,
            texture,
            <1.0, 1.0, 0.0>,
            <0.0, 0.0, 0.0>,
            0.0
    ]);

    if (texture == "")
        say_to(agent, "Applied PRIM_RENDER_MATERIAL and direct PRIM_GLTF_* factor/transform params to face " + (string)face + ". Add a texture asset to also test GLTF texture UUID storage.");
    else
        say_to(agent, "Applied PRIM_RENDER_MATERIAL and direct PRIM_GLTF_* texture/factor/transform params to face " + (string)face + " using texture '" + texture + "'.");

    read_pbr(agent, TEST_FACE);
}

apply_pbr(key agent, integer face)
{
    string material = first_material();
    if (material == "")
    {
        say_to(agent, "Add one material asset to object inventory first. The script will use the first INVENTORY_MATERIAL item.");
        return;
    }

    llSetPrimitiveParams([
        PRIM_RENDER_MATERIAL, face, material
    ]);

    llSetLinkGLTFOverrides(LINK_THIS, face, pbr_overrides());
    say_to(agent, "Applied render material '" + material + "' and GLTF factor overrides to face " + (string)face + ".");
    read_pbr(agent, TEST_FACE);
}

clear_pbr(key agent)
{
    llSetPrimitiveParams([
        PRIM_RENDER_MATERIAL, ALL_SIDES, (string)NULL_KEY
    ]);

    say_to(agent, "Cleared render material and material overrides from all sides.");
    read_pbr(agent, TEST_FACE);
}

string list_value(list values, integer index)
{
    if (index >= llGetListLength(values))
        return "<missing>";
    return llList2String(values, index);
}

read_pbr(key agent, integer face)
{
    list render = llGetLinkPrimitiveParams(LINK_THIS, [
        PRIM_RENDER_MATERIAL, face
    ]);

    list base = llGetLinkPrimitiveParams(LINK_THIS, [
        PRIM_GLTF_BASE_COLOR, face
    ]);

    list rough = llGetLinkPrimitiveParams(LINK_THIS, [
        PRIM_GLTF_METALLIC_ROUGHNESS, face
    ]);

    list emissive = llGetLinkPrimitiveParams(LINK_THIS, [
        PRIM_GLTF_EMISSIVE, face
    ]);

    list normal = llGetLinkPrimitiveParams(LINK_THIS, [
        PRIM_GLTF_NORMAL, face
    ]);

    string report =
        "Face " + (string)face +
        "\nRender material: " + list_value(render, 0) +
        "\nBase texture: " + list_value(base, 0) +
        "\nBase repeats/offset/rot: " + list_value(base, 1) + " / " + list_value(base, 2) + " / " + list_value(base, 3) +
        "\nBase color/alpha: " + list_value(base, 4) + " / " + list_value(base, 5) +
        "\nAlpha mode/cutoff/double-sided: " + alpha_mode_name(llList2Integer(base, 6)) + " / " + list_value(base, 7) + " / " + list_value(base, 8) +
        "\nMetallic/roughness: " + list_value(rough, 4) + " / " + list_value(rough, 5) +
        "\nEmissive color: " + list_value(emissive, 4) +
        "\nNormal texture transform: " + list_value(normal, 0) + " / " + list_value(normal, 1) + " / " + list_value(normal, 2) + " / " + list_value(normal, 3);

    say_to(agent, report);
}

apply_physics(key agent)
{
    llSetPrimitiveParams([
        PRIM_PHYSICS_MATERIAL,
        DENSITY | FRICTION | RESTITUTION | GRAVITY_MULTIPLIER,
        0.75,
        0.65,
        0.35,
        800.0
    ]);

    say_to(agent, "Applied PRIM_PHYSICS_MATERIAL as bits, gravity, restitution, friction, density.");
    read_physics(agent);
}

read_physics(key agent)
{
    list values = llGetPrimitiveParams([
        PRIM_PHYSICS_MATERIAL
    ]);

    if (llGetListLength(values) < 5)
    {
        say_to(agent, "PRIM_PHYSICS_MATERIAL readback returned no data.");
        return;
    }

    say_to(agent,
        "Physics material readback" +
        "\nBits: " + (string)llList2Integer(values, 0) +
        "\nGravity: " + (string)llList2Float(values, 1) +
        "\nRestitution: " + (string)llList2Float(values, 2) +
        "\nFriction: " + (string)llList2Float(values, 3) +
        "\nDensity: " + (string)llList2Float(values, 4)
    );
}

help(key agent)
{
    say_to(agent,
        "ASSET ONLY sets only PRIM_RENDER_MATERIAL so READ PBR can prove GLTF asset-property readback without script overrides." +
        "\nPARAM PBR writes PRIM_RENDER_MATERIAL and direct PRIM_GLTF_* setters in one llSetPrimitiveParams call." +
        "\nAPPLY PBR sets PRIM_RENDER_MATERIAL on face 0 and then writes GLTF factor overrides." +
        "\nREAD PBR proves PRIM_RENDER_MATERIAL and PRIM_GLTF_* set/readback." +
        "\nPHYSICS sets PRIM_PHYSICS_MATERIAL in SL order: bits, gravity, restitution, friction, density." +
        "\nREAD PHYS proves PRIM_PHYSICS_MATERIAL readback."
    );
}

default
{
    state_entry()
    {
        llListen(MENU_CHANNEL, "", NULL_KEY, "");
        llSetText("PBR GLTF + Physics Param Lab\nTouch for menu", <0.3, 0.8, 1.0>, 1.0);
    }

    touch_start(integer count)
    {
        show_menu(llDetectedKey(0));
    }

    listen(integer channel, string name, key id, string message)
    {
        if (channel != MENU_CHANNEL)
            return;

        if (message == "ASSET ONLY") apply_asset_only(id, TEST_FACE);
        else if (message == "PARAM PBR") apply_pbr_params(id, TEST_FACE);
        else if (message == "APPLY PBR") apply_pbr(id, TEST_FACE);
        else if (message == "APPLY ALL") apply_pbr(id, ALL_SIDES);
        else if (message == "READ PBR") read_pbr(id, TEST_FACE);
        else if (message == "PHYSICS") apply_physics(id);
        else if (message == "READ PHYS") read_physics(id);
        else if (message == "CLEAR PBR") clear_pbr(id);
        else if (message == "HELP") help(id);
    }

    changed(integer change)
    {
        if (change & CHANGED_INVENTORY)
            llSetText("PBR GLTF + Physics Param Lab\nMaterial: " + first_material(), <0.3, 0.8, 1.0>, 1.0);

        if (change & CHANGED_OWNER)
            llResetScript();
    }
}
