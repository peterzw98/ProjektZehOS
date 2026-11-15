// Kleine zehos_v1-App fuer die Paketpruefungen im Gast (scripts/test-wasm-doom-desktop.py).
// Sie zeigt eine einfarbige Flaeche; die Farbe unterscheidet die Versionen auf dem Bildschirmfoto.
// Q beendet sie wie das Menue einer echten App, das Schliessen des Fensters ebenso.

#include <stdint.h>

#define ZEHOS_IMPORT(name) __attribute__((import_module("zehos_v1"), import_name(name)))

ZEHOS_IMPORT("present") void zehos_present(const uint32_t* pixels, int32_t width, int32_t height, int32_t tic, int32_t test);
ZEHOS_IMPORT("sleep_ms") void zehos_sleep_ms(uint32_t milliseconds);
ZEHOS_IMPORT("poll_key") int32_t zehos_poll_key(void);
ZEHOS_IMPORT("should_exit") int32_t zehos_should_exit(void);

#ifndef TESTAPP_COLOR
#define TESTAPP_COLOR 0x0000a0e0u
#endif

enum { Width = 160, Height = 100, PressedQ = 0x100 | 16 };

static uint32_t frame[Width * Height];

int main(void)
{
    for (int index = 0; index < Width * Height; index++) frame[index] = TESTAPP_COLOR;
    for (int32_t tic = 0; !zehos_should_exit(); tic++)
    {
        for (int32_t key = zehos_poll_key(); key >= 0; key = zehos_poll_key())
            if (key == PressedQ) return 0;
        zehos_present(frame, Width, Height, tic, 0);
        zehos_sleep_ms(50);
    }
    return 0;
}
