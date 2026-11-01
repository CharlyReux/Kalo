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
static ULONGLONG g_lastF8Check;
static char g_dir[MAX_PATH];
static DWORD g_idlePacket = 1;
static unsigned g_polls[PADS];   /* how often the game asked for each slot since the last log (diagnostics) */

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
    Log("kalo xinput: game polls since last change: slot 1 %d, slot 2 %d", (int)g_polls[0], (int)g_polls[1]);
    for (int i = 0; i < PADS; i++) g_polls[i] = 0;
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
    if (slot < PADS) g_polls[slot]++;
    ReadConfig();
    {
        /* F8 is checked on every query (whichever slot the game is polling), at most every 30 ms. */
        ULONGLONG now = GetTickCount64();
        if (now - g_lastF8Check >= 30)
        {
            g_lastF8Check = now;
            BOOL f8 = (GetAsyncKeyState(VK_F8) & 0x8000) && OurWindowFocused();
            if (f8 && !g_prevF8) Cycle(0, 1);
            g_prevF8 = f8;
        }
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
    /* Every player slot stays "connected" (idle when no pad drives it): games stop polling a slot that reported
       no controller and only look again on a device change, so a pad moved there in game would be ignored. */
    if (result != ERROR_SUCCESS && (int)slot < g_players)
    {
        ZeroMemory(state, sizeof *state);
        state->dwPacketNumber = g_idlePacket;
        result = ERROR_SUCCESS;
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

typedef struct { BYTE Type, SubType; WORD Flags; XGAMEPAD Gamepad; XVIBRATION Vibration; } XCAPS;

__declspec(dllexport) DWORD WINAPI XInputGetCapabilities(DWORD slot, DWORD flags, void *caps)
{
    LoadReal();
    int p = PadFor(slot);
    if (g_getCaps && p >= 0 && g_getCaps(p, flags, caps) == ERROR_SUCCESS) return ERROR_SUCCESS;
    if ((int)slot < g_players && caps)
    {
        /* An idle player slot looks like a standard gamepad (see Query). */
        XCAPS *c = (XCAPS *)caps;
        ZeroMemory(c, sizeof *c);
        c->Type = 1; c->SubType = 1;
        c->Gamepad.wButtons = 0xF3FF; c->Gamepad.bLeftTrigger = c->Gamepad.bRightTrigger = 0xFF;
        c->Gamepad.sThumbLX = c->Gamepad.sThumbLY = c->Gamepad.sThumbRX = c->Gamepad.sThumbRY = (SHORT)0xFFC0;
        c->Vibration.wLeftMotorSpeed = c->Vibration.wRightMotorSpeed = 0xFF;
        return ERROR_SUCCESS;
    }
    return ERROR_DEVICE_NOT_CONNECTED;
}
__declspec(dllexport) void WINAPI XInputEnable(BOOL enable) { LoadReal(); if (g_enable) g_enable(enable); }
__declspec(dllexport) DWORD WINAPI XInputGetBatteryInformation(DWORD slot, BYTE type, void *info)
{ LoadReal(); int p = PadFor(slot); return (g_getBattery && p >= 0) ? g_getBattery(p, type, info) : ERROR_DEVICE_NOT_CONNECTED; }
__declspec(dllexport) DWORD WINAPI XInputGetKeystroke(DWORD slot, DWORD reserved, void *k)
{ LoadReal(); int p = PadFor(slot); return (g_getKeystroke && p >= 0) ? g_getKeystroke(p, reserved, k) : ERROR_EMPTY; }
__declspec(dllexport) DWORD WINAPI XInputGetAudioDeviceIds(DWORD slot, LPWSTR r, UINT *rc, LPWSTR c, UINT *cc)
{ LoadReal(); int p = PadFor(slot); return (g_getAudio && p >= 0) ? g_getAudio(p, r, rc, c, cc) : ERROR_DEVICE_NOT_CONNECTED; }


/* ---- Diagnostics: log the game's attempts to open .mglo files (path, access, result). ---- */
typedef HANDLE (WINAPI *CreateFileWFn)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
typedef HANDLE (WINAPI *CreateFileAFn)(LPCSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
static CreateFileWFn g_realCreateFileW;
static CreateFileAFn g_realCreateFileA;

static void LogLine(const char *text)
{
    char path[MAX_PATH + 32];
    snprintf(path, sizeof path, "%skalo_input.log", g_dir);
    FILE *f = fopen(path, "a");
    if (!f) return;
    SYSTEMTIME t; GetLocalTime(&t);
    fprintf(f, "%02d:%02d:%02d %s\n", t.wHour, t.wMinute, t.wSecond, text);
    fclose(f);
}

static void LogOpen(const char *what, const char *path, DWORD access, DWORD share, DWORD disp, DWORD flags, HANDLE h, DWORD err)
{
    char line[MAX_PATH * 2 + 200];
    snprintf(line, sizeof line, "kalo files: %s '%s' access %08lX share %lX disp %lu flags %08lX -> %s (error %lu)",
             what, path, (unsigned long)access, (unsigned long)share, (unsigned long)disp, (unsigned long)flags,
             h == INVALID_HANDLE_VALUE ? "FAILED" : "ok", (unsigned long)err);
    LogLine(line);
}

static HANDLE WINAPI HookCreateFileW(LPCWSTR name, DWORD access, DWORD share, LPSECURITY_ATTRIBUTES sa, DWORD disp, DWORD flags, HANDLE tmpl)
{
    HANDLE h = g_realCreateFileW(name, access, share, sa, disp, flags, tmpl);
    if (name && (wcsstr(name, L".mglo") || wcsstr(name, L"megalo")))
    {
        DWORD err = GetLastError();
        char a[MAX_PATH * 2];
        WideCharToMultiByte(CP_UTF8, 0, name, -1, a, sizeof a, NULL, NULL);
        LogOpen("CreateFileW", a, access, share, disp, flags, h, err);
        SetLastError(err);
    }
    return h;
}

static HANDLE WINAPI HookCreateFileA(LPCSTR name, DWORD access, DWORD share, LPSECURITY_ATTRIBUTES sa, DWORD disp, DWORD flags, HANDLE tmpl)
{
    HANDLE h = g_realCreateFileA(name, access, share, sa, disp, flags, tmpl);
    if (name && (strstr(name, ".mglo") || strstr(name, "megalo")))
    {
        DWORD err = GetLastError();
        LogOpen("CreateFileA", name, access, share, disp, flags, h, err);
        SetLastError(err);
    }
    return h;
}

/* Points the game exe's imports of CreateFileA/W at the hooks above. */
static void HookImports(void)
{
    BYTE *base = (BYTE *)GetModuleHandleA(NULL);
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)base;
    IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(base + dos->e_lfanew);
    IMAGE_DATA_DIRECTORY dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!dir.VirtualAddress) return;
    int hooked = 0;
    for (IMAGE_IMPORT_DESCRIPTOR *d = (IMAGE_IMPORT_DESCRIPTOR *)(base + dir.VirtualAddress); d->Name; d++)
    {
        if (!d->OriginalFirstThunk) continue;
        IMAGE_THUNK_DATA *names = (IMAGE_THUNK_DATA *)(base + d->OriginalFirstThunk);
        IMAGE_THUNK_DATA *funcs = (IMAGE_THUNK_DATA *)(base + d->FirstThunk);
        for (; names->u1.AddressOfData; names++, funcs++)
        {
            if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal)) continue;
            const char *fn = (const char *)((IMAGE_IMPORT_BY_NAME *)(base + names->u1.AddressOfData))->Name;
            void *hook = NULL;
            if (!strcmp(fn, "CreateFileW")) { g_realCreateFileW = (CreateFileWFn)(ULONG_PTR)funcs->u1.Function; hook = (void *)HookCreateFileW; }
            else if (!strcmp(fn, "CreateFileA")) { g_realCreateFileA = (CreateFileAFn)(ULONG_PTR)funcs->u1.Function; hook = (void *)HookCreateFileA; }
            if (!hook) continue;
            DWORD old;
            if (VirtualProtect(&funcs->u1.Function, sizeof(ULONG_PTR), PAGE_READWRITE, &old))
            {
                funcs->u1.Function = (ULONG_PTR)hook;
                VirtualProtect(&funcs->u1.Function, sizeof(ULONG_PTR), old, &old);
                hooked++;
            }
        }
    }
    char line[96];
    snprintf(line, sizeof line, "kalo files: watching %d file-open imports for .mglo", hooked);
    LogLine(line);
}

/* ---- Megalo labels in playtests ----------------------------------------------------------------------------
 * Megalo label groups ("objects x with label ..." / "for each x") find objects through the runtime map variant: it
 * holds the map's Forge objects and the label strings the map's objects look up. In a playtest the game is started
 * from the console (game_multiplayer, game_set_variant, game_start), so its game options carry no map variant (map
 * id -1), and the game loses the runtime map variant three ways:
 *   1. at the multiplayer start for a new map, it builds it from the options' (empty) variant instead of the map;
 *   2. when the map's zone set is activated, it resets it to "no map" (id -1), with nothing to rebuild it from;
 *   3. at each tag sync (the test build reloading changed tags, which also happens right after a playtest starts),
 *      it deletes the map variant's objects and resets it to "no map" too.
 * Every label string and labelled Forge object is then gone, and every label group finds nothing. The patches make
 * 1 and 2 build the runtime map variant from the map (as a real custom game on the map has it) and make tag syncs
 * leave it alone (the game keeps its objects; a new round or a new playtest rebuilds them from the map).
 *
 * reach_tag_test (HREK 2023.07.17), RVAs:
 *   0x20bd17 (game engine initialize_for_new_map), 24 bytes
 *     was  cmp byte [rdi+2e9],0 / jne / cmp dword [rdi+2e0],0 / jne / cmp byte [rdi+2ea],0 / jne -> build from map
 *     now  cmp dword [rdi+2b4],-1 / je build from map (0x20bd2f) / jmp build from the options' variant (0x20bd39)
 *   0x20d89e (game engine zone set callbacks, 0x20d840), 13 bytes
 *     was  mov edx,-1 / mov rcx,rax / call map_variant_create_default (0x215640)
 *     now  mov rcx,rax / call map_variant_build_from_scenario (0x21bb30) / nop5
 *   0x22429f (tag sync: call 0x4d7810, delete the map variant's objects) and 0x223e48 (after the tag sync: call
 *   0x4d6880, reset the map variant to map -1), 5 bytes each: the call becomes nop5.
 * The original bytes are checked first: another build of the game is left alone. */
typedef struct { ULONG_PTR rva; int size; BYTE was[24]; BYTE now[24]; } Patch;
static const Patch g_labelPatches[] = {
    { 0x20bd17, 24,
      { 0x38,0x9f,0xe9,0x02,0x00,0x00, 0x75,0x08, 0x39,0x9f,0xe0,0x02,0x00,0x00, 0x75,0x12, 0x38,0x9f,0xea,0x02,0x00,0x00, 0x75,0x0a },
      { 0x83,0xbf,0xb4,0x02,0x00,0x00,0xff, 0x74,0x0f, 0xeb,0x17, 0x90,0x90,0x90,0x90,0x90,0x90,0x90,0x90,0x90,0x90,0x90,0x90,0x90 } },
    { 0x20d89e, 13,
      { 0xba,0xff,0xff,0xff,0xff, 0x48,0x8b,0xc8, 0xe8,0x95,0x7d,0x00,0x00 },
      { 0x48,0x8b,0xc8, 0xe8,0x8a,0xe2,0x00,0x00, 0x0f,0x1f,0x44,0x00,0x00 } },
    { 0x22429f, 5,
      { 0xe8,0x6c,0x35,0x2b,0x00 },
      { 0x0f,0x1f,0x44,0x00,0x00 } },
    { 0x223e48, 5,
      { 0xe8,0x33,0x2a,0x2b,0x00 },
      { 0x0f,0x1f,0x44,0x00,0x00 } },
};

static void FixPlaytestMapVariant(void)
{
    char exe[MAX_PATH];
    GetModuleFileNameA(NULL, exe, MAX_PATH);
    const char *name = strrchr(exe, '\\');
    name = name ? name + 1 : exe;
    if (_stricmp(name, "reach_tag_test.exe") != 0) return;
    BYTE *base = (BYTE *)GetModuleHandleA(NULL);
    int count = sizeof g_labelPatches / sizeof g_labelPatches[0];
    /* All or nothing: check every place before changing any. */
    for (int i = 0; i < count; i++)
    {
        const Patch *p = &g_labelPatches[i];
        if (memcmp(base + p->rva, p->was, p->size) && memcmp(base + p->rva, p->now, p->size))
        { LogLine("kalo labels: unknown reach_tag_test build, Megalo labels are not fixed"); return; }
    }
    for (int i = 0; i < count; i++)
    {
        const Patch *p = &g_labelPatches[i];
        BYTE *at = base + p->rva;
        DWORD old;
        if (!VirtualProtect(at, p->size, PAGE_EXECUTE_READWRITE, &old)) { LogLine("kalo labels: could not patch the game"); return; }
        memcpy(at, p->now, p->size);
        VirtualProtect(at, p->size, old, &old);
        FlushInstructionCache(GetCurrentProcess(), at, p->size);
    }
    LogLine("kalo labels: playtest map variants are built from the map (Megalo labels work)");
}


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
        HookImports();
        FixPlaytestMapVariant();
    }
    return TRUE;
}
