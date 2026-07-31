-- Bounded Gauntlet Dark Legacy player-phase oracle.
--
-- Cold/reference run:
--   GAUNTDL_PHASE5_MODE=capture
-- Loads of the resulting "phase5-oracle" MAME state can use:
--   GAUNTDL_PHASE5_MODE=noinput|right|fight|turbo
--
-- The script deliberately writes no instruction trace. Output is limited to
-- state transitions, first writes, and a small set of caller breakpoints.

local mode = os.getenv("GAUNTDL_PHASE5_MODE") or "capture"
local max_frames = tonumber(os.getenv("GAUNTDL_PHASE5_MAX_FRAMES")) or 4400
local phase5_input_start = tonumber(os.getenv("GAUNTDL_PHASE5_INPUT_START")) or 12
local phase5_observation_frames = tonumber(os.getenv("GAUNTDL_PHASE5_OBSERVATION_FRAMES")) or 44
local trace_phase5_edge = os.getenv("GAUNTDL_PHASE5_TRACE") == "1"
local snapshot_phase5 = os.getenv("GAUNTDL_PHASE5_SNAPSHOT") == "1"
local frame = 0
local phase5_frame = nil
local phase4_frame = nil
local exit_frame = nil
local breakpoints_installed = false
local phase4_saved = false
local writer_counts = {}
local phase5_baseline = {}
local compare_ranges = {
	{ 0x00227ab0, 0x00228700 },
	{ 0x00229270, 0x00229c00 },
	{ 0x00231000, 0x00233000 },
}

local machine = manager.machine
local maincpu = machine.devices[":maincpu"]
local program = maincpu and maincpu.spaces["program"]
local system = machine.ioport.ports[":SYSTEM"]
local player1 = machine.ioport.ports[":8WAY_P1"]
local screen = machine.screens[":screen"]

local coin1 = system and system:field(0x0001)
local start1 = system and system:field(0x0004)
local up1 = player1 and player1:field(0x0001)
local down1 = player1 and player1:field(0x0002)
local right1 = player1 and player1:field(0x0008)
local fight1 = player1 and player1:field(0x0010)
local magic1 = player1 and player1:field(0x0020)
local turbo1 = player1 and player1:field(0x0040)

if maincpu.debug then
	maincpu.debug:go()
end

local function set_active(field, active)
	if field then
		field:set_value(active and 1 or 0)
	end
end

local function release_phase5_inputs()
	set_active(right1, false)
	set_active(fight1, false)
	set_active(magic1, false)
	set_active(turbo1, false)
end

local function record_writer(offset, data, mask)
	if not phase5_frame then
		return
	end
	local pc = maincpu.state["PC"] and maincpu.state["PC"].value or 0
	local key = string.format("%08x/%08x", pc, offset)
	writer_counts[key] = (writer_counts[key] or 0) + 1
	if writer_counts[key] <= 3 then
		print(string.format(
			"[phase5-write] frame=%d mode=%s pc=%08x offset=%08x data=%016x mask=%016x",
			frame, mode, pc, offset, data, mask))
	end
end

local player_tap = program:install_write_tap(
	0x00229330, 0x002294a3, "gauntdl-phase5-player", record_writer)
local state_tap = program:install_write_tap(
	0x00227ab0, 0x00227af7, "gauntdl-phase5-main-state", record_writer)

local function install_breakpoint(address, label)
	if not machine.debugger then
		return
	end
	machine.debugger:command(string.format(
		"bpset %08x,1,{printf \"PHASE5_CALL label=%s pc=%%08X a0=%%08X a1=%%08X v0=%%08X s1=%%08X s2=%%08X phase=%%08X state=%%08X input=%%08X norm=%%08X\",pc,a0,a1,v0,s1,s2,ppd@229338,ppd@227ab0,ppd@262b90,ppd@227ba8; g}",
		address, label))
end

local function install_phase5_breakpoints()
	if breakpoints_installed or not machine.debugger then
		return
	end
	machine.debugger:command("focus maincpu")
	install_breakpoint(0x800218a4, "pre-common")
	install_breakpoint(0x800218d8, "phase5-update")
	install_breakpoint(0x800218e8, "post-update")
	install_breakpoint(0x800218f4, "state-refresh")
	install_breakpoint(0x800659c0, "phase5-entry")
	machine.debugger:command(
		"wpset 80229338,4,w,1,{printf \"PHASE5_PHASE_WRITE pc=%08X address=%08X data=%08X mask=%08X\",pc,wpaddr,wpdata,wpmask; g}")
	machine.debugger:command("g")
	breakpoints_installed = true
	print("[phase5-oracle] bounded caller breakpoints installed")
end

local function drive_boot_inputs()
	local cycle = (frame - 1200) % 900
	local active = frame >= 1200 and frame < 2450
	set_active(coin1, active and cycle < 5)
	set_active(start1, active and cycle >= 90 and cycle < 95)
	set_active(up1, active and cycle >= 225 and cycle < 255)
	set_active(down1,
		(frame >= 2240 and frame < 2244) or
		(frame >= 2280 and frame < 2284))
	set_active(fight1,
		(frame < 2200 and active and
			((cycle >= 180 and cycle < 185) or
			(cycle >= 270 and cycle < 275))) or
		(frame >= 2320 and frame < 2324) or
		(frame >= 2360 and frame < 2364) or
		(frame >= 2400 and frame < 2404))
end

local function drive_phase5_input(relative)
	release_phase5_inputs()
	if mode == "right" then
		set_active(right1,
			relative >= phase5_input_start and
			relative < phase5_input_start + 8)
	elseif mode == "fight" then
		set_active(fight1,
			relative >= phase5_input_start and
			relative < phase5_input_start + 4)
	elseif mode == "turbo" then
		set_active(turbo1,
			relative >= phase5_input_start and
			relative < phase5_input_start + 4)
	end
end

local function drive_phase4_input(relative)
	release_phase5_inputs()
	if mode == "phase4-right" then
		set_active(right1, relative >= 170 and relative < 178)
	elseif mode == "phase4-turbo" then
		set_active(turbo1, relative >= 170 and relative < 174)
	elseif mode ~= "phase4-noinput" then
		-- The measured MAME path requires a released Fight edge after the
		-- 300-tick phase-4 timer reaches zero before it enters phase 5.
		set_active(fight1, relative >= 170 and relative < 174)
	end
end

local function capture_phase5_baseline()
	for _, range in ipairs(compare_ranges) do
		for address = range[1], range[2] - 4, 4 do
			phase5_baseline[address] = program:read_u32(address)
		end
	end
end

local function print_phase5_changes()
	local count = 0
	for _, range in ipairs(compare_ranges) do
		for address = range[1], range[2] - 4, 4 do
			local before = phase5_baseline[address]
			local after = program:read_u32(address)
			if before and before ~= after then
				count = count + 1
				if count <= 200 then
					print(string.format(
						"[phase5-change] mode=%s address=%08x before=%08x after=%08x",
						mode, address, before, after))
				end
			end
		end
	end
	print(string.format(
		"[phase5-change-summary] mode=%s count=%d", mode, count))
end

emu.register_frame_done(function()
	frame = frame + 1

	if not phase5_frame and not phase4_frame then
		drive_boot_inputs()
	elseif not phase5_frame then
		set_active(coin1, false)
		set_active(start1, false)
		set_active(up1, false)
		set_active(down1, false)
		drive_phase4_input(frame - phase4_frame)
	else
		set_active(coin1, false)
		set_active(start1, false)
		set_active(up1, false)
		set_active(down1, false)
		drive_phase5_input(frame - phase5_frame)
	end

	local state = program:read_u32(0x00227ab0)
	local active_mask = program:read_u32(0x00227af4)
	local phase = program:read_u32(0x00229338)
	local pc = maincpu.state["PC"] and maincpu.state["PC"].value or 0

	if not phase4_frame and phase == 4 then
		phase4_frame = frame
		install_phase5_breakpoints()
		print(string.format(
			"[phase5-oracle] phase4 frame=%d mode=%s pc=%08x timer=%08x",
			frame, mode, pc, program:read_u32(0x00227bd0)))
	end

	if mode == "capture" and phase == 4 and not phase4_saved then
		phase4_saved = true
		machine:save("phase4-oracle")
		print(string.format(
			"[phase5-oracle] requested save=phase4-oracle frame=%d pc=%08x timer=%08x",
			frame, pc, program:read_u32(0x00227bd0)))
	end

	if not phase5_frame and phase == 5 then
		phase5_frame = frame
		exit_frame = frame + phase5_observation_frames
		capture_phase5_baseline()
		install_phase5_breakpoints()
		print(string.format(
			"[phase5-oracle] reached frame=%d mode=%s state=%08x active=%08x input=%08x norm=%08x",
			frame, mode, state, active_mask,
			program:read_u32(0x00262b90),
			program:read_u32(0x00227ba8)))
		if snapshot_phase5 and screen then
			screen:snapshot(string.format(
				"/home/nichlas/EutherDrive_Android/.build-tmp/mame-phase5-state/snap/oracle-%s-entry.png",
				mode))
		end
		if mode == "capture" then
			machine:save("phase5-oracle")
			print("[phase5-oracle] requested save=phase5-oracle")
		end
	end

	if trace_phase5_edge and phase5_frame and machine.debugger then
		local relative = frame - phase5_frame
		if relative == 11 then
			machine.debugger:command(
				"trace /home/nichlas/EutherDrive_Android/.build-tmp/mame-phase5-edge.tr,maincpu")
			machine.debugger:command("g")
			print("[phase5-oracle] bounded edge trace on")
		elseif relative == 14 then
			machine.debugger:command("trace off")
			print("[phase5-oracle] bounded edge trace off")
		end
	end

	if frame % 100 == 0 or
		(phase >= 4 and frame % 20 == 0) or
		(phase5_frame and frame <= phase5_frame + phase5_observation_frames) then
		print(string.format(
			"[phase5-poll] frame=%d rel=%d mode=%s pc=%08x state=%08x active=%08x phase=%08x timer=%08x input=%08x norm=%08x words=%08x/%08x/%08x/%08x",
			frame,
			phase5_frame and (frame - phase5_frame) or -1,
			mode, pc, state, active_mask, phase,
			program:read_u32(0x00227bd0),
			program:read_u32(0x00262b90),
			program:read_u32(0x00227ba8),
			program:read_u32(0x00229484),
			program:read_u32(0x00229488),
			program:read_u32(0x0022948c),
			program:read_u32(0x00229490)))
	end

	if (exit_frame and frame >= exit_frame) or frame >= max_frames then
		release_phase5_inputs()
		if snapshot_phase5 and screen then
			screen:snapshot(string.format(
				"/home/nichlas/EutherDrive_Android/.build-tmp/mame-phase5-state/snap/oracle-%s-final.png",
				mode))
		end
		if phase5_frame then
			print_phase5_changes()
		end
		print(string.format(
			"[phase5-final] frame=%d mode=%s state=%08x active=%08x phase=%08x input=%08x norm=%08x",
			frame, mode, state, active_mask, phase,
			program:read_u32(0x00262b90),
			program:read_u32(0x00227ba8)))
		for key, count in pairs(writer_counts) do
			print(string.format(
				"[phase5-write-summary] key=%s count=%d", key, count))
		end
		machine:exit()
	end
end, "frame")
