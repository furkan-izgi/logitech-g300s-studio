import ctypes, ctypes.wintypes as wt, hid
k = ctypes.WinDLL("kernel32", use_last_error=True)
k.CreateFileW.restype = ctypes.c_void_p
k.CreateFileW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, wt.LPVOID, wt.DWORD, wt.DWORD, wt.HANDLE]
k.DeviceIoControl.restype = wt.BOOL
k.DeviceIoControl.argtypes = [ctypes.c_void_p, wt.DWORD, wt.LPVOID, wt.DWORD, wt.LPVOID, wt.DWORD, ctypes.POINTER(wt.DWORD), wt.LPVOID]

for d in hid.enumerate(0x046D, 0xC246):
    p = d["path"].decode() if isinstance(d["path"], bytes) else d["path"]
    h = k.CreateFileW(p, 0xC0000000, 3, None, 3, 0, None)
    print(p, "h=", h, "err=", ctypes.get_last_error())
    if not h or h == 0xFFFFFFFFFFFFFFFF:
        continue
    buf = ctypes.create_string_buffer(4096)
    ret = wt.DWORD()
    ok = k.DeviceIoControl(h, 0x000B0004, None, 0, buf, 4096, ctypes.byref(ret), None)
    print("  ok", ok, "n", ret.value, "err", ctypes.get_last_error())
    print("  ", " ".join(f"{b:02x}" for b in buf.raw[:ret.value]))
    k.CloseHandle(h)
