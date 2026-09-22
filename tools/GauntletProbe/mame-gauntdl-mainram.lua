local output_path = os.getenv("GAUNTDL_MAME_MAINRAM_OUT") or "/tmp/mame-gauntdl-reference/mainram-temple.bin"
local dump_frame = tonumber(os.getenv("GAUNTDL_MAME_MAINRAM_FRAME")) or 120
local enter_selected = os.getenv("GAUNTDL_MAME_MAINRAM_ENTER_SELECTED") == "1"
local frame = 0

local maincpu = manager.machine.devices[":maincpu"]
local program = maincpu and maincpu.spaces["program"]
local player1 = manager.machine.ioport.ports[":8WAY_P1"]
local fight1 = player1 and player1:field(0x0010)

local function dump_main_ram()
	if not program then
		print("[gauntdl-reference] main CPU program space unavailable")
		manager.machine:exit()
		return
	end

	local file, message = io.open(output_path, "wb")
	if not file then
		print(string.format("[gauntdl-reference] main RAM open failed path=%s error=%s", output_path, message))
		manager.machine:exit()
		return
	end

	local chunk_size = 4096
	for base = 0, 0x01ffffff, chunk_size do
		local bytes = {}
		for offset = 0, chunk_size - 1 do
			bytes[offset + 1] = string.char(program:read_u8(base + offset))
		end
		file:write(table.concat(bytes))
	end
	file:close()
	print(string.format("[gauntdl-reference] main RAM dumped frame=%d path=%s bytes=0x02000000", frame, output_path))
	manager.machine:exit()
end

emu.register_frame_done(function()
	frame = frame + 1
	if fight1 then
		fight1:set_value(enter_selected and frame >= 60 and frame < 65 and 1 or 0)
	end
	if frame == dump_frame then
		dump_main_ram()
	end
end, "frame")
