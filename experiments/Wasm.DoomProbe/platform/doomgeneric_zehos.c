// ZehOS-Plattformschicht fuer doomgeneric (GPL-2.0-or-later, wie doomgeneric).
// Laeuft als WASIp1-Programm im ZehOS-Wasm-Loader; alle Systemdienste kommen ueber zehos_v1-Imports.

#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include "d_event.h"
#include "doomgeneric.h"
#include "doomkeys.h"

extern int gametic;
// Gebaut mit CMAP256: struct color aus i_video.h hat genau das Layout 0x00RRGGBB eines uint32_t.
extern uint32_t colors[256];

#define ZEHOS_IMPORT(name) __attribute__((import_module("zehos_v1"), import_name(name)))

// Bild als 0x00RRGGBB, zeilenweise; test != 0 markiert das Pruefbild vor dem Spielstart.
ZEHOS_IMPORT("present") void zehos_present(const uint32_t* pixels, int32_t width, int32_t height, int32_t tic, int32_t test);
// 8-Bit-Bild mit Palette 0x00RRGGBB; die Umrechnung uebernimmt der native Kernel statt des Interpreters.
ZEHOS_IMPORT("present_indexed") void zehos_present_indexed(const uint8_t* indices, const uint32_t* palette, int32_t width, int32_t height, int32_t tic);
ZEHOS_IMPORT("ticks_ms") uint32_t zehos_ticks_ms(void);
ZEHOS_IMPORT("sleep_ms") void zehos_sleep_ms(uint32_t milliseconds);
// Liefert -1 ohne Ereignis, sonst Bit 8 = gedrueckt und Bits 0-7 = PS/2-Set-1-Scancode.
ZEHOS_IMPORT("poll_key") int32_t zehos_poll_key(void);
ZEHOS_IMPORT("should_exit") int32_t zehos_should_exit(void);
// Schreibt type, code, x, y als vier i32 und liefert 1, ohne Ereignis 0; unabhaengig von poll_key.
ZEHOS_IMPORT("poll_event") int32_t zehos_poll_event(int32_t* event);
// 1 faengt den Zeiger ein, 0 gibt ihn frei; 0 bei Erfolg. Danach kommen Bewegungen als Typ 9 (dx, dy).
ZEHOS_IMPORT("pointer_capture") int32_t zehos_pointer_capture(int32_t enabled);

enum { EVENT_KEY_DOWN = 1, EVENT_POINTER_MOVE = 4, EVENT_POINTER_DOWN = 5, EVENT_POINTER_UP = 6, EVENT_RESET = 8, EVENT_RELATIVE_MOVE = 9 };
// Pixel des Desktopzeigers sind feiner als DOS-Mausschritte; Faktor 4 dreht mit Standardempfindlichkeit spuerbar.
#define MOUSE_TURN_SCALE 4

static uint32_t test_frame[DOOMGENERIC_RESX * DOOMGENERIC_RESY];

static unsigned char translate(uint8_t code)
{
    static const char letters[] = "\0\0" "1234567890-=" "\0\0" "qwertyuiop[]" "\0\0" "asdfghjkl;'`" "\0\\" "zxcvbnm,./";
    switch (code)
    {
        case 72: case 17: return KEY_UPARROW;
        case 80: case 31: return KEY_DOWNARROW;
        case 75: return KEY_LEFTARROW;
        case 77: return KEY_RIGHTARROW;
        case 30: return KEY_STRAFE_L;
        case 32: return KEY_STRAFE_R;
        case 29: return KEY_FIRE;
        case 57: return KEY_USE;
        case 42: case 54: return KEY_RSHIFT;
        case 28: return KEY_ENTER;
        case 15: return KEY_TAB;
        // Backspace erreicht das Menue auch bei bereits freigegebenem Zeiger.
        case 1: case 14: return KEY_ESCAPE;
        default: break;
    }
    if (code >= 59 && code <= 68) return (unsigned char)(KEY_F1 + (code - 59));
    if (code < sizeof(letters) - 1 && letters[code] != 0) return (unsigned char)letters[code];
    return 0;
}

void DG_Init(void)
{
    // Pruefbild: links oben rot, rechts oben gruen, links unten blau; prueft Kanalfolge und Zeilenrichtung.
    for (int y = 0; y < DOOMGENERIC_RESY; y++)
        for (int x = 0; x < DOOMGENERIC_RESX; x++)
        {
            int right = x >= DOOMGENERIC_RESX / 2, bottom = y >= DOOMGENERIC_RESY / 2;
            test_frame[y * DOOMGENERIC_RESX + x] = !bottom ? (right ? 0x0000FF00u : 0x00FF0000u) : (right ? 0x00FFFFFFu : 0x000000FFu);
        }
    zehos_present(test_frame, DOOMGENERIC_RESX, DOOMGENERIC_RESY, 0, 1);
}

void DG_DrawFrame(void)
{
    zehos_present_indexed(DG_ScreenBuffer, colors, DOOMGENERIC_RESX, DOOMGENERIC_RESY, gametic);
}

void DG_SleepMs(uint32_t ms) { zehos_sleep_ms(ms); }

uint32_t DG_GetTicksMs(void) { return zehos_ticks_ms(); }

static int mouse_captured;
static int mouse_buttons;

// Zeigerbits des Kernels (links 1, rechts 2, Mitte 4) entsprechen Dooms data1 (Feuer, Strafe, Vorwaerts).
static void poll_mouse(void)
{
    int32_t event[4];
    int turn = 0, changed = 0;
    while (zehos_poll_event(event) == 1)
    {
        switch (event[0])
        {
            case EVENT_POINTER_DOWN:
                // Der erste Klick faengt nur ein und feuert nicht.
                if (!mouse_captured) { mouse_captured = zehos_pointer_capture(1) == 0; break; }
                mouse_buttons |= event[1] & 7;
                changed = 1;
                break;
            case EVENT_POINTER_UP:
                mouse_buttons &= ~(event[1] & 7);
                changed = 1;
                break;
            case EVENT_RELATIVE_MOVE:
                turn += event[2];
                changed = 1;
                break;
            // Esc (Tastencode 1) oder Fokusverlust geben im Kernel frei; ebenso zeigt es die erste absolute Bewegung.
            case EVENT_KEY_DOWN:
                if (event[1] == 1) { mouse_captured = 0; mouse_buttons = 0; changed = 1; }
                break;
            case EVENT_POINTER_MOVE:
                mouse_captured = 0;
                mouse_buttons = 0;
                changed = 1;
                break;
            case EVENT_RESET:
                mouse_captured = 0;
                mouse_buttons = 0;
                changed = 1;
                break;
            default:
                break;
        }
    }
    if (!changed) return;
    // Vertikale Bewegung bleibt aus, sonst liefe der Spieler beim Umsehen vor und zurueck.
    event_t ev = { ev_mouse, mouse_buttons, turn * MOUSE_TURN_SCALE, 0, 0 };
    D_PostEvent(&ev);
}

int DG_GetKey(int* pressed, unsigned char* key)
{
    poll_mouse();
    for (;;)
    {
        int32_t event = zehos_poll_key();
        if (event < 0) return 0;
        unsigned char translated = translate((uint8_t)event);
        if (translated == 0) continue;
        *pressed = (event >> 8) & 1;
        *key = translated;
        return 1;
    }
}

void DG_SetWindowTitle(const char* title) { (void)title; }

// I_Quit und I_Error werden per Linker (--wrap) hierher umgeleitet. Die Originale rufen alle Exitfunktionen auf,
// darunter G_CheckDemoStatus, das d_main.c per Cast als void-Funktion registriert; Wasm bricht diesen Aufruf mit
// "indirect call type mismatch" ab. Ausserdem beendet I_Quit in doomgeneric das Programm nicht. Die
// uebersprungenen Exitfunktionen (Netz, Ton, ENDOOM, Konfiguration, Demoaufnahme) sind hier wirkungslos.
void __wrap_I_Quit(void) { exit(0); }

void __wrap_I_Error(const char* error, ...)
{
    va_list arguments;
    va_start(arguments, error);
    vfprintf(stderr, error, arguments);
    va_end(arguments);
    fputc('\n', stderr);
    exit(1);
}

// i_system.c startet fuer Fehlerdialoge externe Programme; ZehOS kennt keine Prozesse.
int system(const char* command) { (void)command; return -1; }

int main(void)
{
    char* arguments[] = { "doom", "-iwad", "freedoom1.wad", NULL };
    doomgeneric_Create(3, arguments);
    while (!zehos_should_exit()) doomgeneric_Tick();
    return 0;
}
