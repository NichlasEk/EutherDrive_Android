local output_dir = os.getenv("GAUNTDL_MAME_REFERENCE_OUT") or "/tmp/mame-gauntdl-reference"
local max_frames = tonumber(os.getenv("GAUNTDL_MAME_MAX_FRAMES")) or 7200
local snapshot_interval = tonumber(os.getenv("GAUNTDL_MAME_SNAPSHOT_INTERVAL")) or 600
local world_steps = tonumber(os.getenv("GAUNTDL_MAME_WORLD_STEPS")) or 0
local world_step_start = tonumber(os.getenv("GAUNTDL_MAME_WORLD_STEP_START")) or 3030
local world_step_interval = tonumber(os.getenv("GAUNTDL_MAME_WORLD_STEP_INTERVAL")) or 30
local save_frame = tonumber(os.getenv("GAUNTDL_MAME_SAVE_FRAME"))
local save_file = os.getenv("GAUNTDL_MAME_SAVE_FILE") or "gauntdl-world-select"
local entry_probe = os.getenv("GAUNTDL_MAME_ENTRY_PROBE")
local entry_code = os.getenv("GAUNTDL_MAME_ENTRY_CODE")
local entry_password = os.getenv("GAUNTDL_MAME_ENTRY_PASSWORD")
local completed_select = os.getenv("GAUNTDL_MAME_COMPLETED_SELECT")
local enter_selected = os.getenv("GAUNTDL_MAME_ENTER_SELECTED")
local frame = 0

local system = manager.machine.ioport.ports[":SYSTEM"]
local player1 = manager.machine.ioport.ports[":8WAY_P1"]
local screen = manager.machine.screens[":screen"]

local coin1 = system and system:field(0x0001)
local start1 = system and system:field(0x0004)
local up1 = player1 and player1:field(0x0001)
local down1 = player1 and player1:field(0x0002)
local left1 = player1 and player1:field(0x0004)
local right1 = player1 and player1:field(0x0008)
local fight1 = player1 and player1:field(0x0010)

local function set_active(field, active)
	if field then
		field:set_value(active and 1 or 0)
	end
end

local function pulse(frame_number, start_frame)
	return frame_number == start_frame
end

local function pulse_series(frame_number, start_frame, count, interval)
	if frame_number < start_frame then
		return false
	end
	local relative = frame_number - start_frame
	local index = math.floor(relative / interval)
	return index < count and (relative % interval) < 5
end

local function frame_done()
	frame = frame + 1

	-- Repeat a short coin/start/fight sequence while the disk-based game boots.
	-- Once gameplay has started, the later pulses are harmless and keep the
	-- reference capture independent of a particular NVRAM boot duration.
	local cycle = (frame - 1200) % 900
	local drive_inputs = frame >= 1200 and frame < 5700
	-- A loaded world-select save starts this Lua frame counter at zero, while a
	-- cold boot reaches the wheel around frame 2850. Arm the scripted sequence
	-- shortly before whichever start frame the caller selected.
	local scripted_world_select =
		world_steps ~= 0 and frame >= math.max(0, world_step_start - 180)
	set_active(coin1, drive_inputs and not scripted_world_select and cycle < 5)
	set_active(start1, drive_inputs and not scripted_world_select and cycle >= 90 and cycle < 95)
	set_active(fight1, drive_inputs and not scripted_world_select and ((cycle >= 180 and cycle < 185) or (cycle >= 270 and cycle < 275)))
	set_active(up1, drive_inputs and not scripted_world_select and cycle >= 225 and cycle < 255)

	if scripted_world_select then
		local step_count = math.abs(world_steps)
		local step_start = world_step_start
		local step_window = frame - step_start
		local pulse_index = math.floor(step_window / world_step_interval)
		local pulse_active = step_window >= 0 and pulse_index < step_count and (step_window % world_step_interval) < 5
		set_active(left1, world_steps < 0 and pulse_active)
		set_active(right1, world_steps > 0 and pulse_active)

		local commit_frame = step_start + step_count * world_step_interval + 60
		set_active(fight1, frame >= commit_frame and frame < commit_frame + 5)
		set_active(up1, frame >= commit_frame + 45 and frame < commit_frame + 75)
	else
		set_active(left1, false)
		set_active(right1, false)
	end

	-- Loaded-state diagnostic for documenting the initials editor's joystick
	-- mapping. It remains inert unless explicitly requested.
	if entry_probe then
		local probe_active = frame >= 60 and frame < 90
		set_active(up1, entry_probe == "up" and probe_active)
		set_active(down1, entry_probe == "down" and probe_active)
		set_active(left1, entry_probe == "left" and probe_active)
		set_active(right1, entry_probe == "right" and probe_active)
	end

	-- The arcade's built-in SJB/964 reference character has every level,
	-- runestone and realm completed. Starting from the saved initials prompt,
	-- enter it through normal game inputs so the hidden levelE1 Temple can be
	-- captured without modifying guest RAM or NVRAM.
	if entry_code == "SJB964" then
		local down_active =
			pulse_series(frame, 60, 9, 15) or -- blank -> S
			pulse_series(frame, 240, 9, 15) or -- S -> J
			pulse_series(frame, 420, 8, 15) -- J -> B
		local fight_active =
			pulse(frame, 210) or
			pulse(frame, 390) or
			pulse(frame, 570)
		set_active(up1, false)
		set_active(down1, down_active)
		set_active(fight1, fight_active)
	end

	if entry_password == "964" then
		local down_active =
			pulse_series(frame, 60, 1, 20) or -- 0 -> 9
			pulse_series(frame, 140, 3, 20) or -- 9 -> 6
			pulse_series(frame, 260, 2, 20) -- 6 -> 4
		local fight_active =
			pulse(frame, 100) or
			pulse(frame, 220) or
			pulse(frame, 320)
		set_active(up1, false)
		set_active(down1, down_active)
		set_active(fight1, fight_active)
	end

	if completed_select == "1" then
		set_active(fight1, pulse(frame, 60))
	end

	if enter_selected == "1" then
		set_active(fight1, frame >= 60 and frame < 65)
	end

	if screen and frame % snapshot_interval == 0 then
		local filename = string.format("%s/frame-%05d.png", output_dir, frame)
		screen:snapshot(filename)
		print(string.format("[gauntdl-reference] snapshot frame=%d path=%s", frame, filename))
	end

	if save_frame and frame == save_frame then
		manager.machine:save(save_file)
		print(string.format("[gauntdl-reference] save frame=%d path=%s", frame, save_file))
	end

	if frame >= max_frames then
		manager.machine:exit()
	end
end

emu.register_frame_done(frame_done, "frame")
