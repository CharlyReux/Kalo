/*
 * Kalo's xinput1_4.dll for reach_tag_test (playtest input routing).
 *
 * The Reach test build drives player 1 (controller 0) with the keyboard and mouse AND with the first
 * gamepad, so a gamepad can't control another split-screen player. Placed next to reach_tag_test.exe,
 * this DLL is loaded instead of the system XInput; it forwards to the real one in System32 but reports
 * each physical gamepad at the controller slot Kalo chose (kalo_input.txt, next to the exe):
 *
 *   players=2          number of split-screen players (for cycling)
 *   pad0=1             physical gamepad 0 drives controller 1 (player 2); -1 = ignored
 *   pad1=2 ...
 *
 * In game, a gamepad changes player with Back + D-pad right (next) / left (previous); F8 (game window
 * focused) moves gamepad 1 to the next player. The file is re-read when it changes, so Kalo can also
 * reassign pads while the game runs. Changes are written to kalo_input.log.
 */
#include <windows.h>
#include <stdio.h>

typedef struct { WORD wButtons; BYTE bLeftTrigger, bRightTrigger; SHORT sThumbLX, sThumbLY, sThumbRX, sThumbRY; } XGAMEPAD;
typedef struct { DWORD dwPacketNumber; XGAMEPAD Gamepad; } XSTATE;
typedef struct { WORD wLeftMotorSpeed, wRightMotorSpeed; } XVIBRATION;

#define BTN_DPAD_LEFT 0x0004
#define BTN_DPAD_RIGHT 0x0008
#define BTN_BACK 0x0020
#define PADS 4

typedef DWORD (WINAPI *GetStateFn)(DWORD, XSTATE*);
typedef DWORD (WINAPI *SetStateFn)(DWORD, XVIBRATION*);
typedef DWORD (WINAPI *GetCapsFn)(DWORD, DWORD, void*);
typedef void (WINAPI *EnableFn)(BOOL);
typedef DWORD (WINAPI *GetBatteryFn)(DWORD, BYTE, void*);
typedef DWORD (WINAPI *GetKeystrokeFn)(DWORD, DWORD, void*);
typedef DWORD (WINAPI *GetAudioFn)(DWORD, LPWSTR, UINT*, LPWSTR, UINT*);

static HMODULE g_real;
static GetStateFn g_getState, g_getStateEx;
static SetStateFn g_setState;
static GetCapsFn g_getCaps;
static EnableFn g_enable;
static GetBatteryFn g_getBattery;
static GetKeystrokeFn g_getKeystroke;
static GetAudioFn g_getAudio;

static CRITICAL_SECTION g_lock;
static int g_map[PADS] = { 0, 1, 2, 3 };   /* physical pad -> controller slot (-1: off) */
static int g_players = 4;
static FILETIME g_fileTime;
static ULONGLONG g_lastCheck;
static WORD g_prevButtons[PADS];
static BOOL g_prevF8;
static char g_dir[MAX_PATH];

static void Log(const char *fmt, int a, int b)
{
    char path[MAX_PATH + 32];
    snprintf(path, sizeof path, "%skalo_input.log", g_dir);
    FILE *f = fopen(path, "a");
    if (!f) return;
    SYSTEMTIME t; GetLocalTime(&t);
    fprintf(f, "%02d:%02d:%02d ", t.wHour, t.wMinute, t.wSecond);
    fprintf(f, fmt, a, b);
    fputc('\n', f);
    fclose(f);
}

static void LoadReal(void)
{
    if (g_real) return;
    char sys[MAX_PATH];
    GetSystemDirectoryA(sys, MAX_PATH);
    strcat(sys, "\\xinput1_4.dll");
    g_real = LoadLibraryA(sys);
    if (!g_real) { Log("kalo xinput: could not load the system xinput1_4.dll (error %d)%.0d", (int)GetLastError(), 0); return; }
    g_getState = (GetStateFn)GetProcAddress(g_real, "XInputGetState");
    g_getStateEx = (GetStateFn)GetProcAddress(g_real, (LPCSTR)100);
    g_setState = (SetStateFn)GetProcAddress(g_real, "XInputSetState");
    g_getCaps = (GetCapsFn)GetProcAddress(g_real, "XInputGetCapabilities");
    g_enable = (EnableFn)GetProcAddress(g_real, "XInputEnable");
    g_getBattery = (GetBatteryFn)GetProcAddress(g_real, "XInputGetBatteryInformation");
    g_getKeystroke = (GetKeystrokeFn)GetProcAddress(g_real, "XInputGetKeystroke");
    g_getAudio = (GetAudioFn)GetProcAddress(g_real, "XInputGetAudioDeviceIds");
}

/* Re-reads kalo_input.txt when its timestamp changes (checked at most every 300 ms). */
static void ReadConfig(void)
{
    ULONGLONG now = GetTickCount64();
    if (now - g_lastCheck < 300) return;
    g_lastCheck = now;
    char path[MAX_PATH + 32];
    snprintf(path, sizeof path, "%skalo_input.txt", g_dir);
    WIN32_FILE_ATTRIBUTE_DATA a;
    if (!GetFileAttributesExA(path, GetFileExInfoStandard, &a)) return;
    if (CompareFileTime(&a.ftLastWriteTime, &g_fileTime) == 0) return;
    g_fileTime = a.ftLastWriteTime;
    FILE *f = fopen(path, "r");
    if (!f) return;
    char line[128];
    while (fgets(line, sizeof line, f))
    {
        int pad, slot, n;
        if (sscanf(line, "pad%d=%d", &pad, &slot) == 2 && pad >= 0 && pad < PADS && slot >= -1 && slot < PADS) g_map[pad] = slot;
        else if (sscanf(line, "players=%d", &n) == 1 && n >= 1 && n <= PADS) g_players = n;
    }
    fclose(f);
    Log("kalo xinput: settings read, gamepad 1 -> player %d, %d players", g_map[0] + 1, g_players);
}

static void Cycle(int pad, int step)
{
    int slot = g_map[pad] < 0 ? 0 : g_map[pad];
    slot = ((slot + step) % g_players + g_players) % g_players;
    g_map[pad] = slot;
    Log("kalo xinput: gamepad %d now drives player %d", pad + 1, slot + 1);
}

static BOOL OurWindowFocused(void)
{
    DWORD pid = 0;
    HWND w = GetForegroundWindow();
    if (!w) return FALSE;
    GetWindowThreadProcessId(w, &pid);
    return pid == GetCurrentProcessId();
}

/* Called once per controller-slot query: refresh settings, hotkeys, then answer for 'slot'. */
static DWORD Query(DWORD slot, XSTATE *state, GetStateFn fn)
{
    LoadReal();
    if (!fn) return ERROR_DEVICE_NOT_CONNECTED;
    EnterCriticalSection(&g_lock);
    ReadConfig();
    if (slot == 0)
    {
        BOOL f8 = OurWindowFocused() && (GetAsyncKeyState(VK_F8) & 0x8000);
        if (f8 && !g_prevF8) Cycle(0, 1);
        g_prevF8 = f8;
    }
    DWORD result = ERROR_DEVICE_NOT_CONNECTED;
    for (int pad = 0; pad < PADS; pad++)
    {
        if (g_map[pad] != (int)slot) continue;
        XSTATE s;
        if (fn(pad, &s) != ERROR_SUCCESS) continue;
        WORD b = s.Gamepad.wButtons;
        if (b & BTN_BACK)
        {
            if ((b & BTN_DPAD_RIGHT) && !(g_prevButtons[pad] & BTN_DPAD_RIGHT)) Cycle(pad, 1);
            if ((b & BTN_DPAD_LEFT) && !(g_prevButtons[pad] & BTN_DPAD_LEFT)) Cycle(pad, -1);
            s.Gamepad.wButtons &= ~(BTN_DPAD_LEFT | BTN_DPAD_RIGHT);
        }
        g_prevButtons[pad] = b;
        if (g_map[pad] != (int)slot) continue; /* just moved to another player */
        *state = s;
        result = ERROR_SUCCESS;
        break;
    }
    LeaveCriticalSection(&g_lock);
    return result;
}

__declspec(dllexport) DWORD WINAPI XInputGetState(DWORD slot, XSTATE *state) { return Query(slot, state, g_getState ? g_getState : (LoadReal(), g_getState)); }
__declspec(dllexport) DWORD WINAPI XInputGetState_proxy(DWORD slot, XSTATE *state) { return XInputGetState(slot, state); }
__declspec(dllexport) DWORD WINAPI XInputGetStateEx(DWORD slot, XSTATE *state) { LoadReal(); return Query(slot, state, g_getStateEx ? g_getStateEx : g_getState); }

__declspec(dllexport) DWORD WINAPI XInputSetState(DWORD slot, XVIBRATION *v)
{
    LoadReal();
    if (!g_setState) return ERROR_DEVICE_NOT_CONNECTED;
    DWORD r = ERROR_DEVICE_NOT_CONNECTED;
    for (int pad = 0; pad < PADS; pad++)
        if (g_map[pad] == (int)slot && g_setState(pad, v) == ERROR_SUCCESS) r = ERROR_SUCCESS;
    return r;
}
__declspec(dllexport) DWORD WINAPI XInputSetState_proxy(DWORD slot, XVIBRATION *v) { return XInputSetState(slot, v); }

/* Capabilities, battery, keystrokes: reported for the pad that drives the slot. */
static int PadFor(DWORD slot) { for (int p = 0; p < PADS; p++) if (g_map[p] == (int)slot) return p; return -1; }

__declspec(dllexport) DWORD WINAPI XInputGetCapabilities(DWORD slot, DWORD flags, void *caps)
{ LoadReal(); int p = PadFor(slot); return (g_getCaps && p >= 0) ? g_getCaps(p, flags, caps) : ERROR_DEVICE_NOT_CONNECTED; }
__declspec(dllexport) void WINAPI XInputEnable(BOOL enable) { LoadReal(); if (g_enable) g_enable(enable); }
__declspec(dllexport) DWORD WINAPI XInputGetBatteryInformation(DWORD slot, BYTE type, void *info)
{ LoadReal(); int p = PadFor(slot); return (g_getBattery && p >= 0) ? g_getBattery(p, type, info) : ERROR_DEVICE_NOT_CONNECTED; }
__declspec(dllexport) DWORD WINAPI XInputGetKeystroke(DWORD slot, DWORD reserved, void *k)
{ LoadReal(); int p = PadFor(slot); return (g_getKeystroke && p >= 0) ? g_getKeystroke(p, reserved, k) : ERROR_EMPTY; }
__declspec(dllexport) DWORD WINAPI XInputGetAudioDeviceIds(DWORD slot, LPWSTR r, UINT *rc, LPWSTR c, UINT *cc)
{ LoadReal(); int p = PadFor(slot); return (g_getAudio && p >= 0) ? g_getAudio(p, r, rc, c, cc) : ERROR_DEVICE_NOT_CONNECTED; }

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        InitializeCriticalSection(&g_lock);
        /* Settings and log live next to this DLL (the game's folder). */
        GetModuleFileNameA(inst, g_dir, MAX_PATH);
        char *slash = strrchr(g_dir, '\\');
        if (slash) slash[1] = 0; else g_dir[0] = 0;
        Log("kalo xinput: loaded (%d gamepad slots)%.0d", PADS, 0);
    }
    return TRUE;
}
