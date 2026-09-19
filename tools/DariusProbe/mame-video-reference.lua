-- Render IntroCapture's video RAM in MAME without running the game's main CPU.
-- Set DARIUS_REFERENCE_DIR to the capture directory. No savestates are modified.
local frame = 0
local cpu = manager.machine.devices[":maincpu"]
local program = cpu.spaces["program"]
local screen = manager.machine.screens[":screen"]
local directory = assert(os.getenv("DARIUS_REFERENCE_DIR"))
local regions = { {"_palette",0x440000}, {"_spriteRam",0x600000}, {"_playfieldRam",0x610000},
 {"_textRam",0x61c000}, {"_charRam",0x61e000}, {"_lineRam",0x620000}, {"_pivotRam",0x630000},
 {"_control0",0x660000}, {"_control1",0x660010} }
emu.register_frame_done(function()
    frame = frame + 1
    if frame == 60 then
        for _, region in ipairs(regions) do
            local file = assert(io.open(directory .. "/" .. region[1] .. ".bin", "rb"))
            local bytes = file:read("a")
            file:close()
            assert(#bytes % 2 == 0)
            for offset = 1, #bytes, 2 do
                program:write_u16(region[2] + offset - 1, bytes:byte(offset) * 256 + bytes:byte(offset + 1))
            end
        end
        program:write_u16(0x41fff0, 0x60fe) -- BRA self, with interrupts masked.
        cpu.state["SR"].value = 0x2700
        cpu.state["PC"].value = 0x41fff0
    end
    if frame >= 61 and frame <= 66 then
        screen:snapshot(string.format("%s/reference%02d.png", directory, frame))
    end
    if frame >= 66 then manager.machine:exit() end
end, "frame")
