// ZehOS-Plattformschicht fuer doomgeneric (GPL-2.0, wie doomgeneric).
// Laeuft als WASIp1-Programm im ZehOS-Wasm-Loader; alle Systemdienste kommen ueber zehos_v1-Imports.

#include <stdint.h>
#include <stdlib.h>
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
        // Esc bleibt in ZehOS der Rueckweg zur Textshell; Backspace oeffnet deshalb das Doom-Menue.
        case 14: return KEY_ESCAPE;
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

int DG_GetKey(int* pressed, unsigned char* key)
{
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

// i_system.c startet fuer Fehlerdialoge externe Programme; ZehOS kennt keine Prozesse.
int system(const char* command) { (void)command; return -1; }

int main(void)
{
    char* arguments[] = { "doom", "-iwad", "freedoom1.wad", NULL };
    doomgeneric_Create(3, arguments);
    while (!zehos_should_exit()) doomgeneric_Tick();
    return 0;
}
