using System.Runtime.InteropServices;
using System.Text;

namespace Snapfield.LinuxReceiver;

/// <summary>
/// Input injection through /dev/uinput — kernel-level virtual devices, so it
/// works identically under Wayland and X11 (no compositor cooperation needed).
///
/// Two devices, mirroring what compositors already trust:
///  - a pointer shaped like QEMU's usb-tablet (absolute axes 0..32767 + mouse
///    buttons + wheel), which libinput classifies as an absolute mouse and maps
///    across the whole desktop;
///  - a plain keyboard carrying every key the VK map can produce.
/// </summary>
public sealed class UinputInjector : IDisposable
{
    // ── evdev / uinput constants (linux/input-event-codes.h, linux/uinput.h) ──
    private const int EV_SYN = 0x00, EV_KEY = 0x01, EV_REL = 0x02, EV_ABS = 0x03;
    private const int SYN_REPORT = 0;
    private const int ABS_X = 0x00, ABS_Y = 0x01;
    private const int REL_HWHEEL = 0x06, REL_WHEEL = 0x08;
    private const int BTN_LEFT = 0x110, BTN_RIGHT = 0x111, BTN_MIDDLE = 0x112;
    public const int AbsMax = 32767;

    // _IOW('U', nr, int) = 0x4004_55_nr ; _IO('U', nr) = 0x0000_55_nr
    private const uint UI_SET_EVBIT = 0x40045564;
    private const uint UI_SET_KEYBIT = 0x40045565;
    private const uint UI_SET_RELBIT = 0x40045566;
    private const uint UI_SET_ABSBIT = 0x40045567;
    private const uint UI_DEV_CREATE = 0x5501;
    private const uint UI_DEV_DESTROY = 0x5502;

    private const int O_WRONLY = 0x1, O_NONBLOCK = 0x800;

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int fd);
    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint Write(int fd, byte[] buf, nuint count);
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(int fd, nuint request, int value);
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(int fd, nuint request);

    private int _pointer = -1;
    private int _keyboard = -1;
    private readonly HashSet<int> _pressed = new(); // keyboard state, for autorepeat values

    // The screen union the controller's pixel coordinates live in.
    private readonly int _unionX, _unionY, _unionW, _unionH;

    public UinputInjector(int unionX, int unionY, int unionW, int unionH)
    {
        _unionX = unionX; _unionY = unionY;
        _unionW = Math.Max(1, unionW); _unionH = Math.Max(1, unionH);
        _pointer = CreatePointer();
        _keyboard = CreateKeyboard();
    }

    private static int OpenUinput()
    {
        var fd = Open("/dev/uinput", O_WRONLY | O_NONBLOCK);
        if (fd < 0)
            throw new IOException(
                "/dev/uinput을 열 수 없습니다 (권한 부족). 다음을 실행한 뒤 재로그인하세요:\n" +
                "  echo 'KERNEL==\"uinput\", MODE=\"0660\", GROUP=\"input\", OPTIONS+=\"static_node=uinput\"' | sudo tee /etc/udev/rules.d/99-snapfield-uinput.rules\n" +
                "  sudo udevadm control --reload-rules && sudo udevadm trigger\n" +
                "  sudo usermod -aG input $USER");
        return fd;
    }

    private static int CreatePointer()
    {
        var fd = OpenUinput();
        Check(Ioctl(fd, UI_SET_EVBIT, EV_KEY));
        Check(Ioctl(fd, UI_SET_KEYBIT, BTN_LEFT));
        Check(Ioctl(fd, UI_SET_KEYBIT, BTN_RIGHT));
        Check(Ioctl(fd, UI_SET_KEYBIT, BTN_MIDDLE));
        Check(Ioctl(fd, UI_SET_EVBIT, EV_ABS));
        Check(Ioctl(fd, UI_SET_ABSBIT, ABS_X));
        Check(Ioctl(fd, UI_SET_ABSBIT, ABS_Y));
        Check(Ioctl(fd, UI_SET_EVBIT, EV_REL));
        Check(Ioctl(fd, UI_SET_RELBIT, REL_WHEEL));
        Check(Ioctl(fd, UI_SET_RELBIT, REL_HWHEEL));
        WriteSetup(fd, "Snapfield Virtual Pointer", absMaxX: AbsMax, absMaxY: AbsMax);
        Check(Ioctl(fd, UI_DEV_CREATE));
        return fd;
    }

    private static int CreateKeyboard()
    {
        var fd = OpenUinput();
        Check(Ioctl(fd, UI_SET_EVBIT, EV_KEY));
        foreach (var key in VkMap.AllKeys())
            Check(Ioctl(fd, UI_SET_KEYBIT, key));
        WriteSetup(fd, "Snapfield Virtual Keyboard", absMaxX: 0, absMaxY: 0);
        Check(Ioctl(fd, UI_DEV_CREATE));
        return fd;
    }

    /// <summary>Legacy uinput_user_dev setup record: name[80] + input_id{4×u16}
    /// + ff_effects_max:u32 + absmax/absmin/absfuzz/absflat[64]×s32 = 1116 bytes.
    /// (The legacy write-based setup works on every kernel; no UI_DEV_SETUP needed.)</summary>
    private static void WriteSetup(int fd, string name, int absMaxX, int absMaxY)
    {
        var dev = new byte[1116];
        var nameBytes = Encoding.UTF8.GetBytes(name);
        Array.Copy(nameBytes, dev, Math.Min(nameBytes.Length, 79));
        BitConverter.GetBytes((ushort)0x03).CopyTo(dev, 80);   // bustype = BUS_USB
        BitConverter.GetBytes((ushort)0x534E).CopyTo(dev, 82); // vendor  ("SN")
        BitConverter.GetBytes((ushort)0x4650).CopyTo(dev, 84); // product ("FP")
        BitConverter.GetBytes((ushort)1).CopyTo(dev, 86);      // version
        if (absMaxX > 0) BitConverter.GetBytes(absMaxX).CopyTo(dev, 92 + ABS_X * 4); // absmax[]
        if (absMaxY > 0) BitConverter.GetBytes(absMaxY).CopyTo(dev, 92 + ABS_Y * 4);
        if (Write(fd, dev, (nuint)dev.Length) != dev.Length)
            throw new IOException("uinput 디바이스 설정 실패");
    }

    private static void Check(int result)
    {
        if (result < 0) throw new IOException($"uinput ioctl 실패 (errno {Marshal.GetLastPInvokeError()})");
    }

    // ── events ────────────────────────────────────────────────────────────────
    /// <summary>struct input_event on 64-bit: timeval{2×s64} + type:u16 + code:u16
    /// + value:s32 = 24 bytes. Zero timestamps — the input core stamps injected
    /// events itself.</summary>
    private static void Emit(int fd, ushort type, ushort code, int value)
    {
        var e = new byte[24];
        BitConverter.GetBytes(type).CopyTo(e, 16);
        BitConverter.GetBytes(code).CopyTo(e, 18);
        BitConverter.GetBytes(value).CopyTo(e, 20);
        Write(fd, e, 24);
    }

    private static void Sync(int fd) => Emit(fd, EV_SYN, SYN_REPORT, 0);

    /// <summary>Warp to controller-space pixels: normalize over the advertised
    /// union box into the tablet's 0..32767 range. Proportional, so it stays
    /// correct under uniform HiDPI scaling.</summary>
    public void MoveTo(int x, int y)
    {
        var ax = (int)((long)Math.Clamp(x - _unionX, 0, _unionW - 1) * AbsMax / Math.Max(1, _unionW - 1));
        var ay = (int)((long)Math.Clamp(y - _unionY, 0, _unionH - 1) * AbsMax / Math.Max(1, _unionH - 1));
        Emit(_pointer, EV_ABS, ABS_X, ax);
        Emit(_pointer, EV_ABS, ABS_Y, ay);
        Sync(_pointer);
    }

    public void Button(int button, bool down)
    {
        var code = button switch { 0 => BTN_LEFT, 1 => BTN_RIGHT, 2 => BTN_MIDDLE, _ => 0 };
        if (code == 0) return;
        Emit(_pointer, EV_KEY, (ushort)code, down ? 1 : 0);
        Sync(_pointer);
    }

    /// <summary>Windows wheel delta (±120 per detent) → evdev clicks. Signs match:
    /// positive is up / right on both sides.</summary>
    public void Wheel(int delta, bool horizontal)
    {
        var clicks = delta / 120;
        if (clicks == 0) clicks = Math.Sign(delta);
        Emit(_pointer, EV_REL, (ushort)(horizontal ? REL_HWHEEL : REL_WHEEL), clicks);
        Sync(_pointer);
    }

    public void Key(int vk, bool down)
    {
        if (!VkMap.TryGet(vk, out var key)) return;
        // Windows hooks forward held-key repeats as more key-downs; the input
        // core drops a duplicate down, so re-encode repeats as value 2.
        int value;
        if (down) value = _pressed.Add(key) ? 1 : 2;
        else { _pressed.Remove(key); value = 0; }
        Emit(_keyboard, EV_KEY, (ushort)key, value);
        Sync(_keyboard);
    }

    /// <summary>Release everything (control left / link dropped): a key or button
    /// stuck down on a headless virtual device is otherwise unrecoverable.</summary>
    public void ReleaseAll()
    {
        foreach (var key in _pressed)
        {
            Emit(_keyboard, EV_KEY, (ushort)key, 0);
        }
        if (_pressed.Count > 0) Sync(_keyboard);
        _pressed.Clear();
        foreach (var btn in new[] { BTN_LEFT, BTN_RIGHT, BTN_MIDDLE })
            Emit(_pointer, EV_KEY, (ushort)btn, 0);
        Sync(_pointer);
    }

    public void Dispose()
    {
        ReleaseAll();
        if (_pointer >= 0) { Ioctl(_pointer, UI_DEV_DESTROY); Close(_pointer); _pointer = -1; }
        if (_keyboard >= 0) { Ioctl(_keyboard, UI_DEV_DESTROY); Close(_keyboard); _keyboard = -1; }
    }
}
