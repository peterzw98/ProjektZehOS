// libnsfb-Oberfläche für den ZehOS-Wasm-Loader: Bild über zehos_v1.present, Eingabe über zehos_v1.poll_event.
// Das Build-Skript kopiert die Datei nach libnsfb/src/surface/; dort gelten libnsfbs interne Header.

#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>

#include "libnsfb.h"
#include "libnsfb_plot.h"
#include "libnsfb_event.h"

#include "nsfb.h"
#include "surface.h"
#include "plot.h"
#include "cursor.h"

#define ZEHOS_IMPORT(name) __attribute__((import_module("zehos_v1"), import_name(name)))

ZEHOS_IMPORT("present") void zehos_present(const uint32_t *pixels, int32_t width, int32_t height, int32_t tic, int32_t test);
ZEHOS_IMPORT("poll_event") int32_t zehos_poll_event(int32_t *event);
ZEHOS_IMPORT("ticks_ms") uint32_t zehos_ticks_ms(void);
ZEHOS_IMPORT("sleep_ms") void zehos_sleep_ms(uint32_t milliseconds);
ZEHOS_IMPORT("should_exit") int32_t zehos_should_exit(void);

// Paketfenster ohne Größenänderung; Absprache mit der Hostseite.
#define ZEHOS_WIDTH 632
#define ZEHOS_HEIGHT 392
#define ZEHOS_MAX_WAIT_MS 16

enum { EV_KEYDOWN = 1, EV_KEYUP, EV_TEXT, EV_MOVE, EV_BUTTONDOWN, EV_BUTTONUP, EV_WHEEL, EV_RESET };

// Ein Hostereignis kann mehrere nsfb-Ereignisse ergeben (Rad, Reset); sie warten hier.
#define PENDING_MAX 32
static nsfb_event_t pending[PENDING_MAX];
static int pending_count;
static int held_modifiers[8];
static int held_count;
static uint32_t tic;

static void push(enum nsfb_event_type_e type, enum nsfb_key_code_e code)
{
    if (pending_count == PENDING_MAX) return;
    pending[pending_count].type = type;
    pending[pending_count].value.keycode = code;
    pending_count++;
}

// Muss FBTK_KEY_UCS4_BASE im gepatchten NetSurf-fbtk entsprechen (patches/netsurf-fbtk-text.patch).
#define UCS4_KEY_BASE 0x40000000

// Normalisierte ZehOS-Tastencodes (Set-1-Nummern ohne E0-Merkmal) auf nsfb-Codes. Die US-Zuordnung der
// Zeichentasten dient nur Strg-Kürzeln; Zeichen selbst kommen als Unicode über EV_TEXT.
static enum nsfb_key_code_e map_key(int code)
{
    static const char row1[] = "1234567890-=";
    static const char row2[] = "qwertyuiop[]";
    static const char row3[] = "asdfghjkl;'`";
    static const char row4[] = "\\zxcvbnm,./";
    if (code >= 2 && code <= 13) return (enum nsfb_key_code_e)row1[code - 2];
    if (code >= 16 && code <= 27) return (enum nsfb_key_code_e)row2[code - 16];
    if (code >= 30 && code <= 41) return (enum nsfb_key_code_e)row3[code - 30];
    if (code >= 43 && code <= 53) return (enum nsfb_key_code_e)row4[code - 43];
    if (code >= 59 && code <= 68) return (enum nsfb_key_code_e)(NSFB_KEY_F1 + code - 59);
    switch (code) {
    case 1: return NSFB_KEY_ESCAPE;
    case 14: return NSFB_KEY_BACKSPACE;
    case 15: return NSFB_KEY_TAB;
    case 28: return NSFB_KEY_RETURN;
    case 29: return NSFB_KEY_LCTRL;
    case 42: return NSFB_KEY_LSHIFT;
    case 54: return NSFB_KEY_RSHIFT;
    case 56: return NSFB_KEY_LALT;
    case 57: return NSFB_KEY_SPACE;
    case 71: return NSFB_KEY_HOME;
    case 72: return NSFB_KEY_UP;
    case 73: return NSFB_KEY_PAGEUP;
    case 75: return NSFB_KEY_LEFT;
    case 77: return NSFB_KEY_RIGHT;
    case 79: return NSFB_KEY_END;
    case 80: return NSFB_KEY_DOWN;
    case 81: return NSFB_KEY_PAGEDOWN;
    case 83: return NSFB_KEY_DELETE;
    case 87: return NSFB_KEY_F11;
    case 88: return NSFB_KEY_F12;
    default: return NSFB_KEY_UNKNOWN;
    }
}

static bool is_modifier(enum nsfb_key_code_e key)
{
    return key == NSFB_KEY_LSHIFT || key == NSFB_KEY_RSHIFT || key == NSFB_KEY_LCTRL || key == NSFB_KEY_LALT;
}

static bool ctrl_held(void)
{
    for (int i = 0; i < held_count; i++)
        if (held_modifiers[i] == NSFB_KEY_LCTRL) return true;
    return false;
}

// Zeichentasten ohne Strg würden sonst doppelt tippen: einmal über die Taste, einmal über EV_TEXT.
static bool is_text_key(enum nsfb_key_code_e key)
{
    return key >= NSFB_KEY_SPACE && key < NSFB_KEY_DELETE;
}

static void track_modifier(enum nsfb_key_code_e key, bool down)
{
    for (int i = 0; i < held_count; i++) {
        if (held_modifiers[i] != (int)key) continue;
        if (!down) held_modifiers[i] = held_modifiers[--held_count];
        return;
    }
    if (down && held_count < (int)(sizeof(held_modifiers) / sizeof(held_modifiers[0])))
        held_modifiers[held_count++] = key;
}

static enum nsfb_key_code_e map_button(int code)
{
    switch (code) {
    case 1: return NSFB_KEY_MOUSE_1;
    case 2: return NSFB_KEY_MOUSE_3;
    case 4: return NSFB_KEY_MOUSE_2;
    default: return NSFB_KEY_UNKNOWN;
    }
}

// Übersetzt ein Hostereignis; false, wenn es nichts für nsfb ergibt (z. B. unbekannte Taste).
static bool translate(const int32_t raw[4], nsfb_event_t *event)
{
    enum nsfb_key_code_e key;
    switch (raw[0]) {
    case EV_KEYDOWN:
    case EV_KEYUP:
        key = map_key(raw[1]);
        if (key == NSFB_KEY_UNKNOWN) return false;
        if (is_modifier(key)) track_modifier(key, raw[0] == EV_KEYDOWN);
        else if (is_text_key(key) && !ctrl_held()) return false;
        event->type = raw[0] == EV_KEYDOWN ? NSFB_EVENT_KEY_DOWN : NSFB_EVENT_KEY_UP;
        event->value.keycode = key;
        return true;
    case EV_TEXT:
        // Steuerzeichen kommen als Tasten (Enter, Rücktaste, Tab); Surrogate liefert der Host nicht.
        if (raw[1] < 0x20 || raw[1] == 0x7f || raw[1] > 0x10ffff) return false;
        event->type = NSFB_EVENT_KEY_DOWN;
        event->value.keycode = (enum nsfb_key_code_e)(UCS4_KEY_BASE + raw[1]);
        return true;
    case EV_MOVE:
        event->type = NSFB_EVENT_MOVE_ABSOLUTE;
        event->value.vector.x = raw[2];
        event->value.vector.y = raw[3];
        event->value.vector.z = 0;
        return true;
    case EV_BUTTONDOWN:
    case EV_BUTTONUP:
        key = map_button(raw[1]);
        if (key == NSFB_KEY_UNKNOWN) return false;
        event->type = raw[0] == EV_BUTTONDOWN ? NSFB_EVENT_KEY_DOWN : NSFB_EVENT_KEY_UP;
        event->value.keycode = key;
        return true;
    case EV_WHEEL:
        // fbtk scrollt je Rastung über Maustaste 4 (aufwärts) bzw. 5 (abwärts), jeweils gedrückt und losgelassen.
        key = raw[1] > 0 ? NSFB_KEY_MOUSE_4 : NSFB_KEY_MOUSE_5;
        for (int steps = abs(raw[1]); steps > 0 && pending_count + 2 <= PENDING_MAX; steps--) {
            push(NSFB_EVENT_KEY_DOWN, key);
            push(NSFB_EVENT_KEY_UP, key);
        }
        return false;
    case EV_RESET:
        while (held_count > 0) push(NSFB_EVENT_KEY_UP, (enum nsfb_key_code_e)held_modifiers[--held_count]);
        return false;
    default:
        return false;
    }
}

static bool take_pending(nsfb_event_t *event)
{
    if (pending_count == 0) return false;
    *event = pending[0];
    pending_count--;
    for (int i = 0; i < pending_count; i++) pending[i] = pending[i + 1];
    return true;
}

static int zehos_defaults(nsfb_t *nsfb)
{
    nsfb->width = ZEHOS_WIDTH;
    nsfb->height = ZEHOS_HEIGHT;
    // XRGB8888 liegt als 0xXXRRGGBB im Speicher; present erwartet 0x00RRGGBB.
    nsfb->format = NSFB_FMT_XRGB8888;
    select_plotters(nsfb);
    return 0;
}

static int zehos_initialise(nsfb_t *nsfb)
{
    if (nsfb->width <= 0 || nsfb->height <= 0 || nsfb->width > 1920 || nsfb->height > 1200) return -1;
    uint8_t *buffer = realloc(nsfb->ptr, (size_t)nsfb->width * nsfb->height * 4);
    if (buffer == NULL) return -1;
    nsfb->ptr = buffer;
    nsfb->linelen = nsfb->width * 4;
    return 0;
}

static int zehos_set_geometry(nsfb_t *nsfb, int width, int height, enum nsfb_format_e format)
{
    // Das Paketfenster hat eine feste Größe, und present erwartet XRGB. NetSurf fragt beim Start eigene Werte an
    // (Vorgabe 800×600); die Oberfläche behält ihre Werte, und das Frontend liest sie danach über nsfb_get_geometry.
    (void)nsfb; (void)width; (void)height; (void)format;
    return 0;
}

static int zehos_finalise(nsfb_t *nsfb)
{
    free(nsfb->ptr);
    nsfb->ptr = NULL;
    return 0;
}

static bool zehos_input(nsfb_t *nsfb, nsfb_event_t *event, int timeout)
{
    (void)nsfb;
    uint32_t start = zehos_ticks_ms();
    for (;;) {
        if (zehos_should_exit()) {
            event->type = NSFB_EVENT_CONTROL;
            event->value.controlcode = NSFB_CONTROL_QUIT;
            return true;
        }
        if (take_pending(event)) return true;

        int32_t raw[4] = { 0, 0, 0, 0 };
        int32_t result = zehos_poll_event(raw);
        if (result < 0) {
            // Hostfehler: lieber beenden als ohne Eingabe weiterlaufen.
            event->type = NSFB_EVENT_CONTROL;
            event->value.controlcode = NSFB_CONTROL_QUIT;
            return true;
        }
        if (result == 1) {
            if (translate(raw, event)) return true;
            continue;
        }

        uint32_t waited = zehos_ticks_ms() - start;
        if (timeout >= 0 && waited >= (uint32_t)timeout) return false;
        uint32_t wait = ZEHOS_MAX_WAIT_MS;
        if (timeout >= 0 && (uint32_t)timeout - waited < wait) wait = (uint32_t)timeout - waited;
        zehos_sleep_ms(wait);
    }
}

static int zehos_update(nsfb_t *nsfb, nsfb_bbox_t *box)
{
    (void)box;
    struct nsfb_cursor_s *cursor = nsfb->cursor;
    if (cursor != NULL && cursor->plotted == false) nsfb_cursor_plot(nsfb, cursor);
    // shortcut: immer das ganze Bild, da present keine Teilbereiche kennt; bei Bedarf Teilaktualisierung im ABI ergänzen.
    zehos_present((const uint32_t *)nsfb->ptr, nsfb->width, nsfb->height, (int32_t)tic++, 0);
    return 0;
}

const nsfb_surface_rtns_t zehos_rtns = {
    .defaults = zehos_defaults,
    .initialise = zehos_initialise,
    .finalise = zehos_finalise,
    .input = zehos_input,
    .geometry = zehos_set_geometry,
    .update = zehos_update,
};

NSFB_SURFACE_DEF(zehos, NSFB_SURFACE_ZEHOS, &zehos_rtns)
