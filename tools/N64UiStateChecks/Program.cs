using System.Diagnostics;
using System.Security.Cryptography;
using EutherDrive.Core;
using EutherDrive.Core.Savestates;
using EutherDrive.UI.Savestates;

if (args.Length != 3) throw new ArgumentException("Usage: N64UiStateChecks ROM GPU_LIBRARY NEW_OUTPUT");
string output = Path.GetFullPath(args[2]);
if (Directory.Exists(output)) throw new IOException("Use a new output directory; this test writes disposable slots");
Directory.CreateDirectory(output);
string rom = Path.Combine(output, "fixture.z64"); File.Copy(args[0], rom);
Environment.SetEnvironmentVariable("EUTHERDRIVE_N64_GPU_LIBRARY", Path.GetFullPath(args[1]));
Environment.SetEnvironmentVariable("EUTHERDRIVE_N64_GPU_VALIDATE", "1");
using var core = new N64Adapter(); core.LoadRom(rom);
if (!core.UsesGpuRendering) throw new Exception("Build this tool with -p:N64LiveGpu=true");
var service = new SavestateService(output);
int pauses = 0, resumes = 0;
var model = new SavestateViewModel(service, () => core, () => { pauses++; return true; },
    running => { if (!running) throw new Exception("Lost UI running state"); resumes++; }, Console.WriteLine);
model.Refresh();
var timeout = Stopwatch.StartNew();
while (core.GraphicsTaskCounter < 3)
{
    core.RunFrame(); Thread.Sleep(10);
    if (timeout.Elapsed.TotalSeconds > 120) throw new TimeoutException("No graphics task reached the UI adapter");
}

void Saved(int slot)
{
    if (model.StatusMessage != $"Savestate: saved S{slot}." || !service.GetSlotInfo(core)[slot - 1].HasData)
        throw new Exception("UI save did not produce a valid slot: " + model.StatusMessage);
}
byte[] SlotPayload(string path, int selected)
{
    using var reader = new BinaryReader(File.OpenRead(path));
    if (System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) != "EUTHSTAT" || reader.ReadInt32() != 1 || reader.ReadInt32() != 3)
        throw new Exception("Invalid state container");
    reader.ReadBytes(32); reader.ReadBytes(reader.ReadInt32());
    for (int i = 1; i <= 3; i++)
    {
        int slot = reader.ReadInt32(); bool present = reader.ReadBoolean(); reader.ReadInt64(); reader.ReadInt64();
        int length = reader.ReadInt32(); long offset = reader.ReadInt64(); byte[] hash = reader.ReadBytes(32);
        if (slot != selected) continue;
        if (!present) throw new Exception("Slot missing");
        reader.BaseStream.Position = offset; byte[] payload = reader.ReadBytes(length);
        if (!SHA256.HashData(payload).AsSpan().SequenceEqual(hash)) throw new Exception("Slot checksum mismatch");
        return payload;
    }
    throw new Exception("Slot not found");
}
model.SaveSlot2Command.Execute(null); Saved(2);
string container = Directory.GetFiles(output, "*.euthstate").Single();
byte[] slot2 = SlotPayload(container, 2);
model.SaveSlot1Command.Execute(null); Saved(1);
if (!slot2.AsSpan().SequenceEqual(SlotPayload(container, 2))) throw new Exception("Saving S1 changed S2");
long frame = service.GetSlotInfo(core)[0].FrameCounter;
for (int i = 0; i < 10; i++) { core.RunFrame(); Thread.Sleep(10); }
model.LoadSlot1Command.Execute(null);
if (model.StatusMessage != "Savestate: loaded S1." || !core.UsesGpuRendering || core.FrameCounter != frame)
    throw new Exception("UI load did not restore adapter/GPU state: " + model.StatusMessage);
core.RunFrame();
if (core.FrameCounter <= frame) throw new Exception("UI did not resume after loading");
byte[] containerHash = SHA256.HashData(File.ReadAllBytes(container));
model.LoadSlot3Command.Execute(null);
if (!model.StatusMessage.Contains("empty", StringComparison.OrdinalIgnoreCase)) throw new Exception("Empty-slot error is not visible in the panel");
var faulty = new SavestateViewModel(service, () => new FailedSave(core.RomIdentity!), () => { pauses++; return true; },
    running => { if (!running) throw new Exception("Lost running state on failure"); resumes++; }, Console.WriteLine);
faulty.Refresh(); faulty.SaveSlot1Command.Execute(null);
if (!faulty.StatusMessage.Contains("injected failure")) throw new Exception("Save error is not visible in the panel");
if (!containerHash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(container))))
    throw new Exception("A failed slot operation modified the container");
if (pauses != resumes) throw new Exception("Unbalanced UI pause/resume");
Console.WriteLine("n64UiSavestates=passed saveLoadCommands=connected backend=GPU otherSlots=preserved failure=preserved panelStatus=visible resume=passed");

sealed class FailedSave(RomIdentity identity) : ISavestateCapable
{
    public RomIdentity RomIdentity => identity;
    public long? FrameCounter => 0;
    public void SaveState(BinaryWriter writer) => throw new InvalidOperationException("injected failure");
    public void LoadState(BinaryReader reader) => throw new NotSupportedException();
}
