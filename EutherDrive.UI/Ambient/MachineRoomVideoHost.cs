using System;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform;

namespace EutherDrive.UI.Ambient;

public sealed class MachineRoomVideoHost : NativeControlHost
{
    public event EventHandler<MachineRoomVideoHandleEventArgs>? NativeHandleReady;
    public event EventHandler? NativeHandleDestroyed;

    public MachineRoomVideoHost()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Focusable = false;
        IsHitTestVisible = false;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        IPlatformHandle handle = base.CreateNativeControlCore(parent);
        NativeHandleReady?.Invoke(
            this,
            new MachineRoomVideoHandleEventArgs(handle.Handle, handle.HandleDescriptor ?? string.Empty));
        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        NativeHandleDestroyed?.Invoke(this, EventArgs.Empty);
        base.DestroyNativeControlCore(control);
    }
}

public sealed class MachineRoomVideoHandleEventArgs : EventArgs
{
    public MachineRoomVideoHandleEventArgs(IntPtr handle, string descriptor)
    {
        Handle = handle;
        Descriptor = descriptor;
    }

    public IntPtr Handle { get; }
    public string Descriptor { get; }
}
