// libnsfb-Oberfläche für den ZehOS-Wasm-Loader: Bild über zehos_v1.present, Eingabe über zehos_v1.poll_event.
// Das Build-Skript kopiert die Datei nach libnsfb/src/surface/; dort gelten libnsfbs interne Header.

#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

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

// Startgröße bis zum ersten Größenereignis: der Inhalt des Paketfensters beim Öffnen (800×560 abzüglich Rahmen und
// Titelzeile). Danach folgt das Bild der Fenstergröße (Ereignis 10, Issue #78); ein älterer Kernel ohne dieses
// Ereignis passt das feste Bild weiterhin seitentreu ein.
#define ZEHOS_WIDTH 798
#define ZEHOS_HEIGHT 519
#define ZEHOS_MAX_WIDTH 1920
#define ZEHOS_MAX_HEIGHT 1200
#define ZEHOS_MAX_WAIT_MS 16

enum { EV_KEYDOWN = 1, EV_KEYUP, EV_TEXT, EV_MOVE, EV_BUTTONDOWN, EV_BUTTONUP, EV_WHEEL, EV_RESET, EV_RESIZE = 10 };

// Ein Hostereignis kann mehrere nsfb-Ereignisse ergeben (Rad, Reset); sie warten hier.
#define PENDING_MAX 32
static nsfb_event_t pending[PENDING_MAX];
static int pending_count;
// Gehaltene Tasten und Maustasten, die nsfb bereits als gedrückt kennt; reset lässt sie alle los.
// PENDING_MAX reicht, um alle gleichzeitig freizugeben.
static int held_keys[PENDING_MAX];
static int held_count;
static uint32_t tic;
// Geaendertes Bild, das noch nicht an den Kernel ging (siehe zehos_update).
static bool dirty;

static void push_event(const nsfb_event_t *event)
{
    if (pending_count < PENDING_MAX) pending[pending_count++] = *event;
}

static void push(enum nsfb_event_type_e type, enum nsfb_key_code_e code)
{
    nsfb_event_t event = { .type = type };
    event.value.keycode = code;
    push_event(&event);
}

// Maustasten und Rad wirken an der Ereignisposition; die letzte Bewegung kann veraltet sein.
static void move_to(nsfb_event_t *event, int x, int y)
{
    event->type = NSFB_EVENT_MOVE_ABSOLUTE;
    event->value.vector.x = x;
    event->value.vector.y = y;
    event->value.vector.z = 0;
}

static bool is_mouse_button(int key)
{
    return key == NSFB_KEY_MOUSE_1 || key == NSFB_KEY_MOUSE_2 || key == NSFB_KEY_MOUSE_3;
}

// Müssen den Werten im gepatchten NetSurf-fbtk entsprechen (patches/netsurf-fbtk-text.patch, netsurf-pointer-cancel.patch).
#define UCS4_KEY_BASE 0x40000000
#define CONTROL_CANCEL_POINTER 0x100

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

static bool ctrl_held(void)
{
    for (int i = 0; i < held_count; i++)
        if (held_keys[i] == NSFB_KEY_LCTRL) return true;
    return false;
}

// Zeichentasten ohne Strg würden sonst doppelt tippen: einmal über die Taste, einmal über EV_TEXT.
static bool is_text_key(enum nsfb_key_code_e key)
{
    return key >= NSFB_KEY_SPACE && key < NSFB_KEY_DELETE;
}

// Liefert false für ein Loslassen ohne vorheriges Drücken (etwa nach reset); das darf nsfb nicht erreichen.
static bool track(enum nsfb_key_code_e key, bool down)
{
    for (int i = 0; i < held_count; i++) {
        if (held_keys[i] != (int)key) continue;
        if (!down) held_keys[i] = held_keys[--held_count];
        return true;
    }
    if (!down) return false;
    if (held_count < PENDING_MAX) held_keys[held_count++] = key;
    return true;
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
        // Zeichentasten ohne Strg melden kein Drücken; ihr Loslassen fällt dann in track heraus.
        if (is_text_key(key) && !ctrl_held() && raw[0] == EV_KEYDOWN) return false;
        if (!track(key, raw[0] == EV_KEYDOWN)) return false;
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
        if (!track(key, raw[0] == EV_BUTTONDOWN)) return false;
        push(raw[0] == EV_BUTTONDOWN ? NSFB_EVENT_KEY_DOWN : NSFB_EVENT_KEY_UP, key);
        move_to(event, raw[2], raw[3]);
        return true;
    case EV_WHEEL:
        // fbtk scrollt je Rastung über Maustaste 4 (aufwärts) bzw. 5 (abwärts), jeweils gedrückt und losgelassen.
        key = raw[1] > 0 ? NSFB_KEY_MOUSE_4 : NSFB_KEY_MOUSE_5;
        // Betrag in 64 Bit: abs(INT32_MIN) wäre undefiniert. Die Warteschlange begrenzt die Rastungen ohnehin.
        for (int64_t steps = raw[1] < 0 ? -(int64_t)raw[1] : raw[1]; steps > 0 && pending_count + 2 <= PENDING_MAX; steps--) {
            push(NSFB_EVENT_KEY_DOWN, key);
            push(NSFB_EVENT_KEY_UP, key);
        }
        move_to(event, raw[2], raw[3]);
        return true;
    case EV_RESET:
    {
        // Fokusverlust ist kein Klick: gehaltene Maustasten bricht NetSurf ohne Loslassen-Ereignis ab.
        bool cancel = false;
        while (held_count > 0) {
            int key = held_keys[--held_count];
            if (is_mouse_button(key)) cancel = true;
            else push(NSFB_EVENT_KEY_UP, (enum nsfb_key_code_e)key);
        }
        if (cancel) {
            nsfb_event_t control = { .type = NSFB_EVENT_CONTROL };
            control.value.controlcode = (enum nsfb_control_e)CONTROL_CANCEL_POINTER;
            push_event(&control);
        }
        return false;
    }
    case EV_RESIZE:
        // gui_resize setzt die Mindestgröße 300×200 selbst durch; set_geometry prüft die Obergrenze.
        event->type = NSFB_EVENT_RESIZE;
        event->value.resize.w = raw[2];
        event->value.resize.h = raw[3];
        return true;
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
    if (nsfb->width <= 0 || nsfb->height <= 0 || nsfb->width > ZEHOS_MAX_WIDTH || nsfb->height > ZEHOS_MAX_HEIGHT) return -1;
    uint8_t *buffer = realloc(nsfb->ptr, (size_t)nsfb->width * nsfb->height * 4);
    if (buffer == NULL) return -1;
    nsfb->ptr = buffer;
    nsfb->linelen = nsfb->width * 4;
    return 0;
}

static int zehos_set_geometry(nsfb_t *nsfb, int width, int height, enum nsfb_format_e format)
{
    // present erwartet XRGB; das Format bleibt. NetSurf fragt vor nsfb_init eigene Werte an (Vorgabe 800×600): Diese
    // Anfrage wird ignoriert, damit das erste Bild die Startgröße hat. Danach kommt eine Größe nur aus Ereignis 10.
    (void)format;
    if (nsfb->ptr == NULL) return 0;
    if (width <= 0 || height <= 0 || width > ZEHOS_MAX_WIDTH || height > ZEHOS_MAX_HEIGHT) return -1;
    if (width == nsfb->width && height == nsfb->height) return 0;
    uint8_t *buffer = realloc(nsfb->ptr, (size_t)width * height * 4);
    if (buffer == NULL) return -1;
    // realloc behaelt die alten Bildpunkte mit der alten Zeilenlaenge; bis NetSurf neu zeichnet, waeren sie schraeg
    // versetzt sichtbar. Leer ist der Rest schwarz statt verzerrt.
    memset(buffer, 0, (size_t)width * height * 4);
    nsfb->ptr = buffer;
    nsfb->width = width;
    nsfb->height = height;
    nsfb->linelen = width * 4;
    // Der bisherige Ausschnitt kann über das kleinere Bild hinausragen.
    nsfb->plotter_fns->set_clip(nsfb, NULL);
    return 0;
}

static int zehos_finalise(nsfb_t *nsfb)
{
    free(nsfb->ptr);
    nsfb->ptr = NULL;
    return 0;
}

static void present_if_dirty(nsfb_t *nsfb)
{
    if (!dirty) return;
    dirty = false;
    zehos_present((const uint32_t *)nsfb->ptr, nsfb->width, nsfb->height, (int32_t)tic++, 0);
}

static bool zehos_input(nsfb_t *nsfb, nsfb_event_t *event, int timeout)
{
    // NetSurf fragt Ereignisse erst nach einem vollstaendigen Neuzeichnen ab; hier ist das Bild fertig.
    present_if_dirty(nsfb);
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
    // Jedes fbtk-Widget meldet sein eigenes Update; ein sofortiges present zeigte Zwischenstaende, nach einem
    // Groessenwechsel etwa die neue Leiste ueber dem noch alten Seitenbereich. Gesendet wird in zehos_input.
    // shortcut: immer das ganze Bild, da present keine Teilbereiche kennt; bei Bedarf Teilaktualisierung im ABI ergänzen.
    dirty = true;
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
